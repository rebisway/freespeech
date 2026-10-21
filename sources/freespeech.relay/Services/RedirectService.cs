/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Load shedding between sibling servers (protocol §10, "Moving a client"). A server that is full — or that an
/// administrator is draining — tells a client which siblings can take it; the client decides, and proves itself again
/// on the new server. Nothing here moves a client by force.
/// </summary>
public interface IRedirectService
{
    /// <summary>Free client slots here: -1 = unlimited (Server:MaxConnections is 0), 0 = full.</summary>
    Task<int> FreeSlotsAsync();

    /// <summary>True when more clients are connected here than Server:MaxConnections allows.</summary>
    Task<bool> IsOverAsync();

    /// <summary>Siblings that answered "I have room", best first (random, weighted by free room), at most 3. Empty when none.</summary>
    Task<IReadOnlyList<string>> PickSiblingsAsync(CancellationToken ct);
}

public class RedirectService : IRedirectService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);   // a surge must not turn into an S2S storm

    private readonly IPresenceService _presence;
    private readonly ISiblingRegistry _siblings;
    private readonly IS2SClient _s2s;
    private readonly RelaySettings _settings;
    private int _max => _settings.Current.MaxConnections;   // changed while the server runs: read at each use
    private readonly ConcurrentDictionary<string, (DateTime At, int? Free)> _cache = new();

    public RedirectService(IPresenceService presence, ISiblingRegistry siblings, IS2SClient s2s, RelaySettings settings)
    {
        _presence = presence;
        _siblings = siblings;
        _s2s = s2s;
        _settings = settings;
    }

    public async Task<int> FreeSlotsAsync() =>
        _max <= 0 ? -1 : Math.Max(0, _max - await _presence.GetRegisteredCountAsync());

    public async Task<bool> IsOverAsync() => _max > 0 && await _presence.GetRegisteredCountAsync() > _max;

    public async Task<IReadOnlyList<string>> PickSiblingsAsync(CancellationToken ct)
    {
        if (!_siblings.IsPublic) return [];
        var siblings = _siblings.Whitelist;
        var answers = await Task.WhenAll(siblings.Select(async s => (s.Url, Free: await FreeOfAsync(s, ct))));

        var open = answers.Where(a => a.Free is not (null or 0)).ToList();
        var picked = new List<string>();
        while (open.Count > 0 && picked.Count < 3)
        {
            // Weighted draw: unlimited counts as a large room; a nearly full sibling is chosen rarely.
            var weights = open.Select(a => a.Free == -1 ? 1_000_000.0 : a.Free!.Value).ToList();
            var roll = Random.Shared.NextDouble() * weights.Sum();
            var i = 0;
            for (; i < weights.Count - 1 && roll >= weights[i]; i++) roll -= weights[i];
            picked.Add(open[i].Url);
            open.RemoveAt(i);
        }
        return picked;
    }

    private async Task<int?> FreeOfAsync(Sibling sibling, CancellationToken ct)
    {
        if (_cache.TryGetValue(sibling.Url, out var c) && DateTime.UtcNow - c.At < CacheFor) return c.Free;
        var free = await _s2s.CapacityAsync(sibling, ct);
        _cache[sibling.Url] = (DateTime.UtcNow, free);
        return free;
    }
}

