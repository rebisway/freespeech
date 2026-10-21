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

namespace FreeSpeech.Client.Backup;

/// <summary>
/// Symmetric encryption for backup files (protocol §2), same primitives
/// as <see cref="FreeSpeech.Client.Contacts.OtcCrypto"/> but keyed by
/// HKDF-SHA256 over the identity's own encryption private key seed instead
/// of an OTC. Deterministic: re-deriving the same identity from the same 3
/// phrases on another device yields the same key, so a backup file is only
/// ever readable by that identity — exactly the multi-device transfer case
/// it exists for, with no extra passphrase to manage.
/// </summary>
public static class BackupCrypto
{
    private const string HkdfSalt = "FreeSpeech-Backup";
    private const string HkdfInfo = "backup-key";

    public static string Encrypt(string json, byte[] identityEncryptionPrivateKey)
    {
        var key = DeriveKey(identityEncryptionPrivateKey);
        var data = Encoding.UTF8.GetBytes(json);
        var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
        var ciphertext = Primitives.AeadEncrypt(key, nonce, null, data);

        var output = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(output, 0);
        ciphertext.CopyTo(output, nonce.Length);
        return Convert.ToBase64String(output);
    }

    public static string? TryDecrypt(string base64Ciphertext, byte[] identityEncryptionPrivateKey)
    {
        try
        {
            var key = DeriveKey(identityEncryptionPrivateKey);
            var input = Convert.FromBase64String(base64Ciphertext);
            if (input.Length <= Primitives.NonceSize) return null;

            var nonce = input[..Primitives.NonceSize];
            var ciphertext = input[Primitives.NonceSize..];
            var plainBytes = Primitives.AeadDecrypt(key, nonce, null, ciphertext);
            return plainBytes == null ? null : Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Streaming format: "FSBK" | version | blocks. Each block =
    // uint32 ciphertext length | nonce(12) | ChaCha20-Poly1305 ciphertext,
    // with AD = block index (8 bytes) + final flag (1 byte), so blocks cannot
    // be reordered, dropped or the file truncated without detection. Bounded
    // memory (1 MiB) whatever the attachment volume.
    // ------------------------------------------------------------------

    private static readonly byte[] Magic = "FSBK"u8.ToArray();
    private const byte StreamVersion = 2;
    private const int BlockSize = 1024 * 1024;

    /// <summary>True when the file starts with the streaming magic (else it is a legacy base64 backup).</summary>
    public static bool IsStreamFormat(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return fs.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>The same check on an open, seekable stream; the position is put back where it was.</summary>
    public static bool IsStreamFormat(Stream stream)
    {
        var start = stream.Position;
        var head = new byte[Magic.Length];
        var ok = stream.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
        stream.Position = start;
        return ok;
    }

    public static void EncryptStream(Stream plain, Stream output, byte[] identityEncryptionPrivateKey)
    {
        var key = DeriveKey(identityEncryptionPrivateKey);
        output.Write(Magic);
        output.WriteByte(StreamVersion);

        var buffer = new byte[BlockSize];
        var next = new byte[BlockSize];
        var read = ReadFully(plain, buffer);
        for (long index = 0; ; index++)
        {
            var nextRead = read == BlockSize ? ReadFully(plain, next) : 0;
            var isFinal = nextRead == 0;

            var nonce = RandomNumberGenerator.GetBytes(Primitives.NonceSize);
            var ciphertext = Primitives.AeadEncrypt(key, nonce, BlockAd(index, isFinal), buffer, 0, read);
            output.Write(BitConverter.GetBytes((uint)ciphertext.Length));
            output.Write(nonce);
            output.Write(ciphertext);

            if (isFinal) return;
            (buffer, next) = (next, buffer);
            read = nextRead;
        }
    }

    /// <summary>Decrypts a stream into <paramref name="plainOut"/>; false if the key is wrong, the file is corrupted, reordered or truncated.</summary>
    public static bool TryDecryptStream(Stream input, Stream plainOut, byte[] identityEncryptionPrivateKey)
    {
        try
        {
            var key = DeriveKey(identityEncryptionPrivateKey);
            var head = new byte[Magic.Length + 1];
            if (input.Read(head, 0, head.Length) != head.Length || !head.AsSpan(0, Magic.Length).SequenceEqual(Magic) || head[^1] != StreamVersion)
                return false;

            var lenBytes = new byte[4];
            var nonce = new byte[Primitives.NonceSize];
            for (long index = 0; ; index++)
            {
                if (input.Read(lenBytes, 0, 4) != 4) return false; // truncated before a final block
                var len = BitConverter.ToUInt32(lenBytes);
                if (len > BlockSize + 64) return false;
                if (input.Read(nonce, 0, nonce.Length) != nonce.Length) return false;
                var ciphertext = new byte[len];
                if (ReadFully(input, ciphertext) != len) return false;

                var isFinal = input.Position == input.Length;
                var plain = Primitives.AeadDecrypt(key, nonce, BlockAd(index, isFinal), ciphertext);
                if (plain == null) return false;
                plainOut.Write(plain);
                if (isFinal) return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BlockAd(long index, bool isFinal)
    {
        var ad = new byte[9];
        BitConverter.GetBytes(index).CopyTo(ad, 0);
        ad[8] = isFinal ? (byte)1 : (byte)0;
        return ad;
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static byte[] DeriveKey(byte[] identityEncryptionPrivateKey) => HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        identityEncryptionPrivateKey,
        outputLength: 32,
        salt: Encoding.UTF8.GetBytes(HkdfSalt),
        info: Encoding.UTF8.GetBytes(HkdfInfo));
}

