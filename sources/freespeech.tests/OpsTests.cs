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

namespace FreeSpeech.Tests;

/// <summary>
/// Run-time log levels, the in-memory log tail and per-identity trace of the relay server (PROTOCOL §10 "Operations"):
/// changed through the admin API, effective at once, and never carrying content or full identifiers.
/// </summary>
public class OpsTests : IClassFixture<ServerFixture>
{
    private const string Secret = "admin-test-secret";
    private readonly ServerFixture _s;
    public OpsTests(ServerFixture s) => _s = s;

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private async Task<HttpResponseMessage> Admin(HttpMethod method, string path, object? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var client = new HttpClient();
        var req = new HttpRequestMessage(method, _s.Http + path);
        req.Headers.Add("X-Admin-Secret", Secret);
        if (body != null) req.Content = JsonContent.Create(body);
        tweak?.Invoke(req);
        return await client.SendAsync(req);
    }

    private async Task<string> Tail(string query = "lines=500") =>
        await (await Admin(HttpMethod.Get, "/api/admin/log/tail?" + query)).Content.ReadAsStringAsync();

    private async Task SetLevel(string category, string level) =>
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/log/level", new { category, level })).StatusCode);

    /// <summary>Registers a and b, then a sends one envelope to b carrying <paramref name="payload"/>.</summary>
    private async Task SendOneAsync(UserIdentity a, UserIdentity b, string payload)
    {
        using var ca = await RawClient.ConnectAsync(_s.WsUri);
        using var cb = await RawClient.ConnectAsync(_s.WsUri);
        await ca.RegisterAsync(a);
        await cb.RegisterAsync(b);
        await ca.SendAsync(new { type = "send", request_id = "s", envelope = new { from = a.DiscoveryId, to = b.DiscoveryId, crypto = 1, msg = payload, ts = 1 } });
        Assert.NotNull(await ca.ReceiveAsync());   // send_result
        Assert.NotNull(await cb.ReceiveAsync());   // the message itself
    }

    [Fact]
    public async Task Admin_isRefusedWhenTheCallIsNotFromTheMachineItself()
    {
        // Behind nginx the forwarded client address is what counts: a call that says it comes from the internet is turned away,
        // even with the right secret.
        var remote = await Admin(HttpMethod.Get, "/api/admin/status", tweak: r => r.Headers.Add("X-Forwarded-For", "203.0.113.9"));
        Assert.Equal(HttpStatusCode.Unauthorized, remote.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Get, "/api/admin/log", tweak: r => r.Headers.Add("X-Forwarded-For", "203.0.113.9"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Get, "/api/admin/status")).StatusCode);
    }

    [Fact]
    public async Task Info_offersTheLicenceAndTheSourceToAnyUser_withoutASecret()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(_s.Http + "/api/info");   // AGPL section 13: the users of a network service may get its source
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AGPL-3.0-or-later", info.GetProperty("license").GetString());
        Assert.True(info.TryGetProperty("source", out _));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("client_access", body);   // nothing about the server's state
        Assert.DoesNotContain("is_public", body);
    }

    [Fact]
    public async Task LogRoutes_refuseAWrongSecret()
    {
        using var client = new HttpClient();
        foreach (var path in new[] { "/api/admin/log", "/api/admin/log/tail", "/api/admin/trace" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _s.Http + path);
            req.Headers.Add("X-Admin-Secret", "nope");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public async Task LogLevel_canBeRaisedAndResetWithoutRestart_andNeverShowsThePayload()
    {
        var a = NewIdentity("ops-a1");
        var b = NewIdentity("ops-b1");
        const string payload = "UNIQUE-CIPHERTEXT-MARKER-0123456789";

        await SendOneAsync(a, b, payload);
        Assert.DoesNotContain("Envelope (crypto=", await Tail());   // debug lines are off by default

        await SetLevel("relay", "debug");
        try
        {
            await SendOneAsync(a, b, payload);
            var log = await Tail();
            Assert.Contains("Envelope (crypto=1, " + payload.Length + " characters)", log);   // the shape of the envelope...
            Assert.DoesNotContain(payload, log);                                              // ...never its content
            Assert.DoesNotContain(a.DiscoveryId, log);                                        // identifiers only shortened
            Assert.DoesNotContain(b.DiscoveryId, log);
        }
        finally { Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, "/api/admin/log/level")).StatusCode); }

        var before = (await Tail()).Split('\n').Count(l => l.Contains("Envelope (crypto="));
        await SendOneAsync(a, b, payload);
        Assert.Equal(before, (await Tail()).Split('\n').Count(l => l.Contains("Envelope (crypto=")));   // reset: no new debug line
    }

    [Fact]
    public async Task LogLevel_rejectsAnUnknownLevel_andListsAliases()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/log/level", new { category = "relay", level = "loud" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/log/level", new { category = "relay", level = "3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/log/level", new { category = "", level = "debug" })).StatusCode);

        var state = await (await Admin(HttpMethod.Get, "/api/admin/log")).Content.ReadAsStringAsync();
        foreach (var alias in new[] { "relay", "s2s", "push", "access", "flags" }) Assert.Contains($"\"{alias}\"", state);
    }

    [Fact]
    public async Task Tail_filtersByLevel_andLimitsTheLineCount()
    {
        await SendOneAsync(NewIdentity("ops-a2"), NewIdentity("ops-b2"), "x");
        var all = (await Tail("lines=500")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(all);
        Assert.Single((await Tail("lines=1")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("[INF]", await Tail("lines=500&level=warning"));
    }

    [Fact]
    public async Task Tail_followStreamsNewLinesAsTheyHappen()
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var req = new HttpRequestMessage(HttpMethod.Get, _s.Http + "/api/admin/log/tail?lines=0&follow=true");
        req.Headers.Add("X-Admin-Secret", Secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var me = NewIdentity("ops-follow");
        using var c = await RawClient.ConnectAsync(_s.WsUri);
        await c.RegisterAsync(me);   // logs "Registered ..." at Information

        string? line;
        do { line = await reader.ReadLineAsync(cts.Token); } while (line != null && !line.Contains("Registered " + me.DiscoveryId[..8]));
        Assert.NotNull(line);
    }

    [Fact]
    public async Task Trace_followsOneIdentityOnly_withShortIdsAndNoContent_andExpiresByItself()
    {
        var a = NewIdentity("ops-trace-a");
        var b = NewIdentity("ops-trace-b");
        var other = NewIdentity("ops-trace-other");

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/trace", new { id = "abc", minutes = 5 })).StatusCode);   // too short to mean anything
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/trace", new { id = a.DiscoveryId[..12], minutes = 5 })).StatusCode);
        try
        {
            const string payload = "TRACE-PAYLOAD-MARKER";
            await SendOneAsync(a, b, payload);
            await SendOneAsync(other, b, payload);   // someone else sending to b: not traced, only a is

            var traced = (await Tail()).Split('\n').Where(l => l.Contains("] trace:")).ToList();
            Assert.Contains(traced, l => l.Contains(a.DiscoveryId[..8] + " connected"));
            Assert.Contains(traced, l => l.Contains($"send to {b.DiscoveryId[..8]} delivered=True"));
            Assert.DoesNotContain(traced, l => l.Contains(other.DiscoveryId[..8]));
            Assert.DoesNotContain(payload, string.Join('\n', traced));
            Assert.DoesNotContain(a.DiscoveryId, string.Join('\n', traced));   // never the full identifier
        }
        finally { Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Delete, "/api/admin/trace?id=all")).StatusCode); }

        // A trace ends on its own: 0.02 minutes is about a second.
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/trace", new { id = b.DiscoveryId[..10], minutes = 0.02 })).StatusCode);
        Assert.Contains(b.DiscoveryId[..10], await (await Admin(HttpMethod.Get, "/api/admin/trace")).Content.ReadAsStringAsync());
        await Task.Delay(1800);
        Assert.DoesNotContain(b.DiscoveryId[..10], await (await Admin(HttpMethod.Get, "/api/admin/trace")).Content.ReadAsStringAsync());
        Assert.Contains("expired", await Tail());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(HttpMethod.Delete, $"/api/admin/trace?id={b.DiscoveryId[..10]}")).StatusCode);
    }

    [Fact]
    public async Task Tail_neverContainsAFullIdentifierAtTheDefaultLevel()
    {
        var a = NewIdentity("ops-a3");
        var b = NewIdentity("ops-b3");
        await SendOneAsync(a, b, "hello");
        var log = await Tail();
        Assert.DoesNotContain(a.DiscoveryId, log);
        Assert.DoesNotContain(b.DiscoveryId, log);
        Assert.DoesNotContain("from " + a.DiscoveryId[..8] + " to " + b.DiscoveryId[..8], log);   // who writes to whom is not at Information level
    }

    [Fact]
    public async Task CommandLine_logAndTrace_work()
    {
        var me = NewIdentity("ops-cli");
        Assert.Equal(0, (await Cli("log", "level", "relay", "debug")).Code);
        Assert.Contains("relay", (await Cli("log")).Output);
        Assert.Equal(0, (await Cli("trace", me.DiscoveryId[..10], "--minutes", "5")).Code);
        Assert.Contains(me.DiscoveryId[..10], (await Cli("trace", "list")).Output);

        using var c = await RawClient.ConnectAsync(_s.WsUri);
        await c.RegisterAsync(me);
        Assert.Contains(me.DiscoveryId[..8] + " connected", (await Cli("log", "tail", "-n", "200")).Output);

        Assert.Equal(0, (await Cli("trace", "stop", "all")).Code);
        Assert.Equal(0, (await Cli("log", "reset")).Code);
        Assert.Equal(1, (await Cli("log", "level", "relay")).Code);       // missing level: usage error
    }

    [Fact]
    public async Task CommandLine_version_isTheRunningServers()
    {
        var (code, output) = await Cli("version");
        Assert.Equal(0, code);
        Assert.Contains("FreeSpeech.Relay", output);
        Assert.Matches(@"FreeSpeech\.Relay \d+\.\d+\.\d+ of \d{4}-\d{2}-\d{2} ", output);   // version and release date, from version.props
        Assert.Contains("running since", output);
    }

    [Fact]
    public async Task CommandLine_prompt_runsTypedCommands_andKeepsGoingAfterAnError()
    {
        var script = "version\nnope\nlog\n\"status\"\nexit\n";
        var (code, output) = await CliWithInput(script, "--shell");
        Assert.Equal(0, code);
        Assert.Contains("relay>", output);   // each program names its own prompt
        Assert.Contains("running since", output);            // greeting and the version command
        Assert.Contains("Unknown command 'nope'", output);   // an error does not end the session
        Assert.Contains("is_public", output);                // the quoted command still ran
    }

    private Task<(int Code, string Output)> Cli(params string[] args) => CliWithInput(null, args);

    private async Task<(int Code, string Output)> CliWithInput(string? stdin, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false };
        info.ArgumentList.Add(ServerFixture.ServerDll());
        info.ArgumentList.Add("admin");
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(_s.Http);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["FS_ADMIN_SECRET"] = Secret;
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (stdin != null) { await process.StandardInput.WriteAsync(stdin); process.StandardInput.Close(); }
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
