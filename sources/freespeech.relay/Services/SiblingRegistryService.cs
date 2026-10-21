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
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// File-backed implementation of <see cref="ISiblingRegistry"/>. A single JSON file (default "siblings.json" next to the server binary,
/// path configurable via "Server:SiblingsFilePath") holds this server's public/private flag, the suggested siblings and the banned
/// addresses. The siblings themselves are the relays accepted in the peers file. Guarded by a plain lock since mutations are rare (admin
/// actions) and each one rewrites the whole file.
/// </summary>
public class SiblingRegistryService : ISiblingRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<SiblingRegistryService> _logger;
    private readonly PeerStore _peers;
    private State _state;

    public SiblingRegistryService(IConfiguration configuration, PeerStore peers, ILogger<SiblingRegistryService> logger)
    {
        _peers = peers;
        _logger = logger;
        _path = configuration["Server:SiblingsFilePath"] ?? "siblings.json";
        _state = Load();
    }

    public bool IsPublic => _state.IsPublic;

    public void SetPublic(bool isPublic)
    {
        lock (_lock)
        {
            _state.IsPublic = isPublic;
            Save();
        }
        _logger.LogInformation("Server public status set to {IsPublic}", isPublic);
    }

    public IReadOnlyList<Sibling> Whitelist
    {
        get
        {
            var linked = _peers.Accepted("relay").Where(p => p.Url.Length > 0).Select(p => new Sibling { Url = p.Url.TrimEnd('/') }).ToList();
            lock (_lock) return [.. linked.Where(s => !IsBlacklistedUnsafe(s.Url))];
        }
    }

    public IReadOnlyList<SiblingCandidate> Candidates { get { lock (_lock) return [.. _state.Candidates]; } }
    public IReadOnlyList<BlacklistEntry> Blacklist { get { lock (_lock) return [.. _state.Blacklist]; } }

    public void SuggestCandidate(string url, string? suggestedBy)
    {
        if (_peers.FindAcceptedByUrl("relay", url) != null) return;
        lock (_lock)
        {
            if (IsBlacklistedUnsafe(url)) return;
            if (_state.Candidates.Any(c => c.Url == url)) return;

            _state.Candidates.Add(new SiblingCandidate { Url = url, SuggestedBy = suggestedBy, SuggestedAt = DateTime.UtcNow });
            Save();
        }
        _logger.LogInformation("Sibling candidate suggested: {Url} (by {By})", url, suggestedBy ?? "unknown");
    }

    public bool DismissCandidate(string url)
    {
        bool removed;
        lock (_lock)
        {
            removed = _state.Candidates.RemoveAll(c => c.Url == url) > 0;
            if (removed) Save();
        }
        return removed;
    }

    public void Ban(string url, string? reason)
    {
        url = url.TrimEnd('/');
        lock (_lock)
        {
            _state.Candidates.RemoveAll(c => c.Url == url);
            _state.Blacklist.RemoveAll(b => b.Url == url);
            _state.Blacklist.Add(new BlacklistEntry { Url = url, Reason = reason, BannedAt = DateTime.UtcNow });
            Save();
        }
        _logger.LogInformation("Address banned: {Url} ({Reason})", url, reason ?? "no reason given");
    }

    public bool Unban(string url)
    {
        url = url.TrimEnd('/');
        bool removed;
        lock (_lock)
        {
            removed = _state.Blacklist.RemoveAll(b => b.Url == url) > 0;
            if (removed) Save();
        }
        return removed;
    }

    public bool IsBlacklisted(string url)
    {
        lock (_lock) return IsBlacklistedUnsafe(url);
    }

    private bool IsBlacklistedUnsafe(string url) => _state.Blacklist.Any(b => b.Url == url.TrimEnd('/'));

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse {Path} — starting with an empty sibling registry", _path);
            return new State();
        }
    }

    /// <summary>Written to a temporary file then moved over the real one, so a crash never leaves a half-written registry.</summary>
    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));

    private class State
    {
        [JsonPropertyName("is_public")] public bool IsPublic { get; set; }
        [JsonPropertyName("candidates")] public List<SiblingCandidate> Candidates { get; set; } = [];
        [JsonPropertyName("blacklist")] public List<BlacklistEntry> Blacklist { get; set; } = [];
    }
}
