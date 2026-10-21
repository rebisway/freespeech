/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Ops;

namespace FreeSpeech.Push;

/// <summary>Counters per linked relay (key "relay-" + first 8 characters of its fingerprint) since the start of the process (memory only).</summary>
public sealed class PushStats
{
    public sealed class Counter
    {
        public long Sent, Failed, Busy, RateLimited, Gone;
        public DateTime? LastUsed;
    }

    private readonly ConcurrentDictionary<string, Counter> _byKey = new();
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public Counter For(string keyId) => _byKey.GetOrAdd(keyId, _ => new Counter());
    public IReadOnlyDictionary<string, Counter> All => _byKey;
}

/// <summary>Limits an operator can change while the gateway runs; saved to <c>settings.json</c>.</summary>
public sealed class PushSettings
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<PushSettings> _logger;
    private State _state;

    public PushSettings(string path, int defaultMinIntervalSeconds, int defaultMaxConcurrent, ILogger<PushSettings> logger)
    {
        _path = path;
        _logger = logger;
        _state = new State { MinIntervalSeconds = defaultMinIntervalSeconds, MaxConcurrent = defaultMaxConcurrent };
        if (File.Exists(path))
        {
            try { _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? _state; }
            catch (JsonException ex) { logger.LogWarning(ex, "Could not parse {Path} — using the configured limits", path); }
        }
    }

    public TimeSpan MinInterval => TimeSpan.FromSeconds(_state.MinIntervalSeconds);
    public int MaxConcurrent => _state.MaxConcurrent;
    /// <summary>In maintenance the gateway answers "busy" to every wake-up, so relays use another gateway: a restart then loses nothing.</summary>
    public bool Maintenance => _state.Maintenance;

    public object Snapshot() => new { min_interval_seconds = _state.MinIntervalSeconds, max_concurrent = _state.MaxConcurrent, maintenance = _state.Maintenance };

    public void Update(int? minIntervalSeconds, int? maxConcurrent, bool? maintenance)
    {
        if (minIntervalSeconds is < 0 or > 3600) throw new ArgumentException("min_interval_seconds must be between 0 and 3600");
        if (maxConcurrent is < 1 or > 100_000) throw new ArgumentException("max_concurrent must be between 1 and 100000");
        lock (_lock)
        {
            if (minIntervalSeconds is { } m) _state.MinIntervalSeconds = m;
            if (maxConcurrent is { } c) _state.MaxConcurrent = c;
            if (maintenance is { } t) _state.Maintenance = t;
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        _logger.LogInformation("Settings changed: {Settings}", JsonSerializer.Serialize(Snapshot()));
    }

    private sealed class State
    {
        [JsonPropertyName("min_interval_seconds")] public int MinIntervalSeconds { get; set; }
        [JsonPropertyName("max_concurrent")] public int MaxConcurrent { get; set; }
        [JsonPropertyName("maintenance")] public bool Maintenance { get; set; }
    }
}
