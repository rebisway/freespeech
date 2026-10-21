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
using FreeSpeech.Client;
using FreeSpeech.Client.Discovery;
using FreeSpeech.Ops;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>A real directory and a real relay linked by keys, the relay telling the directory how it is every few seconds.</summary>
public sealed class DirectoryWorld : IAsyncLifetime
{
    public const string DirectorySecret = "directory-admin-secret-0123456789";
    public const string RelaySecret = "relay-for-directory-secret-0123456789";
    public static readonly string DirectoryDll = TestProcess.Dll("freespeech.directory", "FreeSpeech.Directory.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-dirworld-" + Guid.NewGuid().ToString("N"));
    public TestProcess Directory { get; private set; } = null!;
    public TestProcess Relay { get; private set; } = null!;
    public string Key { get; private set; } = "";

    public Task<(int Code, string Output)> DirectoryCli(params string[] args) => PeerPair.Cli(DirectoryDll, Directory.Http, DirectorySecret, args);
    public Task<(int Code, string Output)> RelayCli(params string[] args) => PeerPair.Cli(PeerPair.RelayDll, Relay.Http, RelaySecret, args);

    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "directory-content"));
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "relay-content"));
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "relay"));
        Directory = await TestProcess.StartAsync(DirectoryDll, new Dictionary<string, string>
        {
            ["Directory__StatePath"] = Path.Combine(_root, "directory"), ["Admin__Secret"] = DirectorySecret, ["ASPNETCORE_CONTENTROOT"] = Path.Combine(_root, "directory-content"),
            ["Peer__PublicUrl"] = "http://127.0.0.1:{port}",
        });
        Key = (await PeerPair.GetJson(Directory.Http + "/peer/identity")).GetProperty("public_key").GetString()!;

        Relay = await TestProcess.StartAsync(PeerPair.RelayDll, new Dictionary<string, string>
        {
            ["Server__SiblingsFilePath"] = Path.Combine(_root, "relay", "siblings.json"), ["Server__AccessFilePath"] = Path.Combine(_root, "relay", "access.json"),
            ["Server__SettingsFilePath"] = Path.Combine(_root, "relay", "settings.json"), ["Server__LimitsFilePath"] = Path.Combine(_root, "relay", "limits.json"),
            ["ASPNETCORE_CONTENTROOT"] = Path.Combine(_root, "relay-content"), ["Admin__Secret"] = RelaySecret, ["Server__MaxConnections"] = "10",
            ["Peer__Name"] = "relay-for-directory", ["Peer__PublicUrl"] = "http://127.0.0.1:{port}", ["Directory__HeartbeatSeconds"] = "5",
        }, "/api/time");
        Assert.Equal(0, (await RelayCli("public")).Code);
        Assert.Equal(0, (await RelayCli("settings", "set", "country", "fr")).Code);

        // The two administrators agree: the relay asks, the directory accepts.
        Assert.Equal(0, (await RelayCli("add", "directory", Directory.Http)).Code);
        Assert.Equal(0, (await DirectoryCli("accept", "--all", "--yes")).Code);
        await RelayCli("peer-refresh");
        Assert.True(await UntilAsync(async () => (await ListAsync()).Contains(Relay.Http)), "the directory never offered the relay");
    }

    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, int seconds = 40)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (await condition()) return true; await Task.Delay(500); }
        return await condition();
    }

    /// <summary>The raw answer of the directory to a client, and the relays of it when its signature is good.</summary>
    public async Task<(JsonElement Raw, IReadOnlyList<string> Relays)> AskAsync(string query = "")
    {
        using var http = new HttpClient();
        var text = await http.GetStringAsync(Directory.Http + "/directory/servers" + query);
        return (JsonDocument.Parse(text).RootElement.Clone(), DirectoryClient.Verify(text, Key, DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("the list is not signed by the directory"));
    }

    public async Task<IReadOnlyList<string>> ListAsync() => (await AskAsync()).Relays;

    public Task DisposeAsync()
    {
        Relay?.Dispose(); Directory?.Dispose();
        try { System.IO.Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

public class DirectoryWorldTests : IClassFixture<DirectoryWorld>, IDisposable
{
    private readonly DirectoryWorld _w;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public DirectoryWorldTests(DirectoryWorld w) => _w = w;

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { System.IO.Directory.Delete(d, true); } catch (IOException) { }
    }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private FreeSpeechClient NewClient(string tag, string defaultServer, IReadOnlyList<DirectoryEntry> directories, string? country = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-dirclient-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, tag), ServerUrl = defaultServer, Directories = directories, Country = country });
        c.CreateIdentity(Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3"), tag);
        c.Initialize();
        _clients.Add(c);
        return c;
    }

    [Fact]
    public async Task A_linked_public_relay_is_offered_with_its_country_and_what_the_directory_administrator_needs()
    {
        var (raw, relays) = await _w.AskAsync("?country=FR&count=3");
        Assert.Equal([_w.Relay.Http], relays);
        Assert.Equal("FR", raw.GetProperty("servers")[0].GetProperty("country").GetString());   // declared with "settings set country fr", kept as a code

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _w.Directory.Http + "/admin/servers");
        request.Headers.Add("X-Admin-Secret", DirectoryWorld.DirectorySecret);
        var servers = JsonDocument.Parse(await (await http.SendAsync(request)).Content.ReadAsStringAsync()).RootElement;
        var one = Assert.Single(servers.EnumerateArray());
        Assert.Equal(10, one.GetProperty("free").GetInt32());                  // max-connections 10, nobody connected
        Assert.True(one.GetProperty("accepts_new").GetBoolean());
        Assert.True(one.GetProperty("reachable").GetBoolean());               // its address answered with its key
        Assert.True(one.GetProperty("listed").GetBoolean());
        Assert.Matches(@"^\d+\.\d+\.\d+$", one.GetProperty("version").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", one.GetProperty("date").GetString());
        Assert.DoesNotContain("users", one.ToString());                         // no number of users is ever told
    }

    [Fact]
    public async Task A_relay_that_stops_taking_new_accounts_or_turns_private_is_no_longer_offered()
    {
        Assert.Equal(0, (await _w.RelayCli("access", "allowlist")).Code);
        Assert.True(await DirectoryWorld.UntilAsync(async () => (await _w.ListAsync()).Count == 0), "the relay in allowlist mode is still offered");
        Assert.Equal(0, (await _w.RelayCli("access", "open")).Code);
        Assert.True(await DirectoryWorld.UntilAsync(async () => (await _w.ListAsync()).Count == 1), "the relay is not offered again");

        Assert.Equal(0, (await _w.RelayCli("private")).Code);   // a private relay says nothing: its last heartbeat expires
        Assert.Equal(0, (await _w.DirectoryCli("settings", "set", "expiry-minutes", "1")).Code);
        Assert.Equal(0, (await _w.RelayCli("public")).Code);
        Assert.True(await DirectoryWorld.UntilAsync(async () => (await _w.ListAsync()).Count == 1), "the relay is not offered again");
        Assert.Equal(0, (await _w.DirectoryCli("settings", "reset")).Code);
    }

    [Fact]
    public async Task Calls_to_the_directory_that_are_not_a_relay_it_accepted_change_nothing()
    {
        using var http = new HttpClient();
        var unsigned = await http.PostAsJsonAsync(_w.Directory.Http + "/directory/heartbeat", new HeartbeatBody { Country = "FR", Free = 5, AcceptsNew = true });
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var stranger = PeerIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "fs-stranger-" + Guid.NewGuid().ToString("N") + ".key"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new HeartbeatBody { Country = "FR", Free = 5, AcceptsNew = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, _w.Directory.Http + "/directory/heartbeat") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        PeerSigning.Sign(request, stranger, "/directory/heartbeat", bytes);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);   // signed, but by a key nobody accepted
        Assert.Equal([_w.Relay.Http], await _w.ListAsync());
    }

    [Fact]
    public async Task A_new_identity_takes_its_first_server_from_the_directory_instead_of_the_default()
    {
        var dead = "http://127.0.0.1:1";   // the default server of the application, which does not answer
        var client = NewClient("newcomer", dead, [new DirectoryEntry(_w.Directory.Http, _w.Key)], "FR");
        var statuses = await client.ConnectAsync();

        var chosen = Assert.Single(statuses);
        Assert.Equal(_w.Relay.Http, chosen.Server.Url);
        Assert.True(chosen.Connected, "the client did not connect to the relay the directory offered");
        Assert.DoesNotContain(client.Servers, s => s.Server.Url == dead);   // the default is not kept next to it
    }

    [Fact]
    public async Task A_new_identity_keeps_the_default_server_when_no_directory_answers_with_the_expected_key()
    {
        var wrongKey = PeerIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "fs-wrongkey-" + Guid.NewGuid().ToString("N") + ".key")).PublicKey;
        var client = NewClient("fallback", _w.Relay.Http, [new DirectoryEntry(_w.Directory.Http, wrongKey), new DirectoryEntry("http://127.0.0.1:1", wrongKey)]);
        var statuses = await client.ConnectAsync();
        var kept = Assert.Single(statuses);
        Assert.Equal(_w.Relay.Http, kept.Server.Url);
        Assert.True(kept.Connected);
    }
}
