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

namespace FreeSpeech.Client.Storage;

/// <summary>
/// Encrypts the content stored in the local database, field by field (the database file itself is plain SQLite). A sealed value is
/// <c>version(1) ‖ nonce(12) ‖ ChaCha20-Poly1305(key, plaintext)</c>, with a <em>context</em> (table, column and row identity) bound as
/// associated data: a sealed value copied to another row, or another column, does not open. The key is derived from the 32-byte
/// database key (which comes from the identity phrases) by HKDF, so a new identity means a new key (<see cref="ClientDatabase.Rekey"/>).
/// Identifiers (conversation, message id, timestamps, states) are not sealed: queries, ordering and indexes need them.
/// </summary>
public sealed class Vault
{
    private const byte Version = 1;
    private const string Salt = "FreeSpeech-Database";
    private const string Info = "fields-v1";

    private readonly byte[] _key;

    public Vault(byte[] databaseKey)
    {
        if (databaseKey.Length != Primitives.KeySize) throw new ArgumentException("The database key is 32 bytes.", nameof(databaseKey));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, databaseKey, Primitives.KeySize, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes(Info));
    }

    public byte[] Seal(byte[] plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
        var ct = Primitives.AeadEncrypt(_key, nonce, Encoding.UTF8.GetBytes(context), plaintext);
        var sealedValue = new byte[1 + nonce.Length + ct.Length];
        sealedValue[0] = Version;
        nonce.CopyTo(sealedValue, 1);
        ct.CopyTo(sealedValue, 1 + nonce.Length);
        return sealedValue;
    }

    public byte[] SealText(string text, string context) => Seal(Encoding.UTF8.GetBytes(text), context);

    /// <summary>The plaintext, or null when the value is malformed, was sealed under another key or context, or was altered.</summary>
    public byte[]? Open(byte[] sealedValue, string context)
    {
        if (sealedValue.Length < 1 + Primitives.NonceSize + Primitives.TagSize || sealedValue[0] != Version) return null;
        var nonce = sealedValue[1..(1 + Primitives.NonceSize)];
        var ct = sealedValue[(1 + Primitives.NonceSize)..];
        return Primitives.AeadDecrypt(_key, nonce, Encoding.UTF8.GetBytes(context), ct);
    }

    public string? OpenText(byte[] sealedValue, string context) => Open(sealedValue, context) is { } plain ? Encoding.UTF8.GetString(plain) : null;
}
