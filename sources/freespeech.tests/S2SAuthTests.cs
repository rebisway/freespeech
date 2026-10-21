/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Identity;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>A relay that already has one accepted sibling, whose key the test holds (so it can sign calls as that sibling).</summary>
public class SiblingFixture : ServerFixture
{
    public static readonly PeerIdentity Sibling = PeerIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "fs-sibling-" + Guid.NewGuid().ToString("N") + ".key"));
    public const string SiblingUrl = "https://sibling.example.test";

    protected override void Configure(ProcessStartInfo info) => AcceptedPeers.Add(("relay", SiblingUrl, Sibling));
}

/// <summary>Two relays linked the way administrators link them: one asks, the other accepts, the first learns it.</summary>
public static class RelayLink
{
    private static readonly HttpClient Http = new();

    public static async Task<HttpResponseMessage> AdminAsync(ServerFixture s, HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, s.Http + path);
        req.Headers.Add("X-Admin-Secret", "admin-test-secret");
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await Http.SendAsync(req);
    }

    /// <summary>Makes both relays public and links them (they must have been started with Server__AllowInsecureSiblings: addresses are plain http on 127.0.0.1).</summary>
    public static async Task LinkAsync(ServerFixture a, ServerFixture b)
    {
        foreach (var s in new[] { a, b }) Assert.True((await AdminAsync(s, HttpMethod.Post, "/api/admin/status", new { is_public = true })).IsSuccessStatusCode);
        var add = await AdminAsync(a, HttpMethod.Post, "/api/admin/peers", new { kind = "relay", url = b.Http });
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());
        var accept = await AdminAsync(b, HttpMethod.Post, "/api/admin/peers/accept", new { kind = "relay", all = true });
        Assert.True(accept.IsSuccessStatusCode, await accept.Content.ReadAsStringAsync());
        Assert.True((await AdminAsync(a, HttpMethod.Post, "/api/admin/peers/refresh", new { })).IsSuccessStatusCode);
    }
}

/// <summary>
/// Server-to-server calls (PROTOCOL §10) against the real relay: only a relay that both administrators accepted can call, with a
/// signature of its key; replay, stale time, tampering and oversized bodies are turned away with 404.
/// </summary>
public class S2SAuthTests : IClassFixture<SiblingFixture>
{
    private readonly SiblingFixture _s;
    private readonly HttpClient _http = new();

    public S2SAuthTests(SiblingFixture s) => _s = s;

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null) => RelayLink.AdminAsync(_s, method, path, body);

    private async Task PublicAsync() => Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/status", new { is_public = true })).IsSuccessStatusCode);

    private const string LookupBody = "{\"discovery_id\":\"abc\"}";

    /// <summary>A signed POST to the relay; the parameters let a test spoil one thing at a time.</summary>
    private static HttpRequestMessage Build(string url, string path, string json, PeerIdentity? who, byte[]? signedBody = null, long? ts = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var req = new HttpRequestMessage(HttpMethod.Post, url + path) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (who != null) PeerSigning.Sign(req, who, path, signedBody ?? body);
        if (ts != null) { req.Headers.Remove(PeerSigning.TsHeader); req.Headers.Add(PeerSigning.TsHeader, ts.Value.ToString()); }
        return req;
    }

    private Task<HttpResponseMessage> PostAsync(string path, string json, PeerIdentity? who, byte[]? signedBody = null, long? ts = null) =>
        _http.SendAsync(Build(_s.Http, path, json, who, signedBody, ts));

    [Fact]
    public async Task SignedRequest_isAccepted()
    {
        await PublicAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/s2s/lookup", LookupBody, SiblingFixture.Sibling)).StatusCode);
    }

    [Fact]
    public async Task Unsigned_andUnknownKey_areRefusedWith404()
    {
        await PublicAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", LookupBody, null)).StatusCode);
        var stranger = PeerIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "fs-stranger-" + Guid.NewGuid().ToString("N") + ".key"));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", LookupBody, stranger)).StatusCode);
    }

    [Fact]
    public async Task ReplayedRequest_isRefused()
    {
        await PublicAsync();
        var first = Build(_s.Http, "/s2s/lookup", LookupBody, SiblingFixture.Sibling);
        var replay = Build(_s.Http, "/s2s/lookup", LookupBody, null);
        foreach (var h in new[] { PeerSigning.IdHeader, PeerSigning.TsHeader, PeerSigning.SigHeader }) replay.Headers.Add(h, first.Headers.GetValues(h).First());
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(replay)).StatusCode);
    }

    [Fact]
    public async Task StaleTimestamp_isRefused()
    {
        await PublicAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", LookupBody, SiblingFixture.Sibling, ts: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600)).StatusCode);
    }

    [Fact]
    public async Task TamperedBody_isRefused()
    {
        await PublicAsync();
        // Signed for one body, sent with another.
        var res = await PostAsync("/s2s/lookup", "{\"discovery_id\":\"other\"}", SiblingFixture.Sibling, signedBody: Encoding.UTF8.GetBytes(LookupBody));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_isRefused()
    {
        await PublicAsync();
        var big = "{\"discovery_id\":\"" + new string('a', 200_000) + "\"}";   // the test server's cap is 64 KiB + 16 KiB
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", big, SiblingFixture.Sibling)).StatusCode);
    }

    [Fact]
    public async Task PrivateServer_answers404EvenToAValidSignature()
    {
        await PublicAsync();
        Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/status", new { is_public = false })).IsSuccessStatusCode);
        try { Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", LookupBody, SiblingFixture.Sibling)).StatusCode); }
        finally { await PublicAsync(); }
    }

    [Fact]
    public async Task ABannedAddress_isRefused_untilUnbanned()
    {
        await PublicAsync();
        Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/blacklist", new { url = SiblingFixture.SiblingUrl, reason = "test" })).IsSuccessStatusCode);
        try { Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/s2s/lookup", LookupBody, SiblingFixture.Sibling)).StatusCode); }
        finally { await AdminAsync(HttpMethod.Delete, $"/api/admin/blacklist?url={Uri.EscapeDataString(SiblingFixture.SiblingUrl)}"); }
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/s2s/lookup", LookupBody, SiblingFixture.Sibling)).StatusCode);
    }
}

public class PairFixtureA : ServerFixture
{
    protected override void Configure(ProcessStartInfo info) => info.Environment["Server__AllowInsecureSiblings"] = "true";
}

public class PairFixtureB : ServerFixture
{
    protected override void Configure(ProcessStartInfo info) => info.Environment["Server__AllowInsecureSiblings"] = "true";
}

/// <summary>Two real servers, linked to each other: a flag set on A reaches B through a signed S2S call.</summary>
public class S2SPairTests : IClassFixture<PairFixtureA>, IClassFixture<PairFixtureB>
{
    private readonly PairFixtureA _a;
    private readonly PairFixtureB _b;
    private readonly HttpClient _http = new();

    public S2SPairTests(PairFixtureA a, PairFixtureB b) { _a = a; _b = b; }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
    private static UserIdentity Identity(string who) => IdentityService.Derive(Pad($"{who} one"), Pad($"{who} two"), Pad($"{who} three"), who);

    [Fact]
    public async Task FlagSetOnA_reachesB_throughSignedS2S()
    {
        await RelayLink.LinkAsync(_a, _b);

        var alice = Identity("pair-alice");
        var bob = Identity("pair-bob");
        using var ca = await RawClient.ConnectAsync(_a.WsUri);
        using var cb = await RawClient.ConnectAsync(_b.WsUri);
        var regA = await ca.RegisterAsync(alice);
        var regB = await cb.RegisterAsync(bob);
        Assert.Equal("registered", regA?.GetProperty("type").GetString());
        Assert.Equal("registered", regB?.GetProperty("type").GetString());

        var postReq = new HttpRequestMessage(HttpMethod.Post, _a.Http + "/api/flags") { Content = new StringContent(
            JsonSerializer.Serialize(new { from = alice.DiscoveryId, to = bob.DiscoveryId, state = "WAITING" }), Encoding.UTF8, "application/json") };
        postReq.Headers.Add("X-Discovery-Id", alice.DiscoveryId);
        postReq.Headers.Add("X-Flag-Token", regA!.Value.GetProperty("http_token").GetString());
        var post = await _http.SendAsync(postReq);
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());

        for (var i = 0; i < 20; i++)
        {
            var getReq = new HttpRequestMessage(HttpMethod.Get, $"{_b.Http}/api/flags/{bob.DiscoveryId}");
            getReq.Headers.Add("X-Discovery-Id", bob.DiscoveryId);
            getReq.Headers.Add("X-Flag-Token", regB!.Value.GetProperty("http_token").GetString());
            var body = await (await _http.SendAsync(getReq)).Content.ReadAsStringAsync();
            if (body.Contains(alice.DiscoveryId)) return;
            await Task.Delay(250);
        }
        Assert.Fail("The flag did not reach server B through S2S.");
    }
}

public class RedirectFixtureA : ServerFixture
{
    protected override void Configure(ProcessStartInfo info)
    {
        info.Environment["Server__AllowInsecureSiblings"] = "true";
        info.Environment["Server__MaxConnections"] = "1";   // one client fits, the second is over capacity
    }
}

public class RedirectFixtureB : ServerFixture
{
    protected override void Configure(ProcessStartInfo info) => info.Environment["Server__AllowInsecureSiblings"] = "true";
}

/// <summary>Moving a client (PROTOCOL §10): a full server suggests a sibling; the real client moves, keeps its identity and tells its contacts.</summary>
public class ServerRedirectTests : IClassFixture<RedirectFixtureA>, IClassFixture<RedirectFixtureB>
{
    private readonly RedirectFixtureA _a;
    private readonly RedirectFixtureB _b;

    public ServerRedirectTests(RedirectFixtureA a, RedirectFixtureB b) { _a = a; _b = b; }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    [Fact]
    public async Task FullServer_suggestsSibling_wireLevel()
    {
        await RelayLink.LinkAsync(_a, _b);
        using var filler = await RawClient.ConnectAsync(_a.WsUri);
        Assert.Equal("registered", (await filler.RegisterAsync(IdentityService.Derive(Pad("fill one"), Pad("fill two"), Pad("fill three"), "filler")))?.GetProperty("type").GetString());

        using var second = await RawClient.ConnectAsync(_a.WsUri);
        Assert.Equal("registered", (await second.RegisterAsync(IdentityService.Derive(Pad("wire one"), Pad("wire two"), Pad("wire three"), "wire")))?.GetProperty("type").GetString());
        var redirect = await second.ReceiveAsync();
        Assert.Equal("server_redirect", redirect?.GetProperty("type").GetString());
        Assert.Equal(_b.Http, redirect!.Value.GetProperty("servers")[0].GetString());
    }

    [Fact]
    public async Task Client_movesToSibling_andRemembersIt()
    {
        await RelayLink.LinkAsync(_a, _b);
        var dir = Path.Combine(Path.GetTempPath(), "fs-redirect-" + Guid.NewGuid().ToString("N"));
        await using var client = new FreeSpeech.Client.FreeSpeechClient(new FreeSpeech.Client.ClientOptions { ProfileDir = dir, ServerUrl = _a.Http });
        var moved = new TaskCompletionSource<(string, string)>();
        client.ServerMoved += (from, to) => moved.TrySetResult((from, to));
        client.CreateIdentity(Pad("move one"), Pad("move two"), Pad("move three"), "mover");
        client.Initialize();

        // Server A already holds one client, so the mover is over capacity there.
        using var filler = await RawClient.ConnectAsync(_a.WsUri);
        Assert.Equal("registered", (await filler.RegisterAsync(IdentityService.Derive(Pad("fill b one"), Pad("fill b two"), Pad("fill b three"), "fillerb")))?.GetProperty("type").GetString());

        await client.ConnectAsync();
        var done = await Task.WhenAny(moved.Task, Task.Delay(15000));
        Assert.Same(moved.Task, done);
        var (from, to) = await moved.Task;
        Assert.Equal(_a.Http, from);
        Assert.Equal(_b.Http, to);
        Assert.Contains(client.Identity.Servers, s => s.Url == _b.Http);
        Assert.DoesNotContain(client.Identity.Servers, s => s.Url == _a.Http);
        Assert.Contains(_a.Http, client.Identity.RetiredServers);
        Assert.True(client.Servers.Single(s => s.Server.Url == _b.Http).Connected);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
