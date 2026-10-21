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
/// Thread-safe in-memory implementation of <see cref="IPendingFlagService"/>.
/// Keyed by "from::to" so there is exactly one flag per direction.
///
/// EXPIRY:
///   WAITING flags expire after 30 days (the sender likely gave up).
///   READY flags expire 2 minutes after NextConnectAt (the rendezvous was missed).
/// </summary>
public class PendingFlagService : IPendingFlagService
{
    private static readonly TimeSpan WaitingExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan ReadyGrace = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingFlag> _flags = new();
    private readonly ILogger<PendingFlagService> _logger;
    private readonly FreeSpeech.Ops.TraceService _trace;

    public PendingFlagService(FreeSpeech.Ops.TraceService trace, ILogger<PendingFlagService> logger)
    {
        _trace = trace;
        _logger = logger;
    }

    /// <inheritdoc/>
    public PendingFlag SetFlag(PendingFlag flag)
    {
        flag.UpdatedAt = DateTime.UtcNow;
        var key = MakeKey(flag.From, flag.To);
        _flags[key] = flag;

        _logger.LogDebug(
            "Flag set: {From} -> {To} state={State} nextConnect={Next}",
            ShortId(flag.From), ShortId(flag.To), flag.State, flag.NextConnectAt?.ToString("u") ?? "-");
        _trace.Write(flag.From, $"flag to {ShortId(flag.To)} state={flag.State}");
        _trace.Write(flag.To, $"flag from {ShortId(flag.From)} state={flag.State}");

        return flag;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PendingFlag> GetFlagsFor(string toDiscoveryId)
    {
        var now = DateTime.UtcNow;
        return _flags.Values
            .Where(f => f.To == toDiscoveryId && !IsExpired(f, now))
            .ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PendingFlag> GetFlagsFrom(string fromDiscoveryId)
    {
        var now = DateTime.UtcNow;
        return _flags.Values
            .Where(f => f.From == fromDiscoveryId && !IsExpired(f, now))
            .ToList();
    }

    /// <inheritdoc/>
    public void RemoveFlag(string fromDiscoveryId, string toDiscoveryId)
    {
        var key = MakeKey(fromDiscoveryId, toDiscoveryId);
        if (_flags.TryRemove(key, out _))
        {
            _logger.LogDebug(
                "Flag removed: {From} -> {To}", ShortId(fromDiscoveryId), ShortId(toDiscoveryId));
            _trace.Write(fromDiscoveryId, $"flag to {ShortId(toDiscoveryId)} removed");
        }
    }

    /// <inheritdoc/>
    public void RemoveAllFlags(string discoveryId)
    {
        var toRemove = _flags.Keys
            .Where(k => k.StartsWith(discoveryId + "::") || k.EndsWith("::" + discoveryId))
            .ToList();

        foreach (var key in toRemove)
            _flags.TryRemove(key, out _);

        if (toRemove.Count > 0)
            _logger.LogInformation("Removed {Count} flags for {Id}", toRemove.Count, discoveryId[..8]);
    }

    /// <inheritdoc/>
    public int PurgeExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;

        foreach (var (key, flag) in _flags)
        {
            if (IsExpired(flag, now) && _flags.TryRemove(key, out _))
                removed++;
        }

        if (removed > 0)
            _logger.LogInformation("Purged {Count} expired flags", removed);

        return removed;
    }

    private static string MakeKey(string from, string to) => $"{from}::{to}";

    /// <summary>Truncates a discovery id for log lines without risking an out-of-range slice.</summary>
    private static string ShortId(string discoveryId) =>
        discoveryId.Length <= 8 ? discoveryId : discoveryId[..8];

    private static bool IsExpired(PendingFlag flag, DateTime now) => flag.State switch
    {
        PendingFlagState.Waiting => now - flag.UpdatedAt > WaitingExpiry,
        PendingFlagState.Ready when flag.NextConnectAt.HasValue => now > flag.NextConnectAt.Value + ReadyGrace,
        PendingFlagState.Ready => now - flag.UpdatedAt > ReadyGrace,
        _ => true,
    };
}
