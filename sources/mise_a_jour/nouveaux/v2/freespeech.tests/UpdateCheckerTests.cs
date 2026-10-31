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
using FreeSpeech.Client.Update;
using Xunit;

namespace FreeSpeech.Tests;

public class UpdateCheckerTests
{
    private static readonly byte[] Seed = SHA256.HashData(Encoding.UTF8.GetBytes("freespeech-update-test"));
    private static readonly string Key = Convert.ToBase64String(Primitives.Ed25519PublicKey(Seed));
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static (byte[] Manifest, string Sig) Make(string published = "2026-10-30", string mobile = "1.3.6", int build = 311, string download = "https://site.example/{lang}/download/")
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"format\":1,\"published\":\"" + published + "\",\"programs\":{" +
            "\"android-apk\":{\"version\":\"" + mobile + "\",\"build\":" + build + ",\"date\":\"2026-10-30\"}," +
            "\"windows\":{\"version\":\"1.3.4\",\"date\":\"2026-10-30\"}}," +
            "\"download\":\"" + download + "\"}");
        return (json, Convert.ToBase64String(Primitives.Ed25519Sign(Seed, json)));
    }

    [Fact]
    public void A_newer_version_is_available_with_the_language_in_the_address()
    {
        var (m, s) = Make();
        var r = UpdateChecker.Evaluate(m, s, Key, "android-apk", "1.3.5", 310, "fr", Now);
        Assert.Equal(UpdateStatus.Available, r.Status);
        Assert.Equal("1.3.6", r.Version);
        Assert.Equal("https://site.example/fr/download/", r.DownloadUrl);
    }

    [Fact]
    public void Same_version_is_up_to_date_unless_the_build_is_higher()
    {
        var (m, s) = Make(mobile: "1.3.5", build: 311);
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Evaluate(m, s, Key, "android-apk", "1.3.5", 310, "en", Now).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(m, s, Key, "android-apk", "1.3.5", 311, "en", Now).Status);
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(m, s, Key, "windows", "1.3.4", null, "en", Now).Status);
    }

    [Fact]
    public void A_version_ahead_of_the_published_one_is_up_to_date()
    {
        var (m, s) = Make();
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Evaluate(m, s, Key, "windows", "2.0.0", null, "en", Now).Status);
    }

    [Fact]
    public void A_wrong_signature_or_a_changed_manifest_is_refused()
    {
        var (m, s) = Make();
        var other = Convert.ToBase64String(Primitives.Ed25519PublicKey(SHA256.HashData(Encoding.UTF8.GetBytes("other"))));
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(m, s, other, "windows", "1.0.0", null, "en", Now).Status);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(m).Replace("site.example", "evil.example"));
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(tampered, s, Key, "windows", "1.0.0", null, "en", Now).Status);
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(m, "not base64!", Key, "windows", "1.0.0", null, "en", Now).Status);
    }

    [Fact]
    public void An_old_manifest_an_unknown_program_or_a_non_https_address_is_refused()
    {
        var (old, oldSig) = Make(published: "2025-01-01");
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(old, oldSig, Key, "windows", "1.0.0", null, "en", Now).Status);
        var (m, s) = Make();
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(m, s, Key, "linux", "1.0.0", null, "en", Now).Status);
        var (http, httpSig) = Make(download: "http://site.example/{lang}/download/");
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Evaluate(http, httpSig, Key, "windows", "1.0.0", null, "en", Now).Status);
    }
}
