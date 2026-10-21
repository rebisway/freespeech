/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Attachments;

/// <summary>
/// On-disk attachment storage under <c>{profile}/attachments</c>:
/// records in the profile database; <c>out/</c> (copies of files we sent,
/// served on request), <c>parts/{ref}/{chunk}.bin</c> (chunks received so
/// far — their presence IS the resume state), <c>in/</c> (finished downloads).
/// </summary>
public class AttachmentStore
{
    private readonly string _dir;
    private readonly DocCollection<AttachmentRecord> _docs;
    private readonly List<AttachmentRecord> _records;
    private readonly object _lock = new();

    public AttachmentStore(string profileDir, ClientDatabase db)
    {
        _dir = Path.Combine(profileDir, "attachments");
        Directory.CreateDirectory(_dir);
        _docs = new DocCollection<AttachmentRecord>(db, "attachments", r => r.Ref);
        _records = _docs.Load();
        Thumbnails = new ThumbnailStore(db);
    }

    /// <summary>The small pictures of the files, in the database.</summary>
    public ThumbnailStore Thumbnails { get; }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal AttachmentStore(string profileDir) : this(profileDir, ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<AttachmentRecord> All { get { lock (_lock) return _records.ToList(); } }

    public AttachmentRecord? Find(string reference)
    {
        lock (_lock) return _records.Find(r => r.Ref == reference);
    }

    /// <summary>
    /// How to read the free space of the drive that holds a folder. The default asks the operating system; the Android app sets its own
    /// (the generic call does not see the right partition there). Null = the space is unknown, so nothing is refused for lack of it.
    /// </summary>
    public static Func<string, long?>? FreeSpaceProbe { get; set; }

    /// <summary>Bytes free where the attachments are kept, or null when unknown.</summary>
    public long? FreeSpace()
    {
        try
        {
            if (FreeSpaceProbe != null) return FreeSpaceProbe(_dir);
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return null;
            var root = Path.GetPathRoot(Path.GetFullPath(_dir));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Adds a record unless one with the same ref exists (a re-delivered offer); returns the stored one.</summary>
    public AttachmentRecord Add(AttachmentRecord record)
    {
        lock (_lock)
        {
            var existing = _records.Find(r => r.Ref == record.Ref);
            if (existing != null) return existing;
            _records.Add(record);
            Save();
            return record;
        }
    }

    public void Save()
    {
        lock (_lock) _docs.Save(_records);
    }

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    /// <summary>Strips any directory part and invalid characters from a peer-supplied file name.</summary>
    public static string SanitizeFileName(string name)
    {
        var clean = SafeName(Path.GetFileName(name.Replace('\\', '/')));
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }

    public string BlobDir => Path.Combine(_dir, "blobs");

    /// <summary>Content-addressed location: one file per Merkle root whatever the conversation, keeping the extension for convenience.</summary>
    public string BlobPath(string root, string name) => Path.Combine(EnsureDir("blobs"), root + SafeExtension(name));

    /// <summary>An already-stored blob for this root (any extension), or null.</summary>
    public string? FindBlob(string root)
    {
        var dir = Path.Combine(_dir, "blobs");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, root + ".*").Concat(Directory.EnumerateFiles(dir, root)).FirstOrDefault() : null;
    }

    /// <summary>Complete record (any conversation) already holding this content, if its file is still there.</summary>
    public AttachmentRecord? FindCompleteByRoot(string root)
    {
        lock (_lock) return _records.Find(r => r.Root == root && r.State == AttachmentState.Complete && r.Path != null && File.Exists(r.Path));
    }

    private static string SafeExtension(string name)
    {
        var ext = Path.GetExtension(SanitizeFileName(name));
        return ext.Length is > 0 and <= 10 ? ext.ToLowerInvariant() : "";
    }

    /// <summary>Per-file leaf hashes (32 bytes each, concatenated) so a holder can build Merkle proofs without re-hashing the file.</summary>
    public void SaveLeaves(string root, IReadOnlyList<byte[]> leaves) =>
        File.WriteAllBytes(Path.Combine(EnsureDir("trees"), root + ".bin"), leaves.SelectMany(l => l).ToArray());

    public List<byte[]>? LoadLeaves(string root)
    {
        var path = Path.Combine(_dir, "trees", root + ".bin");
        if (!File.Exists(path)) return null;
        var all = File.ReadAllBytes(path);
        if (all.Length == 0 || all.Length % MerkleTree.HashSize != 0) return null;
        return Enumerable.Range(0, all.Length / MerkleTree.HashSize).Select(i => all[(i * MerkleTree.HashSize)..((i + 1) * MerkleTree.HashSize)]).ToList();
    }

    private string PartsDir(string reference) => Path.Combine(EnsureDir("parts"), SafeName(reference));

    private string EnsureDir(string sub)
    {
        var d = Path.Combine(_dir, sub);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Stores a verified chunk with its Merkle proof (kept so this device can serve the chunk before it holds the whole file). Written aside then moved: a reader never sees half a chunk.</summary>
    public void WritePart(string reference, int chunkId, byte[] data, IReadOnlyList<byte[]>? proof = null)
    {
        var dir = PartsDir(reference);
        Directory.CreateDirectory(dir);
        if (proof != null) AtomicWrite(Path.Combine(dir, $"{chunkId}.proof"), proof.SelectMany(p => p).ToArray());
        AtomicWrite(Path.Combine(dir, $"{chunkId}.bin"), data);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public bool HasPart(string reference, int chunkId) => File.Exists(Path.Combine(PartsDir(reference), $"{chunkId}.bin"));

    public byte[]? ReadPart(string reference, int chunkId)
    {
        try { var path = Path.Combine(PartsDir(reference), $"{chunkId}.bin"); return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>The Merkle proof stored with a chunk, or null (a chunk stored before proofs were kept cannot be served until the file is complete).</summary>
    public List<byte[]>? ReadProof(string reference, int chunkId)
    {
        try
        {
            var path = Path.Combine(PartsDir(reference), $"{chunkId}.proof");
            if (!File.Exists(path)) return null;
            var all = File.ReadAllBytes(path);
            if (all.Length % MerkleTree.HashSize != 0) return null;
            return Enumerable.Range(0, all.Length / MerkleTree.HashSize).Select(i => all[(i * MerkleTree.HashSize)..((i + 1) * MerkleTree.HashSize)]).ToList();
        }
        catch (IOException) { return null; }
    }

    public HashSet<int> PartIds(string reference)
    {
        var dir = PartsDir(reference);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.bin")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? id : -1)
            .Where(id => id >= 0)
            .ToHashSet();
    }

    /// <summary>Concatenates chunks 0..n-1 into <paramref name="destination"/>.</summary>
    public void Assemble(string reference, int chunks, string destination)
    {
        var dir = PartsDir(reference);
        using var output = File.Create(destination);
        for (var i = 0; i < chunks; i++)
        {
            using var part = File.OpenRead(Path.Combine(dir, $"{i}.bin"));
            part.CopyTo(output);
        }
    }

    public void DeleteParts(string reference)
    {
        var dir = PartsDir(reference);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
