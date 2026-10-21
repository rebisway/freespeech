/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>A relay server linked with a single push gateway.</summary>
public sealed class PushSingleGatewayFixture : ServerFixture
{
    public FakeGateway Gateway { get; } = new();

    protected override void Configure(ProcessStartInfo info)
    {
        AcceptedPeers.Clear();
        AcceptedPeers.Add(("push", Gateway.Url, null));
    }

    public override Task DisposeAsync() { Gateway.Dispose(); return base.DisposeAsync(); }
}

/// <summary>Wake-up rules of the relay (PROTOCOL §9): a failed attempt does not use up the 30 s allowance, and a connection that ends triggers the wake-up it suppressed.</summary>
public class PushWakeTests : IClassFixture<PushSingleGatewayFixture>
{
    private readonly PushSingleGatewayFixture _s;
    private static int _n;

    public PushWakeTests(PushSingleGatewayFixture s) => _s = s;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
    private static UserIdentity Id(string who) => IdentityService.Derive(Pad(who + " one"), Pad(who + " two"), Pad(who + " three"), who);

    private async Task FlagWaitingAsync(UserIdentity from, string httpToken, UserIdentity to)
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_s.Http}/api/flags") { Content = new StringContent(JsonSerializer.Serialize(new { from = from.DiscoveryId, to = to.DiscoveryId, state = "WAITING" }), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Discovery-Id", from.DiscoveryId);
        req.Headers.Add("X-Flag-Token", httpToken);
        Assert.True((await http.SendAsync(req)).IsSuccessStatusCode);
    }

    private async Task<RawClient> RegisterPushAsync(UserIdentity who, string token)
    {
        var ws = await RawClient.ConnectAsync(_s.WsUri);
        await ws.RegisterAsync(who);
        await ws.SendAsync(new { type = "push_register", platform = "fcm", token, request_id = "p" });
        Assert.True((await ws.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean());
        return ws;
    }

    [Fact]
    public async Task FailedWakeUp_doesNotBlockTheNextOne()
    {
        var n = Interlocked.Increment(ref _n);
        var a = Id($"wake-a-{n}"); var b = Id($"wake-b-{n}");
        using var wsA = await RawClient.ConnectAsync(_s.WsUri);
        var regA = await wsA.RegisterAsync(a);
        (await RegisterPushAsync(b, $"tok-fail-{n}")).Dispose();
        await Task.Delay(400);

        _s.Gateway.Status = 500;
        var before = _s.Gateway.Hits;
        await FlagWaitingAsync(a, regA!.Value.GetProperty("http_token").GetString()!, b);
        await Task.Delay(700);
        Assert.Equal(before + 1, _s.Gateway.Hits);   // tried, and failed

        _s.Gateway.Status = 200;
        await FlagWaitingAsync(a, regA.Value.GetProperty("http_token").GetString()!, b);
        await Task.Delay(700);
        Assert.Equal(before + 2, _s.Gateway.Hits);   // inside the 30 s window, yet tried again: the first attempt had failed
    }

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = new HttpClient();
        var req = new HttpRequestMessage(method, _s.Http + path);
        req.Headers.Add("X-Admin-Secret", "admin-test-secret");
        if (body != null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private async Task<string[]> TracedLinesAsync() =>
        (await (await AdminAsync(HttpMethod.Get, "/api/admin/log/tail?lines=500")).Content.ReadAsStringAsync()).Split('\n').Where(l => l.Contains("] trace:")).ToArray();

    [Fact]
    public async Task TheTrace_tellsEveryStepOfAWakeUp_withShortIdentifiersOnly()
    {
        var n = Interlocked.Increment(ref _n);
        var a = Id($"trc-a-{n}"); var b = Id($"trc-b-{n}"); var c = Id($"trc-c-{n}");
        Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/trace", new { id = b.DiscoveryId[..12], minutes = 5 })).IsSuccessStatusCode);
        Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/trace", new { id = a.DiscoveryId[..12], minutes = 5 })).IsSuccessStatusCode);
        try
        {
            using var wsA = await RawClient.ConnectAsync(_s.WsUri);
            var regA = await wsA.RegisterAsync(a);
            (await RegisterPushAsync(b, $"tok-trace-{n}")).Dispose();   // b has a push token and is offline
            await Task.Delay(400);

            // a flags b, twice within the allowance: the second wake-up is held back, and the trace says why.
            _s.Gateway.Status = 200;
            await FlagWaitingAsync(a, regA!.Value.GetProperty("http_token").GetString()!, b);
            await Task.Delay(700);
            await FlagWaitingAsync(a, regA.Value.GetProperty("http_token").GetString()!, b);
            await Task.Delay(700);

            var lines = await TracedLinesAsync();
            Assert.Contains(lines, l => l.Contains(b.DiscoveryId[..8]) && l.Contains("WAITING flag from " + a.DiscoveryId[..8]));
            Assert.Contains(lines, l => l.Contains(a.DiscoveryId[..8]) && l.Contains("WAITING flag raised towards " + b.DiscoveryId[..8]));
            Assert.Contains(lines, l => l.Contains("wake-up via") && l.Contains("Done"));
            Assert.Contains(lines, l => l.Contains("wake-up held back"));
            Assert.DoesNotContain(b.DiscoveryId, string.Join('\n', lines));   // never the full identifier
            Assert.DoesNotContain($"tok-trace-{n}", string.Join('\n', lines));   // never a device token

            // c has no push token: the trace says so.
            Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/trace", new { id = c.DiscoveryId[..12], minutes = 5 })).IsSuccessStatusCode);
            await FlagWaitingAsync(a, regA.Value.GetProperty("http_token").GetString()!, c);
            await Task.Delay(500);
            Assert.Contains(await TracedLinesAsync(), l => l.Contains(c.DiscoveryId[..8]) && l.Contains("no push token registered"));
        }
        finally { await AdminAsync(HttpMethod.Delete, "/api/admin/trace?id=all"); }
    }

    [Fact]
    public async Task TheTrace_showsAContactComingOnlineWhoseSenderIsAbsent_andTheSenderBeingWoken()
    {
        var n = Interlocked.Increment(ref _n);
        var a = Id($"st3-a-{n}"); var b = Id($"st3-b-{n}");
        Assert.True((await AdminAsync(HttpMethod.Post, "/api/admin/trace", new { id = a.DiscoveryId[..12], minutes = 5 })).IsSuccessStatusCode);
        try
        {
            var wsA = await RegisterPushAsync(a, $"tok-st3-{n}");          // a has a push token...
            var regA = await wsA.RegisterAsync(a);
            await Task.Delay(300);
            await FlagWaitingAsync(a, regA!.Value.GetProperty("http_token").GetString()!, b);   // ...and waits for b, who is offline
            wsA.Dispose();                                                    // a's app is closed
            await Task.Delay(500);

            _s.Gateway.Status = 200;
            var before = _s.Gateway.Hits;
            using var wsB = await RawClient.ConnectAsync(_s.WsUri);          // b's phone comes back
            await wsB.RegisterAsync(b);
            await Task.Delay(1000);

            Assert.True(_s.Gateway.Hits > before, "a was not woken when b came online");
            var lines = await TracedLinesAsync();
            Assert.Contains(lines, l => l.Contains(a.DiscoveryId[..8]) && l.Contains(b.DiscoveryId[..8] + " is online") && l.Contains("waking you"));
        }
        finally { await AdminAsync(HttpMethod.Delete, "/api/admin/trace?id=all"); }
    }

    [Fact]
    public async Task ConnectionThatEnds_wakesTheIdentityFlaggedMeanwhile()
    {
        var n = Interlocked.Increment(ref _n);
        var a = Id($"gone-a-{n}"); var b = Id($"gone-b-{n}");
        using var wsA = await RawClient.ConnectAsync(_s.WsUri);
        var regA = await wsA.RegisterAsync(a);
        var wsB = await RegisterPushAsync(b, $"tok-gone-{n}");   // B looks online (a phone that vanished keeps its socket until the keep-alive timeout)
        _s.Gateway.Status = 200;

        var before = _s.Gateway.Hits;
        await FlagWaitingAsync(a, regA!.Value.GetProperty("http_token").GetString()!, b);
        await Task.Delay(700);
        Assert.Equal(before, _s.Gateway.Hits);       // online: no push

        wsB.Dispose();                               // the connection ends
        await Task.Delay(1000);
        Assert.Equal(before + 1, _s.Gateway.Hits);   // and the suppressed wake-up is sent
    }
}
