/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace FreeSpeech.Relay.Services;

/// <summary>What the relay does with one envelope a client sends.</summary>
/// <param name="Code">Null when the envelope may pass; otherwise "quota_exceeded" or "rate_limited".</param>
/// <param name="RetryAfterSeconds">With a code: when the client may try again.</param>
/// <param name="Delay">Without a code: how long to hold the envelope back (the identity is going faster than its allowed throughput).</param>
/// <param name="QuotaLeft">Bytes the identity may still send in the current 24 hours; null when no daily quota applies.</param>
public readonly record struct TrafficVerdict(string? Code, int RetryAfterSeconds, TimeSpan Delay, long? QuotaLeft)
{
    public bool Allowed => Code == null;
}

/// <summary>
/// Volume limits on what one client sends, so that one identity or one address cannot saturate the relay. The relay sees opaque
/// envelopes: it cannot tell a file chunk from a message, so the limit is on bytes, not on files. Three limits, each off at 0:
/// a daily quota per identity (rolling 24 hours), a throughput per identity (bytes per 10 minutes; going faster slows the
/// identity down instead of cutting it) and a daily quota per address.
/// </summary>
public interface ITrafficLimiter
{
    /// <summary>Daily quota per identity in bytes (0 = none), published to clients at registration.</summary>
    long DailyQuota { get; }

    /// <summary>Counts <paramref name="bytes"/> sent by <paramref name="identity"/> from <paramref name="ip"/> when it may pass.</summary>
    TrafficVerdict Check(string identity, string? ip, long bytes);

    /// <summary>The limits in force now.</summary>
    TrafficLimits Limits { get; }

    /// <summary>The limits the configuration gives (what <see cref="Reset"/> goes back to).</summary>
    TrafficLimits Defaults { get; }

    /// <summary>The names of the limits an administrator has changed ("identity_per_day", "identity_per_10_minutes", "address_per_day").</summary>
    IReadOnlyList<string> Overridden { get; }

    /// <summary>Changes the given limits at once (null = unchanged, 0 = off) and keeps them across restarts. Counters are kept.</summary>
    void Set(long? identityPerDay, long? identityPer10Minutes, long? addressPerDay);

    /// <summary>Goes back to the limits of the configuration.</summary>
    void Reset();

    /// <summary>The identities and the addresses that sent the most in the last 24 hours, most first.</summary>
    (IReadOnlyList<(string Key, long Bytes)> Identities, IReadOnlyList<(string Key, long Bytes)> Addresses) Top(int count);
}

/// <summary>The three volume limits, in bytes (0 = off): per identity per 24 hours, per identity per 10 minutes, per address per 24 hours.</summary>
public readonly record struct TrafficLimits(long IdentityPerDay, long IdentityPer10Minutes, long AddressPerDay);

public sealed class TrafficLimiter : ITrafficLimiter
{
    /// <summary>The longest an envelope is held back; a longer wait is refused with a retry time.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

    private const int Hours = 24;
    private const double ThroughputWindowSeconds = 600;

    private readonly string? _file;
    private readonly object _setLock = new();
    private readonly Func<DateTimeOffset> _now;
    private volatile Box _limits;
    private (long? IdentityDay, long? Burst, long? AddressDay) _override;

    /// <summary>The limits in a class, so a reader takes all three from one moment.</summary>
    private sealed record Box(TrafficLimits Value);
    private readonly ConcurrentDictionary<string, Account> _identities = new();
    private readonly ConcurrentDictionary<string, Account> _addresses = new();
    private long _lastPurgeHour;

    public TrafficLimiter(IConfiguration config, Func<DateTimeOffset>? now = null)
    {
        Defaults = new TrafficLimits(
            Math.Max(0, config.GetValue("Server:MaxBytesPerIdentityPerDay", 1024L * 1024 * 1024)),
            Math.Max(0, config.GetValue("Server:MaxBytesPerIdentityPer10Minutes", 300L * 1024 * 1024)),
            Math.Max(0, config.GetValue("Server:MaxBytesPerAddressPerDay", 5L * 1024 * 1024 * 1024)));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _file = Path.GetFullPath(config["Server:LimitsFilePath"] is { Length: > 0 } path ? path : "limits.json");
        _limits = new Box(Defaults);
        if (_file != null) Load();
    }

    public TrafficLimits Defaults { get; }

    public TrafficLimits Limits => _limits.Value;

    public long DailyQuota => _limits.Value.IdentityPerDay;

    public IReadOnlyList<string> Overridden
    {
        get
        {
            lock (_setLock)
            {
                var names = new List<string>();
                if (_override.IdentityDay != null) names.Add("identity_per_day");
                if (_override.Burst != null) names.Add("identity_per_10_minutes");
                if (_override.AddressDay != null) names.Add("address_per_day");
                return names;
            }
        }
    }

    public void Set(long? identityPerDay, long? identityPer10Minutes, long? addressPerDay)
    {
        lock (_setLock)
        {
            if (identityPerDay != null) _override.IdentityDay = Math.Max(0, identityPerDay.Value);
            if (identityPer10Minutes != null) _override.Burst = Math.Max(0, identityPer10Minutes.Value);
            if (addressPerDay != null) _override.AddressDay = Math.Max(0, addressPerDay.Value);
            Apply();
            Save();
        }
    }

    public void Reset()
    {
        lock (_setLock)
        {
            _override = default;
            Apply();
            if (_file != null) try { File.Delete(_file); } catch (IOException) { /* the limits are back in memory; the file goes next time */ }
        }
    }

    private void Apply() => _limits = new Box(new TrafficLimits(_override.IdentityDay ?? Defaults.IdentityPerDay, _override.Burst ?? Defaults.IdentityPer10Minutes, _override.AddressDay ?? Defaults.AddressPerDay));

    /// <summary>The limits an administrator changed are kept beside the other state files; a missing or unreadable file means the configuration's.</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_file!)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file!));
            if (saved == null) return;
            _override = (saved.IdentityPerDay, saved.IdentityPer10Minutes, saved.AddressPerDay);
            Apply();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { /* the configuration's limits stay */ }
    }

    private void Save()
    {
        if (_file == null) return;
        var saved = new Saved { IdentityPerDay = _override.IdentityDay, IdentityPer10Minutes = _override.Burst, AddressPerDay = _override.AddressDay };
        var temp = _file + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(saved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }

    private sealed class Saved
    {
        [System.Text.Json.Serialization.JsonPropertyName("identity_per_day"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public long? IdentityPerDay { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("identity_per_10_minutes"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public long? IdentityPer10Minutes { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("address_per_day"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public long? AddressPerDay { get; set; }
    }

    public (IReadOnlyList<(string Key, long Bytes)> Identities, IReadOnlyList<(string Key, long Bytes)> Addresses) Top(int count)
    {
        var hour = _now().ToUnixTimeSeconds() / 3600;
        List<(string, long)> Of(ConcurrentDictionary<string, Account> accounts) =>
            accounts.Select(a => { lock (a.Value) return (a.Key, a.Value.Used(hour)); }).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(Math.Max(1, count)).ToList();
        return (Of(_identities), Of(_addresses));
    }

    public TrafficVerdict Check(string identity, string? ip, long bytes)
    {
        var verdict = Evaluate(identity, ip, bytes);
        if (verdict.Code != null)
            Notifier.Raise(NotifyKind.Abuse, "Traffic limit reached", $"An identity or an address went over its traffic limit ({verdict.Code}); its messages are refused or slowed. See 'admin limits' and 'admin traffic'.");
        return verdict;
    }

    private TrafficVerdict Evaluate(string identity, string? ip, long bytes)
    {
        var now = _now();
        var hour = now.ToUnixTimeSeconds() / 3600;
        Purge(hour);

        var limits = _limits.Value;   // one moment's limits for the whole check
        var id = _identities.GetOrAdd(identity, _ => new Account());
        var address = ip != null && limits.AddressPerDay > 0 ? _addresses.GetOrAdd(ip, _ => new Account()) : null;

        // Both accounts are locked in the same order (identity, then address) so two connections never wait on each other.
        lock (id)
        {
            if (address != null) Monitor.Enter(address);
            try
            {
                var usedId = id.Used(hour);
                if (limits.IdentityPerDay > 0 && usedId + bytes > limits.IdentityPerDay)
                    return new TrafficVerdict("quota_exceeded", id.SecondsUntilRoom(hour, now, usedId + bytes - limits.IdentityPerDay), TimeSpan.Zero, Math.Max(0, limits.IdentityPerDay - usedId));
                if (address != null && address.Used(hour) + bytes > limits.AddressPerDay)
                    return new TrafficVerdict("quota_exceeded", address.SecondsUntilRoom(hour, now, address.Used(hour) + bytes - limits.AddressPerDay), TimeSpan.Zero, limits.IdentityPerDay > 0 ? Math.Max(0, limits.IdentityPerDay - usedId) : null);

                var delay = TimeSpan.Zero;
                if (limits.IdentityPer10Minutes > 0)
                {
                    var wait = id.Take(now, bytes, limits.IdentityPer10Minutes, limits.IdentityPer10Minutes / ThroughputWindowSeconds);
                    if (wait > MaxDelay) return new TrafficVerdict("rate_limited", (int)Math.Ceiling(wait.TotalSeconds), TimeSpan.Zero, limits.IdentityPerDay > 0 ? Math.Max(0, limits.IdentityPerDay - usedId) : null);
                    delay = wait;
                }

                id.Add(hour, bytes);
                address?.Add(hour, bytes);
                return new TrafficVerdict(null, 0, delay, limits.IdentityPerDay > 0 ? Math.Max(0, limits.IdentityPerDay - usedId - bytes) : null);
            }
            finally { if (address != null) Monitor.Exit(address); }
        }
    }

    /// <summary>Drops the accounts that have sent nothing for a day, at most once an hour.</summary>
    private void Purge(long hour)
    {
        var last = Interlocked.Read(ref _lastPurgeHour);
        if (hour == last || Interlocked.CompareExchange(ref _lastPurgeHour, hour, last) != last) return;
        foreach (var (key, account) in _identities) if (account.IdleSince(hour) >= Hours) _identities.TryRemove(key, out _);
        foreach (var (key, account) in _addresses) if (account.IdleSince(hour) >= Hours) _addresses.TryRemove(key, out _);
    }

    /// <summary>Bytes sent per hour over the last 24 hours, plus a token bucket for the throughput.</summary>
    private sealed class Account
    {
        private readonly long[] _perHour = new long[Hours];
        private long _lastHour;
        private double _tokens = double.NaN;
        private DateTimeOffset _refilled;

        /// <summary>Moves the ring forward to <paramref name="hour"/>, clearing the hours that have gone by.</summary>
        private void Roll(long hour)
        {
            if (_lastHour == 0) { _lastHour = hour; return; }
            var gone = Math.Min(Hours, hour - _lastHour);
            for (var i = 1; i <= gone; i++) _perHour[(_lastHour + i) % Hours] = 0;
            if (hour > _lastHour) _lastHour = hour;
        }

        public long Used(long hour) { Roll(hour); return _perHour.Sum(); }

        public void Add(long hour, long bytes) { Roll(hour); _perHour[hour % Hours] += bytes; }

        public long IdleSince(long hour) => _lastHour == 0 ? Hours : hour - _lastHour;

        /// <summary>Seconds until enough old hours have gone by for <paramref name="excess"/> bytes to fit again (the hour that holds them leaves at its end + 24 hours).</summary>
        public int SecondsUntilRoom(long hour, DateTimeOffset now, long excess)
        {
            long freed = 0;
            for (var age = Hours - 1; age >= 0; age--)   // oldest hour first
            {
                freed += _perHour[(hour - age + Hours * 2) % Hours];
                if (freed >= excess)
                {
                    var leavesAt = (hour - age + Hours) * 3600L;   // the end of that hour, a day later
                    return (int)Math.Max(1, leavesAt - now.ToUnixTimeSeconds());
                }
            }
            return 3600;
        }

        /// <summary>Takes <paramref name="bytes"/> from the bucket (it may go negative); returns how long the sender should wait for it to be paid back.</summary>
        public TimeSpan Take(DateTimeOffset now, long bytes, long capacity, double perSecond)
        {
            if (double.IsNaN(_tokens)) { _tokens = capacity; _refilled = now; }
            _tokens = Math.Min(capacity, _tokens + (now - _refilled).TotalSeconds * perSecond);
            _refilled = now;
            if (_tokens >= bytes) { _tokens -= bytes; return TimeSpan.Zero; }
            var wait = TimeSpan.FromSeconds((bytes - _tokens) / perSecond);
            if (wait <= MaxDelay) _tokens -= bytes;   // a refused envelope costs nothing
            return wait;
        }
    }
}
