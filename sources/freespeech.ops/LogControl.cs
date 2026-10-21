/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>
/// Log levels that an operator can change while the server runs (like Asterisk's "core set verbose"). The starting levels come
/// from <c>Logging:LogLevel</c> in configuration; <see cref="Set"/> overrides them in memory until <see cref="Reset"/> or a restart.
/// Categories are .NET logger names (matched by longest prefix) or friendly aliases ("relay", "s2s", "push", ...).
/// </summary>
public sealed class LogControl
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LogLevel> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogLevel> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string[]> _aliases;
    private readonly ConcurrentDictionary<string, LogLevel> _resolved = new();
    private LogLevel _default = LogLevel.Information;
    private LogLevel? _defaultOverride;

    public LogControl(IConfiguration configuration, IReadOnlyDictionary<string, string[]>? aliases = null)
    {
        _aliases = aliases ?? new Dictionary<string, string[]>();
        foreach (var entry in configuration.GetSection("Logging:LogLevel").GetChildren())
        {
            if (!Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out var level)) continue;
            if (entry.Key.Equals("Default", StringComparison.OrdinalIgnoreCase)) _default = level;
            else _configured[entry.Key] = level;
        }
    }

    /// <summary>The friendly names an operator may use, with the logger prefixes behind each.</summary>
    public IReadOnlyDictionary<string, string[]> Aliases => _aliases;

    /// <summary>Called by the logging pipeline for every log call: cheap (cached per category).</summary>
    public bool IsEnabled(string? category, LogLevel level) =>
        level != LogLevel.None && level >= _resolved.GetOrAdd(category ?? "", Resolve);

    private LogLevel Resolve(string category)
    {
        lock (_lock)
        {
            string? bestKey = null;
            LogLevel best = _defaultOverride ?? _default;
            foreach (var source in new[] { _configured, _overrides })
            {
                foreach (var (prefix, level) in source)
                {
                    if (!category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    // Longest prefix wins; at equal length a runtime override beats configuration (it is read last).
                    if (bestKey == null || prefix.Length >= bestKey.Length) { bestKey = prefix; best = level; }
                }
            }
            return best;
        }
    }

    /// <summary>Sets the level of an alias ("relay"), a logger prefix ("FreeSpeech.Relay.Services"), or "all" (the default level).</summary>
    public void Set(string categoryOrAlias, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(categoryOrAlias)) throw new ArgumentException("category is required", nameof(categoryOrAlias));
        lock (_lock)
        {
            if (categoryOrAlias.Equals("all", StringComparison.OrdinalIgnoreCase)) _defaultOverride = level;
            else if (_aliases.TryGetValue(categoryOrAlias, out var prefixes)) foreach (var p in prefixes) _overrides[p] = level;
            else _overrides[categoryOrAlias] = level;
            _resolved.Clear();
        }
    }

    /// <summary>Back to the levels of the configuration.</summary>
    public void Reset()
    {
        lock (_lock) { _overrides.Clear(); _defaultOverride = null; _resolved.Clear(); }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                @default = (_defaultOverride ?? _default).ToString(),
                overrides = _overrides.ToDictionary(o => o.Key, o => o.Value.ToString()),
                configured = _configured.ToDictionary(o => o.Key, o => o.Value.ToString()),
                aliases = _aliases.ToDictionary(a => a.Key, a => a.Value),
            };
        }
    }
}
