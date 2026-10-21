/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using FreeSpeech.Client.Crypto;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// Crypto=0 (OTC symmetric) operations for contact exchange (protocol §3),
/// with the primitives used in IdentityService (ChaCha20-Poly1305, HKDF-SHA256).
/// Message crypto (crypto=1, X25519 ECDH per message) is in <see cref="MessageCrypto"/> (§5).
/// </summary>
public static class OtcCrypto
{
    private const string OtcAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private const int OtcLength = 16;
    private const string HkdfSalt = "FreeSpeech-OTC";
    private const string HkdfInfo = "sym-key";

    /// <summary>Generates a random 16-character alphanumeric One-Time Code.</summary>
    public static string GenerateOtc()
    {
        var sb = new StringBuilder(OtcLength);
        var bytes = RandomNumberGenerator.GetBytes(OtcLength);
        foreach (var b in bytes)
            sb.Append(OtcAlphabet[b % OtcAlphabet.Length]);
        return sb.ToString();
    }

    /// <summary>Encrypts a plaintext string with an OTC-derived key. Returns base64 (nonce prepended).</summary>
    public static string EncryptWithOtc(string plaintext, string otc)
    {
        var key = DeriveOtcKey(otc);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
        var ciphertext = Primitives.AeadEncrypt(key, nonce, null, data);

        var output = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(output, 0);
        ciphertext.CopyTo(output, nonce.Length);
        return Convert.ToBase64String(output);
    }

    /// <summary>Decrypts a base64 OTC-encrypted payload. Null if the OTC is wrong or data is tampered.</summary>
    public static string? TryDecryptWithOtc(string base64Ciphertext, string otc)
    {
        try
        {
            var key = DeriveOtcKey(otc);
            var input = Convert.FromBase64String(base64Ciphertext);
            if (input.Length <= Primitives.NonceSize) return null;

            var nonce = input[..Primitives.NonceSize];
            var ciphertext = input[Primitives.NonceSize..];
            var plainBytes = Primitives.AeadDecrypt(key, nonce, null, ciphertext);
            return plainBytes == null ? null : Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null; // wrong OTC — expected while brute-trying candidates
        }
    }

    /// <summary>Tries every candidate OTC entry. Returns the matching entry and plaintext, or null.</summary>
    public static (OtcEntry Entry, string Plaintext)? TryDecryptWithAnyOtc(
        string base64Ciphertext, IEnumerable<OtcEntry> candidates)
    {
        foreach (var entry in candidates)
        {
            var result = TryDecryptWithOtc(base64Ciphertext, entry.Otc);
            if (result != null) return (entry, result);
        }
        return null;
    }

    private static byte[] DeriveOtcKey(string otc) => HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        Encoding.UTF8.GetBytes(otc),
        outputLength: 32,
        salt: Encoding.UTF8.GetBytes(HkdfSalt),
        info: Encoding.UTF8.GetBytes(HkdfInfo));
}
