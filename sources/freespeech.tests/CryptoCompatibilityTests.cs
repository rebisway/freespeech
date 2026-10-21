/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Backup;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>
/// Fixed vectors, so any implementation (another language, another platform) can check itself against FreeSpeech: the identity
/// derived from three phrases (Argon2id, protocol §1), a signature, a sealed message, a letter bound to its sender and recipient
/// (§5), a group key wrap, an OTC message and the backups. Regenerated when the identity derivation and the letter signature changed
/// (2026-10-05); the OTC and group-message vectors are older and unchanged, which shows those formats did not move.
/// </summary>
public class CryptoCompatibilityTests
{
    private const string P1 = "the quick brown fox jumps over the lazy dog while the band plays on and on all night";
    private const string P2 = "a journey of a thousand miles begins with a single step taken on a quiet morning";
    private const string P3 = "to be or not to be that is the question whether tis nobler in the mind to suffer";

    private const string DiscoveryId = "feec51fa19910284bac758a7630af2804ee6790937817a9196311ce747a5d127";
    private const string EncPub = "g7Lj1NeElyAIJ5ZTkeo5nHffauZjDahgVbwt5MZfkR4=";
    private const string SignPub = "Z/RJ0sbwHGPiqnHCtlXk72ZRsSq8geG27cbfskfO6Zw=";
    private const string Sig = "RGw/VEs1ZOA5Z+MazHJn3EQ/POsm4Onh+6tDOPB8M5rnTR0D+/beeTQwJVwG5hoPpQZO50MZI1JhCyKJP5QVDA==";
    private const string Sealed = "ar6BYMMhdVaEQTA853lDvQSj/24EjospPYcI0qarn3uQ8sP4XZMJXBHJ1dS0TZgdSqtAbVpP7S52DY/R1QbJDshzAYaUEPBA";
    private const string Letter = "mglxsFNgSksTET69/t6pKg5JdSRC9GdaKsEFBduvE1Ap7sIJWFvVgVhquARIzEo9jX6Bf2TIxwzKmEIwQBW2buRl+COJ4l/S7Kx30GUXnGJm5Fo8PVifP80L3LWbZYQgy6+U8VjTloSV7huJ6uz+oTfx5NG5fdwA3Ue5YgN1OXzuIQaJzjBZ6MXGdFsb8vllh9OiPm4QRV1zAQ+qW6hU76LdxesDx6YLxjAU0O2Xb0QnAfHhfjNX+YA9loLtdCaW4JCZcMXL4KR3xZEQRHgqaHFg1hjK83BYlDeasKM=";
    private const string Otc = "LcqJMi9SZLj6q9mPFdT/qJFY4IxSRbtlskPbod0XrfxENaIIyy8=";
    private const string GroupSeal = "37EP3/SfsQUUjkfMtcoAzQiElJlBAsEfcEhFixn4jqqSzqFgN2k1g2kgZ8O/6pjy";
    private const string GroupWrap = "qLOfPnXBSWHXMH4+FfmXzbT4diACsdm0jXBMhf+iDRDPPXzYn3vOImYDFX3Ii1NNxI7VEft10SD3QGAISTOM5fbLwdEJZ6ajo2+CWzdnpSxAMqHSzSOaGMVHz/ckUK7XzXqIWCgbCXQ=";
    private const string BackupV1 = "GlBrCVK52qc4O+h291JYWESjnAzLBSfQb9vScvvil+XjNfgsvC/RoTQ=";
    private const string BackupStream = "RlNCSwIdAAAA3d46mKZh6mlYJhMjkzGBdKHxEFR7/rLhWowOwoP8LjO4o76K1QQQWPw=";

    private static readonly UserIdentity Id = IdentityService.Derive(P1, P2, P3, "x", IdentityService.Default);
    private static readonly byte[] GroupKey = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public void TheSamePhrases_giveTheSameIdentity()
    {
        Assert.Equal(DiscoveryId, Id.DiscoveryId);
        Assert.Equal(EncPub, Convert.ToBase64String(Id.EncryptionPublicKey));
        Assert.Equal(SignPub, Convert.ToBase64String(Id.SigningPublicKey));
    }

    [Fact]
    public void ASignatureIsByteIdentical_andStillVerifies()
    {
        Assert.Equal(Sig, MessageCrypto.Sign("vector-data", Id.SigningPrivateKey));     // Ed25519 is deterministic
        Assert.True(MessageCrypto.Verify("vector-data", Sig, Id.SigningPublicKey));
        Assert.False(MessageCrypto.Verify("vector-datA", Sig, Id.SigningPublicKey));
    }

    [Fact]
    public void AMessageSealedByTheOldCode_isReadByTheNewOne()
    {
        Assert.Equal("hello vector", MessageCrypto.TryDecryptFromSender(Sealed, Id.EncryptionPrivateKey));

        var letter = MessageCrypto.TryDecrypt("alice-id", Id.DiscoveryId, Letter, 12345, Id.EncryptionPrivateKey, Id.SigningPublicKey);
        Assert.NotNull(letter);
        Assert.Equal(("message", "hi there", (string?)null, true), letter!.Value);
    }

    [Fact]
    public void OtcGroupAndBackupData_writtenByTheOldCode_stillOpen()
    {
        Assert.Equal("otc vector", OtcCrypto.TryDecryptWithOtc(Otc, "ABCDEFGHJKLMNPQR"));
        Assert.Null(OtcCrypto.TryDecryptWithOtc(Otc, "ABCDEFGHJKLMNPQS"));

        Assert.Equal("group vector", GroupCrypto.Open(GroupKey, GroupSeal));
        Assert.Equal(GroupKey, GroupCrypto.UnwrapKey(GroupWrap, Id.EncryptionPrivateKey));

        Assert.Equal("backup vector", BackupCrypto.TryDecrypt(BackupV1, Id.EncryptionPrivateKey));
        var plain = new MemoryStream();
        Assert.True(BackupCrypto.TryDecryptStream(new MemoryStream(Convert.FromBase64String(BackupStream)), plain, Id.EncryptionPrivateKey));
        Assert.Equal("stream vector", System.Text.Encoding.UTF8.GetString(plain.ToArray()));
    }

    [Fact]
    public void TheNewCode_roundTrips_andRefusesTamperingAndWeakKeys()
    {
        var (priv, pub) = Primitives.X25519Generate();
        var other = Primitives.X25519Generate();
        Assert.Equal(Primitives.X25519Agree(priv, other.Public), Primitives.X25519Agree(other.Private, pub));   // both sides agree
        Assert.Null(Primitives.X25519Agree(priv, new byte[32]));                                                  // a low-order point gives no secret

        var key = Enumerable.Repeat((byte)1, 32).ToArray();
        var nonce = new byte[12];
        var ct = Primitives.AeadEncrypt(key, nonce, [9], "data"u8.ToArray());
        Assert.Equal(4 + Primitives.TagSize, ct.Length);
        Assert.Equal("data"u8.ToArray(), Primitives.AeadDecrypt(key, nonce, [9], ct));
        Assert.Null(Primitives.AeadDecrypt(key, nonce, [8], ct));                                                 // wrong associated data
        ct[0] ^= 1;
        Assert.Null(Primitives.AeadDecrypt(key, nonce, [9], ct));                                                 // tampered ciphertext

        var msg = MessageCrypto.Encrypt("alice-id", "bob-id", "message", "round trip", 77, Id.EncryptionPublicKey, Id.SigningPrivateKey);
        Assert.Equal(("message", "round trip", (string?)null, true), MessageCrypto.TryDecrypt("alice-id", "bob-id", msg, 77, Id.EncryptionPrivateKey, Id.SigningPublicKey)!.Value);
        Assert.False(MessageCrypto.TryDecrypt("alice-id", "bob-id", msg, 78, Id.EncryptionPrivateKey, Id.SigningPublicKey)!.Value.SignatureValid);   // the ts is signed
    }

    [Fact]
    public void ALetterCannotBeResentToSomeoneElse_andTheClockNeverRepeats()
    {
        var alice = IdentityService.Derive(P1, P2, P3, "alice");
        var bob = IdentityService.Derive(P2, P3, P1, "bob");
        var letter = MessageCrypto.Encrypt(alice.DiscoveryId, bob.DiscoveryId, "message", "for bob only", 5, bob.EncryptionPublicKey, alice.SigningPrivateKey);

        // Bob reads it fine.
        Assert.True(MessageCrypto.TryDecrypt(alice.DiscoveryId, bob.DiscoveryId, letter, 5, bob.EncryptionPrivateKey, alice.SigningPublicKey)!.Value.SignatureValid);

        // Bob decrypts it and re-encrypts the very same content for Carol, claiming it comes from Alice: the signature covers "to = bob".
        var plain = MessageCrypto.TryDecryptFromSender(letter, bob.EncryptionPrivateKey)!;
        var carol = IdentityService.Derive(P3, P1, P2, "carol");
        var resent = MessageCrypto.EncryptForRecipient(plain, carol.EncryptionPublicKey);
        Assert.False(MessageCrypto.TryDecrypt(alice.DiscoveryId, carol.DiscoveryId, resent, 5, carol.EncryptionPrivateKey, alice.SigningPublicKey)!.Value.SignatureValid);

        // A self-sync copy carries the real recipient and still verifies on the sender's other device...
        var copy = MessageCrypto.Encrypt(alice.DiscoveryId, alice.DiscoveryId, "message", "hello", 6, alice.EncryptionPublicKey, alice.SigningPrivateKey, origTo: bob.DiscoveryId);
        Assert.True(MessageCrypto.TryDecrypt(alice.DiscoveryId, alice.DiscoveryId, copy, 6, alice.EncryptionPrivateKey, alice.SigningPublicKey)!.Value.SignatureValid);
        // ...but orig_to means nothing on an envelope that is not addressed to the sender itself.
        var forged = MessageCrypto.Encrypt(alice.DiscoveryId, bob.DiscoveryId, "message", "hello", 6, bob.EncryptionPublicKey, alice.SigningPrivateKey, origTo: carol.DiscoveryId);
        Assert.False(MessageCrypto.TryDecrypt(alice.DiscoveryId, bob.DiscoveryId, forged, 6, bob.EncryptionPrivateKey, alice.SigningPublicKey)!.Value.SignatureValid);

        // Message ids never repeat on one installation, and the date stays readable.
        var seen = new HashSet<long>();
        for (var i = 0; i < 1_000; i++) Assert.True(seen.Add(FreeSpeech.Client.MessageClock.Next()));   // a burst runs at most 0.4 ms ahead per message
        Assert.True(Math.Abs(new DateTime(FreeSpeech.Client.MessageClock.Next(), DateTimeKind.Utc).Subtract(DateTime.UtcNow).TotalSeconds) < 5);
    }
}