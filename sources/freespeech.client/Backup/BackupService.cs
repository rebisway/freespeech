/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.IO.Compression;
using System.Text.Json;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Client.Backup;

/// <summary>Which attachment files go into a backup (protocol §2). Metadata and history are always included.</summary>
public enum AttachmentBackupMode { None, Recent, All }

public record BackupSummary(int Contacts, int Aliases, int Messages, int AttachmentFilesIncluded, int AttachmentFilesLeftOut, long Bytes);
public record RestoreSummary(int Contacts, int Aliases, int Messages, int AttachmentFilesRestored, int ReceivedNowDownloadable, int SentDropped, int Skipped, BackupProfile? Profile = null, IReadOnlyList<string>? Servers = null);

/// <summary>
/// Builds and restores encrypted backups (§2). Format: streaming-encrypted
/// zip (<see cref="BackupCrypto.EncryptStream"/>) holding <c>manifest.json</c>
/// (<see cref="BackupPayload"/>) and one <c>files/N</c> entry per included
/// attachment file. Legacy backups (one base64 JSON blob) are still readable.
/// Kept free of console I/O so mobile/desktop clients can reuse it as is.
/// </summary>
public class BackupService
{
    private const string ManifestEntry = "manifest.json";

    private readonly byte[] _encryptionPrivateKey;
    private readonly ContactStore _contacts;
    private readonly ContactAliasStore _aliases;
    private readonly HistoryStore _history;
    private readonly AttachmentStore _attachments;

    private readonly GroupStore? _groups;
    private readonly Func<BackupProfile?>? _profile;
    private readonly Identity.RevocationPinStore? _pins;
    private readonly Func<List<string>>? _servers;

    public BackupService(byte[] encryptionPrivateKey, ContactStore contacts, ContactAliasStore aliases, HistoryStore history, AttachmentStore attachments, GroupStore? groups = null, Func<BackupProfile?>? profile = null, Identity.RevocationPinStore? pins = null, Func<List<string>>? servers = null)
    {
        _servers = servers;
        _pins = pins;
        _encryptionPrivateKey = encryptionPrivateKey;
        _contacts = contacts;
        _aliases = aliases;
        _history = history;
        _attachments = attachments;
        _groups = groups;
        _profile = profile;
    }

    /// <param name="mode">None = no files; Recent = files of attachments sent/received within <paramref name="recentMonths"/>; All = every file.</param>
    public BackupSummary Create(string path, AttachmentBackupMode mode, int recentMonths = 6, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var cutoff = now.AddMonths(-recentMonths);

        var payload = new BackupPayload
        {
            CreatedAt = now,
            Contacts = _contacts.All.ToList(),
            Aliases = _aliases.All.ToDictionary(kv => kv.Key, kv => kv.Value),
            Groups = _groups?.All.ToList() ?? [],
            Profile = _profile?.Invoke(),
            Servers = _servers?.Invoke() ?? [],
            RevocationPins = _pins?.All.Select(p => new BackupPin { Id = p.Id, Key = p.Key }).ToList() ?? [],
        };

        var tempZip = Path.GetTempFileName();
        int included = 0, leftOut = 0, messages = 0;
        try
        {
            using (var zipStream = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var rec in _attachments.All)
                {
                    var entry = new BackupAttachment { Record = CloneWithoutPath(rec) };
                    var hasFile = rec.State == AttachmentState.Complete && rec.Path != null && File.Exists(rec.Path);
                    var wanted = mode == AttachmentBackupMode.All || (mode == AttachmentBackupMode.Recent && MessageDate(rec) >= cutoff);

                    if (hasFile && wanted)
                    {
                        entry.FileEntry = $"files/{included}";
                        // Images/videos are already compressed: don't burn CPU on them.
                        var zipEntry = zip.CreateEntry(entry.FileEntry, CompressionLevel.NoCompression);
                        using var src = File.OpenRead(rec.Path!);
                        using var dst = zipEntry.Open();
                        src.CopyTo(dst);
                        included++;
                    }
                    else if (hasFile)
                    {
                        leftOut++;
                    }
                    payload.Attachments.Add(entry);
                }

                // The history goes in blocks of lines, one zip entry each, read from the database as the file is written: a long history is
                // never held in memory whole.
                var block = 0;
                foreach (var conversation in _history.Conversations())
                {
                    var lines = new List<HistoryEntry>(HistoryBlock);
                    foreach (var line in _history.Stream(conversation, HistoryBlock))
                    {
                        lines.Add(line);
                        if (lines.Count >= HistoryBlock) { WriteHistoryBlock(zip, block++, conversation, lines); messages += lines.Count; lines.Clear(); }
                    }
                    if (lines.Count > 0) { WriteHistoryBlock(zip, block++, conversation, lines); messages += lines.Count; }
                }

                var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using var ms = manifest.Open();
                JsonSerializer.Serialize(ms, payload);
            }

            long bytes;
            using (var output = File.Create(path))
            {
                EncryptZipTo(tempZip, output);
                bytes = output.Length;
            }

            return new BackupSummary(payload.Contacts.Count, payload.Aliases.Count, messages, included, leftOut, bytes);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* temp file, best effort */ }
        }

        void EncryptZipTo(string zipPath, Stream output)
        {
            using var input = File.OpenRead(zipPath);
            BackupCrypto.EncryptStream(input, output, _encryptionPrivateKey);
        }
    }

    private const int HistoryBlock = 1000;
    private const string HistoryEntryPrefix = "history/";

    private static void WriteHistoryBlock(ZipArchive zip, int number, string conversation, List<HistoryEntry> lines)
    {
        var entry = zip.CreateEntry($"{HistoryEntryPrefix}{number}", CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new BackupHistoryBlock { Conversation = conversation, Entries = lines });
    }

    /// <summary>Merges a backup into the stores (upsert contacts/aliases, history de-duplicated by message_id, attachment files restored and sha256-checked). Returns null if it cannot be decrypted with this identity.</summary>
    public RestoreSummary? Restore(string path)
    {
        if (!BackupCrypto.IsStreamFormat(path)) return RestoreLegacy(path);

        var tempZip = Path.GetTempFileName();
        try
        {
            using (var input = File.OpenRead(path))
            using (var plain = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
            {
                if (!BackupCrypto.TryDecryptStream(input, plain, _encryptionPrivateKey)) return null;
            }

            using var zip = ZipFile.OpenRead(tempZip);
            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry == null) return null;

            BackupPayload? payload;
            using (var ms = manifestEntry.Open())
                payload = JsonSerializer.Deserialize<BackupPayload>(ms);
            if (payload == null) return null;

            var messages = MergeCore(payload);   // the history of a backup that carries it in the manifest
            foreach (var historyEntry in zip.Entries.Where(e => e.FullName.StartsWith(HistoryEntryPrefix, StringComparison.Ordinal)))
            {
                BackupHistoryBlock? block;
                using (var stream = historyEntry.Open()) block = JsonSerializer.Deserialize<BackupHistoryBlock>(stream);
                if (block == null) continue;
                foreach (var e in block.Entries)
                {
                    _history.Append(block.Conversation, e.Id, e.Ts, e.Direction, e.Text, e.ReplyToId, e.From, e.State, e.EditedTs);
                    messages++;
                }
            }

            int restored = 0, downloadable = 0, dropped = 0, skipped = 0;
            foreach (var item in payload.Attachments)
            {
                var rec = item.Record;
                var existing = _attachments.Find(rec.Ref);
                if (existing is { State: AttachmentState.Complete } && existing.Path != null && File.Exists(existing.Path)) { skipped++; continue; }

                if (item.FileEntry != null && zip.GetEntry(item.FileEntry) is { } fileEntry)
                {
                    // Content-addressed: a blob already stored under this root (same file in another conversation) is reused, not rewritten.
                    var dest = _attachments.FindBlob(rec.Root) ?? _attachments.BlobPath(rec.Root, rec.Name);
                    if (!File.Exists(dest))
                    {
                        using (var src = fileEntry.Open())
                        using (var dst = File.Create(dest))
                            src.CopyTo(dst);

                        List<byte[]> leaves;
                        using (var check = File.OpenRead(dest))
                            leaves = MerkleTree.LeavesOf(check, AttachmentService.ChunkSize);
                        if (Convert.ToHexString(MerkleTree.ContentId(leaves, rec.Size)).ToLowerInvariant() != rec.Root) { File.Delete(dest); skipped++; continue; } // corrupted or forged entry
                        _attachments.SaveLeaves(rec.Root, leaves);
                    }

                    rec.Path = dest;
                    rec.State = AttachmentState.Complete;
                    Upsert(rec, existing);
                    restored++;
                }
                else if (rec.Direction == AttachmentDirection.Received)
                {
                    // File left out: keep the offer so 'download' can fetch it again if the sender still has it.
                    rec.Path = null;
                    if (rec.State != AttachmentState.Archived) rec.State = AttachmentState.Offered;   // an archived file stays archived: its archive brings it back
                    if (existing == null) { _attachments.Add(rec); downloadable++; }
                }
                else
                {
                    dropped++; // sent file not in the backup: we can no longer serve it
                }
            }

            return new RestoreSummary(payload.Contacts.Count, payload.Aliases.Count, messages, restored, downloadable, dropped, skipped, payload.Profile, payload.Servers);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* temp file, best effort */ }
        }
    }

    private RestoreSummary? RestoreLegacy(string path)
    {
        var json = BackupCrypto.TryDecrypt(File.ReadAllText(path), _encryptionPrivateKey);
        if (json == null) return null;
        BackupPayload? payload;
        try { payload = JsonSerializer.Deserialize<BackupPayload>(json); }
        catch (JsonException) { return null; }
        if (payload == null) return null;

        var messages = MergeCore(payload);
        return new RestoreSummary(payload.Contacts.Count, payload.Aliases.Count, messages, 0, 0, 0, 0);
    }

    private int MergeCore(BackupPayload payload)
    {
        foreach (var contact in payload.Contacts) _contacts.Upsert(contact);
        if (_pins != null) foreach (var p in payload.RevocationPins) _pins.TryPin(p.Id, p.Key);   // first sight wins: a pin I already hold stays

        // Groups (§7): a group we already have is left alone (our copy may be more recent); missing ones come back with their
        // states, keys and message log — the peers' catch-up then fills whatever happened since the backup.
        if (_groups != null)
            foreach (var g in payload.Groups.Where(g => _groups.Find(g.GroupId) == null)) _groups.Add(g);
        foreach (var (name, id) in payload.Aliases) _aliases.Set(name, id);

        var messages = 0;
        foreach (var (contactId, entries) in payload.History)
            foreach (var e in entries)
            {
                _history.Append(contactId, e.Id, e.Ts, e.Direction, e.Text, e.ReplyToId, e.From, e.State, e.EditedTs);
                messages++;
            }
        return messages;
    }

    private void Upsert(AttachmentRecord rec, AttachmentRecord? existing)
    {
        if (existing == null) { _attachments.Add(rec); return; }
        existing.Path = rec.Path;
        existing.State = rec.State;
        _attachments.Save();
    }

    /// <summary>The attachment's message date: the ts embedded in its ref ("senderId:ticks").</summary>
    internal static DateTime MessageDate(AttachmentRecord rec)
    {
        var i = rec.Ref.LastIndexOf(':');
        return i >= 0 && long.TryParse(rec.Ref[(i + 1)..], out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : rec.LastActivityAt;
    }

    private static AttachmentRecord CloneWithoutPath(AttachmentRecord r) => new()
    {
        Ref = r.Ref, ContactId = r.ContactId, GroupId = r.GroupId, Direction = r.Direction, Name = r.Name, Mime = r.Mime,
        Size = r.Size, Root = r.Root, Chunks = r.Chunks, State = r.State, Path = null, LastActivityAt = r.LastActivityAt,
    };
}


