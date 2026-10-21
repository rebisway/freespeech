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
/// Manages the WAITING/READY rendezvous flags between clients (protocol §9).
/// Thread-safe, in-memory, never persisted to disk.
/// </summary>
public interface IPendingFlagService
{
    /// <summary>Sets or updates a flag. Creates it if absent, overwrites it if present.</summary>
    PendingFlag SetFlag(PendingFlag flag);

    /// <summary>Returns all active (non-expired) flags addressed TO a given discovery id.</summary>
    IReadOnlyList<PendingFlag> GetFlagsFor(string toDiscoveryId);

    /// <summary>Returns all active flags FROM a given discovery id (to check own outgoing state).</summary>
    IReadOnlyList<PendingFlag> GetFlagsFrom(string fromDiscoveryId);

    /// <summary>Removes a specific flag, typically after a successful rendezvous.</summary>
    void RemoveFlag(string fromDiscoveryId, string toDiscoveryId);

    /// <summary>Removes all flags involving a discovery id.</summary>
    void RemoveAllFlags(string discoveryId);

    /// <summary>Removes all expired flags. Intended to be called periodically by a background task.</summary>
    int PurgeExpired();
}
