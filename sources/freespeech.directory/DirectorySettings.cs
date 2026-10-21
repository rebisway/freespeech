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

namespace FreeSpeech.Directory;

/// <summary>
/// The tunables of the directory that an administrator can change while it runs: the configuration (<c>Directory:*</c>) gives the starting values, a
/// change is applied at once and kept in <c>settings.json</c> (in the state folder), and "reset" goes back to the configuration.
/// </summary>
public sealed class DirectorySettings
{
    public sealed record Values(int ExpiryMinutes, int ProbeMinutes, int ListSize);

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<DirectorySettings> _logger;
    private readonly Dictionary<string, int> _overrides = [];
    private volatile Values _current;

    public DirectorySettings(IConfiguration config, ILogger<DirectorySettings> logger)
    {
        _logger = logger;
        _path = Path.Combine(DirectoryState.StateDirectory(config), "settings.json");
        Defaults = new Values(
            Math.Clamp(config.GetValue("Directory:ExpiryMinutes", 10), 1, 1440),
            Math.Clamp(config.GetValue("Directory:ProbeMinutes", 30), 1, 1440),
            Math.Clamp(config.GetValue("Directory:ListSize", 3), 1, 10));
        _current = Defaults;
        Load();
    }

    public Values Defaults { get; }
    public Values Current => _current;
    public IReadOnlyList<string> Changed { get { lock (_lock) return _overrides.Keys.Order().ToList(); } }

    public object View() => new
    {
        expiry_minutes = Current.ExpiryMinutes, probe_minutes = Current.ProbeMinutes, list_size = Current.ListSize,
        defaults = new { expiry_minutes = Defaults.ExpiryMinutes, probe_minutes = Defaults.ProbeMinutes, list_size = Defaults.ListSize },
        changed = Changed,
    };

    public void Set(SettingsRequest r)
    {
        if (r.ExpiryMinutes is < 1 or > 1440) throw new ArgumentException("expiry_minutes must be between 1 and 1440");
        if (r.ProbeMinutes is < 1 or > 1440) throw new ArgumentException("probe_minutes must be between 1 and 1440");
        if (r.ListSize is < 1 or > 10) throw new ArgumentException("list_size must be between 1 and 10");
        lock (_lock)
        {
            if (r.ExpiryMinutes is { } e) _overrides["expiry_minutes"] = e;
            if (r.ProbeMinutes is { } p) _overrides["probe_minutes"] = p;
            if (r.ListSize is { } l) _overrides["list_size"] = l;
            Apply();
            Save();
        }
        _logger.LogInformation("Settings changed: {Settings}", string.Join(", ", Changed));
    }

    public void Reset()
    {
        lock (_lock) { _overrides.Clear(); Apply(); Save(); }
        _logger.LogInformation("Settings back to the configuration");
    }

    private void Apply() => _current = new Values(
        _overrides.GetValueOrDefault("expiry_minutes", Defaults.ExpiryMinutes), _overrides.GetValueOrDefault("probe_minutes", Defaults.ProbeMinutes), _overrides.GetValueOrDefault("list_size", Defaults.ListSize));

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var p in JsonDocument.Parse(File.ReadAllText(_path)).RootElement.EnumerateObject())
                if (p.Name is "expiry_minutes" or "probe_minutes" or "list_size" && p.Value.ValueKind == JsonValueKind.Number) _overrides[p.Name] = p.Value.GetInt32();
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

/// <summary>Body of POST admin/settings: the settings to change.</summary>
public sealed class SettingsRequest
{
    [JsonPropertyName("expiry_minutes")] public int? ExpiryMinutes { get; set; }
    [JsonPropertyName("probe_minutes")] public int? ProbeMinutes { get; set; }
    [JsonPropertyName("list_size")] public int? ListSize { get; set; }
}

/// <summary>Where the directory keeps its state, for <c>export</c> and <c>import</c>.</summary>
public static class DirectoryState
{
    public static string StateDirectory(IConfiguration c) => Path.GetFullPath(c["Directory:StatePath"] ?? "directory-state");

    /// <summary>Folder of the identity and of the links with relays: the state folder unless <c>Peer:StatePath</c> says otherwise.</summary>
    public static string PeerDirectory(IConfiguration c) => Path.GetFullPath(c["Peer:StatePath"] ?? c["Directory:StatePath"] ?? "directory-state");

    public static StateFiles Files(IConfiguration c) => new("directory",
    [
        new("settings.json", Path.Combine(StateDirectory(c), "settings.json")),
        new("peers.json", Path.Combine(PeerDirectory(c), "peers.json")),
        new("peer-identity.json", Path.Combine(PeerDirectory(c), "peer-identity.json")),
    ]);
}
