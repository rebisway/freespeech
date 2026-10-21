/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>
/// The push gateway (PROTOCOL §9) and its local admin API / command line: relays known by their key, ban and removal of a single
/// relay, limits and maintenance changed at run time, state kept across a restart, and a gateway with no relay that refuses everyone.
/// </summary>
public class PushAdminTests : IAsyncLifetime
{
    private const string Admin = "push-admin-secret";
    private static readonly string Dll = TestProcess.Dll("freespeech.push", "FreeSpeech.Push.dll");

    private readonly string _state = Path.Combine(Path.GetTempPath(), "fs-push-" + Guid.NewGuid().ToString("N"));
    private TestProcess _gateway = null!;
    private int _token;

    private Dictionary<string, string> Env() => new()
    {
        ["Push__Fake"] = "true", ["Push__StatePath"] = _state, ["Push__MinIntervalSeconds"] = "1", ["Admin__Secret"] = Admin,
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_state);
        _gateway = await TestProcess.StartAsync(Dll, Env());
        // From here on the gateway is closed: one relay is accepted. (A gateway with no relay at all is open only in development.)
        await EnrollAsync("bootstrap");
    }

    public Task DisposeAsync()
    {
        _gateway.Dispose();
        try { Directory.Delete(_state, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> AdminCall(HttpMethod method, string path, object? body = null, string secret = Admin)
    {
        using var client = new HttpClient();
        var req = new HttpRequestMessage(method, _gateway.Http + path);
        req.Headers.Add("X-Admin-Secret", secret);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private PeerIdentity NewRelayKey() => PeerIdentity.LoadOrCreate(Path.Combine(_state, "relay-" + Guid.NewGuid().ToString("N") + ".key"));

    /// <summary>A relay asks the gateway for a link (signed request, no address) and the gateway's administrator accepts it.</summary>
    private async Task<PeerIdentity> EnrollAsync(string name)
    {
        var who = NewRelayKey();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new PeerRequestBody { Kind = "relay", Name = name, Url = "", PublicKey = who.PublicKey, Ts = ts, Sig = who.Sign(PeerSigning.RequestText("relay", name, "", ts)) };
        using (var client = new HttpClient()) Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(_gateway.Http + "/peer/request", body)).StatusCode);
        var accepted = await AdminCall(HttpMethod.Post, "/admin/peers/accept", new { kind = "relay", ids = new[] { who.Fingerprint[..12] } });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return who;
    }

    /// <summary>One wake-up with a fresh token (so the per-token interval never interferes), signed by the relay's key; unsigned when <paramref name="who"/> is null.</summary>
    private async Task<HttpResponseMessage> Wake(PeerIdentity? who, string? token = null, string platform = "fcm", Action<HttpRequestMessage>? tweak = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { platform, token = token ?? "tok-" + Interlocked.Increment(ref _token), kind = "wake" });
        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, _gateway.Http + "/push") { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (who != null) PeerSigning.Sign(req, who, "/push", bytes);
        tweak?.Invoke(req);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task AnAcceptedRelay_isServed_anUnknownOrUnsignedCallIsNot()
    {
        var relay = await EnrollAsync("relay one");
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Wake(NewRelayKey())).StatusCode);   // a key nobody accepted
        Assert.Equal(HttpStatusCode.Unauthorized, (await Wake(null)).StatusCode);

        var list = await (await AdminCall(HttpMethod.Get, "/admin/relays")).Content.ReadAsStringAsync();
        Assert.Contains(relay.Fingerprint, list);
        Assert.Contains("relay one", list);
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(_state, "peers.json")), StringComparison.OrdinalIgnoreCase);   // only public keys are kept
    }

    [Fact]
    public async Task BanningOneRelay_leavesTheOthersWorking_andAForgottenRelayMustAskAgain()
    {
        var a = await EnrollAsync("relay A");
        var b = await EnrollAsync("relay B");

        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Post, "/admin/peers/ban", new { id = a.Fingerprint })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Wake(a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Wake(b)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Delete, $"/admin/peers/ban?id={a.Fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Wake(a)).StatusCode);   // unbanned means forgotten: it has to ask again
    }

    [Fact]
    public async Task RemovingARelay_cutsItAtOnce()
    {
        var relay = await EnrollAsync("short-lived");
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Delete, $"/admin/peers?id={relay.Fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Wake(relay)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminCall(HttpMethod.Delete, $"/admin/peers?id={relay.Fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminCall(HttpMethod.Post, "/admin/peers/ban", new { id = "abcdef123456" })).StatusCode);
    }

    [Fact]
    public async Task Counters_areKeptPerRelay()
    {
        var a = await EnrollAsync("counted A");
        var b = await EnrollAsync("counted B");
        await Wake(a); await Wake(a); await Wake(b);
        await Wake(a, token: "dead-token");   // the fake provider calls it unregistered: 410

        var list = JsonDocument.Parse(await (await AdminCall(HttpMethod.Get, "/admin/relays")).Content.ReadAsStringAsync()).RootElement;
        long Count(PeerIdentity who, string field) => list.EnumerateArray().First(r => r.GetProperty("id").GetString() == who.Fingerprint).GetProperty(field).GetInt64();
        Assert.Equal(2, Count(a, "sent"));
        Assert.Equal(1, Count(b, "sent"));

        var status = JsonDocument.Parse(await (await AdminCall(HttpMethod.Get, "/admin/status")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(status.GetProperty("totals").GetProperty("sent").GetInt64() >= 3);
        Assert.Equal(1, status.GetProperty("totals").GetProperty("gone").GetInt64());
        Assert.True(status.GetProperty("linked_relays").GetInt32() >= 3);
    }

    [Fact]
    public async Task Limits_changeAtRunTime_andAreValidated()
    {
        var relay = await EnrollAsync("limits");
        const string token = "same-token";
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay, token)).StatusCode);
        var tooSoon = await Wake(relay, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal("token", tooSoon.Headers.GetValues("X-Push-Reason").Single());

        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Post, "/admin/settings", new { minIntervalSeconds = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay, token)).StatusCode);   // the new limit applies at once

        Assert.Equal(HttpStatusCode.BadRequest, (await AdminCall(HttpMethod.Post, "/admin/settings", new { maxConcurrent = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminCall(HttpMethod.Post, "/admin/settings", new { minIntervalSeconds = -1 })).StatusCode);
        await AdminCall(HttpMethod.Post, "/admin/settings", new { minIntervalSeconds = 1 });
    }

    [Fact]
    public async Task Maintenance_answersBusyToEveryone_andEndsOnRequest()
    {
        var relay = await EnrollAsync("maintenance");
        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Post, "/admin/settings", new { maintenance = true })).StatusCode);
        try
        {
            var busy = await Wake(relay);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal("busy", busy.Headers.GetValues("X-Push-Reason").Single());   // the relay then tries another gateway
            Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Get, "/admin/status")).StatusCode);   // the admin API stays up
        }
        finally { await AdminCall(HttpMethod.Post, "/admin/settings", new { maintenance = false }); }
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay)).StatusCode);
    }

    [Fact]
    public async Task AdminApi_refusesAWrongSecret_andLogsAreAvailable()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await AdminCall(HttpMethod.Get, "/admin/status", secret: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AdminCall(HttpMethod.Post, "/admin/peers/accept", new { all = true }, secret: "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AdminCall(HttpMethod.Get, "/admin/log", secret: "nope")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AdminCall(HttpMethod.Post, "/admin/log/level", new { category = "push", level = "debug" })).StatusCode);
        var relay = await EnrollAsync("logged");
        await Wake(relay);
        var tail = await (await AdminCall(HttpMethod.Get, "/admin/log/tail?lines=200")).Content.ReadAsStringAsync();
        Assert.Contains("Wake-up fcm for relay key", tail);
        Assert.DoesNotContain("tok-", tail);   // the device token is not logged
        await AdminCall(HttpMethod.Delete, "/admin/log/level");
    }

    [Fact]
    public async Task Admin_isRefusedWhenTheForwardedCallerIsNotLocal()
    {
        // Behind nginx every call arrives from 127.0.0.1: the address nginx forwards is the one that counts.
        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, _gateway.Http + "/admin/status");
        req.Headers.Add("X-Admin-Secret", Admin);
        req.Headers.Add("X-Forwarded-For", "203.0.113.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);

        var local = new HttpRequestMessage(HttpMethod.Get, _gateway.Http + "/admin/status");
        local.Headers.Add("X-Admin-Secret", Admin);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(local)).StatusCode);

        // The wake-up endpoint itself is for the relays: reachable through nginx, whatever the address.
        var relay = await EnrollAsync("behind nginx");
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay, "fwd-token", tweak: r => r.Headers.Add("X-Forwarded-For", "198.51.100.4"))).StatusCode);
    }

    [Fact]
    public async Task State_survivesARestart()
    {
        var relay = await EnrollAsync("persistent");
        await AdminCall(HttpMethod.Post, "/admin/settings", new { minIntervalSeconds = 7, maxConcurrent = 5 });
        _gateway.Dispose();

        _gateway = await TestProcess.StartAsync(Dll, Env());
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay)).StatusCode);   // still accepted: the approval is on disk
        var settings = await (await AdminCall(HttpMethod.Get, "/admin/settings")).Content.ReadAsStringAsync();
        Assert.Contains("\"min_interval_seconds\":7", settings);
        Assert.Contains("\"max_concurrent\":5", settings);
        Assert.Contains(relay.Fingerprint, await (await AdminCall(HttpMethod.Get, "/admin/relays")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AGatewayWithNoRelay_outsideDevelopment_refusesEveryone()
    {
        var state = Path.Combine(Path.GetTempPath(), "fs-push-closed-" + Guid.NewGuid().ToString("N"));
        using var closed = await TestProcess.StartAsync(Dll, new Dictionary<string, string> { ["Push__StatePath"] = state, ["Admin__Secret"] = Admin });
        try
        {
            using var client = new HttpClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(closed.Http + "/push", new { platform = "fcm", token = "t", kind = "wake" })).StatusCode);
            var stranger = NewRelayKey();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { platform = "fcm", token = "t", kind = "wake" });
            var signed = new HttpRequestMessage(HttpMethod.Post, closed.Http + "/push") { Content = new ByteArrayContent(bytes) };
            signed.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            PeerSigning.Sign(signed, stranger, "/push", bytes);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(signed)).StatusCode);
        }
        finally { try { Directory.Delete(state, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task CommandLine_managesTheRunningGateway()
    {
        var url = _gateway.Http;
        Assert.Contains("providers", (await TestProcess.AdminCliAsync(Dll, url, Admin, "status")).Output);

        // A relay asks; the administrator sees the request and accepts it.
        var relay = NewRelayKey();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new PeerRequestBody { Kind = "relay", Name = "from the cli", Url = "", PublicKey = relay.PublicKey, Ts = ts, Sig = relay.Sign(PeerSigning.RequestText("relay", "from the cli", "", ts)) };
        using (var client = new HttpClient()) Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(url + "/peer/request", body)).StatusCode);
        Assert.Contains("from the cli", (await TestProcess.AdminCliAsync(Dll, url, Admin, "requests")).Output);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Wake(relay)).StatusCode);   // asked is not accepted
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "accept", relay.Fingerprint[..8])).Code);
        Assert.Equal(HttpStatusCode.OK, (await Wake(relay)).StatusCode);
        Assert.Contains("from the cli", (await TestProcess.AdminCliAsync(Dll, url, Admin, "relays")).Output);

        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "ban", relay.Fingerprint[..8])).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await Wake(relay)).StatusCode);
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "unban", relay.Fingerprint[..8])).Code);
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "set", "min-interval", "0")).Code);
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "maintenance", "on")).Code);
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "maintenance", "off")).Code);

        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "log", "level", "push", "debug")).Code);
        Assert.Equal(0, (await TestProcess.AdminCliAsync(Dll, url, Admin, "log", "reset")).Code);
        Assert.Equal(4, (await TestProcess.AdminCliAsync(Dll, url, "wrong", "status")).Code);
        Assert.Equal(1, (await TestProcess.AdminCliAsync(Dll, url, Admin, "set", "nothing")).Code);
        Assert.Equal(3, (await TestProcess.AdminCliAsync(Dll, "http://127.0.0.1:1", Admin, "status")).Code);
    }
}
