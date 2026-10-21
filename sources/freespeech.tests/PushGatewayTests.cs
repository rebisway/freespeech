/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>A push gateway stand-in that answers every POST /push with a fixed status and counts the calls.</summary>
public sealed class FakeGateway : IDisposable
{
    private readonly HttpListener _listener = new();
    private int _hits;
    public int Hits => Volatile.Read(ref _hits);
    public string Url { get; }
    public int Status { get; set; } = 200;
    public string? Reason { get; set; }
    public string? RetryAfter { get; set; }

    public FakeGateway()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var c = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _hits);
                    c.Response.StatusCode = Status;
                    if (Reason != null) c.Response.Headers["X-Push-Reason"] = Reason;
                    if (RetryAfter != null) c.Response.Headers["Retry-After"] = RetryAfter;
                    c.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); } catch (ObjectDisposedException) { } }
}

/// <summary>A relay server linked with two gateways: "Bad" (the one devices register with) and "Good".</summary>
public abstract class PushFailoverFixture : ServerFixture
{
    public FakeGateway Bad { get; } = new();
    public FakeGateway Good { get; } = new();

    protected abstract void ConfigureBad(FakeGateway bad);

    protected override void Configure(ProcessStartInfo info)
    {
        ConfigureBad(Bad);
        AcceptedPeers.Clear();
        AcceptedPeers.Add(("push", Bad.Url, null));
        AcceptedPeers.Add(("push", Good.Url, null));
    }

    public override Task DisposeAsync() { Bad.Dispose(); Good.Dispose(); return base.DisposeAsync(); }
}

public class PushDownFixture : PushFailoverFixture { protected override void ConfigureBad(FakeGateway bad) => bad.Status = 500; }
public class PushBusyFixture : PushFailoverFixture { protected override void ConfigureBad(FakeGateway bad) { bad.Status = 429; bad.Reason = "busy"; bad.RetryAfter = "30"; } }
public class PushTokenFixture : PushFailoverFixture { protected override void ConfigureBad(FakeGateway bad) { bad.Status = 429; bad.Reason = "token"; } }

/// <summary>Several push gateways (PROTOCOL §9): fail-over, circuit breaker, and no double wake-up on a per-token limit.</summary>
public class PushGatewayTests : IClassFixture<PushDownFixture>, IClassFixture<PushBusyFixture>, IClassFixture<PushTokenFixture>
{
    private readonly PushDownFixture _down;
    private readonly PushBusyFixture _busy;
    private readonly PushTokenFixture _token;
    private static int _n;

    public PushGatewayTests(PushDownFixture d, PushBusyFixture b, PushTokenFixture t) { _down = d; _busy = b; _token = t; }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
    private static UserIdentity Id(string who) => IdentityService.Derive(Pad(who + " one"), Pad(who + " two"), Pad(who + " three"), who);

    /// <summary>B registers a push token on the Bad gateway and goes offline; A (online) then flags WAITING for B: the server wakes B.</summary>
    private static async Task WakeOnceAsync(PushFailoverFixture s)
    {
        var n = Interlocked.Increment(ref _n);
        var a = Id($"push-a-{n}"); var b = Id($"push-b-{n}");
        using var wsA = await RawClient.ConnectAsync(s.WsUri);
        var regA = await wsA.RegisterAsync(a);
        var wsB = await RawClient.ConnectAsync(s.WsUri);
        await wsB.RegisterAsync(b);
        await wsB.SendAsync(new { type = "push_register", platform = "fcm", token = $"tok-{n}", request_id = "p" });
        Assert.True((await wsB.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean());
        wsB.Dispose();
        await Task.Delay(400);

        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{s.Http}/api/flags") { Content = new StringContent(JsonSerializer.Serialize(new { from = a.DiscoveryId, to = b.DiscoveryId, state = "WAITING" }), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Discovery-Id", a.DiscoveryId);
        req.Headers.Add("X-Flag-Token", regA!.Value.GetProperty("http_token").GetString());
        Assert.True((await http.SendAsync(req)).IsSuccessStatusCode);
        await Task.Delay(700);
    }

    // The relay tries its linked gateways in random order, so each test checks totals that hold whichever comes first.

    [Fact]
    public async Task DeadGateway_isSkipped_thenSetAside()
    {
        for (var k = 0; k < 8; k++) await WakeOnceAsync(_down);
        Assert.Equal(8, _down.Good.Hits);      // fail-over: every wake-up got through, once
        Assert.InRange(_down.Bad.Hits, 0, 3);  // the dead gateway is tried at most until the breaker opens (3 failures in a row)
    }

    [Fact]
    public async Task BusyGateway_isSetAsideAtOnce()
    {
        await WakeOnceAsync(_busy);
        await WakeOnceAsync(_busy);
        Assert.Equal(2, _busy.Good.Hits);      // both wake-ups got through
        Assert.InRange(_busy.Bad.Hits, 0, 1);  // "busy, retry in 30 s": not asked again meanwhile
    }

    [Fact]
    public async Task PerTokenLimit_isNotSaturation_noSecondWakeUp()
    {
        await WakeOnceAsync(_token);
        Assert.Equal(1, _token.Bad.Hits + _token.Good.Hits);   // the token was just woken: another gateway would wake it twice
    }
}