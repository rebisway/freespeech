/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text.Json;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Backup;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>Backup (§2): streaming crypto format, attachment modes (none / recent / all), restore semantics, tamper detection, legacy format compatibility.</summary>
public class BackupTests : IDisposable
{
    private const string AliceId = "aaaaaaaa11111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-bk-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private sealed class Profile
    {
        public required ContactStore Contacts { get; init; }
        public required ContactAliasStore Aliases { get; init; }
        public required HistoryStore History { get; init; }
        public required AttachmentStore Attachments { get; init; }
        public required BackupService Backup { get; init; }
    }

    private Profile NewProfile(string name, byte[]? key = null)
    {
        var dir = Path.Combine(_root, name);
        var contacts = new ContactStore(dir);
        var aliases = new ContactAliasStore(dir);
        var history = new HistoryStore(dir);
        var attachments = new AttachmentStore(dir);
        return new Profile { Contacts = contacts, Aliases = aliases, History = history, Attachments = attachments, Backup = new BackupService(key ?? _key, contacts, aliases, history, attachments) };
    }

    [Fact]
    public void TheBackupCarriesMyNameDescriptionAndPicture()
    {
        var dir = Path.Combine(_root, "profile-source");
        var (contacts, aliases, history, attachments) = (new ContactStore(dir), new ContactAliasStore(dir), new HistoryStore(dir), new AttachmentStore(dir));
        var service = new BackupService(_key, contacts, aliases, history, attachments, null,
            () => new BackupProfile { Nickname = "Alice", Description = "a description", Photo = "cGljdHVyZQ==" });
        var file = Path.Combine(_root, "with-profile.enc");
        service.Create(file, AttachmentBackupMode.None);

        var restored = NewProfile("profile-target").Backup.Restore(file);
        Assert.NotNull(restored);
        Assert.Equal("Alice", restored!.Profile?.Nickname);
        Assert.Equal("a description", restored.Profile?.Description);
        Assert.Equal("cGljdHVyZQ==", restored.Profile?.Photo);
    }
    /// <summary>Adds a completed attachment (real file, correct sha256) whose message date is <paramref name="at"/>.</summary>
    private AttachmentRecord AddFile(Profile p, DateTime at, AttachmentDirection dir, int size, string name)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        var reference = $"{AliceId}:{at.Ticks}";
        var leaves = MerkleTree.LeavesOf(new MemoryStream(content), AttachmentService.ChunkSize);
        var root = Convert.ToHexString(MerkleTree.ContentId(leaves, size)).ToLowerInvariant();
        var path = p.Attachments.BlobPath(root, name);
        File.WriteAllBytes(path, content);
        var rec = p.Attachments.Add(new AttachmentRecord
        {
            Ref = reference, ContactId = AliceId, Direction = dir, Name = name, Mime = "application/octet-stream", Size = size,
            Root = root, Chunks = leaves.Count, State = AttachmentState.Complete, Path = path,
        });
        p.History.Append(AliceId, reference, at.Ticks, dir == AttachmentDirection.Sent ? MessageDirection.Sent : MessageDirection.Received, $"[file] {name}");
        return rec;
    }

    private Profile Populated(out AttachmentRecord recentReceived, out AttachmentRecord oldReceived, out AttachmentRecord oldSent)
    {
        var p = NewProfile("src");
        p.Contacts.Upsert(new Contact { DiscoveryId = AliceId, Nickname = "alice", EncryptionPublicKey = "e", SigningPublicKey = "s" });
        p.Aliases.Set("al", AliceId);
        p.History.Append(AliceId, $"{AliceId}:1", 1, MessageDirection.Received, "hello");
        recentReceived = AddFile(p, DateTime.UtcNow.AddDays(-10), AttachmentDirection.Received, 30_000, "recent.jpg");
        oldReceived = AddFile(p, DateTime.UtcNow.AddMonths(-12), AttachmentDirection.Received, 30_000, "old-in.jpg");
        oldSent = AddFile(p, DateTime.UtcNow.AddMonths(-12), AttachmentDirection.Sent, 30_000, "old-out.jpg");
        return p;
    }

    private string BackupPath(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }

    // ------------------------------------------------------------------

    [Fact]
    public void The_name_I_gave_a_contact_and_the_verified_mark_survive_a_backup()
    {
        var src = Populated(out _, out _, out _);
        var contact = src.Contacts.FindById(AliceId)!;
        contact.LocalName = "Thomas (travail)";
        contact.VerifiedAt = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        src.Contacts.Upsert(contact);

        var path = BackupPath("names.enc");
        src.Backup.Create(path, AttachmentBackupMode.None);
        var dst = NewProfile("dst-names");
        Assert.NotNull(dst.Backup.Restore(path));

        var restored = dst.Contacts.FindById(AliceId)!;
        Assert.Equal("Thomas (travail)", restored.LocalName);
        Assert.Equal("Thomas (travail)", restored.DisplayName);
        Assert.Equal(contact.VerifiedAt, restored.VerifiedAt);
        Assert.Same(restored, dst.Contacts.FindByNickname("thomas (travail)"));   // found by the name I gave
        Assert.Same(restored, dst.Contacts.FindByNickname(restored.Nickname));    // and still by their own
    }

    [Fact]
    public void A_contacts_new_nickname_does_not_erase_the_name_I_gave_them()
    {
        var p = NewProfile("rename");
        var c = new Contact { DiscoveryId = "x1", Nickname = "Thomas", EncryptionPublicKey = "a", SigningPublicKey = "b", LocalName = "Thomas du club" };
        p.Contacts.Upsert(c);

        // What an incoming identity_update does: the contact's own fields change, on the same object.
        var existing = p.Contacts.FindById("x1")!;
        existing.Nickname = "Tom";
        p.Contacts.Upsert(existing);

        Assert.Equal("Thomas du club", p.Contacts.FindById("x1")!.DisplayName);
        Assert.Equal("Tom", p.Contacts.FindById("x1")!.Nickname);

        var other = new Contact { DiscoveryId = "x2", Nickname = "Thomas", EncryptionPublicKey = "a", SigningPublicKey = "b" };
        p.Contacts.Upsert(other);
        Assert.Equal("x2", p.Contacts.FindByNickname("Thomas")!.DiscoveryId);   // two Thomas: the plain one answers to "Thomas"
        Assert.Equal("x1", p.Contacts.FindByNickname("Thomas du club")!.DiscoveryId);
    }

    [Fact]
    public void Mode_all_restoresEveryFile_byteIdentical()
    {
        var src = Populated(out var recent, out var oldIn, out var oldOut);
        var path = BackupPath("all.enc");
        var s = src.Backup.Create(path, AttachmentBackupMode.All);
        Assert.Equal(3, s.AttachmentFilesIncluded);
        Assert.Equal(0, s.AttachmentFilesLeftOut);

        var dst = NewProfile("dst");
        var r = dst.Backup.Restore(path)!;
        Assert.Equal(3, r.AttachmentFilesRestored);
        Assert.Single(dst.Contacts.All);
        Assert.Equal(AliceId, dst.Aliases.Resolve("al"));
        Assert.Equal(4, dst.History.Get(AliceId).Count);

        foreach (var original in new[] { recent, oldIn, oldOut })
        {
            var restored = dst.Attachments.Find(original.Ref)!;
            Assert.Equal(AttachmentState.Complete, restored.State);
            Assert.Equal(File.ReadAllBytes(original.Path!), File.ReadAllBytes(restored.Path!));
        }
        // Everything goes back to the content-addressed store under its root (a sent file can be served again).
        Assert.Equal(oldOut.Root, Path.GetFileNameWithoutExtension(dst.Attachments.Find(oldOut.Ref)!.Path));
        Assert.Equal(3, Directory.GetFiles(dst.Attachments.BlobDir).Length);
    }

    [Fact]
    public void Mode_none_keepsHistory_receivedBecomeRedownloadable_sentAreDropped()
    {
        var src = Populated(out var recent, out var oldIn, out var oldOut);
        var path = BackupPath("none.enc");
        var s = src.Backup.Create(path, AttachmentBackupMode.None);
        Assert.Equal(0, s.AttachmentFilesIncluded);
        Assert.Equal(3, s.AttachmentFilesLeftOut);

        var dst = NewProfile("dst");
        var r = dst.Backup.Restore(path)!;
        Assert.Equal(0, r.AttachmentFilesRestored);
        Assert.Equal(2, r.ReceivedNowDownloadable);
        Assert.Equal(1, r.SentDropped);

        Assert.Equal(4, dst.History.Get(AliceId).Count); // conversation intact
        var back = dst.Attachments.Find(recent.Ref)!;
        Assert.Equal(AttachmentState.Offered, back.State); // 'download' can fetch it again
        Assert.Null(back.Path);
        Assert.Null(dst.Attachments.Find(oldOut.Ref));     // we can no longer serve it
    }

    [Fact]
    public void Mode_recent_filtersByMessageDate_notFileDate()
    {
        var src = Populated(out var recent, out var oldIn, out var oldOut);
        var path = BackupPath("recent.enc");
        var s = src.Backup.Create(path, AttachmentBackupMode.Recent, recentMonths: 6);
        Assert.Equal(1, s.AttachmentFilesIncluded);
        Assert.Equal(2, s.AttachmentFilesLeftOut);

        // A wider window pulls the old ones in.
        Assert.Equal(3, src.Backup.Create(BackupPath("recent24.enc"), AttachmentBackupMode.Recent, recentMonths: 24).AttachmentFilesIncluded);

        var dst = NewProfile("dst");
        dst.Backup.Restore(path);
        Assert.Equal(AttachmentState.Complete, dst.Attachments.Find(recent.Ref)!.State);
        Assert.Equal(AttachmentState.Offered, dst.Attachments.Find(oldIn.Ref)!.State);
    }

    [Fact]
    public void Restore_isIdempotent()
    {
        var src = Populated(out _, out _, out _);
        var path = BackupPath("twice.enc");
        src.Backup.Create(path, AttachmentBackupMode.All);

        var dst = NewProfile("dst");
        dst.Backup.Restore(path);
        var second = dst.Backup.Restore(path)!;

        Assert.Equal(0, second.AttachmentFilesRestored);
        Assert.Equal(3, second.Skipped);
        Assert.Equal(4, dst.History.Get(AliceId).Count);
        Assert.Equal(3, dst.Attachments.All.Count);
    }

    [Fact]
    public void WrongIdentity_cannotRestore()
    {
        var src = Populated(out _, out _, out _);
        var path = BackupPath("k.enc");
        src.Backup.Create(path, AttachmentBackupMode.All);

        var stranger = NewProfile("stranger", key: RandomNumberGenerator.GetBytes(32));
        Assert.Null(stranger.Backup.Restore(path));
        Assert.Empty(stranger.Contacts.All);
    }

    [Fact]
    public void TamperedOrTruncatedBackup_isRejected()
    {
        var src = Populated(out _, out _, out _);
        var path = BackupPath("t.enc");
        src.Backup.Create(path, AttachmentBackupMode.All);
        var bytes = File.ReadAllBytes(path);

        var flipped = (byte[])bytes.Clone();
        flipped[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(BackupPath("flipped.enc"), flipped);
        Assert.Null(NewProfile("d1").Backup.Restore(BackupPath("flipped.enc")));

        File.WriteAllBytes(BackupPath("cut.enc"), bytes[..(bytes.Length - 40)]);
        Assert.Null(NewProfile("d2").Backup.Restore(BackupPath("cut.enc")));

        File.WriteAllBytes(BackupPath("headless.enc"), bytes[..3]);
        Assert.Null(NewProfile("d3").Backup.Restore(BackupPath("headless.enc")));
    }

    [Fact]
    public void LegacyV1Backup_stillRestores()
    {
        var payload = new BackupPayload { Version = "1" };
        payload.Contacts.Add(new Contact { DiscoveryId = AliceId, Nickname = "alice", EncryptionPublicKey = "e", SigningPublicKey = "s" });
        payload.History[AliceId] = [new HistoryEntry { Id = $"{AliceId}:5", Direction = MessageDirection.Received, Text = "old format", Ts = 5 }];
        var path = BackupPath("legacy.enc");
        File.WriteAllText(path, BackupCrypto.Encrypt(JsonSerializer.Serialize(payload), _key));

        var dst = NewProfile("dst");
        var r = dst.Backup.Restore(path)!;
        Assert.Equal(1, r.Contacts);
        Assert.Equal("old format", dst.History.Get(AliceId).Single().Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024 - 1)]
    [InlineData(1024 * 1024)]         // exactly one block
    [InlineData(1024 * 1024 + 1)]     // spills into a second block
    [InlineData(3 * 1024 * 1024 + 17)]
    public void StreamCrypto_roundTrips_acrossBlockBoundaries(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        var cipher = new MemoryStream();
        BackupCrypto.EncryptStream(new MemoryStream(data), cipher, _key);
        cipher.Position = 0;

        var plain = new MemoryStream();
        Assert.True(BackupCrypto.TryDecryptStream(cipher, plain, _key));
        Assert.Equal(data, plain.ToArray());
    }

    [Fact]
    public void StreamCrypto_detectsBlockReorderAndTruncationAtBlockBoundary()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024); // 3 full blocks + final empty-or-full
        var cipher = new MemoryStream();
        BackupCrypto.EncryptStream(new MemoryStream(data), cipher, _key);
        var all = cipher.ToArray();

        // Drop the last block entirely: the previous block is no longer flagged final -> must fail.
        var blockLen = 4 + 12 + (1024 * 1024 + 16);
        var truncated = all[..(5 + 2 * blockLen)];
        Assert.False(BackupCrypto.TryDecryptStream(new MemoryStream(truncated), new MemoryStream(), _key));

        // Swap blocks 0 and 1 (same length): the index in the AD must reject it.
        var swapped = (byte[])all.Clone();
        Array.Copy(all, 5 + blockLen, swapped, 5, blockLen);
        Array.Copy(all, 5, swapped, 5 + blockLen, blockLen);
        Assert.False(BackupCrypto.TryDecryptStream(new MemoryStream(swapped), new MemoryStream(), _key));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir, best effort */ }
    }

    [Fact]
    public void BackupMatchesPhrases_checksBeforeAnythingIsCreated()
    {
        string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
        var (a1, a2, a3) = (Pad("backup one"), Pad("backup two"), Pad("backup three"));
        var mine = FreeSpeech.Client.Identity.IdentityService.Derive(a1, a2, a3, "me");
        var file = Path.Combine(Path.GetTempPath(), "fs-match-" + Guid.NewGuid().ToString("N") + ".enc");
        try
        {
            using (var plain = new MemoryStream(new byte[] { 1, 2, 3 }))
            using (var output = File.Create(file))
                BackupCrypto.EncryptStream(plain, output, mine.EncryptionPrivateKey);

            Assert.True(FreeSpeech.Client.FreeSpeechClient.BackupMatchesPhrases(file, a1, a2, a3));
            Assert.False(FreeSpeech.Client.FreeSpeechClient.BackupMatchesPhrases(file, a1, a2, Pad("a typo here")));
        }
        finally { File.Delete(file); }
    }
}
