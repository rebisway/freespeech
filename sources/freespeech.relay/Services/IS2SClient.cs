/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Outgoing S2S calls to a whitelisted sibling (protocol §10). Every
/// call is best-effort: network errors, timeouts and non-2xx responses are
/// swallowed and treated as "that sibling didn't help this time" — a dead
/// or misbehaving sibling must never take down the local request that
/// triggered the S2S attempt.
/// </summary>
public interface IS2SClient
{
    /// <summary>Asks a sibling whether a discovery_id is connected there right now (single-hop — the sibling only answers for its own local presence, no further cascade).</summary>
    Task<bool> LookupAsync(Sibling sibling, string discoveryId, CancellationToken ct);

    /// <summary>Asks a sibling to deliver an envelope to one of its own local connections. Returns true if it reports delivery.</summary>
    Task<bool> RelayAsync(Sibling sibling, Envelope envelope, CancellationToken ct);

    /// <summary>Free client slots on a sibling: -1 = unlimited, 0 = full, null = it did not answer.</summary>
    Task<int?> CapacityAsync(Sibling sibling, CancellationToken ct);

    /// <summary>Propagates a WAITING/READY flag to a sibling (fire-and-forget from the caller's point of view).</summary>
    Task PropagateFlagAsync(Sibling sibling, PendingFlag flag, CancellationToken ct);
}
