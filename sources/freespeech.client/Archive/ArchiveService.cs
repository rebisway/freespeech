/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.IO.Compression;
using System.IO.Pipelines;
using System.Text.Json;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Backup;

namespace FreeSpeech.Client.Archive;

/// <summary>
/// Moves downloaded attachment files into an encrypted archive file, to free space on the device (protocol §2 "Archive"). The messages
/// stay in the chat; an archived attachment keeps its record (state <see cref="AttachmentState.Archived"/>) and, for a picture or a video,
/// its thumbnail. Loading the archive later puts the files back, matched by content, nothing duplicated.
///
/// The file is the format of a backup (<see cref="BackupCrypto"/>: streaming AEAD with a key derived from the identity, a zip inside), with
/// its own index <c>archive.json</c> instead of <c>manifest.json</c>, so neither can be mistaken for the other. It is written straight into
/// its encrypted form (no clear temporary copy as big as the archive) and read back whole before a single file leaves the device.
/// </summary>
public sealed class ArchiveService(byte[] encryptionPrivateKey, AttachmentService attachments)
{
    private const string ManifestEntry = "archive.json";

    private AttachmentStore Store => attachments.Store;

    /// <summary>What <paramref name="selection"/> would archive, without touching anything.</summary>
    public ArchivePreview Preview(ArchiveSelection selection)
    {
        var groups = Eligible(selection);
        return new ArchivePreview(groups.Count, groups.Sum(g => g.Records.Count), groups.Sum(g => g.Size));
    }

    /// <summary>
    /// Writes the archive to <paramref name="path"/>, checks it can be read back, then moves the files out of the device. Nothing is lost if
    /// anything fails before the check: the files are still where they were (and the half-written archive is deleted).
    /// </summary>
    public ArchiveSummary Create(string path, ArchiveSelection selection)
    {
        try
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            return Create(output, selection, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// The same, written to an open <b>seekable</b> stream (a file the person chose, on any storage the phone can reach): no local copy as big as
    /// the archive is needed. <paramref name="reopen"/> gives a fresh stream on what was written, to read it back before anything is removed.
    /// </summary>
    public ArchiveSummary Create(Stream destination, ArchiveSelection selection, Func<Stream> reopen)
    {
        var groups = Eligible(selection);
        if (groups.Count == 0) return new ArchiveSummary(0, 0, 0, 0);

        // The picture or the video stays recognisable in the chat: make sure its thumbnail is in the database before the file goes.
        foreach (var group in groups) attachments.ThumbnailOf(group.Records[0]);

        var manifest = new ArchiveManifest { CreatedAt = DateTime.UtcNow };
        var pipe = new Pipe();
        Exception? failure = null;

        // One side zips into the pipe, the other encrypts what comes out: the archive is never held, nor written, in clear.
        var zipping = Task.Run(() =>
        {
            try
            {
                using var zip = new ZipArchive(pipe.Writer.AsStream(), ZipArchiveMode.Create, leaveOpen: true);
                var number = 0;
                foreach (var group in groups)
                {
                    var item = new ArchiveItem { Root = group.Root, Size = group.Size, File = $"files/{number++}", Records = group.Records.Select(CloneWithoutPath).ToList() };
                    var entry = zip.CreateEntry(item.File, CompressionLevel.NoCompression);   // pictures and videos are compressed already
                    using (var source = File.OpenRead(group.Records[0].Path!))
                    using (var target = entry.Open())
                        source.CopyTo(target);
                    manifest.Items.Add(item);
                }
                using var index = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(index, manifest);
            }
            catch (Exception ex) { failure = ex; }
            finally { pipe.Writer.Complete(failure); }
        });

        long archiveBytes;
        try
        {
            try { BackupCrypto.EncryptStream(pipe.Reader.AsStream(), destination, encryptionPrivateKey); }
            finally { pipe.Reader.Complete(); }
            destination.Flush();
            archiveBytes = destination.Length;
        }
        catch
        {
            zipping.Wait();
            throw;
        }
        zipping.Wait();
        if (failure != null) throw new IOException("The archive could not be written.", failure);

        // Read the whole archive back (every block is authenticated) before anything is removed from the device.
        using (var check = reopen())
            if (!BackupCrypto.TryDecryptStream(check, Stream.Null, encryptionPrivateKey))
                throw new IOException("The archive failed its check after writing; nothing was archived.");

        long freed = 0;
        var records = 0;
        foreach (var group in groups)
        {
            var blob = group.Records[0].Path;
            foreach (var record in group.Records)
            {
                Store.DeleteParts(record.Ref);
                record.State = AttachmentState.Archived;
                record.Path = null;
                records++;
            }
            if (blob != null) TryDelete(blob);
            freed += group.Size;
        }
        Store.Save();
        return new ArchiveSummary(groups.Count, records, freed, archiveBytes);
    }

    /// <summary>
    /// Puts the files of an archive back: each content is written once, checked against its identity, and every record that points at it
    /// becomes complete again. A record the device does not have (an archive made on another device of the identity) is added.
    /// Returns null if the file is not an archive, or cannot be opened with this identity.
    /// </summary>
    public ArchiveRestoreSummary? Restore(string path) => File.Exists(path) ? Restore(() => File.OpenRead(path)) : null;

    /// <summary>The same, reading from a seekable stream opened by <paramref name="open"/> (a file the person chose, on any storage).</summary>
    public ArchiveRestoreSummary? Restore(Func<Stream> open)
    {
        var plainZip = Path.GetTempFileName();
        try
        {
            using (var input = open())
            using (var plain = new FileStream(plainZip, FileMode.Create, FileAccess.Write))
            {
                if (!BackupCrypto.IsStreamFormat(input) || !BackupCrypto.TryDecryptStream(input, plain, encryptionPrivateKey)) return null;
            }

            using var zip = ZipFile.OpenRead(plainZip);
            if (zip.GetEntry(ManifestEntry) is not { } indexEntry) return null;
            ArchiveManifest? manifest;
            using (var index = indexEntry.Open()) manifest = JsonSerializer.Deserialize<ArchiveManifest>(index);
            if (manifest == null) return null;

            int files = 0, records = 0, skipped = 0;
            foreach (var item in manifest.Items)
            {
                if (zip.GetEntry(item.File) is not { } fileEntry || item.Root.Length != 64) { skipped++; continue; }

                // A content already on the device (the same file kept in another conversation) is not written again.
                var dest = Store.FindBlob(item.Root) ?? Store.BlobPath(item.Root, item.Records.FirstOrDefault()?.Name ?? "file");
                if (!File.Exists(dest))
                {
                    using (var source = fileEntry.Open())
                    using (var target = File.Create(dest))
                        source.CopyTo(target);

                    List<byte[]> leaves;
                    using (var check = File.OpenRead(dest)) leaves = MerkleTree.LeavesOf(check, AttachmentService.ChunkSize);
                    if (Convert.ToHexString(MerkleTree.ContentId(leaves, new FileInfo(dest).Length)).ToLowerInvariant() != item.Root)
                    {
                        TryDelete(dest);   // damaged or forged: not kept
                        skipped++;
                        continue;
                    }
                    Store.SaveLeaves(item.Root, leaves);
                }

                foreach (var record in item.Records)
                {
                    var existing = Store.Find(record.Ref);
                    if (existing == null)
                    {
                        record.Path = dest;
                        record.State = AttachmentState.Complete;
                        Store.Add(record);
                    }
                    else
                    {
                        existing.Path = dest;
                        existing.State = AttachmentState.Complete;
                    }
                    records++;
                }
                files++;
            }
            Store.Save();
            return new ArchiveRestoreSummary(files, records, skipped);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { return null; }
        finally { TryDelete(plainZip); }
    }

    // ------------------------------------------------------------------

    private static readonly string[] MediaKinds = ["image/", "video/", "audio/"];

    private sealed record Group(string Root, long Size, List<AttachmentRecord> Records);

    /// <summary>The contents to archive: downloaded files whose records all match the selection, oldest first.</summary>
    private List<Group> Eligible(ArchiveSelection selection) =>
        Store.All.Where(r => r.State == AttachmentState.Complete && r.Path != null && File.Exists(r.Path))
            .GroupBy(r => r.Root)
            .Where(g => g.All(r => Matches(r, selection)))
            .Select(g => new Group(g.Key, g.First().Size, g.ToList()))
            .OrderBy(g => g.Records.Min(BackupService.MessageDate))
            .ToList();

    private static bool Matches(AttachmentRecord record, ArchiveSelection selection)
    {
        var date = BackupService.MessageDate(record);
        if (selection.Before is { } before && date >= before) return false;
        if (selection.From is { } from && date < from) return false;
        if (selection.To is { } to && date >= to) return false;
        if (selection.Conversation is { Length: > 0 } conversation && (record.GroupId ?? record.ContactId) != conversation) return false;
        if (selection.MimePrefix is { Length: > 0 } prefix && !record.Mime.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (selection.OtherKindsOnly && MediaKinds.Any(kind => record.Mime.StartsWith(kind, StringComparison.OrdinalIgnoreCase))) return false;
        return record.Size >= selection.MinSize;
    }

    private static AttachmentRecord CloneWithoutPath(AttachmentRecord r) => new()
    {
        Ref = r.Ref, ContactId = r.ContactId, GroupId = r.GroupId, Direction = r.Direction, Name = r.Name, Mime = r.Mime,
        Size = r.Size, Root = r.Root, Chunks = r.Chunks, State = AttachmentState.Complete, Path = null, LastActivityAt = r.LastActivityAt,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* in use: left for the next cleanup */ }
    }
}
