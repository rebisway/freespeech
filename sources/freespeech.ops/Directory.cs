/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace FreeSpeech.Ops;

/// <summary>
/// Body of POST /directory/heartbeat: what a relay tells the directories it is linked with. Its address is not in it (the directory uses the one it
/// verified when it accepted the relay) and neither is its number of users: a directory only needs to know whether there is room.
/// </summary>
public sealed class HeartbeatBody
{
    /// <summary>ISO 3166 alpha-2 country the relay operates from, as its administrator declares it (null = not stated).</summary>
    [JsonPropertyName("country")] public string? Country { get; set; }
    /// <summary>Places left before the relay offers its clients another server; -1 when it states no limit.</summary>
    [JsonPropertyName("free")] public int Free { get; set; }
    /// <summary>True while the relay is public, takes new accounts (client access open) and has room.</summary>
    [JsonPropertyName("accepts_new")] public bool AcceptsNew { get; set; }
    /// <summary>Version and release date of the relay program, for the directory's administrator.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
}

/// <summary>One relay offered to a client.</summary>
public sealed class ListedRelay
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("country")] public string? Country { get; set; }
}

/// <summary>Answer of GET /directory/servers: the relays and the signature of the directory's key over <see cref="Canonical"/>.</summary>
public sealed class SignedRelayList
{
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("servers")] public List<ListedRelay> Servers { get; set; } = [];
    [JsonPropertyName("sig")] public string Sig { get; set; } = "";

    public static string Canonical(long ts, IEnumerable<ListedRelay> servers) =>
        "directory-list\n" + ts + "\n" + string.Join("\n", servers.Select(s => s.Url + "|" + s.Country));

    public static SignedRelayList Create(PeerIdentity identity, IReadOnlyList<ListedRelay> servers)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new SignedRelayList { Ts = ts, Servers = servers.ToList(), Sig = identity.Sign(Canonical(ts, servers)) };
    }
}

/// <summary>
/// What the directory knows about the relays it accepted: their last heartbeat (memory only — a relay says it again every few minutes), whether
/// their address answered with their key at the last check, and the choice of the relays to offer a newcomer. A relay is offered only when its
/// administrator and the directory's accepted each other, it spoke recently, it was seen at its address and it takes new accounts.
/// </summary>
public sealed class DirectoryRegistry(PeerStore peers, Func<TimeSpan> expiry)
{
    public sealed record Beat(string? Country, int Free, bool AcceptsNew, string? Version, string? Date, DateTime At);
    public sealed record Entry(Peer Peer, Beat Beat, bool Reachable);

    private readonly ConcurrentDictionary<string, Beat> _beats = new();
    private readonly ConcurrentDictionary<string, bool> _reachable = new();
    private const int UnlimitedWeight = 1000;

    public static string? CleanCountry(string? country) =>
        country is { Length: 2 } && char.IsAsciiLetter(country[0]) && char.IsAsciiLetter(country[1]) ? country.ToUpperInvariant() : null;

    private static string? CleanText(string? text, int max) =>
        text is { Length: > 0 } && text.Length <= max && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_') ? text : null;

    public void Record(string peerId, HeartbeatBody body) =>
        _beats[peerId] = new Beat(CleanCountry(body.Country), Math.Clamp(body.Free, -1, 100_000_000), body.AcceptsNew, CleanText(body.Version, 32), CleanText(body.Date, 10), DateTime.UtcNow);

    /// <summary>Whether the relay's address has been looked at yet (a relay is offered only after the first successful look).</summary>
    public bool IsProbed(string peerId) => _reachable.ContainsKey(peerId);

    /// <summary>The country a relay declared in its last heartbeat (null when it declared none or has not spoken).</summary>
    public string? CountryOf(string peerId) => _beats.TryGetValue(peerId, out var b) ? b.Country : null;

    public void SetReachable(string peerId, bool reachable) => _reachable[peerId] = reachable;

    /// <summary>The accepted relays that spoke recently, with their last heartbeat and whether their address answered.</summary>
    public IReadOnlyList<Entry> Known() => peers.Accepted("relay")
        .Where(p => p.Url.Length > 0 && _beats.TryGetValue(p.Id, out var b) && DateTime.UtcNow - b.At <= expiry())
        .Select(p => new Entry(p, _beats[p.Id], _reachable.TryGetValue(p.Id, out var ok) && ok)).ToList();

    private static int Weight(Beat b) => b.Free < 0 ? UnlimitedWeight : b.Free;

    /// <summary>
    /// Up to <paramref name="count"/> relays that take new accounts, drawn at random with a weight equal to the room they have left (so that
    /// newcomers do not all land on the same relay). The relays of <paramref name="country"/> come first; others fill what is missing.
    /// </summary>
    public IReadOnlyList<ListedRelay> Pick(string? country, int count, Random? random = null)
    {
        random ??= Random.Shared;
        country = CleanCountry(country);
        var open = Known().Where(e => e.Reachable && e.Beat.AcceptsNew && Weight(e.Beat) > 0).ToList();
        var result = new List<ListedRelay>();
        void Draw(List<Entry> pool)
        {
            while (result.Count < count && pool.Count > 0)
            {
                var roll = random.NextInt64(pool.Sum(e => (long)Weight(e.Beat)));
                var index = 0;
                foreach (var e in pool) { roll -= Weight(e.Beat); if (roll < 0) break; index++; }
                result.Add(new ListedRelay { Url = pool[index].Peer.Url, Country = pool[index].Beat.Country });
                pool.RemoveAt(index);
            }
        }
        if (country != null) Draw(open.Where(e => e.Beat.Country == country).ToList());
        Draw(open.Where(e => result.All(r => r.Url != e.Peer.Url)).ToList());
        return result;
    }
}
