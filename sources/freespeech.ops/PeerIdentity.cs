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

namespace FreeSpeech.Ops;

/// <summary>
/// A server's own identity towards other servers (relay, push gateway): a signing key pair created at the first start and kept in a
/// file next to the server's state (mode 600). Nothing secret ever has to be copied between servers: the other side only learns the
/// PUBLIC key, whose SHA-256 fingerprint is the server's id. Losing the file means being validated again by the other servers.
/// </summary>
public sealed class PeerIdentity
{
    private readonly ECDsa _key;

    /// <summary>Public key, base64 (SubjectPublicKeyInfo).</summary>
    public string PublicKey { get; }

    /// <summary>SHA-256 of the public key, lowercase hex: the id other servers know this one by.</summary>
    public string Fingerprint { get; }

    private PeerIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Fingerprint = FingerprintOf(PublicKey);
    }

    /// <summary>Loads the key pair from <paramref name="path"/>, or creates and saves one the first time.</summary>
    public static PeerIdentity LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is not a peer identity file");
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.Private), out _);
            return new PeerIdentity(key);
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = JsonSerializer.Serialize(new Stored { Private = Convert.ToBase64String(created.ExportPkcs8PrivateKey()), Created = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(path, json);
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the folder rights still apply */ }
        return new PeerIdentity(created);
    }

    /// <summary>Signs <paramref name="data"/> (UTF-8); returns base64.</summary>
    public string Sign(string data) => Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256));

    public static bool Verify(string publicKey, string data, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(data), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    public static string FingerprintOf(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))).ToLowerInvariant();

    /// <summary>True when <paramref name="publicKey"/> is a well-formed P-256 public key.</summary>
    public static bool IsValidPublicKey(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 512) return false;
        try { using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _); return true; }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private sealed class Stored
    {
        [JsonPropertyName("private")] public string Private { get; set; } = "";
        [JsonPropertyName("created")] public DateTime Created { get; set; }
    }
}
