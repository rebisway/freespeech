/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Globalization;
using System.Text.Json;
using FreeSpeech.Client.Crypto;

namespace FreeSpeech.Client.Update;

public enum UpdateStatus
{
    /// <summary>The installed version is the latest one published.</summary>
    UpToDate,
    /// <summary>A newer version is published.</summary>
    Available,
    /// <summary>The check could not be made: no network, or an answer that is not valid.</summary>
    Failed,
}

/// <param name="Version">The published version (for <see cref="UpdateStatus.Available"/> and <see cref="UpdateStatus.UpToDate"/>).</param>
/// <param name="Date">Its release date, as published (yyyy-MM-dd).</param>
/// <param name="DownloadUrl">The page to open to get it, with the language filled in.</param>
public sealed record UpdateResult(UpdateStatus Status, string? Version = null, string? Date = null, string? DownloadUrl = null);

/// <summary>
/// Looks for a newer version of the application. The publisher's website serves <c>update/latest.json</c> and its detached Ed25519 signature
/// <c>update/latest.json.sig</c> (base64); the public key is built into the application. A manifest whose signature does not verify is ignored, so a
/// compromised website cannot announce a version or send people to another address. Nothing is downloaded or installed: the result only
/// carries the address of the download page. Used on request, or at start-up when the user asked for it, never otherwise.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string baseUrl, string publicKey)
{
    /// <summary>The manifest is not trusted past this age, which stops an old signed copy from hiding newer versions for ever.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(400);

    /// <param name="program">The program id in the manifest: <c>android-apk</c>, <c>windows</c> or <c>linux</c>.</param>
    /// <param name="currentBuild">The Android build number, or null where the program has none.</param>
    /// <param name="language">Language code put in the download address (<c>{lang}</c>).</param>
    public async Task<UpdateResult> CheckAsync(string program, string currentVersion, int? currentBuild, string language, CancellationToken ct = default)
    {
        try
        {
            var address = baseUrl.TrimEnd('/') + "/update/latest.json";
            var json = await http.GetByteArrayAsync(address, ct);
            var sig = await http.GetStringAsync(address + ".sig", ct);
            return Evaluate(json, sig, publicKey, program, currentVersion, currentBuild, language, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new UpdateResult(UpdateStatus.Failed); }
    }

    /// <summary>Decides from a manifest and its signature. A signature, a shape or an age that is wrong gives <see cref="UpdateStatus.Failed"/>.</summary>
    public static UpdateResult Evaluate(byte[] manifest, string signature, string publicKey, string program, string currentVersion, int? currentBuild, string language, DateTimeOffset now)
    {
        var failed = new UpdateResult(UpdateStatus.Failed);
        try
        {
            if (!Primitives.Ed25519Verify(Convert.FromBase64String(publicKey), manifest, Convert.FromBase64String(signature.Trim()))) return failed;
            using var doc = JsonDocument.Parse(manifest);
            var root = doc.RootElement;
            if (root.GetProperty("format").GetInt32() != 1) return failed;
            var published = DateTimeOffset.Parse(root.GetProperty("published").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (now - published > MaxAge) return failed;

            var entry = root.GetProperty("programs").GetProperty(program);
            var version = entry.GetProperty("version").GetString() ?? "";
            var date = entry.TryGetProperty("date", out var d) ? d.GetString() : null;
            int? build = entry.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
            var url = (root.GetProperty("download").GetString() ?? "").Replace("{lang}", Uri.EscapeDataString(language));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return failed;

            if (!Version.TryParse(version, out var latest) || !Version.TryParse(currentVersion, out var current)) return failed;
            var newer = latest > current || (latest == current && build is { } nb && currentBuild is { } cb && nb > cb);
            return new UpdateResult(newer ? UpdateStatus.Available : UpdateStatus.UpToDate, version, date, url);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { return failed; }
    }
}
