/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Client;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Relay;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>The rule that chooses the servers to write to someone, on its own (no network).</summary>
public class RoutePolicyTests
{
    private static ServerEntry S(string url, int priority, bool restricted = false) => new() { Url = url, Priority = priority, Restricted = restricted };

    [Fact]
    public void ACardListsOpenServersFirstThenRestrictedOnes_andRenumbersThePriorities()
    {
        var card = RoutePolicy.CardServers([S("https://private", 0, true), S("https://open1", 1), S("https://private2", 2, true), S("https://open2", 3)], "owner");
        Assert.Equal(["https://open1", "https://open2", "https://private", "https://private2"], card.Select(s => s.Url));
        Assert.Equal([0, 1, 2, 3], card.Select(s => s.Priority));
        Assert.Equal([false, false, true, true], card.Select(s => s.Restricted));
    }

    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(40, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void ARestrictedServerWhoseKeyIsKnownIsWrittenAsAnIdentifier_notAsAnAddress()
    {
        var mine = new ServerEntry { Url = "https://priv.entreprise.local", Priority = 1, Restricted = true, CircleKey = KeyA };
        var card = RoutePolicy.CardServers([S("https://open", 0), mine], "alice");
        var entry = card[1];
        Assert.Equal(CircleKeys.Placeholder, entry.Url);
        Assert.Equal(CircleKeys.Tag(KeyA, "alice"), entry.Circle);
        Assert.DoesNotContain("entreprise", System.Text.Json.JsonSerializer.Serialize(card));
        Assert.Null(card[0].Circle);
    }

    [Fact]
    public void AMemberRecognisesTheServerFromTheIdentifier_andAnOutsiderDoesNot()
    {
        var circle = CircleKeys.Tag(KeyA, "alice");
        var member = new ServerEntry { Url = "https://priv.entreprise.local/", Priority = 0, Restricted = true, CircleKey = KeyA };
        var outsider = new ServerEntry { Url = "https://other.example", Priority = 0, Restricted = true, CircleKey = KeyB };
        Assert.Equal("https://priv.entreprise.local", CircleKeys.Match("alice", circle, [member]));
        Assert.Null(CircleKeys.Match("alice", circle, [outsider]));
        Assert.Null(CircleKeys.Match("bob", circle, [member]));   // the identifier belongs to the owner of the card
        Assert.NotEqual(CircleKeys.Tag(KeyA, "alice"), CircleKeys.Tag(KeyA, "bob"));
    }

    [Fact]
    public void ARenewedKeyKeepsRecognisingTheOldIdentifiersForAWhile()
    {
        var oldCircle = CircleKeys.Tag(KeyA, "alice");
        var server = new ServerEntry { Url = "https://priv", Restricted = true, CircleKey = KeyA };
        Assert.False(CircleKeys.Learn(server, KeyA));
        Assert.True(CircleKeys.Learn(server, KeyB));
        Assert.Equal("https://priv", CircleKeys.Match("alice", oldCircle, [server]));
        Assert.Equal("https://priv", CircleKeys.Match("alice", CircleKeys.Tag(KeyB, "alice"), [server]));
        server.OldCircleKeyUntil = DateTime.UtcNow.AddMinutes(-1);
        Assert.Null(CircleKeys.Match("alice", oldCircle, [server]));
    }

    [Fact]
    public void ARestrictedServerIAmNotLetInOnIsNeverAsked()
    {
        var route = RoutePolicy.Choose([S("https://open", 0), S("https://private", 1, true)], _ => false, false, ["https://mine"], strictApplies: true);
        Assert.Equal(["https://open", "https://mine"], route.Urls);
        Assert.Empty(route.Shared);
        Assert.False(route.Strict);
    }

    [Fact]
    public void WhenARestrictedServerIsShared_onlySharedOnesAreUsed_inTheOrderOfTheCard()
    {
        var route = RoutePolicy.Choose([S("https://open", 0), S("https://pa", 1, true), S("https://pb", 2, true), S("https://other", 3, true)],
            u => u is "https://pa" or "https://pb", false, ["https://mine"], strictApplies: true);
        Assert.Equal(["https://pa", "https://pb"], route.Urls);
        Assert.True(route.Strict);
    }

    [Fact]
    public void WithTheFallbackOn_theSharedOnesComeFirstThenTheOpenOnes()
    {
        var route = RoutePolicy.Choose([S("https://open", 0), S("https://pa", 1, true)], u => u == "https://pa", true, ["https://mine"], strictApplies: true);
        Assert.Equal(["https://pa", "https://open", "https://mine"], route.Urls);
        Assert.False(route.Strict);
    }

    [Fact]
    public void AServerSomeoneDidNotMarkRestrictedButIKnowIsStillShared()
    {
        var route = RoutePolicy.Choose([S("https://open", 0), S("https://x", 1)], u => u == "https://x", false, [], strictApplies: true);
        Assert.Equal(["https://x"], route.Urls);
    }

    [Fact]
    public void AGroupMemberIsNeverRestrictedToTheSharedServers()
    {
        var route = RoutePolicy.Choose([S("https://open", 0), S("https://pa", 1, true)], u => u == "https://pa", false, [], strictApplies: false);
        Assert.Equal(["https://pa", "https://open"], route.Urls);
    }
}

/// <summary>Two real relays, one open and one that restricts who signs up, and members who are on both.</summary>
public class RestrictedRoutingTests : IAsyncLifetime
{
    private const string Secret = "restricted-routing-secret-0123456789";
    private static readonly string Dll = TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-restricted-" + Guid.NewGuid().ToString("N"));
    private readonly List<FreeSpeechClient> _clients = [];
    private TestProcess _open = null!;
    private TestProcess? _private;
    private int _privatePort;
    private Dictionary<string, string> _privateEnv = null!;

    private string OpenUrl => _open.Http;
    private string PrivateUrl => $"http://127.0.0.1:{_privatePort}";

    public async Task InitializeAsync()
    {
        _open = await TestProcess.StartAsync(Dll, Env("open", null), "/api/time");
        _privateEnv = Env("private", "allowlist");
        _privatePort = TestProcess.FreePort();
        _private = await TestProcess.StartAsync(Dll, _privateEnv, "/api/time", _privatePort);
    }

    public Task DisposeAsync()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        _private?.Dispose();
        _open.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Env(string name, string? access)
    {
        var state = Path.Combine(_root, name);
        Directory.CreateDirectory(state);
        var env = new Dictionary<string, string>
        {
            ["Admin__Secret"] = Secret, ["ASPNETCORE_CONTENTROOT"] = state,
            ["Server__AccessFilePath"] = Path.Combine(state, "access.json"), ["Server__SiblingsFilePath"] = Path.Combine(state, "siblings.json"),
            ["Server__LimitsFilePath"] = Path.Combine(state, "limits.json"), ["Server__SettingsFilePath"] = Path.Combine(state, "settings.json"),
        };
        if (access != null) env["Server__ClientAccess"] = access;
        return env;
    }

    private async Task<string> InvitationAsync(string name)
    {
        var (code, output) = await TestProcess.AdminCliAsync(Dll, PrivateUrl, Secret, "invite", "for", name, "--days", "2");
        Assert.True(code == 0, output);
        return JsonDocument.Parse(output).RootElement.GetProperty("token").GetString()!;
    }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    /// <summary>A member who is on the open relay and, with an invitation, on the restricted one; it has learnt that the second one restricts who signs up.</summary>
    private async Task<FreeSpeechClient> MemberAsync(string name)
    {
        var client = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(_root, "c-" + name), ServerUrl = OpenUrl, OpenFallbackDelay = TimeSpan.FromSeconds(1) });
        client.CreateIdentity(Pad(name + " a1"), Pad(name + " a2"), Pad(name + " a3"), name);
        client.Initialize();
        _clients.Add(client);
        await client.ConnectAsync();
        await client.AddServerAsync(PrivateUrl, invitation: await InvitationAsync(name));
        Assert.True(await UntilAsync(() => client.Servers.Any(s => s.Server.Url == PrivateUrl && s.Server.Restricted), 40000), "the restricted server was not recognised");
        return client;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 40000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(150); }
        return condition();
    }

    private static async Task BefriendAsync(FreeSpeechClient a, FreeSpeechClient b)
    {
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1), "the contact exchange did not finish");
    }

    private static string[] Texts(FreeSpeechClient c, string peerId) =>
        c.FindConversation(peerId) is { } conv ? [.. c.GetHistory(conv).Select(l => l.Entry.Text)] : [];

    private async Task RestartPrivateAsync()
    {
        _private = await TestProcess.StartAsync(Dll, _privateEnv, "/api/time", _privatePort);
    }

    [Fact]
    public async Task ACardListsTheOpenServerFirstAndMarksTheRestrictedOne()
    {
        var alice = await MemberAsync("alice");
        var bob = await MemberAsync("bob");
        await BefriendAsync(alice, bob);

        // The address of the restricted server is not in the card: bob knows it only because he is a member of that server himself.
        var servers = bob.Contacts[0].Servers.OrderBy(s => s.Priority).ToList();
        Assert.Equal([OpenUrl, CircleKeys.Placeholder], servers.Select(s => s.Url));
        Assert.Equal([false, true], servers.Select(s => s.Restricted));
        Assert.Null(servers[0].Circle);
        Assert.NotNull(servers[1].Circle);
        Assert.DoesNotContain(PrivateUrl, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(alice.ExportCard("qr"))));
        Assert.Equal(RouteState.PrivateOnly, bob.RouteStateOf(alice.Identity.DiscoveryId));
    }

    [Fact]
    public async Task WhenTheAdministratorRenewsTheKey_theOldCardStillMatchesAndTheContactReceivesTheNewIdentifier()
    {
        var alice = await MemberAsync("alice");
        var bob = await MemberAsync("bob");
        await BefriendAsync(alice, bob);
        var aliceId = alice.Identity.DiscoveryId;
        string? KeyOf(FreeSpeechClient c) => c.Identity.Servers.FirstOrDefault(s => s.Restricted)?.CircleKey;
        var before = KeyOf(alice);
        Assert.NotNull(before);
        Assert.Equal(before, KeyOf(bob));   // every member holds the key of the server

        var (code, output) = await TestProcess.AdminCliAsync(Dll, PrivateUrl, Secret, "circle-key", "renew");
        Assert.True(code == 0, output);
        Assert.True(await UntilAsync(() => KeyOf(alice) != before && KeyOf(bob) != before && KeyOf(alice) == KeyOf(bob)), "the members did not receive the new key");

        // The card bob holds names the server with the old key: still recognised, for a while.
        Assert.Equal(RouteState.PrivateOnly, bob.RouteStateOf(aliceId));
        // Alice sends her card again: bob then holds the identifier made with the new key.
        var fresh = CircleKeys.Tag(KeyOf(alice)!, aliceId);
        Assert.True(await UntilAsync(() => bob.Contacts[0].Servers.Any(s => s.Circle == fresh)), "the contact did not receive the new identifier");
        Assert.Equal(RouteState.PrivateOnly, bob.RouteStateOf(aliceId));
    }

    [Fact]
    public async Task WhenTheRestrictedServerIsDown_theMessageWaitsAndGoesThroughItWhenItIsBack()
    {
        var alice = await MemberAsync("alice");
        var bob = await MemberAsync("bob");
        await BefriendAsync(alice, bob);
        var bobId = bob.Identity.DiscoveryId;

        _private!.Dispose();   // the restricted server goes down, the open one stays up and bob is connected to it
        var sent = await alice.SendAsync(alice.FindConversation(bobId)!, "while it is down");
        Assert.NotEqual(SendStatus.Delivered, sent.Status);
        await Task.Delay(3000);
        await alice.FlushPendingMessagesAsync();
        Assert.DoesNotContain("while it is down", Texts(bob, alice.Identity.DiscoveryId));   // never through the open server by default
        Assert.Equal(RouteState.PrivateDown, alice.RouteStateOf(bobId));

        await RestartPrivateAsync();
        Assert.True(await UntilAsync(() =>
        {
            alice.FlushPendingMessagesAsync().Wait();
            return Texts(bob, alice.Identity.DiscoveryId).Contains("while it is down");
        }, 100000), "the message did not go once the restricted server was back");
        Assert.Equal(RouteState.PrivateOnly, alice.RouteStateOf(bobId));
    }

    [Fact]
    public async Task WithBothSidesAgreeing_anOpenServerCarriesTheMessage_andTheRestrictedOneIsUsedAgainWhenBack()
    {
        var alice = await MemberAsync("alice");
        var bob = await MemberAsync("bob");
        await BefriendAsync(alice, bob);
        var aliceId = alice.Identity.DiscoveryId;
        var bobId = bob.Identity.DiscoveryId;

        await alice.SetOpenFallbackAsync(bobId, OpenFallbackMode.Auto);
        await bob.SetOpenFallbackAsync(aliceId, OpenFallbackMode.Auto);
        Assert.True(await UntilAsync(() => alice.PeerAllowsOpenFallback(bobId) && bob.PeerAllowsOpenFallback(aliceId)), "the policy letters did not arrive");

        _private!.Dispose();
        await alice.SendAsync(alice.FindConversation(bobId)!, "through the open server");
        await Task.Delay(1500);
        Assert.True(await UntilAsync(() =>
        {
            alice.FlushPendingMessagesAsync().Wait();
            return Texts(bob, aliceId).Contains("through the open server");
        }, 30000), "the open server did not carry the message");
        Assert.Equal(RouteState.OpenFallback, alice.RouteStateOf(bobId));

        await RestartPrivateAsync();
        var n = 0;
        Assert.True(await UntilAsync(() =>
        {
            alice.SendAsync(alice.FindConversation(bobId)!, "back " + ++n).Wait();
            Task.Delay(3000).Wait();
            return alice.RouteStateOf(bobId) == RouteState.PrivateOnly;
        }, 120000), "the restricted server was not used again");
    }

    [Fact]
    public async Task InAskMode_theAppAsksOnceAtOnce_andThenUsesTheOpenServerUntilTheRestrictedOneIsBack()
    {
        var alice = await MemberAsync("alice");
        var bob = await MemberAsync("bob");
        await BefriendAsync(alice, bob);
        var aliceId = alice.Identity.DiscoveryId;
        var bobId = bob.Identity.DiscoveryId;

        var questions = 0;
        var proposed = new TaskCompletionSource<string>();
        alice.OpenFallbackProposed += c => { Interlocked.Increment(ref questions); proposed.TrySetResult(c.DiscoveryId); };
        await bob.SetOpenFallbackAsync(aliceId, OpenFallbackMode.Auto);
        await alice.SetOpenFallbackAsync(bobId, OpenFallbackMode.Ask);
        Assert.True(await UntilAsync(() => alice.PeerAllowsOpenFallback(bobId)), "the policy letter did not arrive");

        _private!.Dispose();
        await alice.SendAsync(alice.FindConversation(bobId)!, "after the yes");
        // The question is the delay: it comes with the first failure, not after minutes.
        Assert.Same(proposed.Task, await Task.WhenAny(proposed.Task, Task.Delay(10000)));
        Assert.Equal(bobId, await proposed.Task);
        Assert.DoesNotContain("after the yes", Texts(bob, aliceId));   // nothing went before the user said yes

        alice.ApproveOpenFallback(bobId);
        Assert.True(await UntilAsync(() => Texts(bob, aliceId).Contains("after the yes"), 30000), "the message did not go after the yes");

        // No new question for the next messages: the yes holds while the restricted server is down.
        await alice.SendAsync(alice.FindConversation(bobId)!, "second");
        await alice.SendAsync(alice.FindConversation(bobId)!, "third");
        Assert.True(await UntilAsync(() => Texts(bob, aliceId).Contains("third"), 30000), "the next messages did not go through the open server");
        Assert.Equal(1, Volatile.Read(ref questions));
        Assert.Equal(RouteState.OpenFallback, alice.RouteStateOf(bobId));

        // The restricted server is back: it is used again and the yes is forgotten (a new outage asks again).
        await RestartPrivateAsync();
        var n = 0;
        Assert.True(await UntilAsync(() =>
        {
            alice.SendAsync(alice.FindConversation(bobId)!, "back " + ++n).Wait();
            Task.Delay(3000).Wait();
            return alice.RouteStateOf(bobId) == RouteState.PrivateOnly;
        }, 120000), "the restricted server was not used again");
    }
}
