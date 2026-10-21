/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FreeSpeech.Client.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// The three recovery phrases in a file protected by a password the user chooses (a "key file"). The file holds nothing but the
/// phrases: whoever has the file AND the password can become the user, so both must be kept apart. Format (all integers big-endian):
/// <c>"FSKEY1" ‖ memoryKiB(4) ‖ iterations(4) ‖ salt(16) ‖ nonce(12) ‖ ChaCha20-Poly1305(key, nonce, aad = everything before the
/// ciphertext, "phrase1\nphrase2\nphrase3")</c>, with <c>key = Argon2id(password, salt, memoryKiB, iterations, p = 1)</c>.
/// The cost is written in the file so it can grow later without breaking old files.
/// </summary>
public static class PhraseVault
{
    public const int MinPasswordLength = 10;

    private static readonly byte[] Magic = "FSKEY1"u8.ToArray();
    private const int SaltSize = 16, NonceSize = 12, HeaderSize = 6 + 4 + 4 + SaltSize + NonceSize;
    private const int MaxMemoryKiB = 256 * 1024, MaxIterations = 10;

    /// <summary>Written cost: 64 MiB, 3 passes (one second on a phone). Only the tests lower it.</summary>
    public static readonly IdentityService.KdfCost DefaultCost = new(64 * 1024, 3);

    public static byte[] Seal(IReadOnlyList<string> phrases, string password, IdentityService.KdfCost? cost = null)
    {
        if (phrases.Count != 3) throw new ArgumentException("Exactly three phrases are expected.", nameof(phrases));
        if (password.Length < MinPasswordLength) throw new ClientException($"The password needs at least {MinPasswordLength} characters.", "vault_password_short", MinPasswordLength);

        var c = cost ?? DefaultCost;
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(6), c.MemoryKiB);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(10), c.Iterations);
        RandomNumberGenerator.Fill(header.AsSpan(14, SaltSize + NonceSize));

        var key = DeriveKey(password, header.AsSpan(14, SaltSize).ToArray(), c.MemoryKiB, c.Iterations);
        var plain = Encoding.UTF8.GetBytes(string.Join('\n', phrases.Select(p => p.Trim())));
        var sealedBytes = Primitives.AeadEncrypt(key, header.AsSpan(14 + SaltSize, NonceSize).ToArray(), header, plain);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plain);
        return [.. header, .. sealedBytes];
    }

    /// <summary>The file starts like a key file (cheap test, before asking for a password).</summary>
    public static bool LooksLikeVault(byte[] file) => file.Length > HeaderSize + Primitives.TagSize && file.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static string[] Open(byte[] file, string password)
    {
        if (!LooksLikeVault(file)) throw new ClientException("This is not a FreeSpeech key file.", "vault_bad_file");
        var memory = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(6));
        var iterations = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(10));
        // A hostile file must not make the phone allocate gigabytes.
        if (memory < 8 || memory > MaxMemoryKiB || iterations < 1 || iterations > MaxIterations) throw new ClientException("This key file is not valid.", "vault_bad_file");

        var header = file[..HeaderSize];
        var key = DeriveKey(password, file.AsSpan(14, SaltSize).ToArray(), memory, iterations);
        var plain = Primitives.AeadDecrypt(key, file.AsSpan(14 + SaltSize, NonceSize).ToArray(), header, file[HeaderSize..]);
        CryptographicOperations.ZeroMemory(key);
        if (plain == null) throw new ClientException("Wrong password, or the file was changed.", "vault_wrong_password");

        var phrases = Encoding.UTF8.GetString(plain).Split('\n');
        CryptographicOperations.ZeroMemory(plain);
        if (phrases.Length != 3) throw new ClientException("This key file is not valid.", "vault_bad_file");
        return phrases;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int memoryKiB, int iterations)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithMemoryAsKB(memoryKiB)
            .WithIterations(iterations)
            .WithParallelism(1)
            .Build();
        var generator = new Argon2BytesGenerator();
        generator.Init(parameters);
        var key = new byte[32];
        generator.GenerateBytes(Encoding.UTF8.GetBytes(password), key);
        return key;
    }
}

/// <summary>
/// The printed sheet: the three phrases as one QR code the app can read back when restoring. Paper only, never stored. The first line
/// is a label so the scanner can tell a sheet from a contact card.
/// </summary>
public static class PhraseSheet
{
    private const string Label = "freespeech-phrases-v1";

    public static string Encode(IReadOnlyList<string> phrases) => string.Join('\n', [Label, .. phrases.Select(p => p.Trim())]);

    public static string[]? TryDecode(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        return lines.Length == 4 && lines[0] == Label && lines.Skip(1).All(l => l.Length > 0) ? lines[1..] : null;
    }
}
