/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.History;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>
/// The relay calls a letter delivered once it wrote it to the contact's connection, which may be dead. The contact confirms the letters it keeps; a delivered letter stays in
/// the sender's outbox until then and is sent again if it is not confirmed. Real server, real clients.
/// </summary>
public class AckTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public AckTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (await condition()) return true; await Task.Delay(100); }
        return await condition();
    }

    private static readonly TimeSpan[] Quick = [TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150)];

    private async Task<FreeSpeechClient> ClientAsync(string nickname, bool sendAcks = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-ack-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = _server.Http, AckBackoff = Quick, SendAcks = sendAcks });
        c.CreateIdentity(Pad(nickname + " p1"), Pad(nickname + " p2"), Pad(nickname + " p3"), nickname);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    private async Task<(FreeSpeechClient A, FreeSpeechClient B)> PairAsync(bool bAcks = true)
    {
        var a = await ClientAsync("alice");
        var b = await ClientAsync("bob", bAcks);
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1));
        return (a, b);
    }

    private static string[] Texts(FreeSpeechClient c, string from) => [.. c.GetHistory(c.FindConversation(from)!).Select(l => l.Entry.Text)];

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AReadReceipt_countsAsAConfirmation()
    {
        var (a, b) = await PairAsync(bAcks: false);   // bob never confirms by himself
        await a.SendAsync(a.FindConversation(b.Identity.DiscoveryId)!, "hello");
        Assert.True(await UntilAsync(() => Texts(b, a.Identity.DiscoveryId).Length == 1));

        b.SendReadReceipts = true;
        await b.MarkReadAsync(b.FindConversation(a.Identity.DiscoveryId)!);
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0), "the read receipt did not clear the outbox");
    }

    [Fact]
    public async Task ADeliveredLetter_staysInTheOutboxUntilTheContactConfirmsIt()
    {
        var (a, b) = await PairAsync();
        await a.SendAsync(a.FindConversation(b.Identity.DiscoveryId)!, "hello");
        Assert.True(await UntilAsync(() => Texts(b, a.Identity.DiscoveryId).Length == 1));
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0), "the confirmation did not reach the sender");
    }

    [Fact]
    public async Task AContactThatNeverConfirms_isSentTheLetterAgain_whichIsStoredOnce_thenLeftAlone()
    {
        var (a, b) = await PairAsync(bAcks: false);   // like a version that does not know confirmations
        await a.SendAsync(a.FindConversation(b.Identity.DiscoveryId)!, "hello");
        Assert.Equal(1, a.Outbox.Count);
        Assert.NotNull(a.Outbox[0].SentAt);

        // Every flush after the wait sends it again; once every resend is used and the last wait is over, it is dropped.
        for (var i = 0; i < 12 && a.Outbox.Count > 0; i++)
        {
            await Task.Delay(200);
            await a.FlushPendingMessagesAsync();
        }
        Assert.Empty(a.Outbox);
        Assert.Equal(["hello"], Texts(b, a.Identity.DiscoveryId));
    }

    [Fact]
    public async Task ALetterLostOnADeadConnection_isSentAgain()
    {
        var (a, b) = await PairAsync();
        var bId = b.Identity.DiscoveryId;
        var aId = a.Identity.DiscoveryId;

        b.DropIncoming = true;   // the relay writes to a connection that nobody reads any more
        await a.SendAsync(a.FindConversation(bId)!, "one");
        Assert.Equal(1, a.Outbox.Count);
        await Task.Delay(500);
        Assert.Empty(b.GetHistory(b.FindConversation(aId)!));

        b.DropIncoming = false;
        Assert.True(await UntilAsync(async () => { await a.FlushPendingMessagesAsync(); return Texts(b, aId).Length == 1; }), "the letter was not sent again");
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0), "the confirmation did not reach the sender");
    }

    [Fact]
    public async Task ADeletionLostOnADeadConnection_isSentAgain_likeAMessage()
    {
        var (a, b) = await PairAsync();
        var (aId, bId) = (a.Identity.DiscoveryId, b.Identity.DiscoveryId);
        await a.SendAsync(a.FindConversation(bId)!, "gone");
        Assert.True(await UntilAsync(() => Texts(b, aId).Length == 1));
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0));

        b.DropIncoming = true;
        await a.RetractMessageAsync(a.FindConversation(bId)!, 1);
        Assert.Contains(a.Outbox, p => p.Type == "message_retract");
        await Task.Delay(500);
        Assert.Equal("gone", b.GetHistory(b.FindConversation(aId)!).Single().Entry.Text);

        b.DropIncoming = false;
        Assert.True(await UntilAsync(async () => { await a.FlushPendingMessagesAsync(); return b.GetHistory(b.FindConversation(aId)!).Single().Entry.State == MessageState.Retracted; }), "the deletion was not sent again");
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0));
    }

    [Fact]
    public async Task AnEditLostOnADeadConnection_isSentAgain_andAMessageDeletedBeforeItsConfirmationIsNotSentAgain()
    {
        var (a, b) = await PairAsync();
        var (aId, bId) = (a.Identity.DiscoveryId, b.Identity.DiscoveryId);
        var conv = a.FindConversation(bId)!;
        await a.SendAsync(conv, "first");
        await a.SendAsync(conv, "second");
        Assert.True(await UntilAsync(() => Texts(b, aId).Length == 2));
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0));

        b.DropIncoming = true;
        await a.EditMessageAsync(conv, 1, "first (edited)");
        await a.SendAsync(conv, "third");
        await a.RetractMessageAsync(conv, 3);   // deleted before anyone confirmed it: only the deletion remains to be sent
        Assert.DoesNotContain(a.Outbox, p => p.Type == "message" && p.Text == "third");
        await Task.Delay(500);

        b.DropIncoming = false;
        Assert.True(await UntilAsync(async () =>
        {
            await a.FlushPendingMessagesAsync();
            var lines = b.GetHistory(b.FindConversation(aId)!).Select(l => l.Entry).ToList();
            return lines.Any(e => e.Text == "first (edited)");
        }), "the edit was not sent again");
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0));
    }
}
