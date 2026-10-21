/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Tracks which realtime connections belong to which discovery id.
///
/// MULTI-DEVICE (protocol §1): a discovery id can have several
/// simultaneous connections — the same identity active on several devices at
/// once. The registry is discovery_id -> set of connection ids, never a
/// single value. In-memory only; resets on server restart, clients
/// re-register on connect.
/// </summary>
public interface IPresenceService
{
    /// <summary>Registers a connection under a discovery id (adds, does not replace).</summary>
    Task RegisterAsync(string discoveryId, string connectionId);

    /// <summary>Removes a connection on disconnect. Returns the discovery id it belonged to, if any.</summary>
    Task<string?> UnregisterAsync(string connectionId);

    /// <summary>Returns the discovery id registered for a connection id, or null.</summary>
    Task<string?> GetDiscoveryIdAsync(string connectionId);

    /// <summary>True if the discovery id has at least one active connection.</summary>
    Task<bool> IsOnlineAsync(string discoveryId);

    /// <summary>Returns every active connection id for a discovery id (multi-device fan-out).</summary>
    Task<IReadOnlyList<string>> GetConnectionsAsync(string discoveryId);

    /// <summary>Number of distinct discovery ids currently registered.</summary>
    Task<int> GetRegisteredCountAsync();

    /// <summary>The discovery ids connected right now, with how many connections each has. Memory only: nothing about who connected earlier is kept.</summary>
    IReadOnlyList<(string DiscoveryId, int Connections)> Connected();
}
