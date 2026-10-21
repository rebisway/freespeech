/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace FreeSpeech.Client.Crypto;

/// <summary>
/// The only place the asymmetric and AEAD primitives are called: X25519, Ed25519 and ChaCha20-Poly1305
/// (RFC 7748, RFC 8032, RFC 8439 — the same algorithms and byte formats as libsodium, so data and identities
/// made with the former libsodium backend stay valid). Implemented with BouncyCastle, which is fully managed:
/// it runs unchanged on Windows, Android and iOS, where libsodium ships no native library for .NET.
/// HKDF, SHA-256 and the random generator come from <see cref="System.Security.Cryptography"/>.
/// </summary>
public static class Primitives
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SignatureSize = 64;

    // ------------------------------------------------------------------
    // X25519 (key agreement)
    // ------------------------------------------------------------------

    /// <summary>The X25519 public key of a 32-byte private key (clamped internally, as libsodium does).</summary>
    public static byte[] X25519PublicKey(byte[] privateKey)
    {
        var pub = new byte[X25519.PointSize];
        X25519.ScalarMultBase(privateKey, 0, pub, 0);
        return pub;
    }

    /// <summary>A fresh random X25519 key pair.</summary>
    public static (byte[] Private, byte[] Public) X25519Generate()
    {
        var priv = RandomNumberGenerator.GetBytes(KeySize);
        return (priv, X25519PublicKey(priv));
    }

    /// <summary>The raw 32-byte shared secret, or null when the peer's key is a low-order point (all-zero result), which libsodium refuses too.</summary>
    public static byte[]? X25519Agree(byte[] privateKey, byte[] peerPublicKey)
    {
        var secret = new byte[X25519.PointSize];
        if (!X25519.CalculateAgreement(privateKey, 0, peerPublicKey, 0, secret, 0)) return null;
        return secret.All(b => b == 0) ? null : secret;
    }

    // ------------------------------------------------------------------
    // Ed25519 (signatures); the private key is the 32-byte seed
    // ------------------------------------------------------------------

    public static byte[] Ed25519PublicKey(byte[] seed)
    {
        var pub = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(seed, 0, pub, 0);
        return pub;
    }

    public static byte[] Ed25519Sign(byte[] seed, byte[] data)
    {
        var pub = Ed25519PublicKey(seed);
        var sig = new byte[SignatureSize];
        Ed25519.Sign(seed, 0, pub, 0, null, data, 0, data.Length, sig, 0);
        return sig;
    }

    public static bool Ed25519Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        if (publicKey.Length != Ed25519.PublicKeySize || signature.Length != SignatureSize) return false;
        return Ed25519.Verify(signature, 0, publicKey, 0, null, data, 0, data.Length);
    }

    // ------------------------------------------------------------------
    // ChaCha20-Poly1305 (IETF, 12-byte nonce); output = ciphertext ‖ 16-byte tag
    // ------------------------------------------------------------------

    public static byte[] AeadEncrypt(byte[] key, byte[] nonce, byte[]? associatedData, byte[] plaintext) =>
        AeadEncrypt(key, nonce, associatedData, plaintext, 0, plaintext.Length);

    public static byte[] AeadEncrypt(byte[] key, byte[] nonce, byte[]? associatedData, byte[] buffer, int offset, int count)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, associatedData));
        var output = new byte[cipher.GetOutputSize(count)];
        var n = cipher.ProcessBytes(buffer, offset, count, output, 0);
        cipher.DoFinal(output, n);
        return output;
    }

    /// <summary>The plaintext, or null if the tag does not verify (wrong key, tampered data, wrong associated data).</summary>
    public static byte[]? AeadDecrypt(byte[] key, byte[] nonce, byte[]? associatedData, byte[] ciphertextAndTag)
    {
        if (ciphertextAndTag.Length < TagSize) return null;
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, associatedData));
            var output = new byte[cipher.GetOutputSize(ciphertextAndTag.Length)];
            var n = cipher.ProcessBytes(ciphertextAndTag, 0, ciphertextAndTag.Length, output, 0);
            n += cipher.DoFinal(output, n);
            return n == output.Length ? output : output[..n];
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }
}
