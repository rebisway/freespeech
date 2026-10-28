/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;

namespace FreeSpeech.Ops;

/// <summary>
/// What a server knows of the exchanges with each server it is linked with: when the last one worked, when the last one failed and why, how many failed in a row.
/// An exchange counts in either direction (a call made to the server or a call received from it). Servers are told apart by their address.
/// Kept in memory only (it is rebuilt by the next exchanges); shown by the <c>list</c> command.
/// </summary>
public sealed class LinkStatus(params string[] kinds)
{
    public sealed record State(DateTime? LastOk, DateTime? LastFail, string? Reason, int Failures);

    private readonly ConcurrentDictionary<string, State> _states = new();

    private static string Key(string url) => url.TrimEnd('/').ToLowerInvariant();

    public void Ok(string url) =>
        _states.AddOrUpdate(Key(url), _ => new State(DateTime.UtcNow, null, null, 0), (_, s) => s with { LastOk = DateTime.UtcNow, Reason = null, Failures = 0 });

    public void Fail(string url, string reason) =>
        _states.AddOrUpdate(Key(url), _ => new State(null, DateTime.UtcNow, reason, 1), (_, s) => s with { LastFail = DateTime.UtcNow, Reason = reason, Failures = s.Failures + 1 });

    /// <summary>Whether exchanges with servers of this kind are followed (the others have no regular exchange to follow).</summary>
    public bool Tracks(string kind) => kinds.Contains(kind);

    public State? Get(string url) => _states.TryGetValue(Key(url), out var s) ? s : null;

    /// <summary>The state as the admin API says it: dates in UTC, null when nothing has been exchanged since the start.</summary>
    public object View(string url) =>
        Get(url) is { } s ? new { last_ok = s.LastOk, last_fail = s.LastFail, reason = s.Reason, failures = s.Failures } : new { last_ok = (DateTime?)null, last_fail = (DateTime?)null, reason = (string?)null, failures = 0 };
}
