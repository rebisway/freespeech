/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>Where a peer stands. A link between two servers works only when both administrators said yes: <see cref="Accepted"/> on both sides.</summary>
public static class PeerState
{
    /// <summary>I asked it; its administrator has not answered yet.</summary>
    public const string PendingOut = "pending_out";
    /// <summary>It asked me; my administrator has not answered yet.</summary>
    public const string PendingIn = "pending_in";
    /// <summary>Both sides agreed: its calls are accepted and I may call it.</summary>
    public const string Accepted = "accepted";
    /// <summary>I asked it and it turned me down.</summary>
    public const string Refused = "refused";
    /// <summary>Banned by my administrator: its requests and calls are refused.</summary>
    public const string Banned = "banned";
}

/// <summary>Another server this one knows: its kind ("relay" or "push"), name, address, public key and where the agreement stands.</summary>
public sealed class Peer
{
    /// <summary>Fingerprint of its public key (64 hex).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>What the other administrator declared to be reached at (free text): never proof, a way to check who is asking.</summary>
    [JsonPropertyName("contact")] public string? Contact { get; set; }
    /// <summary>The address the request came from, as this server saw it.</summary>
    [JsonPropertyName("seen_from")] public string? SeenFrom { get; set; }
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = PeerState.PendingIn;
    /// <summary>When the request arrived (pending_in), was sent (pending_out) or the state last changed.</summary>
    [JsonPropertyName("since")] public DateTime Since { get; set; }
    /// <summary>For a peer I asked: whether my request reached it. Until then it is sent again; afterwards "unknown" means it said no.</summary>
    [JsonPropertyName("sent")] public bool Sent { get; set; }
}

/// <summary>What became of a request from another server offered to <see cref="PeerStore.Receive"/>.</summary>
public enum PeerRequestResult { Stored, Updated, AlreadyAccepted, Banned, Closed, Full }

/// <summary>
/// Every peer, request and ban of one server, kept in a file (<c>peers.json</c>, atomic write) so that a restart loses no approval.
/// Requests are bounded (<c>Peer:MaxRequests</c>, 200) and expire (<c>Peer:RequestDays</c>, 14); the queue can be closed.
/// </summary>
public sealed class PeerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly int _maxRequests;
    private readonly TimeSpan _lifetime;
    private readonly ILogger<PeerStore> _logger;
    private readonly object _lock = new();
    private State _state;

    public PeerStore(string path, int maxRequests, TimeSpan requestLifetime, ILogger<PeerStore> logger)
    {
        _path = path;
        _maxRequests = Math.Max(1, maxRequests);
        _lifetime = requestLifetime;
        _logger = logger;
        _state = Load();
    }

    public bool RequestsOpen { get { lock (_lock) return _state.RequestsOpen; } }

    public void SetRequestsOpen(bool open)
    {
        lock (_lock) { _state.RequestsOpen = open; Save(); }
        _logger.LogInformation("Peer requests are now {State}", open ? "open" : "closed");
    }

    /// <summary>Every peer except the requests waiting for me (see <see cref="Requests"/>).</summary>
    public IReadOnlyList<Peer> Peers(string? kind = null)
    {
        lock (_lock) { Purge(); return [.. _state.Peers.Where(p => p.State != PeerState.PendingIn && (kind == null || p.Kind == kind)).OrderBy(p => p.Since)]; }
    }

    /// <summary>Requests waiting for my decision, oldest first.</summary>
    public IReadOnlyList<Peer> Requests(string? kind = null)
    {
        lock (_lock) { Purge(); return [.. _state.Peers.Where(p => p.State == PeerState.PendingIn && (kind == null || p.Kind == kind)).OrderBy(p => p.Since)]; }
    }

    public IReadOnlyList<Peer> Accepted(string kind)
    {
        lock (_lock) return [.. _state.Peers.Where(p => p.State == PeerState.Accepted && p.Kind == kind)];
    }

    public IReadOnlyList<Peer> PendingOut()
    {
        lock (_lock) return [.. _state.Peers.Where(p => p.State == PeerState.PendingOut)];
    }

    public Peer? Find(string id)
    {
        lock (_lock) { Purge(); return Clone(_state.Peers.Find(p => p.Id == Norm(id))); }
    }

    /// <summary>The accepted peer of this kind at this address (the way a relay picks the gateways it may call).</summary>
    public Peer? FindAcceptedByUrl(string kind, string url)
    {
        var wanted = NormUrl(url);
        lock (_lock) return Clone(_state.Peers.Find(p => p.State == PeerState.Accepted && p.Kind == kind && NormUrl(p.Url) == wanted));
    }

    /// <summary>Peers whose id starts with <paramref name="prefix"/> (6+ hex characters), optionally only in one state.</summary>
    public IReadOnlyList<Peer> ByPrefix(string prefix, string? state = null)
    {
        prefix = Norm(prefix);
        lock (_lock) { Purge(); return [.. _state.Peers.Where(p => p.Id.StartsWith(prefix, StringComparison.Ordinal) && (state == null || p.State == state))]; }
    }

    /// <summary>A server asks me (its signature on the request already checked). One entry per key; asking again updates it.</summary>
    public PeerRequestResult Receive(string kind, string? name, string url, string publicKey, string? contact = null, string? seenFrom = null)
    {
        var id = PeerIdentity.FingerprintOf(publicKey);
        lock (_lock)
        {
            Purge();
            var known = _state.Peers.Find(p => p.Id == id);
            if (known != null)
            {
                if (known.State == PeerState.Banned) return PeerRequestResult.Banned;
                if (known.State == PeerState.Accepted) return PeerRequestResult.AlreadyAccepted;
                if (known.State is PeerState.PendingOut or PeerState.Refused)
                {
                    // I asked it (or it had turned me down) and now it asks me: both sides want the link, so it is agreed.
                    known.State = PeerState.Accepted; known.Since = DateTime.UtcNow;
                    known.Name = Clean(name, 64) ?? known.Name;
                    Save();
                    _logger.LogInformation("Peer {Id} ({Kind}) accepted: both sides asked", Short(id), kind);
                    return PeerRequestResult.AlreadyAccepted;
                }
                known.Name = Clean(name, 64) ?? known.Name;
                known.Url = url;
                known.Contact = Clean(contact, 128) ?? known.Contact;
                known.SeenFrom = seenFrom ?? known.SeenFrom;
                known.Since = DateTime.UtcNow;   // asking again keeps the request alive without a second line
                Save();
                return PeerRequestResult.Updated;
            }
            if (!_state.RequestsOpen) return PeerRequestResult.Closed;
            if (_state.Peers.Count(p => p.State == PeerState.PendingIn) >= _maxRequests) return PeerRequestResult.Full;
            _state.Peers.Add(new Peer { Id = id, Kind = kind, Name = Clean(name, 64), Url = url, Contact = Clean(contact, 128), SeenFrom = seenFrom, PublicKey = publicKey, State = PeerState.PendingIn, Since = DateTime.UtcNow });
            Save();
        }
        _logger.LogInformation("Peer request from {Id} ({Kind})", Short(id), kind);
        return PeerRequestResult.Stored;
    }

    /// <summary>I ask another server: its key is pinned now (as shown to my administrator) and the answer is awaited.</summary>
    public Peer AddOutgoing(string kind, string? name, string url, string publicKey)
    {
        var id = PeerIdentity.FingerprintOf(publicKey);
        lock (_lock)
        {
            var known = _state.Peers.Find(p => p.Id == id);
            if (known != null)
            {
                known.Url = url; known.Name = Clean(name, 64) ?? known.Name;
                // They already asked me: my asking is my yes.
                if (known.State == PeerState.PendingIn) { known.State = PeerState.Accepted; known.Since = DateTime.UtcNow; }
                else if (known.State is PeerState.Refused) { known.State = PeerState.PendingOut; known.Since = DateTime.UtcNow; }
                Save();
                return Clone(known)!;
            }
            var peer = new Peer { Id = id, Kind = kind, Name = Clean(name, 64), Url = url, PublicKey = publicKey, State = PeerState.PendingOut, Since = DateTime.UtcNow };
            _state.Peers.Add(peer);
            Save();
            return Clone(peer)!;
        }
    }

    /// <summary>Moves a peer from one state to another (used when the other side's answer is learned).</summary>
    public bool SetState(string id, string state)
    {
        lock (_lock)
        {
            var peer = _state.Peers.Find(p => p.Id == Norm(id));
            if (peer == null || peer.State == state) return false;
            peer.State = state; peer.Since = DateTime.UtcNow;
            Save();
            return true;
        }
    }

    public IReadOnlyList<string> Approve(IEnumerable<string> ids)
    {
        var done = new List<string>();
        lock (_lock)
        {
            foreach (var id in ids.Select(Norm).Distinct())
            {
                var peer = _state.Peers.Find(p => p.Id == id && p.State == PeerState.PendingIn);
                if (peer == null) continue;
                peer.State = PeerState.Accepted; peer.Since = DateTime.UtcNow;
                done.Add(id);
            }
            if (done.Count > 0) Save();
        }
        foreach (var id in done) _logger.LogInformation("Peer request accepted: {Id}", Short(id));
        return done;
    }

    public IReadOnlyList<string> Reject(IEnumerable<string> ids, bool ban)
    {
        var done = new List<string>();
        lock (_lock)
        {
            foreach (var id in ids.Select(Norm).Distinct())
            {
                var peer = _state.Peers.Find(p => p.Id == id && p.State == PeerState.PendingIn);
                if (peer == null) continue;
                if (ban) { peer.State = PeerState.Banned; peer.Since = DateTime.UtcNow; }
                else _state.Peers.Remove(peer);
                done.Add(id);
            }
            if (done.Count > 0) Save();
        }
        foreach (var id in done) _logger.LogInformation("Peer request rejected: {Id}{Ban}", Short(id), ban ? " (banned)" : "");
        return done;
    }

    /// <summary>Forgets a peer (any state). A removed peer may ask again.</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _state.Peers.RemoveAll(p => p.Id == Norm(id)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public bool Ban(string id)
    {
        lock (_lock)
        {
            var peer = _state.Peers.Find(p => p.Id == Norm(id));
            if (peer == null) return false;
            peer.State = PeerState.Banned; peer.Since = DateTime.UtcNow;
            Save();
        }
        _logger.LogInformation("Peer banned: {Id}", Short(id));
        return true;
    }

    public bool Unban(string id)
    {
        lock (_lock)
        {
            var peer = _state.Peers.Find(p => p.Id == Norm(id) && p.State == PeerState.Banned);
            if (peer == null) return false;
            _state.Peers.Remove(peer);
            Save();
            return true;
        }
    }

    public static string Norm(string id) => id.Trim().ToLowerInvariant();
    public static string NormUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();
    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
    private static Peer? Clone(Peer? p) => p == null ? null : new Peer { Id = p.Id, Kind = p.Kind, Name = p.Name, Url = p.Url, PublicKey = p.PublicKey, State = p.State, Since = p.Since, Sent = p.Sent };

    /// <summary>My request to this peer reached it.</summary>
    public void MarkSent(string id)
    {
        lock (_lock)
        {
            var peer = _state.Peers.Find(p => p.Id == Norm(id));
            if (peer == null || peer.Sent) return;
            peer.Sent = true;
            Save();
        }
    }

    /// <summary>Free text from another server: no control characters, bounded.</summary>
    public static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > max) clean = clean[..max];
        return clean.Length == 0 ? null : clean;
    }

    private void Purge() => _state.Peers.RemoveAll(p => p.State == PeerState.PendingIn && p.Since + _lifetime <= DateTime.UtcNow);

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State(); }
        catch (JsonException ex)
        {
            // Never silently forget who was approved or banned: the file is kept as it is and the server starts with no peers.
            _logger.LogError(ex, "Could not parse {Path} — no peer is trusted until it is fixed (a copy is kept as .corrupt)", _path);
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch (IOException) { /* best effort */ }
            return new State { RequestsOpen = false };
        }
    }

    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));

    private sealed class State
    {
        [JsonPropertyName("requests_open")] public bool RequestsOpen { get; set; } = true;
        [JsonPropertyName("peers")] public List<Peer> Peers { get; set; } = [];
    }
}
