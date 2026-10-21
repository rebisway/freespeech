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
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// Creates and recovers user identities from three recovery phrases
/// (protocol §1).
///
/// DERIVATION (deterministic — same 3 phrases always yield the same identity,
/// on any device, with no server involved):
///   seed      = Argon2id(password = phrase1 ‖ 0x00 ‖ phrase2 ‖ 0x00 ‖ phrase3  (each trimmed, UTF-8),
///                        salt = "FreeSpeech-v3-seed", m = 32 MiB, t = 3, p = 1)   (32 bytes)
///   enc_seed  = HKDF-SHA256(seed, salt="FreeSpeech-v2", info="encryption") (32 bytes)
///   sign_seed = HKDF-SHA256(seed, salt="FreeSpeech-v2", info="signing")    (32 bytes)
///   X25519 keypair  &lt;- enc_seed
///   Ed25519 keypair &lt;- sign_seed
///   discovery_id = SHA256(X25519_pub || Ed25519_pub) -> lowercase hex
///
/// Minimum phrase length: 75 Unicode characters each, and all three distinct.
/// </summary>
public static class IdentityService
{
    // Salt/info labels for HKDF — changing these changes every derived identity.
    private const string HkdfSalt = "FreeSpeech-v2";
    private const string HkdfInfoEnc = "encryption";
    private const string HkdfInfoSign = "signing";
    private const string HkdfInfoDatabase = "database-v1";
    private const string HkdfInfoRevocation = "revocation-v1";
    private const int MinPhraseLength = 75;

    /// <summary>True when a phrase meets the minimum length requirement.</summary>
    public static bool IsPhraseValid(string phrase) =>
        !string.IsNullOrEmpty(phrase) && phrase.Trim().Length >= MinPhraseLength;

    /// <summary>
    /// Deterministically derives a <see cref="UserIdentity"/> from three phrases.
    /// Throws <see cref="ArgumentException"/> if a phrase is too short or the
    /// three phrases are not distinct.
    /// </summary>
    public static UserIdentity Derive(string phrase1, string phrase2, string phrase3, string? nickname, KdfCost? cost = null)
    {
        var masterSeed = MasterSeed(phrase1, phrase2, phrase3, cost ?? Current);
        var salt = Encoding.UTF8.GetBytes(HkdfSalt);
        var encSeed = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSeed, 32, salt, Encoding.UTF8.GetBytes(HkdfInfoEnc));
        var signSeed = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSeed, 32, salt, Encoding.UTF8.GetBytes(HkdfInfoSign));

        var encPub = Primitives.X25519PublicKey(encSeed);
        var signPub = Primitives.Ed25519PublicKey(signSeed);

        var combined = new byte[64];
        encPub.CopyTo(combined, 0);
        signPub.CopyTo(combined, 32);
        var discoveryId = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();

        return new UserIdentity
        {
            DiscoveryId = discoveryId,
            EncryptionPublicKey = encPub,
            EncryptionPrivateKey = encSeed,
            SigningPublicKey = signPub,
            SigningPrivateKey = signSeed,
            RevocationPublicKey = Primitives.Ed25519PublicKey(RevocationSeed(masterSeed)),
            Nickname = nickname,
            CreatedAt = DateTime.UtcNow,
        };
    }

    // Argon2id parameters (protocol §1). The salt cannot be per user (the derivation must work from the phrases alone),
    // so it is a fixed public label: it still stops pre-computed tables shared with other applications.
    private const string ArgonSalt = "FreeSpeech-v3-seed";

    /// <summary>Cost of the slow derivation. <see cref="Default"/> is the protocol value; only the tests lower <see cref="Current"/>, to keep thousands of identities cheap.</summary>
    public readonly record struct KdfCost(int MemoryKiB, int Iterations);
    public static readonly KdfCost Default = new(32 * 1024, 3);
    internal static KdfCost Current = Default;

    private static readonly object SeedLock = new();
    private static readonly Dictionary<string, byte[]> Seeds = [];   // a handful at most: one identity per run

    /// <summary>
    /// The deliberately slow step: it makes guessing hand-typed phrases expensive. One result is remembered, because creating
    /// an identity asks for the identity keys and the database key from the same phrases.
    /// </summary>
    private static byte[] MasterSeed(string phrase1, string phrase2, string phrase3, KdfCost? cost = null)
    {
        var c = cost ?? Current;
        ValidatePhrases(phrase1, phrase2, phrase3);
        var password = $"{phrase1.Trim()}\0{phrase2.Trim()}\0{phrase3.Trim()}";
        var cacheKey = $"{c.MemoryKiB}/{c.Iterations}|{password}";
        lock (SeedLock)
        {
            if (Seeds.TryGetValue(cacheKey, out var known)) return (byte[])known.Clone();

            var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                .WithVersion(Argon2Parameters.Version13)
                .WithSalt(Encoding.UTF8.GetBytes(ArgonSalt))
                .WithMemoryAsKB(c.MemoryKiB)
                .WithIterations(c.Iterations)
                .WithParallelism(1)
                .Build();
            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            var seed = new byte[32];
            generator.GenerateBytes(Encoding.UTF8.GetBytes(password), seed);
            if (Seeds.Count >= 256) Seeds.Clear();
            Seeds[cacheKey] = (byte[])seed.Clone();
            return seed;
        }
    }

    private static byte[] RevocationSeed(byte[] masterSeed) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSeed, 32, Encoding.UTF8.GetBytes(HkdfSalt), Encoding.UTF8.GetBytes(HkdfInfoRevocation));

    /// <summary>
    /// The revocation key pair (Ed25519), derived from the phrases only — never stored on a device, which is what makes a wipe signed by
    /// it proof that the phrases were typed (the identity keys sit in the phone's database; a thief has them).
    /// </summary>
    public static (byte[] Seed, byte[] PublicKey) DeriveRevocationKey(string phrase1, string phrase2, string phrase3)
    {
        var seed = RevocationSeed(MasterSeed(phrase1, phrase2, phrase3));
        return (seed, Primitives.Ed25519PublicKey(seed));
    }

    /// <summary>
    /// The 32-byte key that encrypts the local database at rest, derived from the same three phrases with its
    /// own HKDF label — independent of the identity keys and of the backup key. Re-entering the phrases on a
    /// new device yields the same key.
    /// </summary>
    public static byte[] DeriveDatabaseKey(string phrase1, string phrase2, string phrase3) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, MasterSeed(phrase1, phrase2, phrase3), 32, Encoding.UTF8.GetBytes(HkdfSalt), Encoding.UTF8.GetBytes(HkdfInfoDatabase));

    private static void ValidatePhrases(string p1, string p2, string p3)
    {
        if (!IsPhraseValid(p1)) throw new ArgumentException($"Phrase 1 must be at least {MinPhraseLength} characters long.");
        if (!IsPhraseValid(p2)) throw new ArgumentException($"Phrase 2 must be at least {MinPhraseLength} characters long.");
        if (!IsPhraseValid(p3)) throw new ArgumentException($"Phrase 3 must be at least {MinPhraseLength} characters long.");
        if (p1.Trim() == p2.Trim() || p2.Trim() == p3.Trim() || p1.Trim() == p3.Trim())
            throw new ArgumentException("All three phrases must be different.");
    }
}
