/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Relay.Models;

/// <summary>An identity the server names as one of its administrator's tools: its id and the public keys that hash to it (the client checks they do).</summary>
public class ProvisionerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enc_key")] public string EncKey { get; set; } = "";
    [JsonPropertyName("sign_key")] public string SignKey { get; set; } = "";
}

/// <summary>
/// Wire format for the raw WebSocket relay transport (protocol §11).
/// One JSON object per frame; <see cref="Type"/> selects which of the other
/// fields are meaningful. Deliberately a single flat class instead of a
/// polymorphic hierarchy — keeps the wire format trivial to reimplement in
/// any language without a JSON-schema-per-type dance.
/// </summary>
public class WsMessage
{
    /// <summary>
    /// "register" | "register_proof" | "send" | "is_online" | "push_register" | "push_refresh" | "push_unregister" (client -> server)
    /// "challenge" | "registered" | "send_result" | "is_online_result" | "message" | "push_result" | "peer_online" | "server_state" | "server_redirect" | "error" (server -> client)
    /// "peer_online" carries the discovery id of a contact that just connected and has a WAITING flag from this client.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>
    /// Client-chosen correlation id, echoed back on the matching response.
    /// Absent on "register", "registered" and server-pushed "message".
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    /// <summary>Discovery id for "register" and "is_online"/"is_online_result".</summary>
    [JsonPropertyName("discovery_id")]
    public string? DiscoveryId { get; set; }

    /// <summary>Envelope payload for "send" (client -> server) and "message" (server -> client).</summary>
    [JsonPropertyName("envelope")]
    public Envelope? Envelope { get; set; }

    /// <summary>Result of "send": true if at least one recipient connection received the push.</summary>
    [JsonPropertyName("delivered")]
    public bool? Delivered { get; set; }

    /// <summary>
    /// Result of "send": true if the sender has other active connections
    /// besides the one that sent this envelope (protocol §1/§5) — tells
    /// the client whether it needs to additionally send itself an
    /// asymmetrically-self-encrypted copy for live multi-device sync, since
    /// the server can no longer usefully echo a ciphertext addressed to a
    /// third party (crypto=1 is now real E2E, not readable by the sender's
    /// other devices).
    /// </summary>
    [JsonPropertyName("self_multi_device")]
    public bool? SelfMultiDevice { get; set; }

    /// <summary>Result of "is_online".</summary>
    [JsonPropertyName("online")]
    public bool? Online { get; set; }

    /// <summary>"register": the client's X25519 public key (base64, 32 bytes); with <see cref="SignKey"/> it hashes to the discovery id.</summary>
    [JsonPropertyName("enc_key")]
    public string? EncKey { get; set; }

    /// <summary>"server_redirect" (server → client): servers that can take this client instead, best first (§10 "Moving a client").</summary>
    [JsonPropertyName("servers")]
    public List<string>? Servers { get; set; }

    /// <summary>"server_redirect": why — "load" (this server is full) or "admin" (an administrator is draining it).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>"registered" (server → client): token to present as "X-Flag-Token" on /api/flags calls (valid while this connection lives).</summary>
    [JsonPropertyName("http_token")]
    public string? HttpToken { get; set; }

    /// <summary>"register": the client's Ed25519 public key (base64, 32 bytes).</summary>
    [JsonPropertyName("sign_key")]
    public string? SignKey { get; set; }

    /// <summary>"challenge" (server → client): a fresh random value the client must sign.</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>"register": invitation token given by the administrator of a private server (single use; only read when the server restricts access).</summary>
    [JsonPropertyName("invitation")]
    public string? Invitation { get; set; }

    /// <summary>"registered" (server → client): who may register here, "open" or "allowlist" (a private server) — so the app can say what kind of server it is on.</summary>
    [JsonPropertyName("access")]
    public string? Access { get; set; }

    /// <summary>"registered" and "server_state" (server → client), on a server that restricts who signs up: the key that lets its members recognise it in a contact's card (base64, 32 bytes).</summary>
    [JsonPropertyName("circle_key")]
    public string? CircleKey { get; set; }

    /// <summary>"registered": the identities of this server's administrator tools (the console), with their public keys, whose group invitations a client may accept without having them as a contact (§10 "Groups of a private server"). Omitted when there are none.</summary>
    [JsonPropertyName("provisioners")]
    public List<ProvisionerInfo>? Provisioners { get; set; }

    /// <summary>"register": the identity asks the administrator of a private server to be allowed (§10 "Access requests"); sent only after the user agreed.</summary>
    [JsonPropertyName("request_access")]
    public bool? RequestAccess { get; set; }

    /// <summary>"register" with <see cref="RequestAccess"/>: the name the person goes by (at most 64 characters, shown to the administrator).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>"register" with <see cref="RequestAccess"/>: a few words for the administrator (at most 200 characters).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>"register_proof": Ed25519 signature (base64) of the challenge, made with the signing key.</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>"push_register" / "push_refresh": the push network, "fcm" or "apns" (protocol §9).</summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    /// <summary>"push_register" / "push_refresh": the device token given by the push network.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Result of "push_*" (type "push_result").</summary>
    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    /// <summary>Human-readable detail for "error". Not to be confused with type=="message" (a push).</summary>
    [JsonPropertyName("message")]
    public string? ErrorMessage { get; set; }

    /// <summary>"error" for a refused "send": "quota_exceeded" (the 24-hour volume of this identity or address is used up) or "rate_limited" (it sends too fast).</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>"error" with <see cref="Code"/>: seconds until the client may send again.</summary>
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; set; }

    /// <summary>"registered": bytes an identity may send per 24 hours on this server. Omitted when there is no quota.</summary>
    [JsonPropertyName("daily_quota")]
    public long? DailyQuota { get; set; }

    /// <summary>"send_result": bytes this identity may still send in the current 24 hours. Omitted when there is no quota.</summary>
    [JsonPropertyName("quota_left")]
    public long? QuotaLeft { get; set; }
}
