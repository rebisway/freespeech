/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.WebSockets;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Maps a connection id to its live <see cref="WebSocket"/> instance, so a
/// message can be pushed to a specific connection found via
/// <see cref="IPresenceService"/>. Kept separate from presence: this
/// registry is transport-specific, presence is protocol-level.
/// </summary>
public interface ISocketRegistry
{
    void Add(string connectionId, WebSocket socket);
    void Remove(string connectionId);
    WebSocket? Get(string connectionId);

    /// <summary>Snapshot of every live connection id.</summary>
    IReadOnlyList<string> ConnectionIds { get; }
}
