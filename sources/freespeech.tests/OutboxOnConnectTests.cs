/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>
/// A sender woken by push, after its contact has already come back online, gets no "peer_online": the outbox must go out as soon as it is connected,
/// not at the first background tick (30 s later), because the contact's own wake-up listens for a few seconds only. Real server, real clients.
/// </summary>
public class OutboxOnConnectTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public OutboxOnConnectTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    private FreeSpeechClient Options(string dir, TimeSpan? unconfirmedFlagDelay = null)
    {
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = _server.Http, UnconfirmedFlagDelay = unconfirmedFlagDelay });
        _clients.Add(c);
        return c;
    }

    private async Task<(FreeSpeechClient Client, string Dir)> NewAsync(string nickname, TimeSpan? unconfirmedFlagDelay = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-onconnect-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = Options(dir, unconfirmedFlagDelay);
        c.CreateIdentity(Pad(nickname + " p1"), Pad(nickname + " p2"), Pad(nickname + " p3"), nickname);
        c.Initialize();
        await c.ConnectAsync();
        return (c, dir);
    }

    /// <summary>The app is started again on an existing profile (the key is in the profile's key cache).</summary>
    private async Task<FreeSpeechClient> ReopenAsync(string dir)
    {
        var c = Options(dir);
        Assert.True(c.TryLoadIdentity());
        c.Initialize();
        await c.ConnectAsync();
        return c;
    }

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    /// <summary>
    /// A phone in airplane mode closes nothing: for a while the relay still sees its old connection, writes into it and answers "delivered". The sender then raises
    /// no WAITING flag, because for it the letter is delivered. When the contact comes back on a new connection, nothing tells the sender: the letter waits for its
    /// own backoff (30 s, 2 min, 10 min…). A delivered letter that nobody confirms must raise the flag as well, and go out again the moment the contact is back.
    /// </summary>
    [Fact]
    public async Task ALetterDeliveredToADeadConnection_goesOutAgainAsSoonAsTheContactIsBack()
    {
        var (a, aDir) = await NewAsync("alice", unconfirmedFlagDelay: TimeSpan.FromMilliseconds(400));
        var (b, bDir) = await NewAsync("bob");
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1, 20000));
        var bId = b.Identity.DiscoveryId;
        var aId = a.Identity.DiscoveryId;

        b.DropIncoming = true;   // bob's connection is dead without having closed: the relay writes to it, nobody reads
        await a.SendAsync(a.FindConversation(bId)!, "did you get this?");
        Assert.Single(a.Outbox);
        Assert.NotNull(a.Outbox[0].SentAt);   // the relay said "delivered"
        await Task.Delay(1500);               // nobody confirms: alice raises the flag by herself
        await b.DisposeAsync();               // the phone is gone for good, then comes back on a new connection:

        var bob = await ReopenAsync(bDir);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await UntilAsync(() => bob.FindConversation(aId) is { } c && bob.GetHistory(c).Any(l => l.Entry.Text == "did you get this?"), 10000),
            $"the letter did not arrive within 10 s of bob being back (after {stopwatch.Elapsed.TotalSeconds:0.0} s)");
        Assert.True(await UntilAsync(() => a.Outbox.Count == 0, 10000), "bob's confirmation did not clear alice's outbox");
        Assert.True(await UntilAsync(() => !a.FlaggedForUnconfirmed(bId), 5000), "the flag stayed up after the confirmation");   // taken down: alice is not woken for nothing later
    }

    [Fact]
    public async Task ASenderStartedAfterItsContactIsOnline_sendsItsOutboxAtOnce_notAtTheFirstTick()
    {
        var (a, aDir) = await NewAsync("alice");
        var (b, bDir) = await NewAsync("bob");
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1, 20000));
        var bId = b.Identity.DiscoveryId;
        var aId = a.Identity.DiscoveryId;

        await b.DisposeAsync();                                       // bob's phone is off
        await a.SendAsync(a.FindConversation(bId)!, "are you there?");   // not delivered: it waits in alice's outbox
        Assert.Single(a.Outbox);
        Assert.Null(a.Outbox[0].SentAt);
        await a.DisposeAsync();                                       // alice's app is closed

        var bob = await ReopenAsync(bDir);                            // bob's phone is back (woken by push) and is online
        await Task.Delay(500);
        var alice = await ReopenAsync(aDir);                          // alice is woken in turn: bob was already online, no peer_online for her

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await UntilAsync(() => bob.FindConversation(aId) is { } c && bob.GetHistory(c).Any(l => l.Entry.Text == "are you there?"), 12000),
            $"the letter did not arrive within 12 s of alice being connected (after {stopwatch.Elapsed.TotalSeconds:0.0} s)");
        Assert.True(alice.Outbox.Count <= 1);
    }
}
