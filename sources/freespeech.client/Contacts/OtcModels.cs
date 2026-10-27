/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// A One-Time Code we generated and are still waiting to be consumed by a
/// reply card (protocol §3). Matched against incoming crypto=0 envelopes.
/// </summary>
public class OtcEntry
{
    [JsonPropertyName("otc")] public required string Otc { get; set; }

    /// <summary>"manual" (valid 1 month) or "qr" (valid 10 minutes).</summary>
    [JsonPropertyName("flag")] public required string Flag { get; set; }

    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A contact request we prepared but could not yet deliver because the
/// target was offline. The relay never stores this for us (§9/§10 — it only
/// signals presence via flags) — we keep the encrypted card locally and
/// retry until the target is reachable.
/// </summary>
public class PendingContact
{
    [JsonPropertyName("id")] public required string Id { get; set; }

    /// <summary>Their card as imported (out-of-band), including its OTC.</summary>
    [JsonPropertyName("their_card")] public required ContactCard TheirCard { get; set; }

    /// <summary>Our own card (without OTC), JSON-serialized then OTC-encrypted, base64.</summary>
    [JsonPropertyName("encrypted_our_card")] public required string EncryptedOurCard { get; set; }

    [JsonPropertyName("otc")] public required string Otc { get; set; }

    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    [JsonPropertyName("last_attempt_at")] public DateTime? LastAttemptAt { get; set; }
}

/// <summary>A confirmed contact — public keys and servers only, no private material.</summary>
public class Contact
{
    [JsonPropertyName("discovery_id")] public required string DiscoveryId { get; set; }
    [JsonPropertyName("nickname")] public required string Nickname { get; set; }
    [JsonPropertyName("photo")] public string? Photo { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("encryption_public_key")] public required string EncryptionPublicKey { get; set; }
    [JsonPropertyName("signing_public_key")] public required string SigningPublicKey { get; set; }
    [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = [];
    [JsonPropertyName("confirmed_at")] public DateTime ConfirmedAt { get; set; }

    /// <summary>When the user compared the safety number with this contact and found them equal; null while not verified.</summary>
    [JsonPropertyName("verified_at")] public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// The name I gave this contact, seen by me only (two Thomas in the list). It wins over their own <see cref="Nickname"/>, which
    /// they can change at will and which an identity_update overwrites; this one stays. Null = no private name.
    /// </summary>
    [JsonPropertyName("local_name")] public string? LocalName { get; set; }

    /// <summary>Set once this contact migrated to a new identity and I accepted it: the new discovery_id. Nothing is sent to this one any more.</summary>
    [JsonPropertyName("moved_to")] public string? MovedTo { get; set; }

    /// <summary>When the migration was accepted.</summary>
    [JsonPropertyName("moved_at")] public DateTime? MovedAt { get; set; }

    /// <summary>What I allow when the restricted servers I share with this contact do not answer: wait (default), ask me, or fall back on an open server by myself.</summary>
    [JsonPropertyName("open_fallback")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Relay.OpenFallbackMode OpenFallback { get; set; }

    /// <summary>What this contact told me they allow (route_policy letter): the fallback needs both sides.</summary>
    [JsonPropertyName("peer_open_fallback")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool PeerOpenFallback { get; set; }

    /// <summary>What I see: my private name when I gave one, their nickname otherwise.</summary>
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(LocalName) ? Nickname : LocalName;
}

/// <summary>
/// Wrapper for all crypto=0 payloads (contact exchange). Encrypted with the OTC,
/// then base64 into Envelope.Msg.
/// </summary>
public class OtcEnvelope
{
    /// <summary>
    /// "contact_request" — sender's card (A→B first contact).
    /// "contact_ack"     — no payload, sent immediately on receipt.
    /// "contact_confirm" — confirming party's card, sent after acceptance.
    /// </summary>
    [JsonPropertyName("type")] public required string Type { get; set; }

    [JsonPropertyName("payload")] public string? Payload { get; set; }
}
