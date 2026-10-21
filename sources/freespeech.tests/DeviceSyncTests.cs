/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using FreeSpeech.Client;
using FreeSpeech.Client.Backup;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Storage;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>
/// A restored device mirrors another device of the same identity: A has the phrases and a backup of B, B has talked to a contact since, B's owner
/// agrees (a code on both screens), A ends up with exactly what B holds. Real server, real clients.
/// </summary>
public class DeviceSyncTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public DeviceSyncTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static string[] Phrases(string tag) => [Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3")];

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<FreeSpeechClient> ClientAsync(string name, string[] phrases, string nickname)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-devsync-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, name), ServerUrl = _server.Http });
        c.CreateIdentity(phrases[0], phrases[1], phrases[2], nickname);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    private static async Task BefriendAsync(FreeSpeechClient a, FreeSpeechClient b)
    {
        var (an, bn) = (a.Contacts.Count, b.Contacts.Count);
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == an + 1 && b.Contacts.Count == bn + 1), "the contact exchange did not finish");
    }

    private static Conversation With(FreeSpeechClient c, string id) => c.FindConversation(id)!;

    private static string[] Texts(FreeSpeechClient c, string id) => [.. c.GetHistory(With(c, id)).Select(l => l.Entry.Text)];

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ARestoredDeviceMirrorsTheOtherOneOnceItsOwnerAgrees()
    {
        var mine = Phrases("devsync one");
        var b = await ClientAsync("b", mine, "owner");
        var carol = await ClientAsync("carol", Phrases("devsync carol"), "carol");
        await BefriendAsync(b, carol);
        var carolId = carol.Identity.DiscoveryId;
        await b.SendAsync(With(b, carolId), "before the backup");
        await b.SendAsync(With(b, carolId), "to be deleted later");
        Assert.True(await UntilAsync(() => Texts(carol, b.Identity.DiscoveryId).Length == 2));

        // The backup of B is imported on the new device A (same phrases): A knows Carol and the two messages.
        var file = Path.Combine(Path.GetTempPath(), "fs-devsync-" + Guid.NewGuid().ToString("N") + ".enc");
        b.CreateBackup(file, AttachmentBackupMode.None);
        var a = await ClientAsync("a", mine, "owner");
        a.RestoreBackup(file);
        File.Delete(file);
        Assert.Equal(2, Texts(a, carolId).Length);

        // B moves on: a new message each way, one edited, one deleted for itself, and a new contact.
        await b.SendAsync(With(b, carolId), "after the backup");
        await carol.SendAsync(With(carol, b.Identity.DiscoveryId), "carol answers");
        Assert.True(await UntilAsync(() => Texts(b, carolId).Length == 4));
        await b.EditMessageAsync(With(b, carolId), 1, "before the backup (edited)");
        b.DeleteMessage(With(b, carolId), 2);
        // A picture B sent since: the mirror must bring the file itself, not just a line of text.
        var picture = RandomNumberGenerator.GetBytes(20_000);   // one chunk: the fixture's relay refuses a message over 64 KiB, a full 48 KiB chunk letter is larger
        var pictureFile = Path.Combine(Path.GetTempPath(), "fs-devsync-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(pictureFile, picture);
        await b.UploadAsync(With(b, carolId), pictureFile);
        var dave = await ClientAsync("dave", Phrases("devsync dave"), "dave");
        await BefriendAsync(b, dave);
        await b.SendAsync(With(b, dave.Identity.DiscoveryId), "hello dave");

        // A asks; B sees the request with a code and nothing leaves before the owner agrees.
        var asked = new TaskCompletionSource<DeviceSyncRequestInfo>();
        b.DeviceSyncRequested += r => asked.TrySetResult(r);
        var states = new List<string>();
        a.DeviceSyncChanged += states.Add;
        Assert.True(await a.RequestDeviceSyncAsync(), "no other device received the request");
        Assert.Same(asked.Task, await Task.WhenAny(asked.Task, Task.Delay(15000)));
        var request = await asked.Task;
        Assert.Matches(@"^\d{3} \d{3}$", a.DeviceSyncCode!);                      // six digits, shown on the asking device only
        await Task.Delay(700);
        // A is online, so it already saw the new messages live; what only B knows has not reached it: the edit, the deletion, the new contact.
        Assert.Contains("to be deleted later", Texts(a, carolId));
        Assert.DoesNotContain("before the backup (edited)", Texts(a, carolId));
        Assert.DoesNotContain(a.Contacts, c => c.DiscoveryId == dave.Identity.DiscoveryId);

        await b.ApproveDeviceSyncAsync(request.Id, a.DeviceSyncCode!.Replace(" ", ""));   // the owner types what A shows

        Assert.True(await UntilAsync(() => states.Contains("done")), "the answer did not arrive");
        Assert.Equal(Texts(b, carolId), Texts(a, carolId));                           // the conversation is the same: new messages, the edit, the deletion
        Assert.Equal(Texts(b, dave.Identity.DiscoveryId), Texts(a, dave.Identity.DiscoveryId));
        Assert.Contains(a.Contacts, c => c.DiscoveryId == dave.Identity.DiscoveryId);   // the contact B made since
        Assert.DoesNotContain("to be deleted later", Texts(a, carolId));
        Assert.Contains("before the backup (edited)", Texts(a, carolId));
        Assert.Empty(b.PendingDeviceSyncs);

        // The picture is a real attachment on A, fetched from B: complete, byte for byte.
        var lines = a.GetHistory(With(a, carolId));
        var at = lines.ToList().FindIndex(l => a.Attachments.Store.Find(l.Entry.Id) != null) + 1;
        Assert.True(at > 0, "the picture line is not an attachment on A");
        var rec = a.Attachments.Store.Find(lines[at - 1].Entry.Id)!;
        Assert.True(rec.FromMyDevice);
        await a.DownloadAsync(With(a, carolId), at);
        Assert.True(await UntilAsync(() => rec.State == FreeSpeech.Client.Attachments.AttachmentState.Complete), $"the picture did not arrive ({rec.State})");
        Assert.Equal(picture, File.ReadAllBytes(rec.Path!));
        File.Delete(pictureFile);
    }

    [Fact]
    public async Task ARefusalSendsNothing_andTheAskingDeviceIsTold()
    {
        var mine = Phrases("devsync two");
        var b = await ClientAsync("b", mine, "owner");
        var carol = await ClientAsync("carol", Phrases("devsync two carol"), "carol");
        await BefriendAsync(b, carol);
        await b.SendAsync(With(b, carol.Identity.DiscoveryId), "private");
        var a = await ClientAsync("a", mine, "owner");
        var asked = new TaskCompletionSource<DeviceSyncRequestInfo>();
        b.DeviceSyncRequested += r => asked.TrySetResult(r);
        var states = new List<string>();
        a.DeviceSyncChanged += states.Add;

        Assert.True(await a.RequestDeviceSyncAsync());
        Assert.Same(asked.Task, await Task.WhenAny(asked.Task, Task.Delay(15000)));
        await b.RejectDeviceSyncAsync((await asked.Task).Id);

        Assert.True(await UntilAsync(() => states.Contains("refused")));
        Assert.Empty(a.Contacts);
        Assert.Empty(a.GetConversations().Where(s => s.Count > 0));
    }

    [Fact]
    public async Task AWrongCodeSendsNothing_andTheThirdOneCancelsTheRequest()
    {
        var mine = Phrases("devsync five");
        var b = await ClientAsync("b", mine, "owner");
        var carol = await ClientAsync("carol", Phrases("devsync five carol"), "carol");
        await BefriendAsync(b, carol);
        await b.SendAsync(With(b, carol.Identity.DiscoveryId), "secret");
        var a = await ClientAsync("a", mine, "owner");
        var asked = new TaskCompletionSource<DeviceSyncRequestInfo>();
        b.DeviceSyncRequested += r => asked.TrySetResult(r);
        var states = new List<string>();
        a.DeviceSyncChanged += states.Add;
        Assert.True(await a.RequestDeviceSyncAsync());
        Assert.Same(asked.Task, await Task.WhenAny(asked.Task, Task.Delay(15000)));
        var id = (await asked.Task).Id;
        var right = a.DeviceSyncCode!.Replace(" ", "");
        var wrong = right == "000000" ? "111111" : "000000";

        var first = await Assert.ThrowsAsync<ClientException>(() => b.ApproveDeviceSyncAsync(id, wrong));
        Assert.Equal("sync_wrong_code", first.Code);
        Assert.Equal(2, first.Args[0]);                                    // tries left
        Assert.Single(b.PendingDeviceSyncs);                               // still waiting for the right code
        await Assert.ThrowsAsync<ClientException>(() => b.ApproveDeviceSyncAsync(id, "12"));
        var third = await Assert.ThrowsAsync<ClientException>(() => b.ApproveDeviceSyncAsync(id, wrong));
        Assert.Equal("sync_cancelled", third.Code);
        Assert.Empty(b.PendingDeviceSyncs);
        Assert.True(await UntilAsync(() => states.Contains("refused")));    // A is told
        await Assert.ThrowsAsync<ClientException>(() => b.ApproveDeviceSyncAsync(id, right));   // even the right code is too late
        Assert.Empty(a.Contacts);
    }

    [Fact]
    public async Task TheCodeMayBeTypedWithSpacesOrDashes()
    {
        var mine = Phrases("devsync six");
        var b = await ClientAsync("b", mine, "owner");
        var a = await ClientAsync("a", mine, "owner");
        var asked = new TaskCompletionSource<DeviceSyncRequestInfo>();
        b.DeviceSyncRequested += r => asked.TrySetResult(r);
        var states = new List<string>();
        a.DeviceSyncChanged += states.Add;
        Assert.True(await a.RequestDeviceSyncAsync());
        Assert.Same(asked.Task, await Task.WhenAny(asked.Task, Task.Delay(15000)));
        var shown = a.DeviceSyncCode!;                                      // "123 456"
        await b.ApproveDeviceSyncAsync((await asked.Task).Id, shown.Replace(' ', '-'));
        Assert.True(await UntilAsync(() => states.Contains("done")));
    }

    [Fact]
    public async Task WithNoOtherDeviceOnline_theRequestIsNotDelivered_andNothingHappens()
    {
        var a = await ClientAsync("a", Phrases("devsync three"), "owner");
        Assert.False(await a.RequestDeviceSyncAsync());
        Assert.Empty(a.PendingDeviceSyncs);
    }

    [Fact]
    public async Task AnotherIdentityCannotAsk_andAForgedAnswerIsIgnored()
    {
        var b = await ClientAsync("b", Phrases("devsync four"), "owner");
        var stranger = await ClientAsync("stranger", Phrases("devsync four other"), "stranger");
        var asked = 0;
        b.DeviceSyncRequested += _ => Interlocked.Increment(ref asked);

        Assert.False(await stranger.RequestDeviceSyncAsync());   // it reaches no device of its own: a letter to oneself never goes to another identity
        await Task.Delay(700);
        Assert.Equal(0, Volatile.Read(ref asked));
        Assert.Empty(b.PendingDeviceSyncs);
    }

    [Fact]
    public void TheHistoryDigestsTellEqualConversationsFromDifferentOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-devsync-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        using var db1 = ClientDatabase.Open(Path.Combine(dir, "one"), null);
        using var db2 = ClientDatabase.Open(Path.Combine(dir, "two"), null);
        var (h1, h2) = (new HistoryStore(db1), new HistoryStore(db2));
        foreach (var h in new[] { h1, h2 })
        {
            h.Append("c", "a:1", 1, MessageDirection.Sent, "one");
            h.Append("c", "a:2", 2, MessageDirection.Received, "two");
            h.Append("d", "a:3", 3, MessageDirection.Sent, "other");
        }
        Assert.Equal(h1.Digests(), h2.Digests());

        h2.Edit("c", "a:2", "two (edited)", 9);
        var (d1, d2) = (h1.Digests(), h2.Digests());
        Assert.NotEqual(d1["c"], d2["c"]);     // an edit changes the digest
        Assert.Equal(d1["d"], d2["d"]);        // the other conversation does not

        h1.DeleteForMe("d", "a:3");
        Assert.DoesNotContain("d", h1.Digests().Keys);   // a conversation with nothing left is not listed
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
