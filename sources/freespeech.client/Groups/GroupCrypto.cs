/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Crypto;

namespace FreeSpeech.Client.Groups;

/// <summary>Group key primitives (protocol §7): symmetric group messages, key commitments, and per-member key envelopes reusing the 1-to-1 asymmetric scheme.</summary>
public static class GroupCrypto
{
    private const string Salt = "FreeSpeech-Group";
    public const int TagSize = 8;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Derive(byte[] key, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, length, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes(info));

    /// <summary>Prefix of every group message: identifies the key (so group + epoch, also two concurrent keys of one epoch) without revealing the group id to the relay.</summary>
    public static byte[] Tag(byte[] key) => Derive(key, "grp-tag", TagSize);

    public static string TagHex(byte[] key) => Convert.ToHexString(Tag(key)).ToLowerInvariant();

    /// <summary>Commitment to the key, signed inside the GroupState so a key handed over by any peer can be checked.</summary>
    public static string KeyCheck(byte[] key) => Convert.ToBase64String(Derive(key, "grp-check", 32));

    public static bool KeyMatches(byte[] key, string keyCheck) => KeyCheck(key) == keyCheck;

    /// <summary>tag(8) ‖ nonce(12) ‖ ChaCha20-Poly1305(key, plaintext), base64. The tag is bound as associated data.</summary>
    public static string Seal(byte[] key, string plaintext)
    {
        var tag = Tag(key);
        var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
        var ct = Primitives.AeadEncrypt(key, nonce, tag, Encoding.UTF8.GetBytes(plaintext));

        var output = new byte[tag.Length + nonce.Length + ct.Length];
        tag.CopyTo(output, 0);
        nonce.CopyTo(output, tag.Length);
        ct.CopyTo(output, tag.Length + nonce.Length);
        return Convert.ToBase64String(output);
    }

    /// <summary>The tag of a sealed message (hex), or null if it is malformed.</summary>
    public static string? TagOf(string sealedBase64)
    {
        try
        {
            var raw = Convert.FromBase64String(sealedBase64);
            return raw.Length > TagSize + 12 ? Convert.ToHexString(raw.AsSpan(0, TagSize)).ToLowerInvariant() : null;
        }
        catch (FormatException) { return null; }
    }

    public static string? Open(byte[] key, string sealedBase64)
    {
        try
        {
            var raw = Convert.FromBase64String(sealedBase64);
            var nonceLen = Primitives.NonceSize;
            if (raw.Length <= TagSize + nonceLen) return null;

            var tag = raw[..TagSize];
            var nonce = raw[TagSize..(TagSize + nonceLen)];
            var ct = raw[(TagSize + nonceLen)..];
            var plain = Primitives.AeadDecrypt(key, nonce, tag, ct);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The group key encrypted for one member (X25519 ephemeral + HKDF + ChaCha20-Poly1305, the §5 crypto=1 scheme).</summary>
    public static string WrapKey(byte[] groupKey, string memberEncryptionPublicKeyBase64) =>
        MessageCrypto.EncryptForRecipient(Convert.ToBase64String(groupKey), Convert.FromBase64String(memberEncryptionPublicKeyBase64));

    public static byte[]? UnwrapKey(string envelope, byte[] myEncryptionPrivateKey)
    {
        var plain = MessageCrypto.TryDecryptFromSender(envelope, myEncryptionPrivateKey);
        if (plain == null) return null;
        try { return Convert.FromBase64String(plain); }
        catch (FormatException) { return null; }
    }

    public static string HashState(string stateJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateJson))).ToLowerInvariant();

    /// <summary>Signs the exact state string (no canonicalization to reimplement elsewhere).</summary>
    public static string SignState(string stateJson, byte[] signingPrivateKey) => MessageCrypto.Sign(stateJson, signingPrivateKey);

    public static bool VerifyState(string stateJson, string signatureBase64, string signerSigningPublicKeyBase64) =>
        MessageCrypto.Verify(stateJson, signatureBase64, Convert.FromBase64String(signerSigningPublicKeyBase64));

    private static string PhotoHash(string? photo) =>
        photo == null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(photo))).ToLowerInvariant();

    public static string GroupProfileSigData(string groupId, string name, string? photo, long ts, string? description = null) => $"gprofile|{groupId}|{name}|{PhotoHash(photo)}|{ts}|{description}";

    /// <summary>What a member signs to announce their revocation key to a group.</summary>
    public static string RevocationPinSigData(string id, string key) => $"revpin|{id}|{key}";

    public static string MemberProfileSigData(string groupId, string id, string nickname, string? photo, long ts, string? description = null) => $"mprofile|{groupId}|{id}|{nickname}|{PhotoHash(photo)}|{ts}|{description}";

    /// <summary>True if <paramref name="photo"/> is absent or valid base64 of at most 100 KiB.</summary>
    public static bool PhotoAcceptable(string? photo)
    {
        if (photo == null) return true;
        if (photo.Length > GroupProfileOp.MaxPhotoBytes * 4 / 3 + 8) return false;
        try { return Convert.FromBase64String(photo).Length <= GroupProfileOp.MaxPhotoBytes; }
        catch (FormatException) { return false; }
    }

    public static string SponsorSigData(string groupId, string candidateId, long ts) => $"gsponsor|{groupId}|{candidateId}|{ts}";
    public static string PinSigData(string groupId, string kind, string target, long ts) => $"pin|{groupId}|{kind}|{target}|{ts}";
    public static string BanSigData(string groupId, string kind, string id, long ts) => $"ban|{groupId}|{kind}|{id}|{ts}";

    public static string MessageSigData(string type, string payload, long ts, string groupId, int epoch, long seq, string? replyTo = null) =>
        $"{type}|{payload}|{ts}|{groupId}|{epoch}|{seq}|{replyTo}";

    /// <summary>discovery_id is SHA-256(enc_pub ‖ sign_pub) hex: a member entry whose id does not match its keys is forged.</summary>
    public static bool MemberIdMatchesKeys(GroupMember m)
    {
        try
        {
            var combined = Convert.FromBase64String(m.EncryptionPublicKey).Concat(Convert.FromBase64String(m.SigningPublicKey)).ToArray();
            return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant() == m.DiscoveryId;
        }
        catch (FormatException) { return false; }
    }
}
