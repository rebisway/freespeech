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
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client.Discovery;

/// <summary>A directory the application knows: its address and its public key (base64 SubjectPublicKeyInfo), built in.</summary>
public sealed record DirectoryEntry(string Url, string PublicKey);

/// <summary>
/// Asks a directory for relays that take new accounts. The answer is signed by the directory's key, which the application has built in: a list that
/// does not verify, or that is older than a day, is ignored. Used only when the client has no relay to go to (first start, or every relay it knows is
/// down), never in normal operation.
/// </summary>
public sealed class DirectoryClient(HttpClient http, DirectoryEntry entry)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <returns>Up to <paramref name="count"/> relay addresses, or an empty list when the directory is unreachable or its answer is not valid.</returns>
    public async Task<IReadOnlyList<string>> GetRelaysAsync(string? country, int count = 3, CancellationToken ct = default)
    {
        try
        {
            var query = $"?count={Math.Clamp(count, 1, 10)}" + (country is { Length: 2 } ? "&country=" + Uri.EscapeDataString(country) : "");
            var json = await http.GetStringAsync(entry.Url.TrimEnd('/') + "/directory/servers" + query, ct);
            return Verify(json, entry.PublicKey, DateTimeOffset.UtcNow) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return []; }
    }

    /// <summary>The relays of a signed answer, or null when the signature, the age or the shape is wrong.</summary>
    public static IReadOnlyList<string>? Verify(string json, string publicKey, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ts = root.GetProperty("ts").GetInt64();
            var sig = root.GetProperty("sig").GetString() ?? "";
            if (now - DateTimeOffset.FromUnixTimeSeconds(ts) > MaxAge || DateTimeOffset.FromUnixTimeSeconds(ts) - now > TimeSpan.FromMinutes(5)) return null;

            var servers = root.GetProperty("servers").EnumerateArray()
                .Select(s => (Url: s.GetProperty("url").GetString() ?? "", Country: s.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)).ToList();
            var text = "directory-list\n" + ts + "\n" + string.Join("\n", servers.Select(s => s.Url + "|" + s.Country));

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(sig), HashAlgorithmName.SHA256)) return null;
            return servers.Select(s => RelayPool.Normalize(s.Url)).Where(IsRelayUrl).Distinct().ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException or ArgumentException) { return null; }
    }

    private static bool IsRelayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));
}

/// <summary>Several directories, each with its own key: tried in a random order, the first one that gives relays wins.</summary>
public static class DirectoryPool
{
    /// <summary>Reads <c>url|key</c> entries separated by commas or semicolons (the build setting <c>FsDirectories</c>); an entry without a key is left out.</summary>
    public static IReadOnlyList<DirectoryEntry> Parse(string? text) =>
        (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => new DirectoryEntry(p[0], p[1])).ToList();

    public static async Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<DirectoryEntry> entries, string? country, int count = 3, HttpClient? http = null, Random? random = null, CancellationToken ct = default)
    {
        if (entries.Count == 0) return [];
        using var own = http == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) } : null;
        foreach (var entry in entries.OrderBy(_ => (random ?? Random.Shared).Next()))
        {
            var relays = await new DirectoryClient(http ?? own!, entry).GetRelaysAsync(country, count, ct);
            if (relays.Count > 0) return relays;
        }
        return [];
    }
}
