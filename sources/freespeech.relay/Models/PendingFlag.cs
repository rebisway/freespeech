/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Relay.Models;

/// <summary>
/// Possible states of a PendingFlag.
/// </summary>
public static class PendingFlagState
{
    /// <summary>
    /// The emitter has a message for the recipient but the recipient was offline.
    /// The emitter is waiting for the recipient to signal readiness.
    /// </summary>
    public const string Waiting = "WAITING";

    /// <summary>
    /// The recipient has seen the WAITING flag and will be connected at NextConnectAt.
    /// The emitter should schedule a wakeup shortly before that time.
    /// </summary>
    public const string Ready = "READY";
}

/// <summary>
/// A rendezvous signal stored on the server to coordinate offline message delivery
/// (protocol §9). Keyed by the (From, To) discovery-id pair — one entry per
/// direction, not per device or connection.
///
/// LIFECYCLE:
///   1. A has messages for B (offline) -> A sets WAITING { From=A, To=B }.
///   2. B polls and sees the WAITING flag from A -> B sets READY { From=B, To=A, NextConnectAt=T }.
///   3. A polls and sees B is READY at time T -> A schedules a wakeup shortly before T.
///   4. Both connect at time T -> A delivers the message -> both remove their flags.
///
/// STORAGE: in-memory only, never persisted. A server restart clears all flags;
/// clients re-signal on their next poll.
/// </summary>
public class PendingFlag
{
    /// <summary>
    /// Discovery ID of the party that set this flag.
    /// WAITING: the sender who has pending messages.
    /// READY:   the receiver who will be available at NextConnectAt.
    /// </summary>
    [JsonPropertyName("from")]
    public required string From { get; set; }

    /// <summary>
    /// Discovery ID of the party this flag is addressed to.
    /// </summary>
    [JsonPropertyName("to")]
    public required string To { get; set; }

    /// <summary>
    /// Flag state: <see cref="PendingFlagState.Waiting"/> or <see cref="PendingFlagState.Ready"/>.
    /// </summary>
    [JsonPropertyName("state")]
    public required string State { get; set; }

    /// <summary>
    /// UTC time (server clock) when the READY party will be connected.
    /// Null for WAITING flags. The WAITING party should connect a few
    /// seconds before NextConnectAt to be ready first.
    /// </summary>
    [JsonPropertyName("next_connect_at")]
    public DateTime? NextConnectAt { get; set; }

    /// <summary>
    /// UTC timestamp (server clock) when this flag was last set or updated.
    /// Set automatically by <see cref="Services.PendingFlagService.SetFlag"/>.
    /// Used for expiry calculations.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
