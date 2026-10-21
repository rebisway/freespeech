/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Net.Http.Json;
using FreeSpeech.Client.Identity;
using FreeSpeech.Relay.Services;
using Microsoft.Extensions.Configuration;

namespace FreeSpeech.Tests;

/// <summary>The relay's volume limits (PROTOCOL §11 "Volume limits"): a daily quota and a throughput per identity, a daily quota per address.</summary>
public class TrafficLimiterTests : IDisposable
{
    private const long Mib = 1024 * 1024;
    private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _file = Path.Combine(Path.GetTempPath(), "fs-limits-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private TrafficLimiter Make(long day = 1000, long burst = 0, long address = 0) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:MaxBytesPerIdentityPerDay"] = day.ToString(),
            ["Server:MaxBytesPerIdentityPer10Minutes"] = burst.ToString(),
            ["Server:MaxBytesPerAddressPerDay"] = address.ToString(),
            ["Server:LimitsFilePath"] = _file,
        }).Build(), () => _now);

    [Fact]
    public void TheLimitsChangeAtOnce_andKeepTheCounters()
    {
        var t = Make(day: 1000);
        Assert.True(t.Check("a", null, 600).Allowed);
        Assert.False(t.Check("a", null, 600).Allowed);

        t.Set(2000, null, null);                          // raised: what was sent counts, the room grows
        Assert.True(t.Check("a", null, 600).Allowed);
        Assert.Equal(2000, t.DailyQuota);
        Assert.Equal(["identity_per_day"], t.Overridden);

        t.Set(0, null, null);                             // off
        Assert.True(t.Check("a", null, 50_000).Allowed);
        Assert.Null(t.Check("a", null, 1).QuotaLeft);
    }

    [Fact]
    public void TheChangedLimitsSurviveARestart_andResetGoesBackToTheConfiguration()
    {
        var first = Make(day: 1000, burst: 500, address: 3000);
        first.Set(7000, 0, null);
        Assert.True(File.Exists(_file));

        var restarted = Make(day: 1000, burst: 500, address: 3000);   // same configuration, same file
        Assert.Equal(new TrafficLimits(7000, 0, 3000), restarted.Limits);
        Assert.Equal(new TrafficLimits(1000, 500, 3000), restarted.Defaults);
        Assert.Equal(["identity_per_day", "identity_per_10_minutes"], restarted.Overridden);

        restarted.Reset();
        Assert.Equal(new TrafficLimits(1000, 500, 3000), restarted.Limits);
        Assert.Empty(restarted.Overridden);
        Assert.False(File.Exists(_file));
        Assert.Equal(new TrafficLimits(1000, 500, 3000), Make(day: 1000, burst: 500, address: 3000).Limits);
    }

    [Fact]
    public void AnUnreadableLimitsFile_leavesTheConfigurationInForce()
    {
        File.WriteAllText(_file, "{ not json");
        Assert.Equal(new TrafficLimits(1000, 0, 0), Make(day: 1000).Limits);
    }

    [Fact]
    public void Top_listsWhoSentTheMost()
    {
        var t = Make(day: 0, address: 0);
        t.Check("small", "10.0.0.1", 10);
        t.Check("big", "10.0.0.2", 900);
        t.Check("middle", "10.0.0.3", 300);

        var (identities, _) = t.Top(2);
        Assert.Equal(["big", "middle"], identities.Select(i => i.Key));
        Assert.Equal(900, identities[0].Bytes);
    }

    [Fact]
    public void DailyQuota_refusesWhatWouldExceedIt_andReportsWhatIsLeft()
    {
        var t = Make(day: 1000);
        var first = t.Check("a", null, 600);
        Assert.True(first.Allowed);
        Assert.Equal(400, first.QuotaLeft);

        var second = t.Check("a", null, 500);
        Assert.Equal("quota_exceeded", second.Code);
        Assert.Equal(400, second.QuotaLeft);
        Assert.True(second.RetryAfterSeconds > 0);

        Assert.True(t.Check("a", null, 400).Allowed);   // a refused envelope costs nothing
    }

    [Fact]
    public void DailyQuota_isPerIdentity()
    {
        var t = Make(day: 1000);
        Assert.True(t.Check("a", null, 1000).Allowed);
        Assert.False(t.Check("a", null, 1).Allowed);
        Assert.True(t.Check("b", null, 1000).Allowed);
    }

    [Fact]
    public void DailyQuota_isFreedHourByHour_overRollingTwentyFourHours()
    {
        var t = Make(day: 1000);
        Assert.True(t.Check("a", null, 600).Allowed);   // 12:00
        _now = _now.AddHours(10);
        Assert.True(t.Check("a", null, 400).Allowed);   // 22:00
        Assert.False(t.Check("a", null, 100).Allowed);

        _now = _now.AddHours(14);                       // 12:00 the next day: the first 600 have gone
        var v = t.Check("a", null, 500);
        Assert.True(v.Allowed);
        Assert.Equal(100, v.QuotaLeft);                 // 400 sent at 22:00 + 500 now

        _now = _now.AddHours(24);
        Assert.True(t.Check("a", null, 1000).Allowed);  // everything has gone
    }

    [Fact]
    public void RetryAfter_pointsToWhenTheOldestHourLeaves()
    {
        var t = Make(day: 1000);
        t.Check("a", null, 1000);
        _now = _now.AddHours(5);
        var v = t.Check("a", null, 1);
        Assert.Equal("quota_exceeded", v.Code);
        // The 1000 bytes were sent in the 12:00 hour: they leave at 12:00 + 24 h, counting from the hour boundary (13:00 + 23 h = 12:00 next day).
        Assert.InRange(v.RetryAfterSeconds, 18 * 3600 + 1, 19 * 3600 + 1);
    }

    [Fact]
    public void NoQuota_whenZero()
    {
        var t = Make(day: 0);
        Assert.True(t.Check("a", null, 50 * Mib).Allowed);
        Assert.Null(t.Check("a", null, 1).QuotaLeft);
        Assert.Equal(0, t.DailyQuota);
    }

    [Fact]
    public void Throughput_letsABurstThrough_thenSlowsDown_thenRefusesWhatWouldWaitTooLong()
    {
        // 600 bytes per 10 minutes = 1 byte per second.
        var t = Make(day: 0, burst: 600);
        Assert.Equal(TimeSpan.Zero, t.Check("a", null, 600).Delay);   // the whole bucket at once
        var slow = t.Check("a", null, 10);                            // empty: 10 bytes take 10 s to pay back
        Assert.True(slow.Allowed);
        Assert.InRange(slow.Delay.TotalSeconds, 9.5, 10.5);

        var tooSlow = t.Check("a", null, 100);                        // would wait over the 20 s cap
        Assert.Equal("rate_limited", tooSlow.Code);
        Assert.True(tooSlow.RetryAfterSeconds >= 100);

        _now = _now.AddSeconds(610);                                  // the 10 bytes owed are paid back and the bucket is full again
        Assert.Equal(TimeSpan.Zero, t.Check("a", null, 600).Delay);
    }

    [Fact]
    public void AddressQuota_sharedByTheIdentitiesBehindOneAddress()
    {
        var t = Make(day: 0, address: 1000);
        Assert.True(t.Check("a", "10.0.0.1", 600).Allowed);
        Assert.True(t.Check("b", "10.0.0.1", 400).Allowed);
        Assert.Equal("quota_exceeded", t.Check("c", "10.0.0.1", 1).Code);
        Assert.True(t.Check("c", "10.0.0.2", 1).Allowed);
    }

    [Fact]
    public void IdleAccounts_areForgotten()
    {
        var t = Make(day: 1000);
        t.Check("a", null, 1000);
        _now = _now.AddHours(30);
        t.Check("b", null, 1);   // triggers the purge
        Assert.True(t.Check("a", null, 1000).Allowed);
    }
}

/// <summary>A relay with a tiny daily quota, to see the refusal on the wire.</summary>
public class QuotaFixture : ServerFixture
{
    protected override void Configure(ProcessStartInfo info)
    {
        info.Environment["Server__MaxBytesPerIdentityPerDay"] = "3000";
        info.Environment["Server__MaxBytesPerIdentityPer10Minutes"] = "0";
    }
}

public class TrafficLimitWireTests : IClassFixture<QuotaFixture>
{
    private readonly QuotaFixture _s;
    public TrafficLimitWireTests(QuotaFixture s) => _s = s;

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    [Fact]
    public async Task Registered_publishesTheQuota_andSendsReportWhatIsLeft_thenAreRefused()
    {
        var me = NewIdentity("quota-sender");
        var other = NewIdentity("quota-receiver");
        using var c = await RawClient.ConnectAsync(_s.WsUri);

        var registered = await c.RegisterAsync(me);
        Assert.Equal("registered", registered!.Value.GetProperty("type").GetString());
        Assert.Equal(3000, registered.Value.GetProperty("daily_quota").GetInt64());

        var payload = new string('x', 2000);
        await c.SendAsync(new { type = "send", request_id = "s1", envelope = new { from = me.DiscoveryId, to = other.DiscoveryId, crypto = 1, msg = payload, ts = 1 } });
        var ok = await c.ReceiveAsync();
        Assert.Equal("send_result", ok!.Value.GetProperty("type").GetString());
        Assert.Equal(1000, ok.Value.GetProperty("quota_left").GetInt64());

        await c.SendAsync(new { type = "send", request_id = "s2", envelope = new { from = me.DiscoveryId, to = other.DiscoveryId, crypto = 1, msg = payload, ts = 2 } });
        var refused = await c.ReceiveAsync();
        Assert.Equal("error", refused!.Value.GetProperty("type").GetString());
        Assert.Equal("quota_exceeded", refused.Value.GetProperty("code").GetString());
        Assert.Equal("s2", refused.Value.GetProperty("request_id").GetString());
        Assert.True(refused.Value.GetProperty("retry_after").GetInt32() > 0);

        // The connection stays open and small letters still pass.
        await c.SendAsync(new { type = "send", request_id = "s3", envelope = new { from = me.DiscoveryId, to = other.DiscoveryId, crypto = 1, msg = "hi", ts = 3 } });
        Assert.Equal("send_result", (await c.ReceiveAsync())!.Value.GetProperty("type").GetString());
    }
}

/// <summary>The administrator changes the volume limits of a running relay (HTTP admin API and command line), and the clients feel it at once.</summary>
public class LimitsAdminTests : IClassFixture<QuotaFixture>
{
    private readonly QuotaFixture _s;
    public LimitsAdminTests(QuotaFixture s) => _s = s;

    private async Task<(int Status, System.Text.Json.JsonElement Body)> Admin(HttpMethod method, string path, object? body = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(method, _s.Http + path);
        request.Headers.Add("X-Admin-Secret", "admin-test-secret");
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<(int Code, string Output)> Cli(string url, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(ServerFixture.ServerDll());
        info.ArgumentList.Add("admin");
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(url);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["FS_ADMIN_SECRET"] = "admin-test-secret";
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static UserIdentity NewIdentity(string who) =>
        IdentityService.Derive(Pad($"{who} first phrase"), Pad($"{who} second phrase"), Pad($"{who} third phrase"), who);

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    [Fact]
    public async Task Limits_canBeReadChangedAndResetWhileTheRelayRuns()
    {
        var (status, before) = await Admin(HttpMethod.Get, "/api/admin/limits");
        Assert.Equal(200, status);
        Assert.Equal(3000, before.GetProperty("identity_per_day").GetInt64());   // the fixture's configuration
        Assert.Equal(0, before.GetProperty("changed").GetArrayLength());

        var (_, changed) = await Admin(HttpMethod.Post, "/api/admin/limits", new { identity_per_day = 500, address_per_day = 0 });
        Assert.Equal(500, changed.GetProperty("identity_per_day").GetInt64());
        Assert.Equal(2, changed.GetProperty("changed").GetArrayLength());

        // The relay applies it at once: a 600-byte envelope no longer fits.
        var me = NewIdentity("limits-sender");
        using (var c = await RawClient.ConnectAsync(_s.WsUri))
        {
            var registered = await c.RegisterAsync(me);
            Assert.Equal(500, registered!.Value.GetProperty("daily_quota").GetInt64());
            await c.SendAsync(new { type = "send", request_id = "x", envelope = new { from = me.DiscoveryId, to = NewIdentity("limits-to").DiscoveryId, crypto = 1, msg = new string('x', 600), ts = 1 } });
            Assert.Equal("quota_exceeded", (await c.ReceiveAsync())!.Value.GetProperty("code").GetString());
        }

        var (_, reset) = await Admin(HttpMethod.Delete, "/api/admin/limits");
        Assert.Equal(3000, reset.GetProperty("identity_per_day").GetInt64());
        Assert.Equal(0, reset.GetProperty("changed").GetArrayLength());

        Assert.Equal(400, (await Admin(HttpMethod.Post, "/api/admin/limits", new { identity_per_day = -1 })).Status);
    }

    [Fact]
    public async Task TheCommandLine_showsSetsAndResetsTheLimits_andListsTheTraffic()
    {
        var (code, shown) = await Cli(_s.Http, "limits");
        Assert.Equal(0, code);
        Assert.Contains("per identity, per 24 hours", shown);
        Assert.Contains("2.9 KiB", shown);   // 3000 bytes

        (code, shown) = await Cli(_s.Http, "limits", "set", "--identity-day", "2K", "--address-day", "off");
        Assert.Equal(0, code);
        Assert.Contains("2 KiB", shown);
        Assert.Contains("changed", shown);

        (code, _) = await Cli(_s.Http, "limits", "set", "--identity-day", "lots");
        Assert.NotEqual(0, code);
        (code, _) = await Cli(_s.Http, "limits", "set");
        Assert.NotEqual(0, code);

        (code, shown) = await Cli(_s.Http, "traffic", "--limit", "3");
        Assert.Equal(0, code);
        Assert.Contains("Identities, last 24 hours:", shown);

        (code, shown) = await Cli(_s.Http, "limits", "reset");
        Assert.Equal(0, code);
        Assert.DoesNotContain("changed;", shown);
    }
}