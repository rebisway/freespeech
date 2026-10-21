/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.Contacts;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>The "is typing" indicator between two real clients on a real server: sent, throttled, reciprocal, never stored.</summary>
public class TypingTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public TypingTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<(FreeSpeechClient A, FreeSpeechClient B, Conversation AtoB, string Dir)> PairAsync(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-typing-" + Guid.NewGuid().ToString("N"));
        var a = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, "a"), ServerUrl = _server.Http });
        var b = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, "b"), ServerUrl = _server.Http });
        a.CreateIdentity(Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3"), "alice");
        b.CreateIdentity(Pad(tag + " b1"), Pad(tag + " b2"), Pad(tag + " b3"), "bob");
        a.Initialize(); b.Initialize();
        await a.ConnectAsync(); await b.ConnectAsync();

        // A QR card confirms by itself: A shows it, B scans it.
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1), "the contact exchange did not finish");
        var conv = new Conversation(b.Identity.DiscoveryId, b.Identity.DiscoveryId, null);
        return (a, b, conv, dir);
    }

    [Fact]
    public async Task ATypingSignalReachesTheContactAndIsNotStored()
    {
        var (a, b, conv, dir) = await PairAsync("typing one");
        await using var _a = a; await using var _b = b;
        var got = new TaskCompletionSource<(Conversation, string)>();
        b.TypingReceived += (c, who) => got.TrySetResult((c, who));

        await a.SendTypingAsync(conv);

        Assert.Same(got.Task, await Task.WhenAny(got.Task, Task.Delay(10000)));
        var (c, who) = await got.Task;
        Assert.Equal(a.Identity.DiscoveryId, who);
        Assert.Equal(a.Identity.DiscoveryId, c.ContactId);
        Assert.Empty(b.GetLatestMessages(c, 10).Entries);   // a throw-away signal: nothing in the history
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task SignalsAreThrottledAndTheSwitchIsReciprocal()
    {
        var (a, b, conv, dir) = await PairAsync("typing two");
        await using var _a = a; await using var _b = b;
        var count = 0;
        b.TypingReceived += (_, _) => Interlocked.Increment(ref count);

        await a.SendTypingAsync(conv);
        await a.SendTypingAsync(conv);   // inside the throttle window: not sent
        await a.SendTypingAsync(conv);
        Assert.True(await UntilAsync(() => Volatile.Read(ref count) >= 1));
        await Task.Delay(1500);
        Assert.Equal(1, Volatile.Read(ref count));

        // B switches the indicator off: it neither sends nor shows.
        b.SendTypingIndicator = false;
        var before = Volatile.Read(ref count);
        await Task.Delay(TimeSpan.FromSeconds(4.2));   // the throttle window of A has passed
        await a.SendTypingAsync(conv);
        await Task.Delay(1500);
        Assert.Equal(before, Volatile.Read(ref count));
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
