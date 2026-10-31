/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Relay;

/// <summary>An identity the server names as one of its administrator's tools, with the public keys that hash to its id (checked before it is trusted for anything).</summary>
public class ProvisionerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enc_key")] public string EncKey { get; set; } = "";
    [JsonPropertyName("sign_key")] public string SignKey { get; set; } = "";
}

/// <summary>
/// Client-side mirror of the server's wire format for the raw WebSocket
/// relay transport (protocol §11). Plain JSON, one object per frame —
/// deliberately not tied to any RPC framework so it stays trivial to
/// reimplement in any language.
/// </summary>
public class WsMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("discovery_id")]
    public string? DiscoveryId { get; set; }

    [JsonPropertyName("envelope")]
    public Envelope? Envelope { get; set; }

    [JsonPropertyName("delivered")]
    public bool? Delivered { get; set; }

    /// <summary>True if the sender has other active connections besides the one that sent this envelope (protocol §1/§5) — see server-side WsMessage for the full rationale.</summary>
    [JsonPropertyName("self_multi_device")]
    public bool? SelfMultiDevice { get; set; }

    [JsonPropertyName("online")]
    public bool? Online { get; set; }

    [JsonPropertyName("message")]
    public string? ErrorMessage { get; set; }

    /// <summary>"error" for a refused "send": "quota_exceeded" or "rate_limited".</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>"error" with a code: seconds until sending is allowed again.</summary>
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; set; }

    /// <summary>"registered": bytes an identity may send per 24 hours on this server (absent = no quota).</summary>
    [JsonPropertyName("daily_quota")]
    public long? DailyQuota { get; set; }

    /// <summary>"send_result": bytes this identity may still send in the current 24 hours (absent = no quota).</summary>
    [JsonPropertyName("quota_left")]
    public long? QuotaLeft { get; set; }

    /// <summary>"register": my public keys (base64); they hash to my discovery id (protocol §11).</summary>
    [JsonPropertyName("enc_key")]
    public string? EncKey { get; set; }

    [JsonPropertyName("sign_key")]
    public string? SignKey { get; set; }

    /// <summary>"server_redirect": servers that can take this client instead, best first.</summary>
    [JsonPropertyName("servers")]
    public List<string>? Servers { get; set; }

    /// <summary>"server_redirect": "load" or "admin".</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>"registered": token for the HTTP calls of this connection (X-Flag-Token).</summary>
    [JsonPropertyName("http_token")]
    public string? HttpToken { get; set; }

    /// <summary>"challenge": the value to sign.</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>"register": invitation token given by the administrator of a private server (single use).</summary>
    [JsonPropertyName("invitation")]
    public string? Invitation { get; set; }

    /// <summary>"registered": who may register on this server, "open" or "allowlist" (private).</summary>
    [JsonPropertyName("access")]
    public string? Access { get; set; }

    /// <summary>"registered" and "server_state": on a server that restricts who signs up, the key its members use to recognise it in a contact's card (base64, 32 bytes).</summary>
    [JsonPropertyName("circle_key")]
    public string? CircleKey { get; set; }

    /// <summary>"registered": the identities of the server's administrator tools (the console), whose group invitations this client may accept without having them as a contact.</summary>
    [JsonPropertyName("provisioners")]
    public List<ProvisionerInfo>? Provisioners { get; set; }

    /// <summary>"register": ask the administrator of a private server to be allowed (§10 "Access requests"). Only after the user agreed.</summary>
    [JsonPropertyName("request_access")]
    public bool? RequestAccess { get; set; }

    /// <summary>"register" with request_access: the name the person goes by (at most 64 characters).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>"register" with request_access: a few words for the administrator (at most 200 characters).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>"register_proof": Ed25519 signature of the challenge (base64).</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>"push_register" / "push_refresh": "fcm" or "apns" (protocol §9).</summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Result of "push_*".</summary>
    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }
}

/// <summary>
/// What a client needs to prove its identity to a relay server (protocol §11): both public keys, and the signing seed that
/// answers the server's challenge. The seed never leaves the process.
/// </summary>
public sealed record RegisterAuth(byte[] EncPublicKey, byte[] SignPublicKey, byte[] SignSeed)
{
    /// <summary>The exact bytes signed: context, the server's nonce, and my discovery id.</summary>
    public static byte[] Challenge(string nonce, string discoveryId) => System.Text.Encoding.UTF8.GetBytes($"freespeech-register-v1|{nonce}|{discoveryId.ToLowerInvariant()}");
}

/// <summary>What a device registers so a relay server can wake it (protocol §9): push network and its token. The relay picks the gateway.</summary>
public sealed record PushTokenInfo(string Platform, string Token);

public class Envelope
{
    [JsonPropertyName("from")] public required string From { get; set; }
    [JsonPropertyName("to")] public required string To { get; set; }
    [JsonPropertyName("crypto")] public int Crypto { get; set; } = 1;
    [JsonPropertyName("msg")] public required string Msg { get; set; }

    /// <summary>
    /// Sender-local timestamp (UTC ticks), forming the message_id of protocol
    /// §5 together with From — used client-side to dedupe and order local
    /// history, since messages can cross in flight (especially in groups).
    /// </summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }

    /// <summary>message_id ("{From}:{ts}") of the message this one replies to, or null (protocol §5/§7).</summary>
    [JsonPropertyName("reply_to")] public string? ReplyTo { get; set; }
}
