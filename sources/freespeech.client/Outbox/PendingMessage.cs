/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Outbox;

/// <summary>
/// A message we could not deliver live because the recipient was offline.
/// The relay never stores anything for us (§9/§10 — presence flags only),
/// so this is what survives until the recipient is reachable again. Kept
/// separate from ContactExchangeService's PendingContact — same idea
/// (local outbox + retry), different payload.
/// </summary>
public class PendingMessage
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("to")] public required string To { get; set; }
    [JsonPropertyName("text")] public required string Text { get; set; }

    /// <summary>Letter type to re-send ("message" or "attachment"); defaults to "message" for entries queued before this field existed.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "message";

    /// <summary>Sender-local origin timestamp (ticks) — same value used for the message_id/history entry, unchanged across retries.</summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }

    /// <summary>message_id this one replies to (protocol §5/§7), or null.</summary>
    [JsonPropertyName("reply_to")] public string? ReplyToId { get; set; }

    /// <summary>Set for a recipient who is not one of our contacts (a group member): the key to encrypt for and where to reach them.</summary>
    [JsonPropertyName("recipient_enc_key")] public string? RecipientEncKey { get; set; }
    [JsonPropertyName("recipient_servers")] public List<Contacts.ServerEntry>? RecipientServers { get; set; }

    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("last_attempt_at")] public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// When the relay said it delivered this letter, while the contact's acknowledgement has not come yet (<c>null</c> = not delivered: waiting for the contact). The relay
    /// calls a letter delivered once it wrote it to the contact's connection, which may already be dead: the letter stays until the contact confirms having it.
    /// </summary>
    [JsonPropertyName("sent_at")] public DateTime? SentAt { get; set; }

    /// <summary>
    /// The user stopped the waiting for this letter: the periodic run leaves it alone and it no longer keeps the app alive in the background. It stays queued
    /// and goes out like any other when its contact is seen online or when the user asks for a flush.
    /// </summary>
    [JsonPropertyName("parked")] public bool Parked { get; set; }

    /// <summary>How many times a delivered letter was sent again for lack of an acknowledgement.</summary>
    [JsonPropertyName("resends")] public int Resends { get; set; }
}

