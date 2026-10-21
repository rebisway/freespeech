/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using FreeSpeech.Client.Backup;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>
/// Which relays a client has at the start of its life: a built-in fallback when no directory answers, the servers of a backup, then the servers of
/// the very first contact — each only while the user has not chosen anything.
/// </summary>
public class FirstServersTests : IAsyncLifetime, IDisposable
{
    private static readonly string Dll = TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll");
    private const string Dead = "http://127.0.0.1:1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-first-" + Guid.NewGuid().ToString("N"));
    private readonly List<TestProcess> _relays = [];
    private readonly List<FreeSpeechClient> _clients = [];

    private string R(int i) => _relays[i].Http;

    public async Task InitializeAsync()
    {
        for (var i = 0; i < 2; i++)
        {
            var state = Path.Combine(_root, "relay" + i);
            Directory.CreateDirectory(state);
            _relays.Add(await TestProcess.StartAsync(Dll, new Dictionary<string, string>
            {
                ["Admin__Secret"] = "first-servers-secret-0123456789", ["ASPNETCORE_CONTENTROOT"] = state,
                ["Server__AccessFilePath"] = Path.Combine(state, "access.json"), ["Server__SiblingsFilePath"] = Path.Combine(state, "siblings.json"),
                ["Server__LimitsFilePath"] = Path.Combine(state, "limits.json"), ["Server__SettingsFilePath"] = Path.Combine(state, "settings.json"),
            }, "/api/time"));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var r in _relays) r.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private FreeSpeechClient NewClient(string tag, string server, IReadOnlyList<string>? fallbacks = null)
    {
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(_root, "c-" + tag + "-" + Guid.NewGuid().ToString("N")), ServerUrl = server, FallbackServers = fallbacks ?? [] });
        c.CreateIdentity(Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3"), tag);
        c.Initialize();
        _clients.Add(c);
        return c;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private static string[] Urls(FreeSpeechClient c) => [.. c.Servers.Select(s => s.Server.Url)];

    [Fact]
    public async Task A_new_identity_takes_a_built_in_fallback_relay_when_no_directory_answers()
    {
        var c = NewClient("fallback", Dead, [R(0), R(1)]);
        var statuses = await c.ConnectAsync();
        var chosen = Assert.Single(statuses);
        Assert.Contains(chosen.Server.Url, new[] { R(0), R(1) });
        Assert.True(chosen.Connected);
        Assert.DoesNotContain(Dead, Urls(c));
    }

    [Fact]
    public async Task The_servers_of_a_backup_replace_the_initial_ones_when_one_answers_and_are_left_alone_when_none_does()
    {
        var phrases = new[] { Pad("backup a1"), Pad("backup a2"), Pad("backup a3") };
        var file = Path.Combine(_root, "b.enc");

        var old = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(_root, "old"), ServerUrl = R(1) });
        old.CreateIdentity(phrases[0], phrases[1], phrases[2], "owner");
        old.Initialize();
        _clients.Add(old);
        await old.ConnectAsync();
        old.CreateBackup(file, AttachmentBackupMode.None);

        // A recreated identity starts on the application's fallback (relay 0); the backup says relay 1: it answers, so it wins.
        var fresh = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(_root, "fresh"), ServerUrl = R(0), FallbackServers = [R(0)] });
        fresh.CreateIdentity(phrases[0], phrases[1], phrases[2], "owner");
        fresh.Initialize();
        _clients.Add(fresh);
        fresh.RestoreBackup(file);
        await fresh.ConnectAsync();
        Assert.Equal([R(1)], Urls(fresh));

        // An identity whose user chose the server (no fallback given) keeps it.
        var chosen = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(_root, "chosen"), ServerUrl = R(0) });
        chosen.CreateIdentity(phrases[0], phrases[1], phrases[2], "owner");
        chosen.Initialize();
        _clients.Add(chosen);
        chosen.RestoreBackup(file);
        Assert.Equal([R(0)], Urls(chosen));
    }

    [Fact]
    public async Task The_very_first_contact_brings_its_server_when_the_user_left_the_initial_one_and_it_answers()
    {
        var carol = NewClient("carol", R(1));   // chose relay 1 herself
        await carol.ConnectAsync();
        var bob = NewClient("bob", R(0), [R(0)]);   // left the application's default
        await bob.ConnectAsync();

        await bob.ImportCardAsync(carol.ExportCard("qr"));
        Assert.True(await UntilAsync(() => carol.Contacts.Count == 1 && bob.Contacts.Count == 1), "the contact exchange did not finish");
        Assert.True(await UntilAsync(() => Urls(bob).SequenceEqual([R(1)])), "bob did not move to carol's server: " + string.Join(",", Urls(bob)));
        Assert.True(await UntilAsync(() => bob.Servers.All(s => s.Connected)));

        var dave = NewClient("dave", R(0), [R(0)]);   // a second contact changes nothing
        await dave.ConnectAsync();
        await bob.ImportCardAsync(dave.ExportCard("qr"));
        Assert.True(await UntilAsync(() => bob.Contacts.Count == 2));
        Assert.Equal([R(1)], Urls(bob));
    }

    [Fact]
    public async Task A_user_who_chose_the_server_is_never_moved_by_a_contact()
    {
        var carol = NewClient("carol2", R(1));
        await carol.ConnectAsync();
        var bob = NewClient("bob2", R(0));   // no fallback given: the server was chosen
        await bob.ConnectAsync();
        await bob.ImportCardAsync(carol.ExportCard("qr"));
        Assert.True(await UntilAsync(() => carol.Contacts.Count == 1 && bob.Contacts.Count == 1));
        await Task.Delay(1500);
        Assert.Equal([R(0)], Urls(bob));
    }
}
