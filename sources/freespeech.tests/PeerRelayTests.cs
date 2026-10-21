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
using FreeSpeech.Client.Identity;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>Two real relay servers, each with its own state folder, that can be restarted where they were.</summary>
public sealed class RelayDuo : IDisposable
{
    public const string SecretA = "relay-a-admin-secret-0123456789";
    public const string SecretB = "relay-b-admin-secret-0123456789";
    public static readonly string Dll = PeerPair.RelayDll;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fs-duo-" + Guid.NewGuid().ToString("N"));
    public int PortA { get; } = TestProcess.FreePort();
    public int PortB { get; } = TestProcess.FreePort();
    public string UrlA => $"http://127.0.0.1:{PortA}";
    public string UrlB => $"http://127.0.0.1:{PortB}";
    public Uri WsA => new($"ws://127.0.0.1:{PortA}/ws/relay");
    public Uri WsB => new($"ws://127.0.0.1:{PortB}/ws/relay");
    public TestProcess? A { get; private set; }
    public TestProcess? B { get; private set; }
    private readonly Dictionary<string, string> _extraA = [];

    public RelayDuo(Action<Dictionary<string, string>>? envA = null) { Directory.CreateDirectory(Dir); envA?.Invoke(_extraA); }

    private Task<TestProcess> Start(string name, int port, string secret, Dictionary<string, string>? extra)
    {
        Directory.CreateDirectory(Path.Combine(Dir, name));
        var env = new Dictionary<string, string>
        {
            ["Server__SiblingsFilePath"] = Path.Combine(Dir, name, "siblings.json"), ["Server__AccessFilePath"] = Path.Combine(Dir, name, "access.json"),
            ["Admin__Secret"] = secret, ["Server__AllowInsecureSiblings"] = "true",   // plain http on 127.0.0.1: tests only
            ["Peer__Name"] = "relay-" + name, ["Peer__PublicUrl"] = "http://127.0.0.1:{port}",
        };
        if (extra != null) foreach (var (k, v) in extra) env[k] = v;
        return TestProcess.StartAsync(Dll, env, "/api/time", port);
    }

    public async Task StartAsync(bool makePublic = true)
    {
        A = await Start("a", PortA, SecretA, _extraA);
        B = await Start("b", PortB, SecretB, null);
        if (makePublic) { Assert.Equal(0, (await CliA("public")).Code); Assert.Equal(0, (await CliB("public")).Code); }
    }

    public async Task RestartAsync()
    {
        A?.Dispose(); B?.Dispose();
        A = await Start("a", PortA, SecretA, _extraA);
        B = await Start("b", PortB, SecretB, null);
    }

    public Task<(int Code, string Output)> CliA(params string[] args) => PeerPair.Cli(Dll, UrlA, SecretA, args);
    public Task<(int Code, string Output)> CliB(params string[] args) => PeerPair.Cli(Dll, UrlB, SecretB, args);

    public async Task<string> FingerprintA() => (await PeerPair.GetJson(UrlA + "/peer/identity")).GetProperty("fingerprint").GetString()!;
    public async Task<string> FingerprintB() => (await PeerPair.GetJson(UrlB + "/peer/identity")).GetProperty("fingerprint").GetString()!;

    /// <summary>Links A and B: A asks, B accepts, A learns it.</summary>
    public async Task LinkAsync()
    {
        Assert.Equal(0, (await CliA("add", "relay", UrlB)).Code);
        // Nothing waits when B already knew A (it had not forgotten it): then the link is agreed at once, and "--all" does nothing.
        Assert.Equal(0, (await CliB("accept", "relay", "--all", "--yes")).Code);
        await CliA("peer-refresh");
    }

    public void Dispose()
    {
        A?.Dispose(); B?.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Linking two relays by mutual consent, (PROTOCOL §10 "Linking servers"): they become siblings, their
/// server-to-server calls are signed by their keys, and everything survives a restart.
/// </summary>
public class PeerRelayTests
{
    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);
    private static UserIdentity Id(string who) => IdentityService.Derive(Pad(who + " one"), Pad(who + " two"), Pad(who + " three"), who);

    /// <summary>a (on relay A) sends one envelope to b (on relay B): was it delivered, and did b receive it?</summary>
    private static async Task<(bool Delivered, bool Received)> SendAcrossAsync(Uri from, Uri to, UserIdentity a, UserIdentity b, string payload)
    {
        using var ca = await RawClient.ConnectAsync(from);
        using var cb = await RawClient.ConnectAsync(to);
        await ca.RegisterAsync(a);
        await cb.RegisterAsync(b);
        await ca.SendAsync(new { type = "send", request_id = "s", envelope = new { from = a.DiscoveryId, to = b.DiscoveryId, crypto = 1, msg = payload, ts = 1 } });
        var result = await ca.ReceiveAsync();
        var delivered = result is { } r && r.TryGetProperty("delivered", out var d) && d.GetBoolean();
        var received = await cb.ReceiveAsync(1500) != null;
        return (delivered, received);
    }

    [Fact]
    public async Task TwoRelays_linkedByKeys_forwardMessagesBothWays_andStayLinkedAfterARestart()
    {
        using var duo = new RelayDuo();
        await duo.StartAsync();
        var (idA, idB) = (await duo.FingerprintA(), await duo.FingerprintB());
        var (a, b) = (Id("duo-a"), Id("duo-b"));

        // Not linked: A has no sibling, the message cannot reach b.
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "before"));

        var add = await duo.CliA("add", "relay", duo.UrlB);
        Assert.Equal(0, add.Code);
        Assert.Contains(idB, add.Output);                                   // B's fingerprint, to compare with its administrator
        Assert.Contains("relay-a", (await duo.CliB("requests", "relay")).Output);
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "still waiting"));   // asked is not agreed

        Assert.Equal(0, (await duo.CliB("accept", "relay", idA[..10])).Code);
        await duo.CliA("peer-refresh");
        Assert.Contains("linked", (await duo.CliA("list", "relay")).Output);
        Assert.Contains("linked", (await duo.CliB("list", "relay")).Output);

        // Siblings now: listed by address, with nothing secret.
        var siblings = await PeerPair.GetJson(duo.UrlA + "/api/admin/siblings", RelayDuo.SecretA);
        Assert.Equal(duo.UrlB, siblings[0].GetProperty("url").GetString());
        Assert.DoesNotContain("secret", siblings.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The signed server-to-server calls carry the message, both ways.
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "A to B"));
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsB, duo.WsA, b, a, "B to A"));

        // The approval is on disk: after a restart of both, no new request, same link.
        await duo.RestartAsync();
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "after restart"));
        Assert.Equal(idA, await duo.FingerprintA());
    }

    [Fact]
    public async Task RemovingOrBanningTheLink_stopsTheForwardingAtOnce()
    {
        using var duo = new RelayDuo();
        await duo.StartAsync();
        await duo.LinkAsync();
        var (a, b) = (Id("cut-a"), Id("cut-b"));
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "linked"));

        // B bans A: A's calls are refused (404, like a route that does not exist), whatever A still believes.
        Assert.Equal(0, (await duo.CliB("ban", "relay", (await duo.FingerprintA())[..10])).Code);
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "banned"));
        Assert.Equal(0, (await duo.CliB("unban", "relay", (await duo.FingerprintA())[..10])).Code);
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "forgotten"));   // unbanning forgets: it has to ask again
        await duo.CliA("peer-refresh");                                                          // A learns that B no longer knows it
        Assert.Contains("refused", (await duo.CliA("list", "relay")).Output);
        Assert.Equal(0, (await duo.CliB("accept", "relay", "--all", "--yes")).Code);                // (nothing waiting: it did not ask again)

        // A removes the link on its side: it no longer calls B.
        await duo.LinkAsync();
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "linked again"));
        Assert.Equal(0, (await duo.CliA("remove", "relay", (await duo.FingerprintB())[..10])).Code);
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "removed"));

        // The older address ban still wins over a link.
        await duo.LinkAsync();
        Assert.Equal((true, true), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "again"));
        Assert.Equal(0, (await duo.CliA("server-ban", duo.UrlB, "test")).Code);
        Assert.Equal((false, false), await SendAcrossAsync(duo.WsA, duo.WsB, a, b, "banned address"));
    }

    [Fact]
    public async Task Linking_needsPublicRelays_andAnAddress()
    {
        using var duo = new RelayDuo();
        await duo.StartAsync(makePublic: false);

        var privateRelay = await duo.CliA("add", "relay", duo.UrlB);
        Assert.Equal(4, privateRelay.Code);
        Assert.Contains("is private", privateRelay.Output);
        Assert.Equal(0, (await duo.CliA("public")).Code);
        Assert.Equal(0, (await duo.CliA("add", "relay", duo.UrlB)).Code);                 // A is public: the request is sent (B stays private, its choice)

        // No address, no link: the other relay would have no way to call this one.
        using var anonymous = new RelayDuo(envA: e => e["Peer__PublicUrl"] = "");
        await anonymous.StartAsync();
        var noUrl = await anonymous.CliA("add", "relay", anonymous.UrlB);
        Assert.Equal(4, noUrl.Code);
        Assert.Contains("Peer:PublicUrl", noUrl.Output);
    }

    [Fact]
    public async Task ARelayThatGivesNoAddress_cannotBeAccepted_andRequestsMustBeSigned()
    {
        using var duo = new RelayDuo();
        await duo.StartAsync();
        var rogue = PeerIdentity.LoadOrCreate(Path.Combine(duo.Dir, "rogue.json"));

        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var http = new HttpClient();
        var body = new PeerRequestBody { Kind = "relay", Name = "no address", Url = "", PublicKey = rogue.PublicKey, Ts = t, Sig = rogue.Sign(PeerSigning.RequestText("relay", "no address", "", t)) };
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync(duo.UrlB + "/peer/request", body)).StatusCode);

        var refused = await duo.CliB("accept", "relay", rogue.Fingerprint[..10]);
        Assert.Equal(4, refused.Code);
        Assert.Contains("gave no address", refused.Output);
        Assert.Contains("1 matching", (await duo.CliB("requests", "relay")).Output);   // still waiting, never trusted

        // A call to the server-to-server routes signed by a key nobody accepted is turned away like a route that does not exist.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { discovery_id = new string('a', 64) });
        using var request = new HttpRequestMessage(HttpMethod.Post, duo.UrlB + "/s2s/lookup") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        PeerSigning.Sign(request, rogue, "/s2s/lookup", payload);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(request)).StatusCode);
    }
}
