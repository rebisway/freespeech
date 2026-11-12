/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Contacts;

/// <summary>One relay server this identity is reachable on, in preference order (protocol §3/§8).</summary>
public class ServerEntry
{
    [JsonPropertyName("url")] public required string Url { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }

    /// <summary>True for a server that restricts who may sign up (a private server): nobody tries it who is not let in on it. Omitted when false.</summary>
    [JsonPropertyName("restricted")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Restricted { get; set; }

    /// <summary>In a card: the identifier of a restricted server in place of its address (<see cref="Relay.CircleKeys.Tag"/>); the members of that server recognise it, nobody else learns the address. Omitted for an open server.</summary>
    [JsonPropertyName("circle")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Circle { get; set; }

    /// <summary>My own restricted server: the key its administrator gave the members (never written in a card), the one before it for a while after a renewal, and until when.</summary>
    [JsonPropertyName("circle_key")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CircleKey { get; set; }
    [JsonPropertyName("old_circle_key")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OldCircleKey { get; set; }
    [JsonPropertyName("old_circle_key_until")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? OldCircleKeyUntil { get; set; }

    /// <summary>A copy of an own server entry, circle keys included (the copy of a card entry has none).</summary>
    public ServerEntry Copy() => new() { Url = Url, Priority = Priority, Restricted = Restricted, Circle = Circle, CircleKey = CircleKey, OldCircleKey = OldCircleKey, OldCircleKeyUntil = OldCircleKeyUntil };
}

/// <summary>
/// Exportable contact card (protocol §3). It embeds no
/// NextOnline/PollIntervalSeconds scheduling — a rendezvous
/// after import is handled by the existing generic WAITING/READY flags
/// (§9), not duplicated per-feature.
///
/// EXPORT FLOW (A → B, out-of-band): A generates an OTC, embeds it here,
/// shares the base64 card (QR, copy/paste, ...). The server never sees it.
///
/// IMPORT FLOW (B receives A's card): B builds its own card (without OTC),
/// encrypts it with A's OTC (Envelope crypto=0), sends it to A via the relay.
///
/// CONFIRM/FINALIZE: A decrypts with a stored OTC, confirms (auto for "qr",
/// manual accept for "manual"), replies with its own card encrypted the same
/// way. Both sides then store a confirmed Contact.
/// </summary>
public class ContactCard
{
    [JsonPropertyName("version")] public string Version { get; set; } = "2.0";

    [JsonPropertyName("nickname")] public required string Nickname { get; set; }

    [JsonPropertyName("photo")] public string? Photo { get; set; }

    /// <summary>Short free text about the person (at most 300 characters), shown on their card.</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("discovery_id")] public required string DiscoveryId { get; set; }

    [JsonPropertyName("encryption_public_key")] public required string EncryptionPublicKey { get; set; }

    [JsonPropertyName("signing_public_key")] public required string SigningPublicKey { get; set; }

    /// <summary>Ordered by preference (§8 multi-server routing).</summary>
    [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// Ed25519 public key of the revocation key derived from the owner's phrases (§1). The receiver pins the first one it sees and never
    /// replaces it: a later migration notice (<c>identity_moved</c>) counts only when signed by it. Null on cards from before it existed.
    /// </summary>
    [JsonPropertyName("revocation_public_key")] public string? RevocationPublicKey { get; set; }

    /// <summary>Announced capability only — never the push token itself (§9).</summary>
    [JsonPropertyName("enable_push")] public bool EnablePush { get; set; }

    /// <summary>
    /// One-Time Code, present only in the first card (A → B, out-of-band).
    /// Null in cards that travel over the server (already OTC-encrypted).
    ///   "qr"     export: valid 10 minutes.
    ///   "manual" export: valid 1 month.
    /// </summary>
    [JsonPropertyName("otc")] public string? Otc { get; set; }

    /// <summary>"manual" or "qr" — null once past the first, out-of-band card.</summary>
    [JsonPropertyName("flag")] public string? Flag { get; set; }

    /// <summary>UTC Unix timestamp (milliseconds).</summary>
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
}
