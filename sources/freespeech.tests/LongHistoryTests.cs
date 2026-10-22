/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Backup;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>A long history is read, saved and compared in blocks: nothing needs the whole of it in memory.</summary>
public class LongHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-long-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private HistoryStore NewHistory(string name, int messages)
    {
        var history = new HistoryStore(Path.Combine(_root, name));
        for (var i = 1; i <= messages; i++) history.Append("bob", $"bob:{i}", i, i % 2 == 0 ? MessageDirection.Sent : MessageDirection.Received, $"line {i}");
        return history;
    }

    [Fact]
    public void Stream_walksTheWholeConversationInOrder_acrossBlocks()
    {
        var history = NewHistory("a", 1234);
        var lines = history.Stream("bob", block: 100).ToList();
        Assert.Equal(1234, lines.Count);
        Assert.Equal(Enumerable.Range(1, 1234).Select(i => $"line {i}"), lines.Select(l => l.Text));
    }

    [Fact]
    public void Reactions_canBeReadForSomeMessagesOnly()
    {
        var history = NewHistory("b", 450);
        history.SetReaction("bob", "bob:3", "me", "👍", 10);
        history.SetReaction("bob", "bob:400", "me", "❤️", 11);
        history.SetReaction("bob", "bob:401", "bob", "😂", 12);

        var some = history.Reactions("bob", ["bob:3", "bob:401"]);
        Assert.Equal(2, some.Count);
        Assert.Equal("👍", some["bob:3"].Single().Emoji);
        Assert.DoesNotContain("bob:400", some.Keys);

        var many = history.Reactions("bob", Enumerable.Range(1, 450).Select(i => $"bob:{i}").ToList());   // more ids than one block
        Assert.Equal(3, many.Count);
    }

    [Fact]
    public void Digests_doNotDependOnHowTheHistoryIsRead()
    {
        var one = NewHistory("c1", 700);
        var two = NewHistory("c2", 700);
        Assert.Equal(one.Digests(), two.Digests());
        two.Edit("bob", "bob:5", "changed", 99);
        Assert.NotEqual(one.Digests()["bob"], two.Digests()["bob"]);
    }

    [Fact]
    public void ARestoredConversation_keepsItsReadMarks_andIsNotUnread()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        BackupService Service(string name, HistoryStore history)
        {
            var dir = Path.Combine(_root, name);
            return new BackupService(key, new ContactStore(dir), new ContactAliasStore(dir), history, new AttachmentStore(dir));
        }

        var source = NewHistory("rm-src", 10);   // odd ids are received
        source.AdvanceRead("bob", HistoryStore.ReadByMe, new HistoryCursor(7, "bob:7"));
        var file = Path.Combine(_root, "marks.enc");
        Service("rm-src", source).Create(file, AttachmentBackupMode.None);

        var target = new HistoryStore(Path.Combine(_root, "rm-dst"));
        Service("rm-dst", target).Restore(file);
        Assert.Equal(source.UnreadCount("bob"), target.UnreadCount("bob"));
        Assert.Equal(1, target.UnreadCount("bob"));   // bob:9 was never read
    }

    [Fact]
    public void ABackupWithoutReadMarks_restoresEveryConversationAsRead()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.Combine(_root, "legacy");
        var history = NewHistory("legacy", 6);
        var payload = new BackupPayload { History = { ["bob"] = history.Stream("bob", 100).ToList() } };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var file = Path.Combine(_root, "legacy.enc");
        File.WriteAllText(file, BackupCrypto.Encrypt(json, key));

        var target = new HistoryStore(Path.Combine(_root, "legacy-dst"));
        var dst = Path.Combine(_root, "legacy-dst");
        new BackupService(key, new ContactStore(dst), new ContactAliasStore(dst), target, new AttachmentStore(dst)).Restore(file);
        Assert.Equal(0, target.UnreadCount("bob"));
    }

    [Fact]
    public void ABackupOfALongHistory_isWrittenInBlocks_andRestoredWhole()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        BackupService Service(string name, HistoryStore history)
        {
            var dir = Path.Combine(_root, name);
            return new BackupService(key, new ContactStore(dir), new ContactAliasStore(dir), history, new AttachmentStore(dir));
        }

        var source = NewHistory("src", 2600);   // more than two blocks of a backup
        var file = Path.Combine(_root, "long.enc");
        var summary = Service("src", source).Create(file, AttachmentBackupMode.None);
        Assert.Equal(2600, summary.Messages);

        var target = new HistoryStore(Path.Combine(_root, "dst"));
        var restored = Service("dst", target).Restore(file);
        Assert.NotNull(restored);
        Assert.Equal(2600, restored!.Messages);
        Assert.Equal(2600, target.Count("bob"));
        Assert.Equal("line 2600", target.Latest("bob", 1).Entries.Single().Text);
    }
}
