/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Crypto;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// Asymmetric message encryption (protocol §5, crypto=1): same primitives as
/// <see cref="OtcCrypto"/> but keyed by a fresh ephemeral X25519 pair per message
/// instead of a shared OTC.
///
/// Sign-then-encrypt: the Ed25519 signature travels inside the encrypted
/// payload (the protocol's Envelope has no separate signature field), so the
/// wire layout stays exactly "ephemeral_pub(32) || nonce(12) || ciphertext".
/// </summary>
public static class MessageCrypto
{
    private const string EcdhHkdfSalt = "FreeSpeech-ECDH";
    private const string EcdhHkdfInfo = "msg-key";

    private class LetterPayload
    {
        /// <summary>Letter type (protocol §5): "message" or "identity_update" so far.</summary>
        [JsonPropertyName("type")] public required string Type { get; set; }
        [JsonPropertyName("payload")] public required string Payload { get; set; }
        [JsonPropertyName("sig")] public required string Sig { get; set; }

        /// <summary>
        /// Original recipient, set only on a self-sync copy of a "message"
        /// (an envelope addressed to one's own discovery_id so another of
        /// the sender's devices can decrypt it — see ConsoleApp's self-sync
        /// handling). Unused for other Letter types.
        /// </summary>
        [JsonPropertyName("orig_to")] public string? OrigTo { get; set; }
    }

    /// <summary>
    /// What a 1-to-1 letter signature covers (protocol §5): a domain tag, who sends it, who it is FOR, and the letter itself.
    /// Binding the recipient stops a recipient from re-sending the letter to a third party as if the sender had written it to them.
    /// For a self-sync copy (envelope addressed to the sender) the signed recipient is the real one (<c>orig_to</c>).
    /// </summary>
    public static string SignedText(string from, string to, string type, string payload, long ts) =>
        $"freespeech-letter-v1|{from}|{to}|{type}|{payload}|{ts}";

    /// <summary>Encrypts a Letter of the given <paramref name="type"/> from <paramref name="from"/> to <paramref name="to"/> (discovery ids), signing it with <paramref name="mySigningPrivateKey"/> first.</summary>
    public static string Encrypt(string from, string to, string type, string payload, long ts, byte[] recipientEncPublicKey, byte[] mySigningPrivateKey, string? origTo = null)
    {
        var letter = new LetterPayload
        {
            Type = type,
            Payload = payload,
            Sig = Sign(SignedText(from, origTo ?? to, type, payload, ts), mySigningPrivateKey),
            OrigTo = origTo,
        };
        var json = JsonSerializer.Serialize(letter);
        return EncryptForRecipient(json, recipientEncPublicKey);
    }

    /// <summary>
    /// Decrypts and verifies an incoming ciphertext. Returns null if the
    /// ciphertext is malformed or cannot be decrypted with our key.
    /// <c>SignatureValid</c> reflects whether it was genuinely signed by
    /// <paramref name="senderSigningPublicKey"/> — a false here means the
    /// content decrypted but the signature doesn't check out (should not be
    /// trusted, but is surfaced rather than silently discarded).
    /// </summary>
    public static (string Type, string Payload, string? OrigTo, bool SignatureValid)? TryDecrypt(
        string from, string to, string base64Ciphertext, long ts, byte[] myEncPrivateKey, byte[] senderSigningPublicKey)
    {
        var json = TryDecryptFromSender(base64Ciphertext, myEncPrivateKey);
        if (json == null) return null;

        try
        {
            var letter = JsonSerializer.Deserialize<LetterPayload>(json);
            if (letter == null) return null;

            // orig_to only counts on a copy the sender addressed to itself; anywhere else the envelope's own recipient is the one signed.
            var signedTo = letter.OrigTo != null && from == to ? letter.OrigTo : to;
            var valid = Verify(SignedText(from, signedTo, letter.Type, letter.Payload, ts), letter.Sig, senderSigningPublicKey);
            return (letter.Type, letter.Payload, letter.OrigTo, valid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The type and payload of a ciphertext addressed to me, <b>not verified</b>: only to find which key to verify it with (see <c>FreeSpeechClient</c>, invitations from a stranger).</summary>
    public static (string Type, string Payload)? PeekUnverified(string base64Ciphertext, byte[] myEncPrivateKey)
    {
        var json = TryDecryptFromSender(base64Ciphertext, myEncPrivateKey);
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<LetterPayload>(json) is { } l ? (l.Type, l.Payload) : null; }
        catch (JsonException) { return null; }
    }

    public static string EncryptForRecipient(string plaintext, byte[] recipientEncPublicKey)
    {
        var (ephemeralPriv, ephemeralPub) = Primitives.X25519Generate();
        var sharedSecret = Primitives.X25519Agree(ephemeralPriv, recipientEncPublicKey)
            ?? throw new CryptographicException("X25519 key agreement failed.");

        var symKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, Encoding.UTF8.GetBytes(EcdhHkdfSalt), Encoding.UTF8.GetBytes(EcdhHkdfInfo));

        var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
        var ciphertext = Primitives.AeadEncrypt(symKey, nonce, null, Encoding.UTF8.GetBytes(plaintext));

        var output = new byte[ephemeralPub.Length + nonce.Length + ciphertext.Length];
        ephemeralPub.CopyTo(output, 0);
        nonce.CopyTo(output, ephemeralPub.Length);
        ciphertext.CopyTo(output, ephemeralPub.Length + nonce.Length);
        return Convert.ToBase64String(output);
    }

    public static string? TryDecryptFromSender(string base64Ciphertext, byte[] myEncPrivateKey)
    {
        try
        {
            var input = Convert.FromBase64String(base64Ciphertext);
            const int epLen = 32;
            const int nonceLen = 12;
            if (input.Length <= epLen + nonceLen) return null;

            var ephemeralPub = input[..epLen];
            var nonce = input[epLen..(epLen + nonceLen)];
            var ciphertext = input[(epLen + nonceLen)..];

            var sharedSecret = Primitives.X25519Agree(myEncPrivateKey, ephemeralPub);
            if (sharedSecret == null) return null;

            var symKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, Encoding.UTF8.GetBytes(EcdhHkdfSalt), Encoding.UTF8.GetBytes(EcdhHkdfInfo));
            var plainBytes = Primitives.AeadDecrypt(symKey, nonce, null, ciphertext);
            return plainBytes == null ? null : Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    public static string Sign(string data, byte[] signingPrivateKey)
    {
        return Convert.ToBase64String(Primitives.Ed25519Sign(signingPrivateKey, Encoding.UTF8.GetBytes(data)));
    }

    public static bool Verify(string data, string base64Signature, byte[] signingPublicKey)
    {
        try
        {
            return Primitives.Ed25519Verify(signingPublicKey, Encoding.UTF8.GetBytes(data), Convert.FromBase64String(base64Signature));
        }
        catch
        {
            return false;
        }
    }
}
