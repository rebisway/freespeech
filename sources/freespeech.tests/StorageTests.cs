/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Tests;

/// <summary>The profile database: encryption at rest, atomic collections, paged history, key derivation.</summary>
public class StorageTests : IDisposable
{
    private const string P1 = "the quick brown fox jumps over the lazy dog while the band plays on and on all night";
    private const string P2 = "a journey of a thousand miles begins with a single step taken on a quiet morning";
    private const string P3 = "to be or not to be that is the question whether tis nobler in the mind to suffer";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static void Fill(HistoryStore h, string conv, int count)
    {
        for (var i = 1; i <= count; i++)
            h.Append(conv, $"a:{i}", i, i % 2 == 0 ? MessageDirection.Sent : MessageDirection.Received, $"m{i}");
    }

    [Fact]
    public void Database_IsEncryptedAtRest_AndNeedsTheRightKey()
    {
        var dir = Dir("enc");
        using (var db = ClientDatabase.Open(dir, Key(1)))
            new HistoryStore(db).Append("c", "a:1", 1, MessageDirection.Sent, "a very secret sentence");

        Assert.DoesNotContain("a very secret sentence", File.ReadAllText(ClientDatabase.PathFor(dir), System.Text.Encoding.Latin1));
        Assert.Throws<ClientException>(() => ClientDatabase.Open(dir, Key(2)));
        Assert.Throws<ClientException>(() => ClientDatabase.Open(dir, null));

        using var again = ClientDatabase.Open(dir, Key(1));
        Assert.Equal("a very secret sentence", new HistoryStore(again).Get("c").Single().Text);
    }

    [Fact]
    public void DatabaseKey_IsDeterministic_AndIndependentFromTheIdentityKeys()
    {
        var k1 = IdentityService.DeriveDatabaseKey(P1, P2, P3);
        Assert.Equal(k1, IdentityService.DeriveDatabaseKey(P1, P2, P3));
        Assert.NotEqual(k1, IdentityService.DeriveDatabaseKey(P2, P1, P3));

        var id = IdentityService.Derive(P1, P2, P3, "x");
        Assert.NotEqual(id.EncryptionPrivateKey, k1);
        Assert.NotEqual(id.SigningPrivateKey, k1);
    }

    [Fact]
    public void Collections_PersistAcrossReopen_InInsertionOrder_AndDeletions()
    {
        var dir = Dir("docs");
        using (var db = ClientDatabase.Open(dir, Key(3)))
        {
            var contacts = new ContactStore(db);
            foreach (var n in new[] { "zed", "amy", "bob" })
                contacts.Upsert(new Contact { DiscoveryId = "id-" + n, Nickname = n, EncryptionPublicKey = "e", SigningPublicKey = "s", ConfirmedAt = DateTime.UtcNow });
            contacts.Upsert(new Contact { DiscoveryId = "id-amy", Nickname = "amy2", EncryptionPublicKey = "e", SigningPublicKey = "s", ConfirmedAt = DateTime.UtcNow });
            new ContactAliasStore(db).Set("al", "id-zed");
        }

        using var db2 = ClientDatabase.Open(dir, Key(3));
        Assert.Equal(["zed", "bob", "amy2"], new ContactStore(db2).All.Select(c => c.Nickname));
        Assert.Equal("id-zed", new ContactAliasStore(db2).Resolve("al"));
    }

    [Fact]
    public void Reactions_oneEmojiPerPerson_latestWins_emptyTakesItBack_andAnyOrderOfArrival()
    {
        using var db = ClientDatabase.Open(Dir("react"), null);
        var h = new HistoryStore(db);

        Assert.True(h.SetReaction("c", "x:1", "bob", "👍", 10));               // may arrive before the message itself
        Assert.False(h.SetReaction("c", "x:1", "bob", "❤️", 5));               // older than what is stored: ignored
        Assert.True(h.SetReaction("c", "x:1", "alice", "😂", 12));
        Assert.True(h.SetReaction("c", "x:1", "bob", "❤️", 20));              // replaces bob's 👍

        Assert.Equal([("alice", "😂"), ("bob", "❤️")], h.Reactions("c")["x:1"]);

        Assert.True(h.SetReaction("c", "x:1", "alice", "", 30));              // taken back
        Assert.Equal([("bob", "❤️")], h.Reactions("c")["x:1"]);

        h.DeleteConversation("c");
        Assert.Empty(h.Reactions("c"));
    }

    [Fact]
    public void ReplayedMessage_afterDeletion_doesNotComeBack()
    {
        using var db = ClientDatabase.Open(Dir("replay"), null);
        var h = new HistoryStore(db);

        // Deleted on this device: the same envelope arriving again (a replay, or a second server) is not stored again.
        Assert.True(h.Append("c", "bob:1", 1, MessageDirection.Received, "private"));
        Assert.True(h.DeleteForMe("c", "bob:1"));
        Assert.False(h.Append("c", "bob:1", 1, MessageDirection.Received, "private"));
        Assert.DoesNotContain(h.Get("c"), e => e.Text == "private");

        // Deleted for everyone by its author: the replay does not restore the text either.
        Assert.True(h.Append("c", "bob:2", 2, MessageDirection.Received, "oops"));
        Assert.True(h.Retract("c", "bob:2"));
        Assert.False(h.Append("c", "bob:2", 2, MessageDirection.Received, "oops"));
        Assert.DoesNotContain(h.Get("c"), e => e.Text == "oops");

        // An old edit replayed after a newer one does not roll the text back.
        Assert.True(h.Append("c", "bob:3", 3, MessageDirection.Received, "v0"));
        Assert.True(h.Edit("c", "bob:3", "v2", 200));
        Assert.False(h.Edit("c", "bob:3", "v1", 100));
        Assert.Equal("v2", h.Get("c").Single(e => e.Id == "bob:3").Text);
    }

    [Fact]
    public void History_OrdersByOriginTimestamp_IgnoresDuplicates()
    {
        using var db = ClientDatabase.Open(Dir("h1"), null);
        var h = new HistoryStore(db);
        h.Append("c", "x:3", 30, MessageDirection.Received, "third");
        h.Append("c", "x:1", 10, MessageDirection.Received, "first");
        h.Append("c", "x:2", 20, MessageDirection.Sent, "second");
        h.Append("c", "x:2", 20, MessageDirection.Sent, "second again");

        Assert.Equal(["first", "second", "third"], h.Get("c").Select(e => e.Text));
        Assert.Equal(3, h.Count("c"));
        Assert.Equal(2, h.IndexOf("c", "x:2"));
        Assert.Equal("third", h.At("c", 3)!.Text);
        Assert.Null(h.At("c", 4));
        Assert.Equal(0, h.IndexOf("c", "nope"));
    }

    [Fact]
    public void History_PagesBackwardsFromTheLatest()
    {
        using var db = ClientDatabase.Open(Dir("h2"), null);
        var h = new HistoryStore(db);
        Fill(h, "c", 25);
        Fill(h, "other", 5);

        var page = h.Latest("c", 10);
        Assert.Equal(Enumerable.Range(16, 10).Select(i => $"m{i}"), page.Entries.Select(e => e.Text));
        Assert.True(page.HasMoreBefore);
        Assert.False(page.HasMoreAfter);

        page = h.Before("c", page.Oldest!.Value, 10);
        Assert.Equal(Enumerable.Range(6, 10).Select(i => $"m{i}"), page.Entries.Select(e => e.Text));
        Assert.True(page.HasMoreBefore);

        page = h.Before("c", page.Oldest!.Value, 10);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"m{i}"), page.Entries.Select(e => e.Text));
        Assert.False(page.HasMoreBefore);
        Assert.Empty(h.Before("c", page.Oldest!.Value, 10).Entries);
    }

    [Fact]
    public void History_PagesForward_AndNewMessagesDoNotShiftTheCursor()
    {
        using var db = ClientDatabase.Open(Dir("h3"), null);
        var h = new HistoryStore(db);
        Fill(h, "c", 12);

        var top = h.Latest("c", 4);              // m9..m12 on screen
        h.Append("c", "a:13", 13, MessageDirection.Received, "m13");   // arrives while scrolling
        var older = h.Before("c", top.Oldest!.Value, 4);
        Assert.Equal(["m5", "m6", "m7", "m8"], older.Entries.Select(e => e.Text));   // unchanged by the new arrival

        var newer = h.After("c", top.Newest!.Value, 10);
        Assert.Equal(["m13"], newer.Entries.Select(e => e.Text));
        Assert.False(newer.HasMoreAfter);
        Assert.True(newer.HasMoreBefore);
    }

    [Fact]
    public void History_Around_CentresOnTheMessage()
    {
        using var db = ClientDatabase.Open(Dir("h4"), null);
        var h = new HistoryStore(db);
        Fill(h, "c", 30);

        var page = h.Around("c", "a:15", 9);
        Assert.Equal(Enumerable.Range(11, 9).Select(i => $"m{i}"), page.Entries.Select(e => e.Text));
        Assert.True(page.HasMoreBefore);
        Assert.True(page.HasMoreAfter);

        var edge = h.Around("c", "a:1", 9);
        Assert.Equal("m1", edge.Entries[0].Text);
        Assert.False(edge.HasMoreBefore);

        Assert.Empty(h.Around("c", "missing", 9).Entries);
    }

    [Fact]
    public void History_Search_IgnoresCaseAndAccents_newestFirst_inEveryConversation()
    {
        using var db = ClientDatabase.Open(Dir("s1"), null);
        var h = new HistoryStore(db);
        h.Append("a", "x:1", 10, MessageDirection.Received, "Rendez-vous à l'ÉTÉ prochain");
        h.Append("b", "y:1", 20, MessageDirection.Sent, "l'ete dernier était chaud");
        h.Append("b", "y:2", 30, MessageDirection.Sent, "rien à voir");
        h.Append("a", "x:2", 40, MessageDirection.Received, "50% de réduction cet Été");

        var hits = h.Search("ete", 10);
        Assert.Equal(["x:2", "y:1", "x:1"], hits.Select(x => x.Entry.Id));
        Assert.Equal(["a", "b", "a"], hits.Select(x => x.Conversation));
        Assert.Equal(["y:1"], h.Search("ete", 10, "b").Select(x => x.Entry.Id));
        Assert.Single(h.Search("été", 1));                          // the limit
        Assert.Empty(h.Search("%", 10).Where(x => !x.Entry.Text.Contains('%')));   // % and _ are plain characters, not wildcards
        Assert.Empty(h.Search("   ", 10));
    }

    [Fact]
    public void History_Search_NeverFindsADeletedMessage_andReadsLongHistoriesInBlocks()
    {
        using var db = ClientDatabase.Open(Dir("s2"), null);
        var h = new HistoryStore(db);
        for (var i = 1; i <= 2500; i++) h.Append("c", $"a:{i}", i, MessageDirection.Received, i % 500 == 0 ? $"needle {i}" : $"hay {i}");
        h.DeleteForMe("c", "a:1000");
        h.Append("c", "a:3000", 3000, MessageDirection.Received, "needle gone");
        h.Retract("c", "a:3000");

        var hits = h.Search("needle", 10);
        Assert.Equal(["a:2500", "a:2000", "a:1500", "a:500"], hits.Select(x => x.Entry.Id));   // 1000 deleted for me, 3000 retracted: no text left
    }

    [Fact]
    public void History_EqualTimestamps_AreOrderedDeterministically()
    {
        using var db = ClientDatabase.Open(Dir("h5"), null);
        var h = new HistoryStore(db);
        h.Append("g", "b:5", 5, MessageDirection.Received, "from b");
        h.Append("g", "a:5", 5, MessageDirection.Received, "from a");

        Assert.Equal(["from a", "from b"], h.Get("g").Select(e => e.Text));
        var page = h.Before("g", new HistoryCursor(5, "b:5"), 10);
        Assert.Equal(["from a"], page.Entries.Select(e => e.Text));
    }

    // ------------------------------------------------------------------
    // Edit / delete / file removal
    // ------------------------------------------------------------------

    [Fact]
    public void Edit_replacesTheText_keepsTheId_marksItEdited_andOnlyTheNewestEditWins()
    {
        using var db = ClientDatabase.Open(Dir("e1"), null);
        var h = new HistoryStore(db);
        h.Append("c", "a:1", 1, MessageDirection.Sent, "helo");

        Assert.True(h.Edit("c", "a:1", "hello", editTs: 10));
        Assert.False(h.Edit("c", "a:1", "older edit", editTs: 5));      // a late, older edit changes nothing
        Assert.True(h.Edit("c", "a:1", "hello world", editTs: 20));

        var e = h.Find("c", "a:1")!;
        Assert.Equal("hello world", e.Text);
        Assert.True(e.Edited);
        Assert.Equal(20, e.EditedTs);
        Assert.Equal(1, h.Count("c"));
    }

    [Fact]
    public void Retract_erasesTheContent_keepsALine_andBeatsAnyLaterEdit()
    {
        using var db = ClientDatabase.Open(Dir("e2"), null);
        var h = new HistoryStore(db);
        h.Append("c", "a:1", 1, MessageDirection.Received, "a secret");
        h.Append("c", "a:2", 2, MessageDirection.Received, "reply", replyToId: "a:1");

        Assert.True(h.Retract("c", "a:1"));
        Assert.False(h.Retract("c", "a:1"));                             // already deleted: no change to announce
        Assert.False(h.Edit("c", "a:1", "resurrected", 99));

        var e = h.Find("c", "a:1")!;
        Assert.Equal(MessageState.Retracted, e.State);
        Assert.Equal("", e.Text);
        Assert.Equal(2, h.Count("c"));                                   // the line stays, so replies to it still resolve
        Assert.Equal(1, h.IndexOf("c", "a:1"));
    }

    [Fact]
    public void DeleteForMe_hidesTheMessage_frees_itsNumber_isNotBackedUp_andDoesNotComeBack()
    {
        using var db = ClientDatabase.Open(Dir("e3"), null);
        var h = new HistoryStore(db);
        for (var i = 1; i <= 3; i++) h.Append("c", $"a:{i}", i, MessageDirection.Received, $"m{i}");

        Assert.True(h.DeleteForMe("c", "a:2"));
        Assert.Equal(["m1", "m3"], h.Get("c").Select(e => e.Text));
        Assert.Equal(2, h.IndexOf("c", "a:3"));                          // numbering closes the gap
        Assert.Null(h.Find("c", "a:2"));
        Assert.Equal(["m1", "m3"], h.Latest("c", 10).Entries.Select(e => e.Text));
        Assert.Equal(2, h.AllEntries["c"].Count);

        h.Append("c", "a:2", 2, MessageDirection.Received, "m2");        // the same message delivered again (group catch-up)
        Assert.Equal(["m1", "m3"], h.Get("c").Select(e => e.Text));
    }

    [Fact]
    public void AChangeThatArrivesBeforeItsMessage_isParkedAndAppliedWhenTheMessageIsStored()
    {
        using var db = ClientDatabase.Open(Dir("e4"), null);
        var h = new HistoryStore(db);

        Assert.False(h.Edit("c", "a:1", "edited first", 50));            // the edit overtook the message
        Assert.False(h.Retract("c", "a:2"));
        Assert.False(h.Edit("c", "a:2", "also edited", 60));             // a deletion beats an edit, whatever the order
        Assert.False(h.FileRetract("c", "a:3"));

        h.Append("c", "a:1", 1, MessageDirection.Received, "original");
        h.Append("c", "a:2", 2, MessageDirection.Received, "secret");
        h.Append("c", "a:3", 3, MessageDirection.Received, "[file] x.png (10 bytes, image/png)");

        Assert.Equal("edited first", h.Find("c", "a:1")!.Text);
        Assert.Equal(MessageState.Retracted, h.Find("c", "a:2")!.State);
        Assert.Equal("", h.Find("c", "a:2")!.Text);
        Assert.Equal(MessageState.FileRetracted, h.Find("c", "a:3")!.State);
        Assert.Equal("", h.Find("c", "a:3")!.Text);
    }

    [Fact]
    public void ReadMarks_countUnreadMessages_andOnlyMoveForward()
    {
        using var db = ClientDatabase.Open(Dir("e5r"), null);
        var h = new HistoryStore(db);
        h.Append("c", "a:1", 1, MessageDirection.Received, "one");
        h.Append("c", "a:2", 2, MessageDirection.Received, "two");
        h.Append("c", "me:3", 3, MessageDirection.Sent, "mine");   // my own messages are never unread
        h.Append("c", "a:4", 4, MessageDirection.Received, "four");
        Assert.Equal(3, h.UnreadCount("c"));
        Assert.Equal("a:4", h.LastReceived("c")!.Id);

        Assert.True(h.AdvanceRead("c", HistoryStore.ReadByMe, new HistoryCursor(2, "a:2")));
        Assert.Equal(1, h.UnreadCount("c"));
        Assert.False(h.AdvanceRead("c", HistoryStore.ReadByMe, new HistoryCursor(1, "a:1")));   // never back
        Assert.Equal(new HistoryCursor(2, "a:2"), h.ReadCursor("c", HistoryStore.ReadByMe));

        h.Append("c", "a:5", 5, MessageDirection.Received, "five");
        Assert.Equal(2, h.UnreadCount("c"));
        Assert.True(h.AdvanceRead("c", HistoryStore.ReadByMe, new HistoryCursor(5, "a:5")));
        Assert.Equal(0, h.UnreadCount("c"));

        Assert.Null(h.ReadCursor("c", HistoryStore.ReadByPeer));   // the two sides are independent
        Assert.True(h.AdvanceRead("c", HistoryStore.ReadByPeer, new HistoryCursor(3, "me:3")));
        Assert.Equal(new HistoryCursor(3, "me:3"), h.ReadCursor("c", HistoryStore.ReadByPeer));
    }

    [Fact]
    public void DeleteConversation_erasesItsMessagesMarksAndParkedChanges_andNothingElse()
    {
        using var db = ClientDatabase.Open(Dir("e5d"), null);
        var h = new HistoryStore(db);
        h.Append("g", "a:1", 1, MessageDirection.Received, "one");
        h.Append("g", "a:2", 2, MessageDirection.Received, "two");
        h.DeleteForMe("g", "a:2");                                         // a hidden row goes too
        h.Edit("g", "a:9", "parked", 5);                                   // an edit waiting for a message not here yet
        h.AdvanceRead("g", HistoryStore.ReadByMe, new HistoryCursor(1, "a:1"));
        h.Append("other", "b:1", 1, MessageDirection.Received, "keep me");

        h.DeleteConversation("g");

        Assert.Empty(h.Get("g"));
        Assert.Null(h.ReadCursor("g", HistoryStore.ReadByMe));
        h.Append("g", "a:9", 9, MessageDirection.Received, "late");        // the parked edit is gone: the message is stored as received
        Assert.Equal("late", h.Find("g", "a:9")!.Text);
        Assert.Equal("keep me", h.Find("other", "b:1")!.Text);
    }

    [Fact]
    public void RemovedContacts_areArchived_andForgottenOnlyWhenAskedTo()
    {
        var dir = Dir("e5c");
        using (var db = ClientDatabase.Open(dir, null))
        {
            var archive = new FreeSpeech.Client.Contacts.ArchivedContactStore(db);
            archive.Add(new FreeSpeech.Client.Contacts.ArchivedContact { DiscoveryId = "c1", Nickname = "Carol" });
            archive.Add(new FreeSpeech.Client.Contacts.ArchivedContact { DiscoveryId = "c1", Nickname = "Carol B" });   // one entry per contact
            Assert.Equal("Carol B", Assert.Single(archive.All).Nickname);
        }
        using (var again = ClientDatabase.Open(dir, null))                                                      // survives a restart
        {
            var archive = new FreeSpeech.Client.Contacts.ArchivedContactStore(again);
            Assert.Equal("Carol B", archive.Find("c1")?.Nickname);
            Assert.True(archive.Remove("c1"));
            Assert.False(archive.Remove("c1"));
            Assert.Null(archive.Find("c1"));
        }
    }

    [Fact]
    public void FileStates_keepTheLine()
    {
        using var db = ClientDatabase.Open(Dir("e5"), null);
        var h = new HistoryStore(db);
        h.Append("c", "a:1", 1, MessageDirection.Received, "[file] a.png (1 bytes, image/png)");
        h.Append("c", "a:2", 2, MessageDirection.Received, "[file] b.png (1 bytes, image/png)");

        Assert.True(h.FileDelete("c", "a:1"));                           // I deleted the file here: the name stays
        Assert.Equal(MessageState.FileDeleted, h.Find("c", "a:1")!.State);
        Assert.Contains("a.png", h.Find("c", "a:1")!.Text);

        Assert.True(h.FileRetract("c", "a:2"));                          // the author removed it for everyone: nothing stays
        Assert.Equal("", h.Find("c", "a:2")!.Text);
        Assert.True(h.Retract("c", "a:1"));                              // a whole-message deletion still applies afterwards
    }

    [Fact]
    public void ChangedMessages_surviveABackupRestore()
    {
        using var db = ClientDatabase.Open(Dir("e6"), null);
        var h = new HistoryStore(db);
        h.Append("c", "a:1", 1, MessageDirection.Sent, "v1", from: "a");
        h.Edit("c", "a:1", "v2", 9);
        h.Append("c", "a:2", 2, MessageDirection.Received, "gone", from: "a");
        h.Retract("c", "a:2");

        using var db2 = ClientDatabase.Open(Dir("e6b"), null);
        var h2 = new HistoryStore(db2);
        foreach (var e in h.AllEntries["c"]) h2.Append("c", e.Id, e.Ts, e.Direction, e.Text, e.ReplyToId, e.From, e.State, e.EditedTs);

        Assert.Equal("v2", h2.Find("c", "a:1")!.Text);
        Assert.True(h2.Find("c", "a:1")!.Edited);
        Assert.Equal(MessageState.Retracted, h2.Find("c", "a:2")!.State);
        Assert.Equal("a", h2.Find("c", "a:1")!.From);
    }

    [Fact]
    public void ADatabaseMadeByTheFirstSchema_isUpgradedOnOpen()
    {
        var dir = Dir("e7");
        using (var first = ClientDatabase.Open(dir, null))
            new HistoryStore(first).Append("c", "a:1", 1, MessageDirection.Received, "old message");

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={ClientDatabase.PathFor(dir)};Pooling=False"))
        {
            raw.Open();
            foreach (var sql in new[] { "ALTER TABLE messages DROP COLUMN state", "ALTER TABLE messages DROP COLUMN edited_ts", "DROP TABLE message_ops", "PRAGMA user_version = 1" })
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        using var db = ClientDatabase.Open(dir, null);
        var h = new HistoryStore(db);
        Assert.Equal("old message", h.Find("c", "a:1")!.Text);
        Assert.True(h.Edit("c", "a:1", "now editable", 5));
        Assert.True(h.Retract("c", "a:1"));
    }
    [Fact]
    public void Append_saysWhetherTheMessageWasNew_soADuplicateIsNotAnnouncedTwice()
    {
        using var db = ClientDatabase.Open(Dir("e8"), null);
        var h = new HistoryStore(db);

        Assert.True(h.Append("c", "a:1", 1, MessageDirection.Received, "hello"));
        Assert.False(h.Append("c", "a:1", 1, MessageDirection.Received, "hello"));        // the same delivery again

        h.DeleteForMe("c", "a:1");
        Assert.False(h.Append("c", "a:1", 1, MessageDirection.Received, "hello"));        // deleted here: it does not come back either
    }

    [Fact]
    public void History_ScalesToManyMessages_WithoutLoadingThemAll()
    {
        using var db = ClientDatabase.Open(Dir("h6"), null);
        var h = new HistoryStore(db);
        db.Transaction(run =>
        {
            for (var i = 1; i <= 20000; i++)
                run("INSERT INTO messages(conv, id, ts, direction, text) VALUES('c', $i, $t, 0, 'x')", [("$i", $"a:{i}"), ("$t", (long)i)]);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = h.Latest("c", 50);
        var older = h.Before("c", page.Oldest!.Value, 50);
        Assert.Equal(50, older.Entries.Count);
        Assert.Equal(20000, h.Count("c"));
        Assert.True(sw.ElapsedMilliseconds < 500, $"paging took {sw.ElapsedMilliseconds} ms");
    }
}
