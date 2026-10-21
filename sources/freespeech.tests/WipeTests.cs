/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.Identity;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>The remote wipe: the revocation key, and an order sent from one device reaching another of the same identity on a real server.</summary>
public class WipeTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public WipeTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private static (string, string, string) Phrases(string tag) => (Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3"));

    private FreeSpeechClient Device(string dir, string name, (string, string, string) p)
    {
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, name), ServerUrl = _server.Http });
        c.CreateIdentity(p.Item1, p.Item2, p.Item3, "owner");
        c.Initialize();
        return c;
    }

    [Fact]
    public void TheRevocationKeyComesFromThePhrasesAndIsNotTheIdentityKey()
    {
        var (p1, p2, p3) = Phrases("wipe key");
        var id = IdentityService.Derive(p1, p2, p3, null);
        var (seed, pub) = IdentityService.DeriveRevocationKey(p1, p2, p3);
        Assert.Equal(pub, id.RevocationPublicKey);                                 // an identity carries only the public half
        Assert.Equal(pub, IdentityService.DeriveRevocationKey(p1, p2, p3).PublicKey);   // same phrases, same key, on any device
        Assert.NotEqual(id.SigningPublicKey, pub);                                 // a thief holding the identity keys cannot sign a wipe
        Assert.NotEqual(id.SigningPrivateKey, seed);
        Assert.NotEqual(pub, IdentityService.DeriveRevocationKey(p2, p1, p3).PublicKey);
    }

    [Fact]
    public void AnOrderErasesADeviceInstalledBeforeItOnly()
    {
        var order = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(FreeSpeechClient.OrderAppliesTo(DateTime.UtcNow.AddDays(-1), order));
        Assert.False(FreeSpeechClient.OrderAppliesTo(DateTime.UtcNow.AddDays(1), order));   // restored after the order: never erased by it
    }

    [Fact]
    public async Task AWipeSentFromOneDeviceErasesTheOlderOneAndNotTheSender()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-wipe-" + Guid.NewGuid().ToString("N"));
        var p = Phrases("wipe one");
        var stolen = Device(dir, "stolen", p);
        await Task.Delay(50);   // the new phone is installed after the old one
        var fresh = Device(dir, "fresh", p);
        await using var _a = stolen; await using var _b = fresh;
        var erased = 0;
        var freshErased = 0;
        stolen.WipeRequested += () => Interlocked.Increment(ref erased);
        fresh.WipeRequested += () => Interlocked.Increment(ref freshErased);
        await stolen.ConnectAsync(); await fresh.ConnectAsync();
        Assert.True(stolen.RemoteWipeReady);

        Assert.True(await fresh.SendWipeAsync(p.Item1, p.Item2, p.Item3), "no other device received the order");

        Assert.True(await UntilAsync(() => Volatile.Read(ref erased) == 1), "the old device did not obey");
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref freshErased));   // the sender never erases itself
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task WrongPhrasesCannotSendAWipe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-wipe-" + Guid.NewGuid().ToString("N"));
        var p = Phrases("wipe two");
        var device = Device(dir, "d", p);
        await using var _d = device;
        var other = Phrases("wipe someone else");
        var ex = await Assert.ThrowsAsync<ClientException>(() => device.SendWipeAsync(other.Item1, other.Item2, other.Item3));
        Assert.Equal("wipe_wrong_phrases", ex.Code);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ADeviceThatCannotCheckTheSignatureNeverObeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-wipe-" + Guid.NewGuid().ToString("N"));
        var p = Phrases("wipe three");
        var old = Device(dir, "old", p);
        await Task.Delay(50);
        var fresh = Device(dir, "fresh", p);
        await using var _a = old; await using var _b = fresh;
        old.Identity.RevocationPublicKey = null;   // an identity from before the revocation key: nothing to verify against
        var erased = 0;
        old.WipeRequested += () => Interlocked.Increment(ref erased);
        await old.ConnectAsync(); await fresh.ConnectAsync();

        await fresh.SendWipeAsync(p.Item1, p.Item2, p.Item3);

        await Task.Delay(2500);
        Assert.Equal(0, Volatile.Read(ref erased));
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
