/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net;
using System.Text.Json;
using FreeSpeech.Client.Discovery;
using FreeSpeech.Ops;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpeech.Tests;

/// <summary>The directory's choice of relays (country, room left, new accounts, address checked, recent heartbeat), the signed list and the client's pool of directories.</summary>
public class DirectoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-dir-" + Guid.NewGuid().ToString("N"));
    private readonly List<PeerIdentity> _relays = [];

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private DirectoryRegistry Setup(int relayCount, TimeSpan? expiry = null)
    {
        for (var i = 0; i < relayCount; i++) _relays.Add(PeerIdentity.LoadOrCreate(Path.Combine(_dir, $"relay{i}.key")));
        PeerSeed.Accepted(_dir, _relays.Select((r, i) => ("relay", $"https://r{i}.exemple.org", (PeerIdentity?)r)));
        var peers = new PeerStore(Path.Combine(_dir, "peers.json"), 10, TimeSpan.FromDays(14), NullLogger<PeerStore>.Instance);
        return new DirectoryRegistry(peers, () => expiry ?? TimeSpan.FromMinutes(10));
    }

    /// <summary>A relay that spoke and whose address was seen (the normal case), unless said otherwise.</summary>
    private void Beat(DirectoryRegistry registry, int relay, string? country, int free = -1, bool acceptsNew = true, bool reachable = true)
    {
        registry.Record(_relays[relay].Fingerprint, new HeartbeatBody { Country = country, Free = free, AcceptsNew = acceptsNew, Version = "1.2.0", Date = "2026-10-17" });
        registry.SetReachable(_relays[relay].Fingerprint, reachable);
    }

    [Fact]
    public void Country_comes_first_then_others_fill()
    {
        var registry = Setup(3);
        Beat(registry, 0, "FR"); Beat(registry, 1, "DE"); Beat(registry, 2, "FR");
        for (var i = 0; i < 20; i++)
        {
            var pick = registry.Pick("fr", 3);
            Assert.Equal(3, pick.Count);
            Assert.All(pick.Take(2), r => Assert.Equal("FR", r.Country));
            Assert.Equal("DE", pick[2].Country);
        }
    }

    [Fact]
    public void Only_relays_that_take_new_accounts_have_room_were_seen_and_spoke_recently_are_offered()
    {
        var registry = Setup(6);
        Beat(registry, 0, "FR");
        Beat(registry, 1, "FR", acceptsNew: false);   // closed to new accounts
        Beat(registry, 2, "FR", free: 0);              // full
        Beat(registry, 3, "FR", reachable: false);     // its address does not answer with its key
        registry.Record(_relays[4].Fingerprint, new HeartbeatBody { Country = "FR", Free = -1, AcceptsNew = true });   // spoke, never looked at yet
        // relay 5 never spoke
        Assert.Equal(["https://r0.exemple.org"], registry.Pick(null, 9).Select(r => r.Url));
        Assert.False(registry.IsProbed(_relays[4].Fingerprint));

        var stale = Setup(0, TimeSpan.Zero);   // an expiry of zero: every heartbeat is already too old
        Beat(stale, 0, "FR");
        Assert.Empty(stale.Pick(null, 5));
    }

    [Fact]
    public void The_room_left_weighs_the_draw()
    {
        var registry = Setup(2);
        Beat(registry, 0, null, free: 10);
        Beat(registry, 1, null, free: 1000);
        var roomy = Enumerable.Range(0, 300).Count(_ => registry.Pick(null, 1)[0].Url == "https://r1.exemple.org");
        Assert.InRange(roomy, 270, 300);
    }

    [Fact]
    public void A_heartbeat_is_cleaned_before_it_is_kept()
    {
        var registry = Setup(1);
        registry.Record(_relays[0].Fingerprint, new HeartbeatBody { Country = "fr", Free = int.MaxValue, AcceptsNew = true, Version = "<script>", Date = "2026-10-17" });
        registry.SetReachable(_relays[0].Fingerprint, true);
        var entry = Assert.Single(registry.Known());
        Assert.Equal("FR", entry.Beat.Country);
        Assert.Equal(100_000_000, entry.Beat.Free);
        Assert.Null(entry.Beat.Version);   // not a version: dropped, not displayed
        Assert.Equal("2026-10-17", entry.Beat.Date);
        registry.Record(_relays[0].Fingerprint, new HeartbeatBody { Country = "France" });
        Assert.Null(Assert.Single(registry.Known()).Beat.Country);
    }

    [Fact]
    public void A_signed_list_verifies_on_the_client_and_nothing_else_does()
    {
        var directory = PeerIdentity.LoadOrCreate(Path.Combine(_dir, "directory.key"));
        var list = SignedRelayList.Create(directory, [new ListedRelay { Url = "https://a.exemple.org/", Country = "FR" }, new ListedRelay { Url = "https://b.exemple.org", Country = null }]);
        var json = JsonSerializer.Serialize(list);

        Assert.Equal(["https://a.exemple.org", "https://b.exemple.org"], DirectoryClient.Verify(json, directory.PublicKey, DateTimeOffset.UtcNow));
        Assert.Null(DirectoryClient.Verify(json.Replace("a.exemple.org", "evil.exemple.org"), directory.PublicKey, DateTimeOffset.UtcNow));   // altered
        var other = PeerIdentity.LoadOrCreate(Path.Combine(_dir, "other.key"));
        Assert.Null(DirectoryClient.Verify(json, other.PublicKey, DateTimeOffset.UtcNow));                                                      // another key
        Assert.Null(DirectoryClient.Verify(json, directory.PublicKey, DateTimeOffset.UtcNow + TimeSpan.FromDays(2)));                           // too old
        Assert.Null(DirectoryClient.Verify("not json", directory.PublicKey, DateTimeOffset.UtcNow));

        var http = SignedRelayList.Create(directory, [new ListedRelay { Url = "http://far.exemple.org" }, new ListedRelay { Url = "https://ok.exemple.org" }]);
        Assert.Equal(["https://ok.exemple.org"], DirectoryClient.Verify(JsonSerializer.Serialize(http), directory.PublicKey, DateTimeOffset.UtcNow));   // plain http only for this machine
    }

    [Fact]
    public void The_build_setting_lists_directories_with_their_keys_and_leaves_out_an_entry_without_one()
    {
        var entries = DirectoryPool.Parse("https://directory1.exemple.org|KEY1; https://directory2.exemple.org|KEY2 ;https://directory3.exemple.org|;;");
        Assert.Equal([new DirectoryEntry("https://directory1.exemple.org", "KEY1"), new DirectoryEntry("https://directory2.exemple.org", "KEY2")], entries);
        Assert.Empty(DirectoryPool.Parse(null));
        Assert.Empty(DirectoryPool.Parse(""));
    }

    /// <summary>Answers each directory's address with a prepared answer (or a failure).</summary>
    private sealed class Handler(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.Host);
            var (status, body) = answers.TryGetValue(request.RequestUri.Host, out var a) ? a : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task The_pool_tries_the_directories_in_turn_and_trusts_only_a_list_signed_by_the_key_of_its_directory()
    {
        var one = PeerIdentity.LoadOrCreate(Path.Combine(_dir, "one.key"));
        var two = PeerIdentity.LoadOrCreate(Path.Combine(_dir, "two.key"));
        string Answer(PeerIdentity who, string relay) => JsonSerializer.Serialize(SignedRelayList.Create(who, [new ListedRelay { Url = relay, Country = "FR" }]));
        var entries = new[] { new DirectoryEntry("https://one.exemple.org", one.PublicKey), new DirectoryEntry("https://two.exemple.org", two.PublicKey) };

        // The first is down: the second answers.
        var handler = new Handler(new() { ["one.exemple.org"] = (HttpStatusCode.InternalServerError, ""), ["two.exemple.org"] = (HttpStatusCode.OK, Answer(two, "https://relay-b.exemple.org")) });
        using (var http = new HttpClient(handler))
            Assert.Equal(["https://relay-b.exemple.org"], await DirectoryPool.ResolveAsync(entries, "FR", 3, http, new Random(1)));
        Assert.Contains("two.exemple.org", handler.Asked);

        // The first answers with a list signed by another key (a directory that is not the one built in): it is ignored and the second is asked.
        var forged = new Handler(new() { ["one.exemple.org"] = (HttpStatusCode.OK, Answer(two, "https://evil.exemple.org")), ["two.exemple.org"] = (HttpStatusCode.OK, Answer(two, "https://relay-b.exemple.org")) });
        using (var http = new HttpClient(forged))
            Assert.Equal(["https://relay-b.exemple.org"], await DirectoryPool.ResolveAsync(entries, null, 3, http));

        // Nobody answers: nothing.
        using (var http = new HttpClient(new Handler([])))
            Assert.Empty(await DirectoryPool.ResolveAsync(entries, null, 3, http));
    }

    [Fact]
    public void Link_status_follows_the_last_exchanges_of_a_server()
    {
        var links = new LinkStatus("directory");
        Assert.True(links.Tracks("directory"));
        Assert.False(links.Tracks("relay"));
        Assert.Null(links.Get("d1"));
        links.Fail("d1", "unreachable");
        links.Fail("d1", "unreachable");
        Assert.Equal(2, links.Get("d1")!.Failures);
        Assert.Null(links.Get("d1")!.LastOk);
        links.Ok("d1");
        Assert.Equal(0, links.Get("d1")!.Failures);
        Assert.Null(links.Get("d1")!.Reason);
        Assert.NotNull(links.Get("d1")!.LastOk);
    }

    [Fact]
    public void The_check_rhythm_has_a_default_and_bounds()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var check = new PeerCheckSettings(config, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
        Assert.Equal(60, check.Seconds);
        check.Set(120);
        Assert.Equal(120, check.Seconds);
        Assert.Throws<ArgumentException>(() => check.Set(5));
        Assert.Throws<ArgumentException>(() => check.Set(4000));
        Assert.Equal(120, check.Seconds);
    }
}
