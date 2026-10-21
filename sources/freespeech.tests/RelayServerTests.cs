/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Tests;

/// <summary>
/// The real relay server, started in a process on a free local port (never the public one), with a fake push gateway beside it.
/// Everything is spoken over raw WebSocket JSON, so these tests check the wire protocol itself (PROTOCOL §9 and §11).
/// </summary>
public class ServerFixture : IAsyncLifetime
{
    private Process? _server;
    private HttpListener? _gateway;
    private readonly string _state = Path.Combine(Path.GetTempPath(), "fs-test-" + Guid.NewGuid().ToString("N"));

    public int Port { get; private set; }
    public int GatewayPort { get; private set; }
    public ConcurrentQueue<string> GatewayCalls { get; } = new();
    /// <summary>Peers written as accepted in the relay's peers file before it starts (a derived fixture may change them in <see cref="Configure"/>). The fake gateway is one of them.</summary>
    protected List<(string Kind, string Url, FreeSpeech.Ops.PeerIdentity? Who)> AcceptedPeers { get; } = [];
    public Uri WsUri => new($"ws://127.0.0.1:{Port}/ws/relay");
    public string Http => $"http://127.0.0.1:{Port}";
    public string StateDir => _state;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string ServerDll()
    {
        // .../freespeech.tests/bin/<config>/net10.0/ -> .../freespeech.relay/bin/<config>/net10.0/
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = dir.Name;
        var config = dir.Parent!.Name;
        var projectsRoot = dir.Parent.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(projectsRoot, "freespeech.relay", "bin", config, framework, "FreeSpeech.Relay.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build freespeech.relay first.", dll);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_state);
        Port = FreePort();
        GatewayPort = FreePort();

        // Fake gateway: records every POST /push body and answers 200.
        _gateway = new HttpListener();
        _gateway.Prefixes.Add($"http://127.0.0.1:{GatewayPort}/");
        _gateway.Start();
        _ = Task.Run(async () =>
        {
            while (_gateway.IsListening)
            {
                try
                {
                    var c = await _gateway.GetContextAsync();
                    using var reader = new StreamReader(c.Request.InputStream);
                    GatewayCalls.Enqueue($"{c.Request.Headers["X-Peer-Id"]}|{await reader.ReadToEndAsync()}");
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                }
                catch (Exception) when (!_gateway.IsListening) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        });

        var info = new ProcessStartInfo("dotnet", $"\"{ServerDll()}\" --urls http://127.0.0.1:{Port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["Server__SiblingsFilePath"] = Path.Combine(_state, "siblings.json");
        info.Environment["Server__AccessFilePath"] = Path.Combine(_state, "access.json");
        info.Environment["Server__LimitsFilePath"] = Path.Combine(_state, "limits.json");
        info.Environment["Server__MaxMessageBytes"] = "65536";   // small, so the size test stays small
        info.Environment["Peer__StatePath"] = _state;
        info.Environment["Peer__PublicUrl"] = Http;
        info.Environment["Admin__Secret"] = "admin-test-secret";
        AcceptedPeers.Add(("push", $"http://127.0.0.1:{GatewayPort}", null));   // the push gateway this relay is linked with
        Configure(info);
        PeerSeed.Accepted(_state, AcceptedPeers);
        _server = Process.Start(info)!;
        _server.OutputDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        return WaitUntilUpAsync();
    }

    /// <summary>Lets a derived fixture adjust the server's environment before it starts.</summary>
    protected virtual void Configure(ProcessStartInfo info) { }

    private async Task WaitUntilUpAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 40; i++)
        {
            try { if ((await http.GetAsync($"{Http}/api/time")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("The test server did not start.");
    }

    public virtual Task DisposeAsync()
    {
        try { _server?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _server?.Dispose();
        _gateway?.Stop();
        try { Directory.Delete(_state, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

/// <summary>A bare WebSocket speaking the relay's JSON frames, with helpers to register the way a real client does.</summary>
public sealed class RawClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    public WebSocketState State => _ws.State;
    public WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;

    public static async Task<RawClient> ConnectAsync(Uri uri)
    {
        var c = new RawClient();
        await c._ws.ConnectAsync(uri, CancellationToken.None);
        return c;
    }

    public Task SendAsync(object frame) => SendRawAsync(JsonSerializer.Serialize(frame));

    public Task SendRawAsync(string text) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Next frame as a JSON object, or null when the server closed or nothing came in time.</summary>
    public async Task<JsonElement?> ReceiveAsync(int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        var buffer = new byte[16 * 1024];
        using var all = new MemoryStream();
        try
        {
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return null; }
                all.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
        return JsonDocument.Parse(all.ToArray()).RootElement.Clone();
    }

    /// <summary>The honest registration: keys, then the signed challenge. Returns the server's last answer.</summary>
    public async Task<JsonElement?> RegisterAsync(UserIdentity me, byte[]? signSeedOverride = null)
    {
        await SendAsync(new { type = "register", discovery_id = me.DiscoveryId, enc_key = Convert.ToBase64String(me.EncryptionPublicKey), sign_key = Convert.ToBase64String(me.SigningPublicKey), request_id = "1" });
        var challenge = await ReceiveAsync();
        if (challenge is not { } c || c.GetProperty("type").GetString() != "challenge") return challenge;

        var data = Encoding.UTF8.GetBytes($"freespeech-register-v1|{c.GetProperty("nonce").GetString()}|{me.DiscoveryId}");
        var signature = Primitives.Ed25519Sign(signSeedOverride ?? me.SigningPrivateKey, data);
        await SendAsync(new { type = "register_proof", signature = Convert.ToBase64String(signature), request_id = "2" });
        return await ReceiveAsync();
    }

    public void Dispose() => _ws.Dispose();
}

public class RelayServerTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _s;
    public RelayServerTests(ServerFixture s) => _s = s;

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static string Type(JsonElement? e) => e is { } v && v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "(nothing)";

    [Fact]
    public async Task Register_withProof_isAccepted()
    {
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await c.RegisterAsync(NewIdentity("alice-ok"))));
    }

    [Fact]
    public async Task Register_withoutKeys_isRefused()
    {
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        await c.SendAsync(new { type = "register", discovery_id = NewIdentity("bare").DiscoveryId, request_id = "1" });
        Assert.Equal("error", Type(await c.ReceiveAsync()));
    }

    [Fact]
    public async Task Register_asSomeoneElse_isRefused()
    {
        var victim = NewIdentity("victim");
        var attacker = NewIdentity("attacker");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        // The attacker names the victim's id but can only offer his own keys: they do not hash to that id.
        await c.SendAsync(new { type = "register", discovery_id = victim.DiscoveryId, enc_key = Convert.ToBase64String(attacker.EncryptionPublicKey), sign_key = Convert.ToBase64String(attacker.SigningPublicKey), request_id = "1" });
        Assert.Equal("error", Type(await c.ReceiveAsync()));
    }

    [Fact]
    public async Task Register_withTheVictimsPublicKeysButAnotherSignature_isRefused()
    {
        var victim = NewIdentity("victim2");
        var attacker = NewIdentity("attacker2");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        // Public keys are public: he can send the victim's. He cannot sign the challenge without the victim's secret seed.
        var answer = await c.RegisterAsync(victim, signSeedOverride: attacker.SigningPrivateKey);
        Assert.Equal("error", Type(answer));
    }

    [Fact]
    public async Task Refused_registration_doesNotMakeTheSocketAnIdentity()
    {
        var victim = NewIdentity("victim3");
        var friend = NewIdentity("friend3");
        using var legit = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await legit.RegisterAsync(friend)));

        using var attacker = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("error", Type(await attacker.RegisterAsync(victim, signSeedOverride: NewIdentity("other").SigningPrivateKey)));

        // The victim is not online: the friend's presence query must say so, and a send is not delivered.
        await legit.SendAsync(new { type = "is_online", discovery_id = victim.DiscoveryId, request_id = "3" });
        var online = await legit.ReceiveAsync();
        Assert.False(online!.Value.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task Proof_withoutRegister_isRefused()
    {
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        await c.SendAsync(new { type = "register_proof", signature = Convert.ToBase64String(new byte[64]), request_id = "1" });
        Assert.Equal("error", Type(await c.ReceiveAsync()));
    }

    [Fact]
    public async Task OversizedMessage_closesTheConnection()
    {
        var me = NewIdentity("big");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await c.RegisterAsync(me)));

        // 100 KB of JSON against a 64 KB limit (the fixture's setting).
        await c.SendRawAsync("{\"type\":\"send\",\"pad\":\"" + new string('a', 100_000) + "\"}");
        Assert.Null(await c.ReceiveAsync());
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, c.CloseStatus);
    }

    [Fact]
    public async Task Send_fromAnotherIdentity_isRefused()
    {
        var a = NewIdentity("sender-a");
        var b = NewIdentity("sender-b");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await c.RegisterAsync(a)));
        await c.SendAsync(new { type = "send", request_id = "5", envelope = new { from = b.DiscoveryId, to = a.DiscoveryId, crypto = 1, msg = "x", ts = 1 } });
        Assert.Equal("error", Type(await c.ReceiveAsync()));
    }

    [Fact]
    public async Task Push_wakeUp_andPeerOnline()
    {
        var a = NewIdentity("push-a");
        var b = NewIdentity("push-b");
        using var wsA = await RawClient.ConnectAsync(_s.WsUri);
        var regA = await wsA.RegisterAsync(a);
        Assert.Equal("registered", Type(regA));

        // B registers a token; the client names no gateway (a "gateway" field, if sent, changes nothing: the relay calls only the one it is linked with).
        var wsB = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await wsB.RegisterAsync(b)));
        await wsB.SendAsync(new { type = "push_register", platform = "fcm", token = "tok-B", gateway = "http://evil.example", request_id = "p2" });
        Assert.True((await wsB.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean());

        // B goes offline; A sets a WAITING flag towards B: the gateway is called once, with a payload that names nobody.
        wsB.Dispose();
        await Task.Delay(500);
        using (var http = new HttpClient())
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_s.Http}/api/flags") { Content = new StringContent(JsonSerializer.Serialize(new { from = a.DiscoveryId, to = b.DiscoveryId, state = "WAITING" }), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Discovery-Id", a.DiscoveryId);
            // Knowing the id is not enough: without the connection's token the call is refused.
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Clone(req))).StatusCode);
            req.Headers.Add("X-Flag-Token", regA!.Value.GetProperty("http_token").GetString());
            Assert.True((await http.SendAsync(req)).IsSuccessStatusCode);
        }
        await Task.Delay(1000);
        var call = Assert.Single(_s.GatewayCalls);
        Assert.Matches("^[0-9a-f]{64}\\|", call);   // signed with the relay's key: its fingerprint, no secret
        Assert.Contains("tok-B", call);
        Assert.DoesNotContain(a.DiscoveryId, call);
        Assert.DoesNotContain(b.DiscoveryId, call);

        // B comes back: A, who is waiting, is told at once.
        using var wsB2 = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await wsB2.RegisterAsync(b)));
        var told = await wsA.ReceiveAsync();
        Assert.Equal("peer_online", Type(told));
        Assert.Equal(b.DiscoveryId, told!.Value.GetProperty("discovery_id").GetString());
    }

    private static HttpRequestMessage Clone(HttpRequestMessage r)
    {
        var c = new HttpRequestMessage(r.Method, r.RequestUri) { Content = r.Content == null ? null : new StringContent(r.Content.ReadAsStringAsync().Result, Encoding.UTF8, "application/json") };
        foreach (var h in r.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return c;
    }
}

