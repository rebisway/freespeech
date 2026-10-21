/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.Concurrent;
using FreeSpeech.Client;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Tests;

/// <summary>The client library's diagnostic trace: off by default, switchable at run time, and never carrying content or full identifiers.</summary>
[Collection("ClientLog")]   // the level is process-wide: these tests must not overlap each other
public class ClientLogTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _s;
    public ClientLogTests(ServerFixture s) => _s = s;

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static RelayPool PoolFor(UserIdentity me) =>
        new(me.DiscoveryId, new RegisterAuth(me.EncryptionPublicKey, me.SigningPublicKey, me.SigningPrivateKey));

    [Fact]
    public async Task Level_offByDefault_writesNothing_andCanBeSwitchedOnAtRunTime()
    {
        Assert.Equal(ClientLogLevel.Off, ClientLog.Level);
        var lines = new ConcurrentQueue<string>();
        void Sink(DateTime t, ClientLogLevel l, string c, string text) => lines.Enqueue($"{l}|{c}|{text}");
        ClientLog.Written += Sink;
        try
        {
            var me = NewIdentity("log-off");
            await using (var pool = PoolFor(me)) await pool.AddOwnAsync(_s.Http);
            Assert.DoesNotContain(lines, l => l.Contains(me.DiscoveryId[..8]));

            ClientLog.Level = ClientLogLevel.Info;
            var me2 = NewIdentity("log-on");
            await using (var pool = PoolFor(me2)) await pool.AddOwnAsync(_s.Http);
            Assert.Contains(lines, l => l.StartsWith("Info|relay|") && l.Contains("registered as " + me2.DiscoveryId[..8]));
        }
        finally { ClientLog.Level = ClientLogLevel.Off; ClientLog.Written -= Sink; }
    }

    [Fact]
    public async Task Trace_neverCarriesContentOrFullIdentifiers()
    {
        var lines = new ConcurrentQueue<string>();
        void Sink(DateTime t, ClientLogLevel l, string c, string text) => lines.Enqueue(text);
        ClientLog.Written += Sink;
        ClientLog.Level = ClientLogLevel.Trace;
        try
        {
            var a = NewIdentity("log-a");
            var b = NewIdentity("log-b");
            await using var pa = PoolFor(a);
            await using var pb = PoolFor(b);
            await pa.AddOwnAsync(_s.Http);
            await pb.AddOwnAsync(_s.Http);
            var received = new TaskCompletionSource();
            pb.OnMessage += _ => received.TrySetResult();

            const string secret = "SECRET-CIPHERTEXT-MARKER-42";
            await pa.SendAsync([_s.Http], new FreeSpeech.Client.Relay.Envelope { From = a.DiscoveryId, To = b.DiscoveryId, Crypto = 1, Msg = secret, Ts = 1 });
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var all = string.Join('\n', lines);
            Assert.Contains("send to " + b.DiscoveryId[..8], all);
            Assert.Contains("message from " + a.DiscoveryId[..8], all);
            Assert.DoesNotContain(secret, all);
            Assert.DoesNotContain(a.DiscoveryId, all);
            Assert.DoesNotContain(b.DiscoveryId, all);
            Assert.DoesNotContain("http://127.0.0.1", all);   // hosts only, no addresses with ports/paths
        }
        finally { ClientLog.Level = ClientLogLevel.Off; ClientLog.Written -= Sink; }
    }

    [Fact]
    public async Task ARefusedRegistration_isTraced_withTheServersReason()
    {
        var lines = new ConcurrentQueue<string>();
        void Sink(DateTime t, ClientLogLevel l, string c, string text) => lines.Enqueue($"{l}|{text}");
        ClientLog.Written += Sink;
        ClientLog.Level = ClientLogLevel.Warning;
        try
        {
            using var http = new HttpClient();
            var set = new HttpRequestMessage(HttpMethod.Post, _s.Http + "/api/admin/access/mode") { Content = System.Net.Http.Json.JsonContent.Create(new { mode = "allowlist" }) };
            set.Headers.Add("X-Admin-Secret", "admin-test-secret");
            Assert.True((await http.SendAsync(set)).IsSuccessStatusCode);
            try
            {
                await using var pool = PoolFor(NewIdentity("log-refused"));
                await pool.AddOwnAsync(_s.Http);
                Assert.Contains(lines, l => l.StartsWith("Warning|") && l.Contains("registration refused (private_server)"));
            }
            finally
            {
                var reset = new HttpRequestMessage(HttpMethod.Post, _s.Http + "/api/admin/access/mode") { Content = System.Net.Http.Json.JsonContent.Create(new { mode = "open" }) };
                reset.Headers.Add("X-Admin-Secret", "admin-test-secret");
                await http.SendAsync(reset);
            }
        }
        finally { ClientLog.Level = ClientLogLevel.Off; ClientLog.Written -= Sink; }
    }

    [Theory]
    [InlineData("off", ClientLogLevel.Off)]
    [InlineData("INFO", ClientLogLevel.Info)]
    [InlineData("information", ClientLogLevel.Info)]
    [InlineData("warn", ClientLogLevel.Warning)]
    [InlineData("trace", ClientLogLevel.Trace)]
    public void TryParse_acceptsNamesInAnyCase(string text, ClientLogLevel expected)
    {
        Assert.True(ClientLog.TryParse(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("loud")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_refusesAnythingElse(string? text) => Assert.False(ClientLog.TryParse(text, out _));

    [Fact]
    public void Short_and_Host_keepOnlyWhatAnOperatorNeeds()
    {
        Assert.Equal("0123abcd", ClientLog.Short("0123abcd4567ef89"));
        Assert.Equal("-", ClientLog.Short(null));
        Assert.Equal("fs.example.org", ClientLog.Host("wss://fs.example.org:8443/ws/relay"));
        Assert.Equal("?", ClientLog.Host("not a url"));
    }

    [Fact]
    public void ConsoleOptions_areTakenOutOfTheArguments_andSetTheLevel()
    {
        var file = Path.Combine(Path.GetTempPath(), "fs-log-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var rest = FreeSpeech.Console.ConsoleLog.Setup(["alice", "--log", "debug", "http://localhost:5299", "--log-file", file]);
            Assert.Equal(["alice", "http://localhost:5299"], rest);
            Assert.Equal(ClientLogLevel.Debug, ClientLog.Level);

            ClientLog.Info("test", "hello file");
            Assert.Contains("[INF] test: hello file", File.ReadAllText(file));

            Assert.Equal("Log level set to warning.", FreeSpeech.Console.ConsoleLog.Command(["log", "warning"]));
            Assert.Equal(ClientLogLevel.Warning, ClientLog.Level);
            Assert.StartsWith("Levels:", FreeSpeech.Console.ConsoleLog.Command(["log", "loud"]));
        }
        finally
        {
            ClientLog.Level = ClientLogLevel.Off;
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
