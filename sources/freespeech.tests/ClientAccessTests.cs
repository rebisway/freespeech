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
using System.Text.Json;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>
/// Access control of a private server (PROTOCOL §10 "Access control") and runtime management of push gateways / banned servers,
/// all through the real server process and its admin API and command line — no restart between a change and its effect.
/// </summary>
public partial class ClientAccessTests : IClassFixture<ServerFixture>
{
    private const string Secret = "admin-test-secret";
    private readonly ServerFixture _s;
    public ClientAccessTests(ServerFixture s) => _s = s;

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static string Type(JsonElement? e) => e is { } v && v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "(nothing)";
    private static string Message(JsonElement? e) => e is { } v && v.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

    private static async Task<HttpResponseMessage> Admin(string http, HttpMethod method, string path, object? body = null, string secret = Secret)
    {
        using var client = new HttpClient();
        var req = new HttpRequestMessage(method, http + path);
        req.Headers.Add("X-Admin-Secret", secret);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private Task<HttpResponseMessage> Admin(HttpMethod method, string path, object? body = null, string secret = Secret) => Admin(_s.Http, method, path, body, secret);

    private async Task SetMode(string mode) => Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/access/mode", new { mode })).StatusCode);

    private async Task<string> Invite(string note = "test")
    {
        var response = await Admin(HttpMethod.Post, "/api/admin/invitations", new { note, days = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<JsonElement?> RegisterAsync(UserIdentity me, string? invitation = null, bool ask = false, string? name = null, string? note = null, Uri? ws = null)
    {
        using var c = await RawClient.ConnectAsync(ws ?? _s.WsUri);
        await c.SendAsync(new { type = "register", discovery_id = me.DiscoveryId, enc_key = Convert.ToBase64String(me.EncryptionPublicKey), sign_key = Convert.ToBase64String(me.SigningPublicKey), invitation, request_access = ask ? true : (bool?)null, name, note, request_id = "1" });
        var challenge = await c.ReceiveAsync();
        var data = System.Text.Encoding.UTF8.GetBytes($"freespeech-register-v1|{challenge!.Value.GetProperty("nonce").GetString()}|{me.DiscoveryId}");
        await c.SendAsync(new { type = "register_proof", signature = Convert.ToBase64String(FreeSpeech.Client.Crypto.Primitives.Ed25519Sign(me.SigningPrivateKey, data)), request_id = "2" });
        return await c.ReceiveAsync();
    }

    [Fact]
    public async Task AdminRoutes_refuseAWrongSecret()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Get, "/api/admin/access", secret: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Post, "/api/admin/invitations", new { }, secret: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = "00" }, secret: "")).StatusCode);
    }

    [Fact]
    public void VerificationCode_isTheSameOnThePhoneAndOnTheServer_inGroupsOfFour()
    {
        var id = "3fa9c1b27e40aabbccddeeff00112233445566778899aabbccddeeff00112233";
        Assert.Equal("3FA9-C1B2-7E40", FreeSpeech.Client.FreeSpeechClient.VerificationCode(id));
        Assert.Equal("3FA9-C1B2-7E40", FreeSpeech.Ops.AdminCliKit.VerificationCode(id));
    }

    [Fact]
    public async Task ConnectedList_showsWhoIsHereNow_andLetsTheAdministratorAllowThemBeforeGoingPrivate()
    {
        await SetMode("open");
        var here = NewIdentity("here-1");
        var banned = NewIdentity("here-banned");
        using var a = await RawClient.ConnectAsync(_s.WsUri);
        using var b = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await a.RegisterAsync(here)));
        Assert.Equal("registered", Type(await b.RegisterAsync(banned)));
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = banned.DiscoveryId, reason = "test" })).StatusCode);

        var list = await (await Admin(HttpMethod.Get, "/api/admin/connected")).Content.ReadFromJsonAsync<JsonElement>();
        var mine = list.EnumerateArray().First(e => e.GetProperty("id").GetString() == here.DiscoveryId);
        Assert.False(mine.GetProperty("allowed").GetBoolean());
        Assert.Equal(1, mine.GetProperty("connections").GetInt32());

        // Allowing the people who are here (the banned one is left out) before the server goes "on approval".
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients/allow-connected", new { })).StatusCode);
        var allowed = (await (await Admin(HttpMethod.Get, "/api/admin/access")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowed").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains(here.DiscoveryId, allowed);
        Assert.DoesNotContain(banned.DiscoveryId, allowed);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Get, "/api/admin/connected", secret: "nope")).StatusCode);
    }

    [Fact]
    public async Task AConnectedClient_isToldWhenTheAdministratorChangesWhoMaySignUp()
    {
        await SetMode("open");
        try
        {
            using var live = await RawClient.ConnectAsync(_s.WsUri);
            Assert.Equal("open", (await live.RegisterAsync(NewIdentity("told-1")))!.Value.GetProperty("access").GetString());

            await SetMode("allowlist");
            var told = await live.ReceiveAsync();
            Assert.Equal("server_state", Type(told));
            Assert.Equal("allowlist", told!.Value.GetProperty("access").GetString());   // the app shows the server as private from now on, with no reconnection

            await SetMode("open");
            Assert.Equal("open", (await live.ReceiveAsync())!.Value.GetProperty("access").GetString());
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task OpenServer_takesEveryoneButTheBanned_andCutsABannedClientAtOnce()
    {
        await SetMode("open");
        var me = NewIdentity("open-user");
        var first = await RegisterAsync(me);
        Assert.Equal("registered", Type(first));
        Assert.Equal("open", first!.Value.GetProperty("access").GetString());   // "public server" for the app

        using var live = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await live.RegisterAsync(me)));

        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = me.DiscoveryId, reason = "spam" })).StatusCode);
        Assert.Null(await live.ReceiveAsync());   // the live connection was closed by the server
        var refused = await RegisterAsync(me);
        Assert.Equal("error", Type(refused));
        Assert.Equal("access_refused", Message(refused));

        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, $"/api/admin/clients/ban?id={me.DiscoveryId}")).StatusCode);
        Assert.Equal("registered", Type(await RegisterAsync(me)));
    }

    [Fact]
    public async Task PrivateServer_refusesStrangers_andAcceptsAllowedIdentities()
    {
        await SetMode("allowlist");
        try
        {
            var friend = NewIdentity("friend-allowed");
            var stranger = NewIdentity("stranger");

            var refused = await RegisterAsync(stranger);
            Assert.Equal("error", Type(refused));
            Assert.Equal("private_server", Message(refused));
            Assert.Equal("private_server", Message(await RegisterAsync(friend)));

            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients", new { id = friend.DiscoveryId, note = "Anna" })).StatusCode);
            var registered = await RegisterAsync(friend);
            Assert.Equal("registered", Type(registered));
            Assert.Equal("allowlist", registered!.Value.GetProperty("access").GetString());   // the app can say "private server"
            Assert.Equal("private_server", Message(await RegisterAsync(stranger)));   // allowing one identity does not open the door to others
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task RemovingAnAllowedClient_cutsItAndRefusesItAgain()
    {
        await SetMode("allowlist");
        try
        {
            var me = NewIdentity("to-remove");
            await Admin(HttpMethod.Post, "/api/admin/clients", new { id = me.DiscoveryId });
            using var live = await RawClient.ConnectAsync(_s.WsUri);
            Assert.Equal("registered", Type(await live.RegisterAsync(me)));

            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, $"/api/admin/clients?id={me.DiscoveryId}")).StatusCode);
            Assert.Null(await live.ReceiveAsync());
            Assert.Equal("private_server", Message(await RegisterAsync(me)));
            Assert.Equal(HttpStatusCode.NotFound, (await Admin(HttpMethod.Delete, $"/api/admin/clients?id={me.DiscoveryId}")).StatusCode);
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task Invitation_worksOnce_thenTheIdentityStaysAllowed()
    {
        await SetMode("allowlist");
        try
        {
            var token = await Invite("for Bob");
            var bob = NewIdentity("bob-invited");
            var eve = NewIdentity("eve-late");

            Assert.Equal("private_server", Message(await RegisterAsync(bob, "fs-inv-wrong")));
            Assert.Equal("registered", Type(await RegisterAsync(bob, token)));
            Assert.Equal("registered", Type(await RegisterAsync(bob)));                       // reconnects without the token
            Assert.Equal("private_server", Message(await RegisterAsync(eve, token)));       // the token is spent
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task RevokedInvitation_isRefused_andListedInvitationsNeverShowTheToken()
    {
        await SetMode("allowlist");
        try
        {
            var token = await Invite("will be revoked");
            var json = await (await Admin(HttpMethod.Get, "/api/admin/access")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, json);
            Assert.DoesNotContain(token[7..], json);

            var id = JsonDocument.Parse(json).RootElement.GetProperty("invitations").EnumerateArray()
                .First(i => i.GetProperty("note").GetString() == "will be revoked").GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, $"/api/admin/invitations?id={id}")).StatusCode);
            Assert.Equal("private_server", Message(await RegisterAsync(NewIdentity("late-guest"), token)));
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task ABannedClient_isRefusedEvenWithAValidInvitation()
    {
        await SetMode("allowlist");
        try
        {
            var banned = NewIdentity("banned-guest");
            await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = banned.DiscoveryId });
            Assert.Equal("access_refused", Message(await RegisterAsync(banned, await Invite())));
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task PushGateway_isUsableOnlyWhenLinked_andBanningItsAddressStopsItAtOnce()
    {
        var linked = $"http://127.0.0.1:{_s.GatewayPort}";   // the fixture's relay is linked with this gateway
        var me = NewIdentity("push-user");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        Assert.Equal("registered", Type(await c.RegisterAsync(me)));

        async Task<bool> TryRegister()
        {
            await c.SendAsync(new { type = "push_register", platform = "fcm", token = "device-token", request_id = "p" });
            return (await c.ReceiveAsync())!.Value.GetProperty("ok").GetBoolean();
        }

        Assert.True(await TryRegister());   // the relay is linked with a gateway

        // Banning the only linked gateway leaves none: push is off until the ban is lifted.
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/blacklist", new { url = linked, reason = "abuse" })).StatusCode);
        Assert.False(await TryRegister());
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, $"/api/admin/blacklist?url={Uri.EscapeDataString(linked)}")).StatusCode);
        Assert.True(await TryRegister());
    }

    [Fact]
    public async Task AddressBlacklist_listsBannedAddresses_andForgetsThemOnUnban()
    {
        const string url = "https://sibling.example.org";
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/blacklist", new { url, reason = "test" })).StatusCode);
        Assert.Contains(url, await (await Admin(HttpMethod.Get, "/api/admin/blacklist")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, $"/api/admin/blacklist?url={Uri.EscapeDataString(url)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(HttpMethod.Delete, $"/api/admin/blacklist?url={Uri.EscapeDataString(url)}")).StatusCode);
    }
    [Fact]
    public async Task ClientLibrary_reportsAPrivateServer_andConnectsOnceGivenAnInvitation()
    {
        await SetMode("allowlist");
        try
        {
            var me = NewIdentity("lib-user");
            await using var pool = new FreeSpeech.Client.Relay.RelayPool(me.DiscoveryId, new FreeSpeech.Client.Relay.RegisterAuth(me.EncryptionPublicKey, me.SigningPublicKey, me.SigningPrivateKey));

            await pool.AddOwnAsync(_s.Http);
            Assert.False(pool.IsConnected(_s.Http));
            Assert.Equal("private_server", pool.RefusalOf(_s.Http));

            await pool.AddOwnAsync(_s.Http, await Invite("for the library test"));   // the same server, now with the token
            Assert.True(pool.IsConnected(_s.Http));
            Assert.Null(pool.RefusalOf(_s.Http));

            await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = me.DiscoveryId });
            await Task.Delay(300);
            Assert.False(pool.IsConnected(_s.Http));
            await pool.MaintainAsync();
            Assert.Equal("access_refused", pool.RefusalOf(_s.Http));
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task CommandLine_managesTheRunningServer()
    {
        var me = NewIdentity("cli-user");
        var (code, output) = await Cli(_s.Http, Secret, "status");
        Assert.Equal(0, code);
        Assert.Contains("client_access", output);

        Assert.Equal(0, (await Cli(_s.Http, Secret, "access", "allowlist")).Code);
        try
        {
            Assert.Equal("private_server", Message(await RegisterAsync(me)));
            Assert.Equal(0, (await Cli(_s.Http, Secret, "allow", me.DiscoveryId, "added", "by", "cli")).Code);
            Assert.Equal("registered", Type(await RegisterAsync(me)));
            Assert.Contains("added by cli", (await Cli(_s.Http, Secret, "clients")).Output);

            var invite = await Cli(_s.Http, Secret, "invite", "for", "Carl", "--days", "2");
            Assert.Equal(0, invite.Code);
            var token = JsonDocument.Parse(invite.Output).RootElement.GetProperty("token").GetString();
            Assert.Equal("registered", Type(await RegisterAsync(NewIdentity("cli-carl"), token)));

            Assert.Equal(0, (await Cli(_s.Http, Secret, "ban", me.DiscoveryId, "cli", "test")).Code);
            Assert.Equal("access_refused", Message(await RegisterAsync(me)));
            Assert.Equal(0, (await Cli(_s.Http, Secret, "unban", me.DiscoveryId)).Code);
        }
        finally { await Cli(_s.Http, Secret, "access", "open"); }

        Assert.Equal(4, (await Cli(_s.Http, "wrong-secret", "status")).Code);          // refused
        Assert.Equal(1, (await Cli(_s.Http, Secret, "no-such-command")).Code);         // usage
        Assert.Equal(3, (await Cli("http://127.0.0.1:1", Secret, "status")).Code);     // unreachable
    }

    [Fact]
    public async Task Access_survivesARestart_andACorruptedFileFailsClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var me = NewIdentity("restart-user");
        try
        {
            var (process, http, ws) = await StartServer(dir);
            try
            {
                Assert.Equal(HttpStatusCode.OK, (await Admin(http, HttpMethod.Post, "/api/admin/access/mode", new { mode = "allowlist" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Admin(http, HttpMethod.Post, "/api/admin/clients", new { id = me.DiscoveryId })).StatusCode);
            }
            finally { Stop(process); }

            (process, http, ws) = await StartServer(dir);
            try
            {
                var status = await (await Admin(http, HttpMethod.Get, "/api/admin/status")).Content.ReadAsStringAsync();
                Assert.Contains("\"client_access\":\"allowlist\"", status);
                Assert.Contains("\"allowed_client_count\":1", status);
            }
            finally { Stop(process); }

            // A damaged access file must never turn a private server into an open one.
            File.WriteAllText(Path.Combine(dir, "access.json"), "{ not json");
            (process, http, ws) = await StartServer(dir);
            try
            {
                var status = await (await Admin(http, HttpMethod.Get, "/api/admin/status")).Content.ReadAsStringAsync();
                Assert.Contains("\"client_access\":\"allowlist\"", status);
            }
            finally { Stop(process); }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(int Code, string Output)> Cli(string url, string secret, params string[] args)
    {
        // Input is redirected and closed at once: a command that wants a confirmation must not wait for a keyboard that is not there.
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(ServerFixture.ServerDll());
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

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<(Process Process, string Http, Uri Ws)> StartServer(string stateDir)
    {
        var port = FreePort();
        var info = new ProcessStartInfo("dotnet", $"\"{ServerFixture.ServerDll()}\" --urls http://127.0.0.1:{port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["Server__SiblingsFilePath"] = Path.Combine(stateDir, "siblings.json");
        info.Environment["Server__AccessFilePath"] = Path.Combine(stateDir, "access.json");
        info.Environment["Admin__Secret"] = Secret;
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 40; i++)
        {
            try { if ((await http.GetAsync($"http://127.0.0.1:{port}/api/time")).IsSuccessStatusCode) return (process, $"http://127.0.0.1:{port}", new Uri($"ws://127.0.0.1:{port}/ws/relay")); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        Stop(process);
        throw new TimeoutException("The test server did not start.");
    }

    private static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
