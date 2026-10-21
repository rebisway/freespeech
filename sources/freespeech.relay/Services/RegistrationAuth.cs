/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Proof of identity at registration (protocol §11). A discovery id is <c>SHA-256(enc public key ‖ signing public key)</c>,
/// so a client that sends both public keys and signs a fresh server nonce with the signing key proves it owns that id:
/// nobody else can register as it, receive its envelopes, or make senders believe a message was delivered.
/// </summary>
public static class RegistrationAuth
{
    private const string Context = "freespeech-register-v1";

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The exact bytes the client signs.</summary>
    public static byte[] Challenge(string discoveryId, string nonce) => Encoding.UTF8.GetBytes($"{Context}|{nonce}|{discoveryId}");

    /// <summary>True when the two keys hash to <paramref name="discoveryId"/>.</summary>
    public static bool KeysMatchId(string discoveryId, byte[] encKey, byte[] signKey)
    {
        if (encKey.Length != 32 || signKey.Length != 32) return false;
        var combined = new byte[64];
        encKey.CopyTo(combined, 0);
        signKey.CopyTo(combined, 32);
        var expected = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
        return string.Equals(expected, discoveryId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Verify(string discoveryId, string nonce, byte[] signKey, byte[] signature)
    {
        if (signKey.Length != Ed25519.PublicKeySize || signature.Length != Ed25519.SignatureSize) return false;
        var data = Challenge(discoveryId, nonce);
        return Ed25519.Verify(signature, 0, signKey, 0, null, data, 0, data.Length);
    }
}
