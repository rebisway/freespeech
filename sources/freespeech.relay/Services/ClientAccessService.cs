/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Ops;

namespace FreeSpeech.Relay.Services;

/// <summary>Why a registration was accepted or refused by <see cref="IClientAccess.Check"/>.</summary>
public enum AccessDecision { Allowed, Banned, PrivateServer }

/// <summary>What became of an access request offered to <see cref="IClientAccess.Request"/>.</summary>
public enum RequestResult { Stored, AlreadyPending, Disabled, Full }

/// <summary>An unknown identity asking to be allowed on a private server; it waits here until the administrator decides.</summary>
public sealed class AccessRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("requested_at")] public DateTime RequestedAt { get; set; }
}

public sealed class AllowedClient
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("added_at")] public DateTime AddedAt { get; set; }
    /// <summary>Public keys (base64) the identity proved at its last registration. Only kept for allowed identities of a private server: the administrator's tools need them to
    /// build a group with that person. Public information, never a secret; empty until the person has connected once after being allowed.</summary>
    [JsonPropertyName("enc_key")] public string? EncKey { get; set; }
    [JsonPropertyName("sign_key")] public string? SignKey { get; set; }
}

public sealed class BannedClient
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("banned_at")] public DateTime BannedAt { get; set; }
}

public sealed class Invitation
{
    /// <summary>First 8 hex characters of the token's hash: enough to list and revoke, useless to use.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Who may register on this server (protocol §10 "Access control"). Orthogonal to public/private (which is only about
/// server-to-server traffic): an <c>open</c> server takes everybody who is not banned; an <c>allowlist</c> server (a private
/// server for a family, a company, a group) takes only identities the admin allowed or invited. A ban always wins.
/// </summary>
public interface IClientAccess
{
    /// <summary>"open" or "allowlist".</summary>
    string Mode { get; }
    void SetMode(string mode);

    IReadOnlyList<AllowedClient> Allowed { get; }
    IReadOnlyList<BannedClient> Banned { get; }
    IReadOnlyList<Invitation> Invitations { get; }

    /// <summary>
    /// Decides a registration whose identity is already proven. On an allowlist server a valid invitation is consumed here and
    /// the identity becomes allowed — so the check and the consumption are one atomic step.
    /// </summary>
    AccessDecision Check(string discoveryId, string? invitation);

    void Allow(string discoveryId, string? note);

    /// <summary>Keeps the keys an allowed identity just proved (no effect for an identity that is not on the allow-list: an open server keeps nothing about who connects).</summary>
    void RememberKeys(string discoveryId, string encKey, string signKey);

    /// <summary>
    /// The identities of the administrator's tools (the console) that may create groups for the members of this private server. Advertised to every client in "registered",
    /// so a client may accept a group invitation from them without having them as a contact. Each is also allowed to register.
    /// </summary>
    IReadOnlyList<string> Provisioners { get; }
    void AddProvisioner(string discoveryId, string? encKey = null, string? signKey = null);
    bool RemoveProvisioner(string discoveryId);

    bool Remove(string discoveryId);
    void Ban(string discoveryId, string? reason);
    bool Unban(string discoveryId);

    /// <summary>Creates a single-use invitation and returns its token — shown once, only its hash is kept.</summary>
    string CreateInvitation(string? note, TimeSpan validFor);
    bool RevokeInvitation(string id);

    /// <summary>The key members of this server hold to recognise it in the cards of their contacts without its address being written there; null while the server is open.</summary>
    string? CircleKey { get; }

    /// <summary>Draws a new circle key (the members receive it at once).</summary>
    string RenewCircleKey();

    /// <summary>Whether an unknown identity may leave an access request (on by default; the administrator can close the queue).</summary>
    bool RequestsOpen { get; }
    void SetRequestsOpen(bool on);

    /// <summary>Requests waiting for a decision, oldest first. Old ones expire by themselves (Server:AccessRequestDays, default 14).</summary>
    IReadOnlyList<AccessRequest> Requests { get; }
    bool IsPending(string discoveryId);

    /// <summary>Queues the request of an identity whose keys are proven. Bounded (Server:MaxAccessRequests, default 500) and one per identity.</summary>
    RequestResult Request(string discoveryId, string? name, string? note);

    /// <summary>Allows these requesters (and drops their requests). Returns the ids that really were pending.</summary>
    IReadOnlyList<string> ApproveRequests(IEnumerable<string> ids);

    /// <summary>Turns these requests down, optionally banning the identities. Returns the ids that really were pending.</summary>
    IReadOnlyList<string> RejectRequests(IEnumerable<string> ids, bool ban, string? reason);
}

/// <summary>File-backed <see cref="IClientAccess"/> ("Server:AccessFilePath", default access.json). Every change is saved at once and applies at once: no restart.</summary>
public sealed class ClientAccessService : IClientAccess
{
    public const string Open = "open";
    public const string AllowList = "allowlist";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<ClientAccessService> _logger;
    private readonly RelaySettings _settings;
    private int _maxRequests => _settings.Current.MaxAccessRequests;   // changed while the server runs: read at each use
    private TimeSpan _requestLifetime => TimeSpan.FromDays(_settings.Current.AccessRequestDays);
    private State _state;

    public ClientAccessService(IConfiguration configuration, ILogger<ClientAccessService> logger, RelaySettings settings)
    {
        _logger = logger;
        _settings = settings;
        _path = configuration["Server:AccessFilePath"] ?? "access.json";
        _state = Load();
        // A configuration value only sets the starting mode of a server that has no access file yet.
        if (!File.Exists(_path) && configuration["Server:ClientAccess"] is { } initial && IsMode(initial)) _state.Mode = initial.ToLowerInvariant();
    }

    public string Mode { get { lock (_lock) return _state.Mode; } }

    public IReadOnlyList<AllowedClient> Allowed { get { lock (_lock) return [.. _state.Allowed]; } }
    public IReadOnlyList<BannedClient> Banned { get { lock (_lock) return [.. _state.Banned]; } }
    public IReadOnlyList<Invitation> Invitations { get { lock (_lock) { PurgeExpired(); return [.. _state.Invitations]; } } }

    public void SetMode(string mode)
    {
        if (!IsMode(mode)) throw new ArgumentException("mode must be 'open' or 'allowlist'", nameof(mode));
        lock (_lock) { _state.Mode = mode.ToLowerInvariant(); Save(); }
        _logger.LogInformation("Client access mode set to {Mode}", mode);
    }

    public AccessDecision Check(string discoveryId, string? invitation)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            if (_state.Banned.Any(b => b.Id == id)) return AccessDecision.Banned;
            if (_state.Mode == Open) return AccessDecision.Allowed;
            if (_state.Allowed.Any(a => a.Id == id)) return AccessDecision.Allowed;

            if (!string.IsNullOrWhiteSpace(invitation))
            {
                PurgeExpired();
                var hash = Hash(invitation.Trim());
                var match = _state.Invitations.Find(i => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(i.Hash), Encoding.ASCII.GetBytes(hash)));
                if (match != null)
                {
                    _state.Invitations.Remove(match);
                    _state.Requests.RemoveAll(r => r.Id == id);
                    _state.Allowed.Add(new AllowedClient { Id = id, Note = match.Note, AddedAt = DateTime.UtcNow });
                    Save();
                    _logger.LogInformation("Invitation {Invitation} used by {Id}", match.Id, Short(id));
                    return AccessDecision.Allowed;
                }
            }
            return AccessDecision.PrivateServer;
        }
    }

    public string? CircleKey
    {
        get
        {
            lock (_lock)
            {
                if (_state.Mode != AllowList) return null;
                if (string.IsNullOrEmpty(_state.CircleKey)) { _state.CircleKey = NewCircleKey(); Save(); }
                return _state.CircleKey;
            }
        }
    }

    public string RenewCircleKey()
    {
        lock (_lock) { _state.CircleKey = NewCircleKey(); Save(); }
        _logger.LogInformation("Circle key renewed");
        return _state.CircleKey!;
    }

    private static string NewCircleKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public bool RequestsOpen { get { lock (_lock) return _state.RequestsOpen; } }

    public void SetRequestsOpen(bool on)
    {
        lock (_lock) { _state.RequestsOpen = on; Save(); }
        _logger.LogInformation("Access requests are now {State}", on ? "open" : "closed");
    }

    public IReadOnlyList<AccessRequest> Requests { get { lock (_lock) { PurgeExpired(); return [.. _state.Requests.OrderBy(r => r.RequestedAt)]; } } }

    public bool IsPending(string discoveryId)
    {
        var id = Normalize(discoveryId);
        lock (_lock) { PurgeExpired(); return _state.Requests.Any(r => r.Id == id); }
    }

    public RequestResult Request(string discoveryId, string? name, string? note)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            PurgeExpired();
            if (_state.Requests.Find(r => r.Id == id) is { } known)
            {
                // The person asked again (app closed and reopened, new name or note): the same request is refreshed, never doubled.
                // The date is renewed too, so someone who keeps coming back does not expire while the administrator is away.
                known.Name = Clean(name, 64) ?? known.Name;
                known.Note = Clean(note, 200) ?? known.Note;
                known.RequestedAt = DateTime.UtcNow;
                Save();
                return RequestResult.AlreadyPending;
            }
            if (!_state.RequestsOpen) return RequestResult.Disabled;
            if (_state.Requests.Count >= _maxRequests) return RequestResult.Full;
            _state.Requests.Add(new AccessRequest { Id = id, Name = Clean(name, 64), Note = Clean(note, 200), RequestedAt = DateTime.UtcNow });
            Save();
        }
        _logger.LogInformation("Access requested by {Id}", Short(id));
        Notifier.Raise(NotifyKind.Access, "Client waiting for approval", "A client asked for access to this private server. See them with 'admin pending' (verify the code they read out), then 'admin accept <id>'.");
        return RequestResult.Stored;
    }

    public IReadOnlyList<string> ApproveRequests(IEnumerable<string> ids)
    {
        var done = new List<string>();
        lock (_lock)
        {
            foreach (var wanted in ids.Select(Normalize).Distinct())
            {
                var request = _state.Requests.Find(r => r.Id == wanted);
                if (request == null) continue;
                _state.Requests.Remove(request);
                _state.Banned.RemoveAll(b => b.Id == wanted);
                _state.Allowed.RemoveAll(a => a.Id == wanted);
                _state.Allowed.Add(new AllowedClient { Id = wanted, Note = request.Name ?? request.Note, AddedAt = DateTime.UtcNow });
                done.Add(wanted);
            }
            if (done.Count > 0) Save();
        }
        foreach (var id in done) _logger.LogInformation("Access request accepted: {Id}", Short(id));
        return done;
    }

    public IReadOnlyList<string> RejectRequests(IEnumerable<string> ids, bool ban, string? reason)
    {
        var done = new List<string>();
        lock (_lock)
        {
            foreach (var wanted in ids.Select(Normalize).Distinct())
            {
                if (_state.Requests.RemoveAll(r => r.Id == wanted) == 0) continue;
                if (ban)
                {
                    _state.Banned.RemoveAll(b => b.Id == wanted);
                    _state.Banned.Add(new BannedClient { Id = wanted, Reason = reason, BannedAt = DateTime.UtcNow });
                }
                done.Add(wanted);
            }
            if (done.Count > 0) Save();
        }
        foreach (var id in done) _logger.LogInformation("Access request rejected: {Id}{Ban}", Short(id), ban ? " (banned)" : "");
        return done;
    }

    public void Allow(string discoveryId, string? note)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            _state.Requests.RemoveAll(r => r.Id == id);
            _state.Banned.RemoveAll(b => b.Id == id);
            var before = _state.Allowed.Find(a => a.Id == id);   // allowing again keeps the keys already proven
            _state.Allowed.RemoveAll(a => a.Id == id);
            _state.Allowed.Add(new AllowedClient { Id = id, Note = note, AddedAt = DateTime.UtcNow, EncKey = before?.EncKey, SignKey = before?.SignKey });
            Save();
        }
        _logger.LogInformation("Client allowed: {Id}", Short(id));
    }

    public void RememberKeys(string discoveryId, string encKey, string signKey)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            var entry = _state.Allowed.Find(a => a.Id == id);
            if (entry == null || (entry.EncKey == encKey && entry.SignKey == signKey)) return;
            entry.EncKey = encKey;
            entry.SignKey = signKey;
            Save();
        }
    }

    public IReadOnlyList<string> Provisioners { get { lock (_lock) return [.. _state.Provisioners]; } }

    public void AddProvisioner(string discoveryId, string? encKey = null, string? signKey = null)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            if (!_state.Provisioners.Contains(id)) _state.Provisioners.Add(id);
            Save();
        }
        Allow(id, "console");   // the console's identity must be able to register
        if (encKey != null && signKey != null) RememberKeys(id, encKey, signKey);   // given by the tool itself (checked against the id by the caller): no need to wait for it to reconnect
        _logger.LogInformation("Provisioner added: {Id}", Short(id));
    }

    public bool RemoveProvisioner(string discoveryId)
    {
        var id = Normalize(discoveryId);
        bool removed;
        lock (_lock)
        {
            removed = _state.Provisioners.Remove(id);
            if (removed) Save();
        }
        return removed;
    }

    public bool Remove(string discoveryId)
    {
        var id = Normalize(discoveryId);
        bool removed;
        lock (_lock)
        {
            removed = _state.Allowed.RemoveAll(a => a.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) _logger.LogInformation("Client removed: {Id}", Short(id));
        return removed;
    }

    public void Ban(string discoveryId, string? reason)
    {
        var id = Normalize(discoveryId);
        lock (_lock)
        {
            _state.Provisioners.Remove(id);
            _state.Allowed.RemoveAll(a => a.Id == id);
            _state.Requests.RemoveAll(r => r.Id == id);
            _state.Banned.RemoveAll(b => b.Id == id);
            _state.Banned.Add(new BannedClient { Id = id, Reason = reason, BannedAt = DateTime.UtcNow });
            Save();
        }
        _logger.LogInformation("Client banned: {Id} ({Reason})", Short(id), reason ?? "no reason given");
    }

    public bool Unban(string discoveryId)
    {
        var id = Normalize(discoveryId);
        bool removed;
        lock (_lock)
        {
            removed = _state.Banned.RemoveAll(b => b.Id == id) > 0;
            if (removed) Save();
        }
        return removed;
    }

    public string CreateInvitation(string? note, TimeSpan validFor)
    {
        var token = "fs-inv-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        var hash = Hash(token);
        lock (_lock)
        {
            PurgeExpired();
            _state.Invitations.Add(new Invitation { Id = hash[..8], Hash = hash, Note = note, ExpiresAt = DateTime.UtcNow + validFor });
            Save();
        }
        return token;
    }

    public bool RevokeInvitation(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _state.Invitations.RemoveAll(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
        }
        return removed;
    }

    private static bool IsMode(string mode) => mode.Equals(Open, StringComparison.OrdinalIgnoreCase) || mode.Equals(AllowList, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string id) => id.Trim().ToLowerInvariant();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private void PurgeExpired()
    {
        _state.Invitations.RemoveAll(i => i.ExpiresAt <= DateTime.UtcNow);
        _state.Requests.RemoveAll(r => r.RequestedAt + _requestLifetime <= DateTime.UtcNow);
    }

    /// <summary>Free text from a stranger: no control characters (they would forge lines in a terminal or a log), bounded length.</summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > max) clean = clean[..max];
        return clean.Length == 0 ? null : clean;
    }

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State(); }
        catch (JsonException ex)
        {
            // Fail closed: an unreadable access file must not silently turn a private server into an open one.
            _logger.LogError(ex, "Could not parse {Path} — the server will refuse every registration until it is fixed", _path);
            return new State { Mode = AllowList };
        }
    }

    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));

    private sealed class State
    {
        [JsonPropertyName("mode")] public string Mode { get; set; } = Open;
        [JsonPropertyName("allowed")] public List<AllowedClient> Allowed { get; set; } = [];
        [JsonPropertyName("banned")] public List<BannedClient> Banned { get; set; } = [];
        [JsonPropertyName("invitations")] public List<Invitation> Invitations { get; set; } = [];
        [JsonPropertyName("requests_open")] public bool RequestsOpen { get; set; } = true;
        [JsonPropertyName("requests")] public List<AccessRequest> Requests { get; set; } = [];
        [JsonPropertyName("provisioners")] public List<string> Provisioners { get; set; } = [];
        [JsonPropertyName("circle_key")] public string? CircleKey { get; set; }
    }
}
