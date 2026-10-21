/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// Local cryptographic identity of the running user (protocol §1).
/// Deterministically derived from three recovery phrases (>= 75 chars each).
/// Stored in the local profile directory; private keys never leave the device.
/// There is no recovery if the phrases are lost — that loss is accepted by
/// the protocol design.
/// </summary>
public class UserIdentity
{
    /// <summary>
    /// Unique network identifier derived from both public keys:
    /// SHA256(EncryptionPublicKey || SigningPublicKey) -> lowercase hex (64 chars).
    /// Shared with contacts and servers. Does not reveal the keypairs.
    /// </summary>
    [JsonPropertyName("discovery_id")]
    public required string DiscoveryId { get; set; }

    /// <summary>X25519 public key (32 bytes) — shared with contacts to encrypt messages for this identity.</summary>
    [JsonPropertyName("encryption_public_key")]
    public required byte[] EncryptionPublicKey { get; set; }

    /// <summary>X25519 private key seed (32 bytes). Never transmitted, stored locally only.</summary>
    [JsonPropertyName("encryption_private_key")]
    public required byte[] EncryptionPrivateKey { get; set; }

    /// <summary>Ed25519 public key (32 bytes) — shared with contacts for signature verification.</summary>
    [JsonPropertyName("signing_public_key")]
    public required byte[] SigningPublicKey { get; set; }

    /// <summary>Ed25519 private key seed (32 bytes). Never transmitted, stored locally only.</summary>
    [JsonPropertyName("signing_private_key")]
    public required byte[] SigningPrivateKey { get; set; }

    /// <summary>
    /// Ed25519 public key (32 bytes) of the revocation key, derived from the phrases with its own HKDF label. Only this public half is kept:
    /// the private half exists while the phrases are typed. A remote wipe (§1) counts only when signed by it. Null on identities created
    /// before it existed, until the phrases are typed once (<c>EnableRemoteWipe</c>).
    /// </summary>
    [JsonPropertyName("revocation_public_key")]
    public byte[]? RevocationPublicKey { get; set; }

    /// <summary>Groups where my revocation key was already announced to the members (with a signed member profile): it is announced once per group.</summary>
    [JsonPropertyName("revocation_announced_groups")]
    public List<string> RevocationAnnouncedGroups { get; set; } = [];

    /// <summary>Display name chosen by the user, broadcast to contacts.</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    /// <summary>UTC timestamp when this identity was first created on this device.</summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Profile picture (base64 JPEG/PNG/WebP, at most 100 KiB), broadcast to contacts in the ContactCard and to group co-members.</summary>
    [JsonPropertyName("photo")]
    public string? Photo { get; set; }

    /// <summary>"About me" text (at most 300 characters), broadcast like the nickname and picture.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The public server this identity advertises to contacts (protocol
    /// §8, single-server scope for now — full simultaneous multi-server
    /// client is a later step). Null on identities created before this
    /// field existed, or before ever changing it: <c>ConsoleApp</c> falls
    /// back to whatever server URL it was started with.
    /// </summary>
    [JsonPropertyName("public_server")]
    public string? PublicServer { get; set; }

    /// <summary>
    /// Servers this identity advertises to contacts and stays registered on
    /// (protocol §8), ordered by <c>Priority</c>. Supersedes the single
    /// <see cref="PublicServer"/>, which is migrated into it on first load.
    /// </summary>
    [JsonPropertyName("servers")]
    public List<Contacts.ServerEntry> Servers { get; set; } = [];

    /// <summary>Servers this identity was moved away from (§10 "Moving a client"): not silently re-added from the configured default at the next start.</summary>
    [JsonPropertyName("retired_servers")]
    public List<string> RetiredServers { get; set; } = [];

    /// <summary>
    /// True while the servers are the ones the application gave to a brand-new identity (directories or built-in fallback) and nobody chose them:
    /// a backup or the first contact may still replace them. Cleared as soon as one of these happens or the user edits the list.
    /// </summary>
    [JsonPropertyName("servers_initial")]
    public bool ServersInitial { get; set; }

    /// <summary>When this identity last moved servers on a server's suggestion (UTC): a cap per day stops redirect loops.</summary>
    [JsonPropertyName("moves")]
    public List<DateTime> Moves { get; set; } = [];
}
