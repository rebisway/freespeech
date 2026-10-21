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
/// Pushes an envelope to every locally-connected socket registered under
/// its recipient. Shared by the live WebSocket "send" path
/// (<see cref="RelaySocketHandler"/>) and S2S envelope forwarding
/// (protocol §10, <c>/s2s/relay</c>) so both go through the exact same
/// delivery logic.
/// </summary>
public interface IMessageDeliveryService
{
    /// <summary>
    /// Delivers to every open local connection for <c>envelope.To</c>,
    /// except <paramref name="excludeConnectionId"/> if given (used to stop
    /// a self-addressed send echoing back to the connection that sent it —
    /// protocol §1/§5). Returns true if at least one connection received it.
    /// </summary>
    Task<bool> DeliverLocallyAsync(Envelope envelope, string? excludeConnectionId, CancellationToken ct);
}
