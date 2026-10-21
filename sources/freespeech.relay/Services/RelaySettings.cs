/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Ops;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// The relay's tunables that an administrator can change while it runs: the configuration (<c>Server:*</c>) gives the starting values, a change
/// is applied at once and kept in <c>settings.json</c> (<c>Server:SettingsFilePath</c>), and "reset" goes back to the configuration.
/// Read through <see cref="Current"/> at the moment of use, never copied.
/// </summary>
public sealed class RelaySettings
{
    public sealed record Values(int MaxConnections, int MaxMessageBytes, int S2SRatePerMinute, string SourceUrl, int MaxAccessRequests, int AccessRequestDays, string Country);

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<RelaySettings> _logger;
    private readonly Dictionary<string, JsonElement> _overrides = [];
    private volatile Values _current;

    public RelaySettings(IConfiguration config, ILogger<RelaySettings> logger)
    {
        _logger = logger;
        _path = Path.GetFullPath(config["Server:SettingsFilePath"] is { Length: > 0 } p ? p : "settings.json");
        Defaults = new Values(
            Math.Clamp(config.GetValue("Server:MaxConnections", 0), 0, 1_000_000),
            Math.Clamp(config.GetValue("Server:MaxMessageBytes", 512 * 1024), 1024, 64 * 1024 * 1024),
            Math.Clamp(config.GetValue("Server:S2SRatePerMinute", 1200), 1, 1_000_000),
            config["Server:SourceUrl"] ?? "",
            Math.Clamp(config.GetValue("Server:MaxAccessRequests", 500), 1, 100_000),
            Math.Clamp(config.GetValue("Server:AccessRequestDays", 14), 1, 365),
            DirectoryRegistry.CleanCountry(config["Directory:Country"]) ?? "");
        _current = Defaults;
        Load();
    }

    public Values Defaults { get; }
    public Values Current => _current;

    /// <summary>Names (as in the API) of the settings an administrator changed.</summary>
    public IReadOnlyList<string> Changed { get { lock (_lock) return _overrides.Keys.Order().ToList(); } }

    public object View() => new
    {
        max_connections = Current.MaxConnections, max_message_bytes = Current.MaxMessageBytes, s2s_rate_per_minute = Current.S2SRatePerMinute,
        source_url = Current.SourceUrl, max_access_requests = Current.MaxAccessRequests, access_request_days = Current.AccessRequestDays, country = Current.Country,
        defaults = new
        {
            max_connections = Defaults.MaxConnections, max_message_bytes = Defaults.MaxMessageBytes, s2s_rate_per_minute = Defaults.S2SRatePerMinute,
            source_url = Defaults.SourceUrl, max_access_requests = Defaults.MaxAccessRequests, access_request_days = Defaults.AccessRequestDays, country = Defaults.Country,
        },
        changed = Changed,
    };

    /// <summary>Changes the given settings (a missing one stays as it is) and keeps them across restarts. Throws <see cref="ArgumentException"/> for a value out of range.</summary>
    public void Set(SettingsRequest r)
    {
        if (r.MaxConnections is < 0 or > 1_000_000) throw new ArgumentException("max_connections must be between 0 (no limit) and 1000000");
        if (r.MaxMessageBytes is < 1024 or > 64 * 1024 * 1024) throw new ArgumentException("max_message_bytes must be between 1024 and 67108864");
        if (r.S2SRatePerMinute is < 1 or > 1_000_000) throw new ArgumentException("s2s_rate_per_minute must be between 1 and 1000000");
        if (r.SourceUrl != null && r.SourceUrl.Length > 0 && (r.SourceUrl.Length > 200 || !Uri.TryCreate(r.SourceUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            throw new ArgumentException("source_url must be an http(s) address of at most 200 characters (or empty)");
        if (r.MaxAccessRequests is < 1 or > 100_000) throw new ArgumentException("max_access_requests must be between 1 and 100000");
        if (r.AccessRequestDays is < 1 or > 365) throw new ArgumentException("access_request_days must be between 1 and 365");
        if (r.Country != null && r.Country.Length > 0 && DirectoryRegistry.CleanCountry(r.Country) == null) throw new ArgumentException("country must be a two-letter ISO 3166 code (or empty)");

        lock (_lock)
        {
            void Put(string name, object? value) { if (value != null) _overrides[name] = JsonSerializer.SerializeToElement(value); }
            Put("max_connections", r.MaxConnections); Put("max_message_bytes", r.MaxMessageBytes); Put("s2s_rate_per_minute", r.S2SRatePerMinute);
            Put("source_url", r.SourceUrl); Put("max_access_requests", r.MaxAccessRequests); Put("access_request_days", r.AccessRequestDays);
            Put("country", r.Country == null ? null : DirectoryRegistry.CleanCountry(r.Country) ?? "");
            Apply();
            Save();
        }
        _logger.LogInformation("Settings changed: {Settings}", string.Join(", ", Changed));
    }

    /// <summary>Goes back to the configuration.</summary>
    public void Reset()
    {
        lock (_lock) { _overrides.Clear(); Apply(); Save(); }
        _logger.LogInformation("Settings back to the configuration");
    }

    private void Apply()
    {
        int Int(string name, int fallback) => _overrides.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        _current = new Values(Int("max_connections", Defaults.MaxConnections), Int("max_message_bytes", Defaults.MaxMessageBytes), Int("s2s_rate_per_minute", Defaults.S2SRatePerMinute),
            _overrides.TryGetValue("source_url", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : Defaults.SourceUrl,
            Int("max_access_requests", Defaults.MaxAccessRequests), Int("access_request_days", Defaults.AccessRequestDays),
            _overrides.TryGetValue("country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : Defaults.Country);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name is "max_connections" or "max_message_bytes" or "s2s_rate_per_minute" or "source_url" or "max_access_requests" or "access_request_days" or "country")
                    _overrides[p.Name] = p.Value.Clone();
            Apply();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("Could not read {Path} ({Message}): using the configuration", _path, ex.Message);
            _overrides.Clear();
        }
    }

    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Body of POST api/admin/settings: the settings to change.</summary>
public class SettingsRequest
{
    [JsonPropertyName("max_connections")] public int? MaxConnections { get; set; }
    [JsonPropertyName("max_message_bytes")] public int? MaxMessageBytes { get; set; }
    [JsonPropertyName("s2s_rate_per_minute")] public int? S2SRatePerMinute { get; set; }
    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    [JsonPropertyName("max_access_requests")] public int? MaxAccessRequests { get; set; }
    [JsonPropertyName("access_request_days")] public int? AccessRequestDays { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
}
