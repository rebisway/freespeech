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
/// Transport envelope for client-to-server-to-client communication
/// (protocol §5). The server only reads From/To for routing — it never
/// decrypts EncryptedMessage.
/// </summary>
public class Envelope
{
    /// <summary>Sender's discovery id (visible to the server for routing).</summary>
    [JsonPropertyName("from")]
    public required string From { get; set; }

    /// <summary>Recipient's discovery id (visible to the server for routing).</summary>
    [JsonPropertyName("to")]
    public required string To { get; set; }

    /// <summary>
    /// Encryption scheme used for EncryptedMessage:
    ///   0 = OTC symmetric (ChaCha20-Poly1305, key derived from the One-Time Code)
    ///   1 = Asymmetric (X25519 ECDH + ChaCha20-Poly1305 + Ed25519 signature)
    /// </summary>
    [JsonPropertyName("crypto")]
    public int Crypto { get; set; } = 1;

    /// <summary>
    /// Recipient's relay server URL (protocol §8). Used client-side to pick
    /// which server to connect to; NOT used for server-side auto-learning of
    /// siblings (§10 — that would leak private server addresses).
    /// </summary>
    [JsonPropertyName("to_server")]
    public string? ToServerUrl { get; set; }

    /// <summary>Encrypted payload (base64). The server never decrypts this field.</summary>
    [JsonPropertyName("msg")]
    public required string EncryptedMessage { get; set; }

    /// <summary>
    /// Sender-local timestamp (UTC ticks), set once at send time and never
    /// touched by the server. Combined with <see cref="From"/> it forms the
    /// message_id of protocol §5 (<c>(discovery_id_émetteur, timestamp)</c>) —
    /// no global counter, no server clock dependency, safe under concurrent
    /// offline senders. The server only relays it, it never assigns or reads it.
    /// </summary>
    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    /// <summary>
    /// message_id of the message this one replies to ("{senderDiscoveryId}:{ts}"),
    /// or null. Protocol §5/§7 — reply_to is the explicit targeting
    /// mechanism, since there is no total order across senders. The server
    /// only relays it, same as everything else in the envelope.
    /// </summary>
    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; set; }
}
