/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Identity;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>A real relay and a real push gateway, each with its own state folder, that can be restarted where they were.</summary>
public sealed class PeerPair : IDisposable
{
    public const string RelaySecret = "relay-admin-secret-0123456789";
    public const string PushSecret = "push-admin-secret-0123456789ab";
    public static readonly string RelayDll = TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll");
    public static readonly string PushDll = TestProcess.Dll("freespeech.push", "FreeSpeech.Push.dll");

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fs-peers-" + Guid.NewGuid().ToString("N"));
    public int RelayPort { get; } = TestProcess.FreePort();
    public int PushPort { get; } = TestProcess.FreePort();
    public string RelayUrl => $"http://127.0.0.1:{RelayPort}";
    public string PushUrl => $"http://127.0.0.1:{PushPort}";
    public Uri RelayWs => new($"ws://127.0.0.1:{RelayPort}/ws/relay");
    public TestProcess? Relay { get; private set; }
    public TestProcess? Push { get; private set; }
    private readonly Dictionary<string, string> _relayExtra = [];
    private readonly Dictionary<string, string> _pushExtra = [];

    public PeerPair(Action<Dictionary<string, string>>? relayEnv = null, Action<Dictionary<string, string>>? pushEnv = null)
    {
        Directory.CreateDirectory(Dir);
        relayEnv?.Invoke(_relayExtra);
        pushEnv?.Invoke(_pushExtra);
    }

    public async Task StartAsync()
    {
        await StartPushAsync();
        await StartRelayAsync();
    }

    public async Task StartRelayAsync()
    {
        var env = new Dictionary<string, string>
        {
            ["Server__SiblingsFilePath"] = Path.Combine(Dir, "relay", "siblings.json"), ["Server__AccessFilePath"] = Path.Combine(Dir, "relay", "access.json"),
            ["Admin__Secret"] = RelaySecret, ["Peer__Name"] = "relay-under-test", ["Peer__PublicUrl"] = "http://127.0.0.1:{port}",
        };
        foreach (var (k, v) in _relayExtra) env[k] = v;
        Directory.CreateDirectory(Path.Combine(Dir, "relay"));
        Relay = await TestProcess.StartAsync(RelayDll, env, "/api/time", RelayPort);
    }

    public async Task StartPushAsync()
    {
        var env = new Dictionary<string, string>
        {
            ["Push__Fake"] = "true", ["Push__StatePath"] = Path.Combine(Dir, "push"), ["Admin__Secret"] = PushSecret,
            ["Peer__Name"] = "gateway-under-test", ["Peer__PublicUrl"] = "http://127.0.0.1:{port}",
        };
        foreach (var (k, v) in _pushExtra) env[k] = v;
        Push = await TestProcess.StartAsync(PushDll, env, "/health", PushPort);
    }

    public void StopRelay() { Relay?.Dispose(); Relay = null; }
    public void StopPush() { Push?.Dispose(); Push = null; }

    public Task<(int Code, string Output)> RelayCli(params string[] args) => Cli(RelayDll, RelayUrl, RelaySecret, args);
    public Task<(int Code, string Output)> PushCli(params string[] args) => Cli(PushDll, PushUrl, PushSecret, args);

    /// <summary>Input is redirected and closed at once: a command that wants a confirmation must not wait for a keyboard.</summary>
    public static async Task<(int Code, string Output)> Cli(string dll, string url, string secret, string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("admin");
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(url);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["FS_ADMIN_SECRET"] = secret;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    public static async Task<JsonElement> GetJson(string url, string? secret = null)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (secret != null) request.Headers.Add("X-Admin-Secret", secret);
        var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> RelayFingerprint() => (await GetJson(RelayUrl + "/peer/identity")).GetProperty("fingerprint").GetString()!;
    public async Task<string> PushFingerprint() => (await GetJson(PushUrl + "/peer/identity")).GetProperty("fingerprint").GetString()!;

    public void Dispose()
    {
        StopRelay(); StopPush();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Linking a relay and a push gateway by mutual consent, with no shared secret (PROTOCOL Â§10 "Linking servers"): each asks, the
/// other's administrator accepts, calls are signed, and everything survives a restart because it is saved on disk.
/// </summary>
public class PeerTests
{
    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
    private static UserIdentity Id(string who) => IdentityService.Derive(Pad(who + " one"), Pad(who + " two"), Pad(who + " three"), who);

    [Fact]
    public async Task Identity_isCreatedOnce_isPublicWithoutSecrets_andSurvivesARestart()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();

        var identity = await PeerPair.GetJson(pair.RelayUrl + "/peer/identity");
        Assert.Equal("relay", identity.GetProperty("kind").GetString());
        Assert.Equal("relay-under-test", identity.GetProperty("name").GetString());
        var fingerprint = identity.GetProperty("fingerprint").GetString()!;
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(PeerIdentity.FingerprintOf(identity.GetProperty("public_key").GetString()!), fingerprint);
        Assert.DoesNotContain("private", identity.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(pair.Dir, "relay", "peer-identity.json")));
        Assert.Equal("push", (await PeerPair.GetJson(pair.PushUrl + "/peer/identity")).GetProperty("kind").GetString());

        pair.StopRelay();
        await pair.StartRelayAsync();
        Assert.Equal(fingerprint, await pair.RelayFingerprint());   // the same server after a restart

        var (code, output) = await pair.RelayCli("peer-id");
        Assert.Equal(0, code);
        Assert.Contains(fingerprint, output);
    }

    [Fact]
    public async Task RelayAsksPush_theGatewayAdministratorAccepts_andTheLinkSurvivesRestarts()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var relayId = await pair.RelayFingerprint();
        var pushId = await pair.PushFingerprint();

        var add = await pair.RelayCli("add", "push", pair.PushUrl);
        Assert.Equal(0, add.Code);
        Assert.Contains(pushId, add.Output);                          // the fingerprint is shown, to compare with the gateway's administrator
        Assert.Contains("Request sent", add.Output);
        Assert.Contains("asked", (await pair.RelayCli("list", "push")).Output);

        // The gateway sees the request, with the relay's name and fingerprint â€” and nothing is trusted yet.
        var requests = await pair.PushCli("requests");
        Assert.Contains("relay-under-test", requests.Output);
        Assert.Contains(relayId[..12], requests.Output);
        Assert.Contains("No server linked", (await pair.PushCli("list")).Output);

        var accepted = await pair.PushCli("accept", relayId[..8]);
        Assert.Equal(0, accepted.Code);
        Assert.Contains("Accepted: 1", accepted.Output);

        // The relay learns it (immediately here, by the refresh loop otherwise).
        await pair.RelayCli("peer-refresh");
        Assert.Contains("linked", (await pair.RelayCli("list", "push")).Output);
        Assert.Contains("linked", (await pair.PushCli("list")).Output);

        // Nothing is lost by a restart, whichever server restarts: the approval is on disk.
        pair.StopRelay(); pair.StopPush();
        await pair.StartPushAsync(); await pair.StartRelayAsync();
        Assert.Contains("linked", (await pair.RelayCli("list", "push")).Output);
        Assert.Contains("linked", (await pair.PushCli("list")).Output);
        Assert.Contains("No relay request waiting", (await pair.PushCli("requests")).Output);
        Assert.Equal(relayId, await pair.RelayFingerprint());
    }

    [Fact]
    public async Task TheContactAKindOfServerDeclaresReachesTheOtherAdministrator_andListShowsEveryKind()
    {
        using var pair = new PeerPair(relayEnv: e => e["Peer__Contact"] = "admin@relay.example.org");
        await pair.StartAsync();
        var relayId = await pair.RelayFingerprint();

        Assert.Equal(0, (await pair.RelayCli("add", "push", pair.PushUrl)).Code);
        var requests = (await pair.PushCli("requests")).Output;
        Assert.Contains("contact: admin@relay.example.org", requests);   // a way to check who is asking, next to the name and the fingerprint
        Assert.Contains("seen from: 127.0.0.1", requests);
        Assert.Equal("admin@relay.example.org", (await PeerPair.GetJson(pair.RelayUrl + "/peer/identity", PeerPair.RelaySecret)).GetProperty("contact").GetString());

        Assert.Equal(0, (await pair.PushCli("accept", relayId[..8])).Code);
        var list = (await pair.PushCli("list")).Output;                   // no kind: every server
        Assert.Contains("relay", list);
        Assert.Contains("linked", list);
        Assert.Contains("<admin@relay.example.org>", list);

        // The contact is changed while the relay runs, and signed with the request.
        Assert.Equal(0, (await pair.RelayCli("self", "set", "contact", "tel 555 0100")).Code);
        Assert.Contains("tel 555 0100", (await pair.RelayCli("self")).Output);
        Assert.Equal(0, (await pair.RelayCli("self", "set", "contact", "")).Code);
        Assert.DoesNotContain("tel 555 0100", (await pair.RelayCli("self")).Output);
    }

    [Fact]
    public async Task PushAsksRelay_theRelayAdministratorAccepts()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var pushId = await pair.PushFingerprint();

        Assert.Equal(0, (await pair.PushCli("add", "relay", pair.RelayUrl)).Code);
        var requests = await pair.RelayCli("requests", "push");
        Assert.Contains("gateway-under-test", requests.Output);
        Assert.Contains(pair.PushUrl, requests.Output);
        Assert.Contains(pushId[..12], requests.Output);

        Assert.Equal(0, (await pair.RelayCli("accept", "push", pushId[..10])).Code);
        await pair.PushCli("peer-refresh");
        Assert.Contains("linked", (await pair.PushCli("list")).Output);
        Assert.Contains("linked", (await pair.RelayCli("list", "push")).Output);
    }

    [Fact]
    public async Task WhenBothAskEachOther_theLinkIsAgreedAtOnce()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        Assert.Equal(0, (await pair.RelayCli("add", "push", pair.PushUrl)).Code);
        var second = await pair.PushCli("add", "relay", pair.RelayUrl);   // the gateway had a request waiting from this relay: asking back is accepting
        Assert.Equal(0, second.Code);
        Assert.Contains("already asked", second.Output);
        await pair.RelayCli("peer-refresh");
        Assert.Contains("linked", (await pair.RelayCli("list", "push")).Output);
        Assert.Contains("linked", (await pair.PushCli("list")).Output);
    }

    [Fact]
    public async Task AWakeUp_goesThroughOnlyOnceTheGatewayAgreed_andIsSigned()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var relayId = await pair.RelayFingerprint();
        var (a, b) = (Id("peer-wake-a"), Id("peer-wake-b"));

        // Before any link the relay does not accept this gateway for its users.
        using (var ws = await RawClient.ConnectAsync(pair.RelayWs))
        {
            await ws.RegisterAsync(b);
            await ws.SendAsync(new { type = "push_register", platform = "fcm", token = "tok-before", gateway = pair.PushUrl, request_id = "p" });
            Assert.False((await ws.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean());
        }

        await pair.RelayCli("add", "push", pair.PushUrl);
        await pair.PushCli("accept", "relay", relayId[..8]);
        await pair.RelayCli("peer-refresh");

        using var wsA = await RawClient.ConnectAsync(pair.RelayWs);
        var regA = await wsA.RegisterAsync(a);
        var wsB = await RawClient.ConnectAsync(pair.RelayWs);
        await wsB.RegisterAsync(b);
        await wsB.SendAsync(new { type = "push_register", platform = "fcm", token = "tok-after", gateway = pair.PushUrl, request_id = "p" });
        Assert.True((await wsB.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean());   // now allowed
        wsB.Dispose();
        await Task.Delay(400);

        using var http = new HttpClient();
        var flag = new HttpRequestMessage(HttpMethod.Post, $"{pair.RelayUrl}/api/flags") { Content = new StringContent(JsonSerializer.Serialize(new { from = a.DiscoveryId, to = b.DiscoveryId, state = "WAITING" }), Encoding.UTF8, "application/json") };
        flag.Headers.Add("X-Discovery-Id", a.DiscoveryId);
        flag.Headers.Add("X-Flag-Token", regA!.Value.GetProperty("http_token").GetString());
        Assert.True((await http.SendAsync(flag)).IsSuccessStatusCode);

        // The wake-up reached the gateway with the relay's signature and was served: no secret was ever exchanged.
        var sent = 0L;
        for (var i = 0; i < 20 && sent == 0; i++)
        {
            await Task.Delay(250);
            sent = (await PeerPair.GetJson(pair.PushUrl + "/admin/status", PeerPair.PushSecret)).GetProperty("totals").GetProperty("sent").GetInt64();
        }
        Assert.Equal(1, sent);
    }

    // ------------------------------------------------------------------ the gateway's door, with a relay of our own making

    private static async Task<(HttpStatusCode Status, string? State, string? Error)> Ask(string url, PeerIdentity who, string kind, string name, string reqUrl, long? ts = null, PeerIdentity? signer = null, string? asKey = null)
    {
        var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new PeerRequestBody { Kind = kind, Name = name, Url = reqUrl, PublicKey = asKey ?? who.PublicKey, Ts = t, Sig = (signer ?? who).Sign(PeerSigning.RequestText(kind, name, reqUrl, t)) };
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync(url + "/peer/request", body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, json.TryGetProperty("state", out var s) ? s.GetString() : null, json.TryGetProperty("error", out var e) ? e.GetString() : null);
    }

    private static PeerIdentity NewIdentity(string dir, string name) => PeerIdentity.LoadOrCreate(Path.Combine(dir, name + ".json"));

    private static async Task<HttpResponseMessage> SignedPush(string pushUrl, PeerIdentity who, Action<HttpRequestMessage, byte[]>? tamper = null, long? ts = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { platform = "fcm", token = "tok-" + Guid.NewGuid().ToString("N"), kind = "wake" });
        using var request = new HttpRequestMessage(HttpMethod.Post, pushUrl + "/push") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        PeerSigning.Sign(request, who, "/push", body);
        if (ts != null) { request.Headers.Remove(PeerSigning.TsHeader); request.Headers.Add(PeerSigning.TsHeader, ts.Value.ToString()); }
        tamper?.Invoke(request, body);
        using var http = new HttpClient();
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task SignedCalls_areServedOnlyForAnAcceptedRelay_andRefusedWhenForgedStaleReplayedOrBanned()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var rogue = NewIdentity(pair.Dir, "rogue");
        var other = NewIdentity(pair.Dir, "other");
        // The test gateway is in "fake" mode, which is open until a relay is accepted (development only): from here on it is closed, as in production.
        var bootstrap = NewIdentity(pair.Dir, "bootstrap");
        Assert.Equal("pending", (await Ask(pair.PushUrl, bootstrap, "relay", "bootstrap", "")).State);
        Assert.Equal(0, (await pair.PushCli("accept", bootstrap.Fingerprint[..8])).Code);

        // Unknown, unsigned: refused. A signature of a relay nobody accepted: refused.
        using (var http = new HttpClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(pair.PushUrl + "/push", new { platform = "fcm", token = "t", kind = "wake" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, rogue)).StatusCode);

        // It asks; waiting is not enough.
        var asked = await Ask(pair.PushUrl, rogue, "relay", "rogue relay", "http://127.0.0.1:1");
        Assert.Equal((HttpStatusCode.Accepted, "pending"), (asked.Status, asked.State));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, rogue)).StatusCode);

        // Accepted (its address does not answer: accepted but unverified).
        var accepted = await pair.PushCli("accept", rogue.Fingerprint[..8]);
        Assert.Equal(0, accepted.Code);
        Assert.Contains("not verified", accepted.Output);
        Assert.Equal(HttpStatusCode.OK, (await SignedPush(pair.PushUrl, rogue)).StatusCode);

        // Forged: another key's signature under this fingerprint, a changed body, a stale time, a call played twice.
        var forged = await SignedPush(pair.PushUrl, other, (req, _) => { req.Headers.Remove(PeerSigning.IdHeader); req.Headers.Add(PeerSigning.IdHeader, rogue.Fingerprint); });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var changed = await SignedPush(pair.PushUrl, rogue, (req, _) => req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"platform\":\"fcm\",\"token\":\"another\",\"kind\":\"wake\"}")) { Headers = { ContentType = new("application/json") } });
        Assert.Equal(HttpStatusCode.Unauthorized, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, rogue, ts: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds())).StatusCode);

        var body = JsonSerializer.SerializeToUtf8Bytes(new { platform = "fcm", token = "tok-replay", kind = "wake" });
        HttpRequestMessage Make()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, pair.PushUrl + "/push") { Content = new ByteArrayContent(body) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return r;
        }
        var first = Make();
        PeerSigning.Sign(first, rogue, "/push", body);
        var replay = Make();
        foreach (var h in new[] { PeerSigning.IdHeader, PeerSigning.TsHeader, PeerSigning.SigHeader }) replay.Headers.Add(h, first.Headers.GetValues(h).First());
        using (var http = new HttpClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(first)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(replay)).StatusCode);
        }

        // Banned: refused with 403 at once; unbanned and removed: refused as unknown.
        Assert.Equal(0, (await pair.PushCli("ban", rogue.Fingerprint[..8])).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignedPush(pair.PushUrl, rogue)).StatusCode);
        var again = await Ask(pair.PushUrl, rogue, "relay", "rogue", "");   // a banned relay cannot queue again
        Assert.Equal((HttpStatusCode.Forbidden, "banned"), (again.Status, again.State));
        Assert.Equal(0, (await pair.PushCli("unban", rogue.Fingerprint[..8])).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, rogue)).StatusCode);

    }

    [Fact]
    public async Task ARequest_mustBeSignedByTheKeyItPresents_beRecent_andBeOfAnAcceptedKind()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var a = NewIdentity(pair.Dir, "a");
        var b = NewIdentity(pair.Dir, "b");

        var forged = await Ask(pair.PushUrl, a, "relay", "a", "", signer: b);   // a's key, b's signature
        Assert.Equal((HttpStatusCode.BadRequest, "bad_signature"), (forged.Status, forged.Error));
        var stale = await Ask(pair.PushUrl, a, "relay", "a", "", ts: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        Assert.Equal("bad_time", stale.Error);
        var wrongKind = await Ask(pair.PushUrl, a, "push", "a", "");   // a gateway does not take requests from a gateway
        Assert.Equal("kind_not_accepted", wrongKind.Error);
        var badUrl = await Ask(pair.PushUrl, a, "relay", "a", "ftp://nope");
        Assert.Equal("bad_url", badUrl.Error);
        Assert.Contains("No relay request waiting", (await pair.PushCli("requests")).Output);   // none of these was kept

        var ok = await Ask(pair.PushUrl, a, "relay", "A  \r\n[ERROR] forged line", "");
        Assert.Equal("pending", ok.State);
        var listed = (await PeerPair.GetJson(pair.PushUrl + "/admin/peers/requests", PeerPair.PushSecret)).GetProperty("requests")[0];
        Assert.DoesNotContain(listed.GetProperty("name").GetString()!, c => char.IsControl(c));
    }

    [Fact]
    public async Task Requests_areBounded_oneEntryPerKey_andTheQueueCanBeClosed()
    {
        using var pair = new PeerPair(pushEnv: e => e["Peer__MaxRequests"] = "2");
        await pair.StartAsync();
        var keys = Enumerable.Range(1, 3).Select(i => NewIdentity(pair.Dir, "k" + i)).ToArray();

        Assert.Equal("pending", (await Ask(pair.PushUrl, keys[0], "relay", "first", "")).State);
        Assert.Equal("pending", (await Ask(pair.PushUrl, keys[0], "relay", "first, again", "")).State);     // same key: updated, not doubled
        Assert.Equal("pending", (await Ask(pair.PushUrl, keys[1], "relay", "second", "")).State);
        var full = await Ask(pair.PushUrl, keys[2], "relay", "third", "");
        Assert.Equal((HttpStatusCode.TooManyRequests, "full"), (full.Status, full.State));
        Assert.Contains("2 matching", (await pair.PushCli("requests")).Output);
        Assert.Contains("first, again", (await pair.PushCli("requests")).Output);

        Assert.Equal(0, (await pair.PushCli("reject", "--all", "--yes")).Code);
        Assert.Equal(0, (await pair.PushCli("peer-requests", "close")).Code);
        var closed = await Ask(pair.PushUrl, keys[2], "relay", "third", "");
        Assert.Equal((HttpStatusCode.Forbidden, "closed"), (closed.Status, closed.State));
        Assert.Contains("queue is closed", (await pair.PushCli("requests")).Output);
        Assert.Equal(0, (await pair.PushCli("peer-requests", "open")).Code);
        Assert.Equal("pending", (await Ask(pair.PushUrl, keys[2], "relay", "third", "")).State);
    }

    [Fact]
    public async Task OnAccept_theAddressGivenMustShowTheKeyAsked_orTheRequestIsNotAccepted()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var liar = NewIdentity(pair.Dir, "liar");

        // "I am the relay at <the real relay's address>" â€” but that address shows another key.
        Assert.Equal("pending", (await Ask(pair.PushUrl, liar, "relay", "pretends", pair.RelayUrl)).State);
        var refused = await pair.PushCli("accept", liar.Fingerprint[..8]);
        Assert.Equal(4, refused.Code);
        Assert.Contains("NOT accepted", refused.Output);
        Assert.Contains("1 matching", (await pair.PushCli("requests")).Output);   // still waiting, never trusted
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, liar)).StatusCode);
    }

    [Fact]
    public async Task CommandLine_refusesWhatCannotWork()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();

        Assert.Equal(4, (await pair.RelayCli("add", "push", "http://127.0.0.1:1")).Code);                // unreachable
        Assert.Contains("cannot reach", (await pair.RelayCli("add", "push", "http://127.0.0.1:1")).Output);
        Assert.Contains("https", (await pair.RelayCli("add", "push", "http://push.example.org")).Output);   // plain http only for a private network
        Assert.Contains("not the one you gave", (await pair.RelayCli("add", "push", pair.PushUrl, "--fingerprint", "deadbeef00")).Output);
        Assert.Contains("is private", (await pair.RelayCli("add", "relay", pair.PushUrl)).Output);        // a private relay takes no part in server-to-server traffic
        Assert.Contains("is a push server, not a relay", (await pair.PushCli("add", "relay", pair.PushUrl)).Output);
        var waiting = NewIdentity(pair.Dir, "waiting");
        Assert.Equal("pending", (await Ask(pair.PushUrl, waiting, "relay", "waiting", "")).State);
        Assert.Equal(1, (await pair.PushCli("accept", "--all")).Code);                                  // no keyboard, no --yes: nothing is done
        Assert.Contains("1 matching", (await pair.PushCli("requests")).Output);
        Assert.Equal(1, (await pair.PushCli("accept")).Code);                                           // nothing named
        Assert.Equal(4, (await pair.PushCli("accept", "abcdef")).Code);                                  // nobody waits under that id
        Assert.Equal(0, (await pair.PushCli("reject", "--all", "--yes")).Code);

        // Nothing of this is open to the world: the admin routes need the secret.
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(pair.PushUrl + "/admin/peers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(pair.RelayUrl + "/api/admin/peers", new { kind = "push", url = pair.PushUrl })).StatusCode);

        // The clients' own commands are not shadowed by the peer commands.
        Assert.Equal(0, (await pair.RelayCli("pending")).Code);
        Assert.Equal(0, (await pair.RelayCli("requests", "open")).Code);
    }

    [Fact]
    public async Task OldRequestsExpire_andADamagedFileTrustsNobody()
    {
        using var pair = new PeerPair();
        await pair.StartAsync();
        var old = NewIdentity(pair.Dir, "old");
        var fresh = NewIdentity(pair.Dir, "fresh");
        Assert.Equal("pending", (await Ask(pair.PushUrl, fresh, "relay", "fresh", "")).State);
        pair.StopPush();

        var file = Path.Combine(pair.Dir, "push", "peers.json");
        var state = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        var peers = state.GetProperty("peers").EnumerateArray().Select(p => (object)p.Clone()).ToList();
        peers.Add(new Dictionary<string, object?> { ["id"] = old.Fingerprint, ["kind"] = "relay", ["name"] = "old", ["url"] = "", ["public_key"] = old.PublicKey, ["state"] = "pending_in", ["since"] = DateTime.UtcNow.AddDays(-30) });
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, object?> { ["requests_open"] = true, ["peers"] = peers }));

        await pair.StartPushAsync();
        var listing = (await pair.PushCli("requests")).Output;
        Assert.Contains("fresh", listing);
        Assert.Contains("1 matching", listing);   // the month-old request is gone
        pair.StopPush();

        File.WriteAllText(file, "{ not json");
        await pair.StartPushAsync();
        Assert.True(File.Exists(file + ".corrupt"));
        Assert.Contains("queue is closed", (await pair.PushCli("requests")).Output);   // fails closed: no request taken, nobody trusted
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignedPush(pair.PushUrl, fresh)).StatusCode);
    }
}
