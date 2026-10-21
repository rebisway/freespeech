/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>Three real relays X, Y and Z, each with its own state folder: X knows Y, Y knows Z, X does not know Z.</summary>
public sealed class RelayChain : IAsyncLifetime
{
    private static readonly string Dll = PeerPair.RelayDll;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-chain-" + Guid.NewGuid().ToString("N"));
    private readonly (string Name, string Secret, int Port)[] _relays =
    [
        ("x", "relay-x-admin-secret-0123456789", TestProcess.FreePort()),
        ("y", "relay-y-admin-secret-0123456789", TestProcess.FreePort()),
        ("z", "relay-z-admin-secret-0123456789", TestProcess.FreePort()),
    ];
    private readonly List<TestProcess> _processes = [];

    public string Url(string name) => $"http://127.0.0.1:{_relays.First(r => r.Name == name).Port}";
    public Task<(int Code, string Output)> Cli(string name, params string[] args) => PeerPair.Cli(Dll, Url(name), _relays.First(r => r.Name == name).Secret, args);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        foreach (var (name, secret, port) in _relays)
        {
            Directory.CreateDirectory(Path.Combine(_dir, name));
            _processes.Add(await TestProcess.StartAsync(Dll, new Dictionary<string, string>
            {
                ["Server__SiblingsFilePath"] = Path.Combine(_dir, name, "siblings.json"), ["Server__AccessFilePath"] = Path.Combine(_dir, name, "access.json"),
                ["Server__SettingsFilePath"] = Path.Combine(_dir, name, "settings.json"), ["Server__LimitsFilePath"] = Path.Combine(_dir, name, "limits.json"),
                ["Admin__Secret"] = secret, ["Server__AllowInsecureSiblings"] = "true",   // plain http on 127.0.0.1: tests only
                ["Peer__Name"] = "relay-" + name, ["Peer__PublicUrl"] = "http://127.0.0.1:{port}",
            }, "/api/time", port));
            Assert.Equal(0, (await Cli(name, "public")).Code);
        }
        await LinkAsync("x", "y");
        await LinkAsync("y", "z");
    }

    /// <summary>"from" asks "to" for a link, "to" accepts, "from" learns it.</summary>
    private async Task LinkAsync(string from, string to)
    {
        Assert.Equal(0, (await Cli(from, "add", "relay", Url(to))).Code);
        Assert.Equal(0, (await Cli(to, "accept", "relay", "--all", "--yes")).Code);
        await Cli(from, "peer-refresh");
    }

    /// <summary>The addresses of the siblings a relay talks to.</summary>
    public async Task<string> SiblingsOf(string name) =>
        (await PeerPair.GetJson(Url(name) + "/api/admin/siblings", _relays.First(r => r.Name == name).Secret)).ToString();

    public Task DisposeAsync()
    {
        foreach (var p in _processes) p.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// X knows Y, Y knows Z, X does not know Z; A is on X and B on Z. A relay forwards for its own siblings only (one hop), so nothing goes through Y:
/// A sends to B on Z directly (B's card lists Z), B answers on X directly (A's card lists X). Both relays are public and open, which is what lets each
/// client visit the other's server.
/// </summary>
public class ChainTopologyTests : IClassFixture<RelayChain>, IDisposable
{
    private readonly RelayChain _chain;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public ChainTopologyTests(RelayChain chain) => _chain = chain;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<FreeSpeechClient> ClientAsync(string tag, string serverUrl)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-chain-client-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, tag), ServerUrl = serverUrl });
        c.CreateIdentity(Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3"), tag);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private static string[] Texts(FreeSpeechClient c, string peer) => [.. c.GetHistory(c.FindConversation(peer)!).Select(l => l.Entry.Text)];

    [Fact]
    public async Task AMessageGoesToAClientOfAnUnlinkedRelayAndTheAnswerComesBack()
    {
        // The topology is the one that was asked for: X has Y only, Z has Y only, Y has both.
        var x = await _chain.SiblingsOf("x"); var y = await _chain.SiblingsOf("y"); var z = await _chain.SiblingsOf("z");
        Assert.Contains(_chain.Url("y"), x); Assert.DoesNotContain(_chain.Url("z"), x);
        Assert.Contains(_chain.Url("y"), z); Assert.DoesNotContain(_chain.Url("x"), z);
        Assert.Contains(_chain.Url("x"), y); Assert.Contains(_chain.Url("z"), y);

        var a = await ClientAsync("alice-on-x", _chain.Url("x"));
        var b = await ClientAsync("bob-on-z", _chain.Url("z"));

        // The contact exchange already uses both directions: B writes to A on X, A answers B on Z.
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1), "the contact exchange did not finish");
        var (aId, bId) = (b.Contacts[0].DiscoveryId, a.Contacts[0].DiscoveryId);

        Assert.True(await a.IsOnlineAsync(bId), "A cannot see that B is online (B's card lists Z)");
        Assert.True(await b.IsOnlineAsync(aId), "B cannot see that A is online (A's card lists X)");

        var sent = await a.SendAsync(a.FindConversation(bId)!, "hello B, from A on X");
        Assert.Equal(SendStatus.Delivered, sent.Status);
        Assert.True(await UntilAsync(() => Texts(b, aId).Contains("hello B, from A on X")), "B did not receive A's message");

        var answer = await b.SendAsync(b.FindConversation(aId)!, "hello A, from B on Z");
        Assert.Equal(SendStatus.Delivered, answer.Status);
        Assert.True(await UntilAsync(() => Texts(a, bId).Contains("hello A, from B on Z")), "A did not receive B's answer");
    }
}
