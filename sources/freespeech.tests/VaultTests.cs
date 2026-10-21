/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Storage;
using Microsoft.Data.Sqlite;

namespace FreeSpeech.Tests;

/// <summary>The local database is a plain SQLite file whose content fields are sealed by the <see cref="Vault"/>.</summary>
public class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-vault-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private const string Secret = "the plain words nobody may read in the file";

    [Fact]
    public void Vault_roundTrips_andRefusesAWrongKeyContextTamperingAndTruncation()
    {
        var vault = new Vault(Key(1));
        var sealedValue = vault.SealText(Secret, "ctx");
        Assert.Equal(Secret, vault.OpenText(sealedValue, "ctx"));
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(sealedValue));

        Assert.Null(new Vault(Key(2)).OpenText(sealedValue, "ctx"));          // another key
        Assert.Null(vault.OpenText(sealedValue, "other"));                    // another row or column
        var flipped = (byte[])sealedValue.Clone(); flipped[^1] ^= 1;
        Assert.Null(vault.OpenText(flipped, "ctx"));                          // altered
        var versionFlipped = (byte[])sealedValue.Clone(); versionFlipped[0] ^= 1;
        Assert.Null(vault.OpenText(versionFlipped, "ctx"));
        Assert.Null(vault.OpenText(sealedValue[..10], "ctx"));                // truncated
        Assert.Null(vault.OpenText([], "ctx"));
    }

    [Fact]
    public void Vault_sealsTwiceDifferently_soEqualTextsCannotBeLinked()
    {
        var vault = new Vault(Key(1));
        Assert.NotEqual(vault.SealText("same", "ctx"), vault.SealText("same", "ctx"));
    }

    [Fact]
    public void Vault_needsA32ByteKey() => Assert.Throws<ArgumentException>(() => new Vault(new byte[16]));

    [Fact]
    public void ContentIsSealedInEveryTable_butIdentifiersStayReadable()
    {
        var dir = Dir("content");
        using (var db = ClientDatabase.Open(dir, Key(1)))
        {
            var history = new HistoryStore(db);
            history.Append("conv", "a:1", 1, MessageDirection.Received, Secret, replyToId: "b:9", from: "author-id");
            history.Edit("conv", "a:2", Secret + " parked", 6);   // parked: its message is not here yet
            history.SetReaction("conv", "a:1", "who-id", "👍", 7);
            new ThumbnailStore(db).Put("root", new PictureThumbnail(Encoding.ASCII.GetBytes(Secret), 10, 10));
            var outbox = new DocCollection<PendingMessage>(db, "outbox", m => m.Id);
            outbox.Save([new PendingMessage { Id = "a:3", To = "peer", Text = Secret, CreatedAt = DateTime.UtcNow }]);
        }

        SqliteConnection.ClearAllPools();
        var wal = ClientDatabase.PathFor(dir) + "-wal";
        var whole = File.ReadAllText(ClientDatabase.PathFor(dir), Encoding.Latin1) + (File.Exists(wal) ? File.ReadAllText(wal, Encoding.Latin1) : "");
        Assert.DoesNotContain(Secret, whole);
        Assert.DoesNotContain("author-id", whole);
        Assert.DoesNotContain("👍", whole);

        using var raw = new SqliteConnection($"Data Source={ClientDatabase.PathFor(dir)};Pooling=False");
        raw.Open();
        Assert.Equal("conv", Scalar(raw, "SELECT conv FROM messages"));         // identifiers are plain: queries and indexes need them
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(text) FROM messages"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(reply_to) FROM messages"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(from_id) FROM messages"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(text) FROM message_ops"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(emoji) FROM reactions"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(data) FROM thumbnails"));
        Assert.Equal("blob", Scalar(raw, "SELECT typeof(json) FROM docs"));
    }

    private static string Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    [Fact]
    public void SealedContent_readsBack_afterReopen()
    {
        var dir = Dir("readback");
        using (var db = ClientDatabase.Open(dir, Key(1)))
        {
            var h = new HistoryStore(db);
            h.Append("c", "a:1", 1, MessageDirection.Received, Secret, replyToId: "b:1", from: "m1");
            h.SetReaction("c", "a:1", "w", "❤️", 2);
            h.Append("c", "a:2", 3, MessageDirection.Sent, "second");
            h.Retract("c", "a:2");                                   // an erased message keeps an empty (plain) text
            h.Edit("c", "a:9", "parked then applied", 8);
            h.Append("c", "a:9", 8, MessageDirection.Received, "original");
            new ThumbnailStore(db).Put("r", new PictureThumbnail([1, 2, 3, 4], 2, 2));
        }

        using var again = ClientDatabase.Open(dir, Key(1));
        var history = new HistoryStore(again);
        var first = history.Find("c", "a:1")!;
        Assert.Equal(Secret, first.Text);
        Assert.Equal("b:1", first.ReplyToId);
        Assert.Equal("m1", first.From);
        Assert.Equal([("w", "❤️")], history.Reactions("c")["a:1"]);
        Assert.Equal("", history.Find("c", "a:2")!.Text);
        Assert.Equal("parked then applied", history.Find("c", "a:9")!.Text);
        Assert.Equal([1, 2, 3, 4], new ThumbnailStore(again).Get("r")!.Data);
        Assert.Contains(history.Search("plain words", 5), hit => hit.Entry.Id == "a:1");   // the search opens the texts and compares them
    }

    [Fact]
    public void ASealedValueCopiedToAnotherRow_doesNotOpen()
    {
        var dir = Dir("swap");
        using (var db = ClientDatabase.Open(dir, Key(1)))
        {
            var h = new HistoryStore(db);
            h.Append("c", "a:1", 1, MessageDirection.Sent, "first message");
            h.Append("c", "a:2", 2, MessageDirection.Sent, "second message");
            db.Execute("UPDATE messages SET text = (SELECT text FROM messages WHERE id = 'a:1') WHERE id = 'a:2'");
        }

        using var again = ClientDatabase.Open(dir, Key(1));
        var ex = Assert.Throws<ClientException>(() => new HistoryStore(again).Find("c", "a:2"));
        Assert.Equal("db_open", ex.Code);
    }

    [Fact]
    public void WrongKey_noKey_andKeyOnAPlainDatabase_areRefused()
    {
        var sealedDir = Dir("sealed");
        using (ClientDatabase.Open(sealedDir, Key(1))) { }
        Assert.Equal("db_open", Assert.Throws<ClientException>(() => ClientDatabase.Open(sealedDir, Key(2))).Code);
        Assert.Equal("db_open", Assert.Throws<ClientException>(() => ClientDatabase.Open(sealedDir, null)).Code);

        var plainDir = Dir("plain");
        using (ClientDatabase.Open(plainDir, null)) { }
        Assert.Equal("db_open", Assert.Throws<ClientException>(() => ClientDatabase.Open(plainDir, Key(1))).Code);
        using (ClientDatabase.Open(plainDir, null)) { }   // and a plain one still opens plain
    }

    [Fact]
    public void Rekey_sealsEverythingAgain_theOldKeyStopsWorking()
    {
        var dir = Dir("rekey");
        using (var db = ClientDatabase.Open(dir, Key(1)))
        {
            var h = new HistoryStore(db);
            h.Append("c", "a:1", 1, MessageDirection.Received, Secret, replyToId: "b:1", from: "m");
            h.SetReaction("c", "a:1", "w", "👍", 2);
            h.Append("c", "a:2", 3, MessageDirection.Sent, "erased");
            h.Retract("c", "a:2");
            new ThumbnailStore(db).Put("r", new PictureThumbnail([9, 8, 7], 1, 1));
            new DocCollection<PendingMessage>(db, "outbox", m => m.Id).Save([new PendingMessage { Id = "a:3", To = "p", Text = "queued", CreatedAt = DateTime.UtcNow }]);
            db.Rekey(Key(2));
            Assert.Equal(Secret, h.Find("c", "a:1")!.Text);   // the open database keeps working
        }

        Assert.Equal("db_open", Assert.Throws<ClientException>(() => ClientDatabase.Open(dir, Key(1))).Code);
        using var again = ClientDatabase.Open(dir, Key(2));
        var history = new HistoryStore(again);
        var e = history.Find("c", "a:1")!;
        Assert.Equal((Secret, "b:1", "m"), (e.Text, e.ReplyToId, e.From));
        Assert.Equal([("w", "👍")], history.Reactions("c")["a:1"]);
        Assert.Equal("", history.Find("c", "a:2")!.Text);
        Assert.Equal([9, 8, 7], new ThumbnailStore(again).Get("r")!.Data);
        Assert.Equal("queued", new DocCollection<PendingMessage>(again, "outbox", m => m.Id).Load().Single().Text);
    }

    private const string P1 = "the quick brown fox jumps over the lazy dog while the band plays on and on all night";
    private const string P2 = "a journey of a thousand miles begins with a single step taken on a quiet morning";
    private const string P3 = "to be or not to be that is the question whether tis nobler in the mind to suffer";

    [Fact]
    public async Task AKeyCachedForAnotherIdentity_withNoIdentityInTheDatabase_doesNotBlockANewIdentity()
    {
        var dir = Dir("stale-key");
        Directory.CreateDirectory(dir);
        new PlainFileKeyCache(dir).Store(Key(7));                       // left by an identity that no longer has its data
        File.WriteAllBytes(ClientDatabase.PathFor(dir), Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray());   // a file this build cannot read

        await using (var client = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = "http://127.0.0.1:1" }))
        {
            Assert.False(client.TryLoadIdentity());
            Assert.False(client.NeedsPhrases);
            Assert.Equal("alice", client.CreateIdentity(P1, P2, P3, "alice").Nickname);
        }

        await using var again = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = "http://127.0.0.1:1" });
        Assert.True(again.TryLoadIdentity());
    }

    [Fact]
    public void ADatabaseInAnEarlierEncryptedFormat_isSetAside_notDeleted_andANewOneStarts()
    {
        var dir = Dir("legacy");
        Directory.CreateDirectory(dir);
        var foreign = new byte[4096];
        Random.Shared.NextBytes(foreign);   // an encrypted file has no "SQLite format 3" header
        File.WriteAllBytes(ClientDatabase.PathFor(dir), foreign);
        File.WriteAllBytes(ClientDatabase.PathFor(dir) + "-wal", [1, 2, 3]);

        using (var db = ClientDatabase.Open(dir, Key(1)))
            new HistoryStore(db).Append("c", "a:1", 1, MessageDirection.Sent, "fresh start");

        Assert.Equal(foreign, File.ReadAllBytes(ClientDatabase.PathFor(dir) + ".legacy"));
        Assert.True(File.Exists(ClientDatabase.PathFor(dir) + "-wal.legacy"));
        using var again = ClientDatabase.Open(dir, Key(1));
        Assert.Equal("fresh start", new HistoryStore(again).Get("c").Single().Text);
    }
}
