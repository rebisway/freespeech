/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;
using FreeSpeech.Relay.Services;
using Microsoft.AspNetCore.Mvc;

namespace FreeSpeech.Relay.Controllers;

/// <summary>
/// Admin management of this server's public/private status and S2S sibling
/// registry (protocol §10) — plain JSON over HTTP like every other
/// endpoint here, so a future web admin UI (or curl, or a script) can drive
/// it in any language, no framework-specific admin console required.
///
/// Every route requires the "X-Admin-Secret" header to match
/// "Admin:Secret" in configuration, and a call from the machine itself
/// (loopback; "Admin:AllowRemote" lifts this for a private network). If the
/// secret is empty or missing, every route refuses (fail-safe default — an
/// admin API with no secret configured must not be silently wide open).
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly ISiblingRegistry _siblings;
    private readonly IConfiguration _configuration;

    private readonly IRedirectService _redirect;
    private readonly ISocketRegistry _sockets;

    private readonly IClientAccess _access;
    private readonly IPresenceService _presence;

    private readonly RelaySocketHandler _handler;
    private readonly ITrafficLimiter _traffic;
    private readonly RelaySettings _settings;
    private readonly FreeSpeech.Ops.PeerOptions _peer;

    public AdminController(ISiblingRegistry siblings, IConfiguration configuration, IRedirectService redirect, ISocketRegistry sockets, IClientAccess access, IPresenceService presence, RelaySocketHandler handler, ITrafficLimiter traffic, RelaySettings settings, FreeSpeech.Ops.PeerOptions peer)
    {
        _traffic = traffic;
        _settings = settings;
        _peer = peer;
        _handler = handler;
        _access = access;
        _presence = presence;
        _redirect = redirect;
        _sockets = sockets;
        _siblings = siblings;
        _configuration = configuration;
    }

    // ------------------------------------------------------------------
    // Status (public/private toggle)
    // ------------------------------------------------------------------

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        if (!IsAuthorized()) return Unauthorized();

        return Ok(new
        {
            is_public = _siblings.IsPublic,
            public_url = _peer.PublicUrl,   // this relay's public address (also the phones' settings); null when not configured
            connected_clients = await _presence.GetRegisteredCountAsync(),
            max_connections = _settings.Current.MaxConnections,   // 0 = no limit set
            sibling_count = _siblings.Whitelist.Count,
            candidate_count = _siblings.Candidates.Count,
            blacklist_count = _siblings.Blacklist.Count,
            client_access = _access.Mode,
            allowed_client_count = _access.Allowed.Count,
            pending_request_count = _access.Requests.Count,
            banned_client_count = _access.Banned.Count,
        });
    }

    [HttpPost("status")]
    public IActionResult SetStatus([FromBody] SetStatusRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();

        _siblings.SetPublic(body.IsPublic);
        return Ok();
    }

    /// <summary>Asks up to <c>count</c> connected clients to move to a sibling that has room (e.g. before maintenance). Clients decide; none is cut off.</summary>
    [HttpPost("drain")]
    public async Task<IActionResult> Drain([FromBody] DrainRequest body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var servers = await _redirect.PickSiblingsAsync(ct);
        if (servers.Count == 0) return Conflict(new { error = "no sibling has room" });

        var asked = 0;
        foreach (var id in _sockets.ConnectionIds)
        {
            if (asked >= Math.Max(0, body.Count)) break;
            if (_sockets.Get(id) is { State: System.Net.WebSockets.WebSocketState.Open } socket)
            {
                try { await RelaySocketHandler.SendRedirectAsync(socket, servers, "admin", ct); asked++; }
                catch (System.Net.WebSockets.WebSocketException) { /* gone meanwhile */ }
            }
        }
        return Ok(new { asked, servers });
    }

    // ------------------------------------------------------------------
    // Settings (tunables of the service)
    // ------------------------------------------------------------------

    /// <summary>The settings in force, those of the configuration, and which ones an administrator changed.</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_settings.View());
    }

    /// <summary>Changes the given settings at once (a missing one stays as it is) and keeps them across restarts.</summary>
    [HttpPost("settings")]
    public IActionResult SetSettings([FromBody] SettingsRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        try { _settings.Set(body); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        return Ok(_settings.View());
    }

    /// <summary>Goes back to the settings of the configuration.</summary>
    [HttpDelete("settings")]
    public IActionResult ResetSettings()
    {
        if (!IsAuthorized()) return Unauthorized();
        _settings.Reset();
        return Ok(_settings.View());
    }

    // ------------------------------------------------------------------
    // Volume limits (bandwidth)
    // ------------------------------------------------------------------

    /// <summary>The volume limits in force (bytes, 0 = off), those of the configuration, and which ones an administrator changed.</summary>
    [HttpGet("limits")]
    public IActionResult GetLimits()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(LimitsView());
    }

    /// <summary>Changes the given limits at once (a missing one is left as it is, 0 turns it off) and keeps them across restarts.</summary>
    [HttpPost("limits")]
    public IActionResult SetLimits([FromBody] LimitsRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (body.IdentityPerDay is < 0 || body.IdentityPer10Minutes is < 0 || body.AddressPerDay is < 0) return BadRequest(new { error = "a limit cannot be negative (0 turns it off)" });
        _traffic.Set(body.IdentityPerDay, body.IdentityPer10Minutes, body.AddressPerDay);
        return Ok(LimitsView());
    }

    /// <summary>Goes back to the limits of the configuration.</summary>
    [HttpDelete("limits")]
    public IActionResult ResetLimits()
    {
        if (!IsAuthorized()) return Unauthorized();
        _traffic.Reset();
        return Ok(LimitsView());
    }

    /// <summary>Who sent the most in the last 24 hours: identities and addresses, most first (<c>limit</c> of each, 10 by default).</summary>
    [HttpGet("traffic")]
    public IActionResult GetTraffic([FromQuery] int limit = 10)
    {
        if (!IsAuthorized()) return Unauthorized();
        var (identities, addresses) = _traffic.Top(Math.Clamp(limit, 1, 200));
        return Ok(new
        {
            identities = identities.Select(i => new { id = i.Key, bytes_24h = i.Bytes }),
            addresses = addresses.Select(a => new { address = a.Key, bytes_24h = a.Bytes }),
        });
    }

    private object LimitsView() => new
    {
        identity_per_day = _traffic.Limits.IdentityPerDay,
        identity_per_10_minutes = _traffic.Limits.IdentityPer10Minutes,
        address_per_day = _traffic.Limits.AddressPerDay,
        defaults = new { identity_per_day = _traffic.Defaults.IdentityPerDay, identity_per_10_minutes = _traffic.Defaults.IdentityPer10Minutes, address_per_day = _traffic.Defaults.AddressPerDay },
        changed = _traffic.Overridden,
    };

    // ------------------------------------------------------------------
    // Siblings (whitelist)
    // ------------------------------------------------------------------

    [HttpGet("siblings")]
    public IActionResult GetSiblings()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_siblings.Whitelist.Select(s => new { url = s.Url }));
    }

    // ------------------------------------------------------------------
    // Candidates (suggested, not yet trusted)
    // ------------------------------------------------------------------

    [HttpGet("candidates")]
    public IActionResult GetCandidates()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_siblings.Candidates);
    }

    [HttpDelete("candidates")]
    public IActionResult DismissCandidate([FromQuery] string url)
    {
        if (!IsAuthorized()) return Unauthorized();
        return _siblings.DismissCandidate(url) ? Ok() : NotFound();
    }

    // ------------------------------------------------------------------
    // Blacklist
    // ------------------------------------------------------------------

    [HttpGet("blacklist")]
    public IActionResult GetBlacklist()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_siblings.Blacklist);
    }

    [HttpPost("blacklist")]
    public IActionResult Ban([FromBody] BanRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Url))
            return BadRequest(new { error = "url is required" });

        _siblings.Ban(body.Url, body.Reason);
        return Ok();
    }

    [HttpDelete("blacklist")]
    public IActionResult Unban([FromQuery] string url)
    {
        if (!IsAuthorized()) return Unauthorized();
        return _siblings.Unban(url) ? Ok() : NotFound();
    }

    // ------------------------------------------------------------------
    // Client access (who may register on this server)
    // ------------------------------------------------------------------

    [HttpGet("access")]
    public IActionResult GetAccess()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(new { mode = _access.Mode, requests_open = _access.RequestsOpen, pending_requests = _access.Requests.Count, allowed = _access.Allowed, banned = _access.Banned, invitations = _access.Invitations.Select(i => new { id = i.Id, note = i.Note, expires_at = i.ExpiresAt }) });
    }

    [HttpPost("access/mode")]
    public async Task<IActionResult> SetAccessMode([FromBody] AccessModeRequest body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        try { _access.SetMode(body.Mode); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        await TellClientsAsync(ct);
        return Ok();
    }

    /// <summary>Tells every connected client the sign-up rule now in force, so what they show follows the server's setting at once.</summary>
    private Task TellClientsAsync(CancellationToken ct) => _handler.BroadcastStateAsync(ct);

    /// <summary>The public keys of the allowed identities that have connected since they were allowed: what a group needs. Public data, behind the admin secret like everything here.</summary>
    [HttpGet("clients/keys")]
    public IActionResult GetClientKeys()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_access.Allowed.Where(a => a.EncKey != null && a.SignKey != null).Select(a => new { id = a.Id, note = a.Note, enc_key = a.EncKey, sign_key = a.SignKey }));
    }

    /// <summary>The identities of the administrator's tools (the console) that create groups for this server's members.</summary>
    [HttpGet("provisioners")]
    public IActionResult GetProvisioners()
    {
        if (!IsAuthorized()) return Unauthorized();
        return Ok(_access.Provisioners);
    }

    [HttpPost("provisioners")]
    public async Task<IActionResult> AddProvisioner([FromBody] ProvisionerRequest body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!IsDiscoveryId(body.Id)) return BadRequest(new { error = "id must be a 64-hex discovery id" });

        // The tool may give its public keys at once; they are only taken if they really hash to that id.
        string? enc = null, sign = null;
        if (!string.IsNullOrEmpty(body.EncKey) || !string.IsNullOrEmpty(body.SignKey))
        {
            try
            {
                if (!RegistrationAuth.KeysMatchId(body.Id, Convert.FromBase64String(body.EncKey ?? ""), Convert.FromBase64String(body.SignKey ?? ""))) return BadRequest(new { error = "the keys do not match this id" });
                (enc, sign) = (body.EncKey, body.SignKey);
            }
            catch (FormatException) { return BadRequest(new { error = "invalid key encoding" }); }
        }
        _access.AddProvisioner(body.Id, enc, sign);
        await TellClientsAsync(ct);
        return Ok();
    }

    [HttpDelete("provisioners")]
    public async Task<IActionResult> RemoveProvisioner([FromQuery] string id, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!_access.RemoveProvisioner(id)) return NotFound();
        await TellClientsAsync(ct);
        return Ok();
    }

    /// <summary>The identities connected right now (memory only), each with its state here: allowed, banned or neither. To allow or ban from a list.</summary>
    [HttpGet("connected")]
    public IActionResult GetConnected()
    {
        if (!IsAuthorized()) return Unauthorized();
        var allowed = _access.Allowed.Select(a => a.Id).ToHashSet();
        var banned = _access.Banned.Select(b => b.Id).ToHashSet();
        return Ok(_presence.Connected().Select(c => new { id = c.DiscoveryId, connections = c.Connections, allowed = allowed.Contains(c.DiscoveryId), banned = banned.Contains(c.DiscoveryId) }));
    }

    /// <summary>Allows every identity connected right now: used before switching to "on approval", so nobody who is here is turned away at their next connection.</summary>
    [HttpPost("clients/allow-connected")]
    public IActionResult AllowConnected()
    {
        if (!IsAuthorized()) return Unauthorized();
        var banned = _access.Banned.Select(b => b.Id).ToHashSet();
        var done = 0;
        foreach (var c in _presence.Connected().Where(c => !banned.Contains(c.DiscoveryId)))
        {
            _access.Allow(c.DiscoveryId, "connected when the server became private");
            done++;
        }
        return Ok(new { allowed = done });
    }

    [HttpPost("clients")]
    public IActionResult AllowClient([FromBody] ClientRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!IsDiscoveryId(body.Id)) return BadRequest(new { error = "id must be a 64-character hexadecimal discovery id" });
        _access.Allow(body.Id, body.Note);
        return Ok();
    }

    /// <summary>Removes a client from the allow-list and cuts its live connections.</summary>
    [HttpDelete("clients")]
    public async Task<IActionResult> RemoveClient([FromQuery] string id)
    {
        if (!IsAuthorized()) return Unauthorized();
        var removed = _access.Remove(id);
        if (removed) await DisconnectAsync(id);
        return removed ? Ok() : NotFound();
    }

    [HttpPost("clients/ban")]
    public async Task<IActionResult> BanClient([FromBody] ClientRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!IsDiscoveryId(body.Id)) return BadRequest(new { error = "id must be a 64-character hexadecimal discovery id" });
        _access.Ban(body.Id, body.Reason);
        await DisconnectAsync(body.Id);
        return Ok();
    }

    [HttpDelete("clients/ban")]
    public IActionResult UnbanClient([FromQuery] string id)
    {
        if (!IsAuthorized()) return Unauthorized();
        return _access.Unban(id) ? Ok() : NotFound();
    }

    /// <summary>Creates a single-use invitation. The token is in this answer only; the server keeps its hash.</summary>
    [HttpPost("invitations")]
    public IActionResult CreateInvitation([FromBody] InvitationRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        var days = Math.Clamp(body.Days <= 0 ? 7 : body.Days, 1, 90);
        var token = _access.CreateInvitation(body.Note, TimeSpan.FromDays(days));
        return Ok(new { token, expires_in_days = days });
    }

    [HttpDelete("invitations")]
    public IActionResult RevokeInvitation([FromQuery] string id)
    {
        if (!IsAuthorized()) return Unauthorized();
        return _access.RevokeInvitation(id) ? Ok() : NotFound();
    }

    // ------------------------------------------------------------------
    // Access requests (unknown identities waiting for the administrator)
    // ------------------------------------------------------------------

    /// <summary>The queue, oldest first. <c>name</c> filters on the name or the note (contains), <c>limit</c>/<c>offset</c> page through it.</summary>
    [HttpGet("access/requests")]
    public IActionResult GetRequests([FromQuery] string? name, [FromQuery(Name = "older_than_days")] int olderThanDays = 0, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        if (!IsAuthorized()) return Unauthorized();
        var all = Filter(_access.Requests, name, olderThanDays).ToList();
        var page = all.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500))
            .Select(r => new { id = r.Id, name = r.Name, note = r.Note, requested_at = r.RequestedAt });
        return Ok(new { open = _access.RequestsOpen, total = all.Count, requests = page });
    }

    /// <summary>Opens or closes the queue: when closed, unknown identities are simply told the server is private (requests already waiting stay).</summary>
    [HttpPost("access/requests/open")]
    public IActionResult SetRequestsOpen([FromBody] RequestsOpenRequest body)
    {
        if (!IsAuthorized()) return Unauthorized();
        _access.SetRequestsOpen(body.Open);
        return Ok();
    }

    [HttpPost("access/requests/accept")]
    public IActionResult AcceptRequests([FromBody] RequestDecision body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!Select(body, out var ids, out var unknown, out var ambiguous, out var error)) return BadRequest(new { error });
        var done = _access.ApproveRequests(ids);
        return Ok(new { accepted = done.Select(Short), unknown, ambiguous });
    }

    [HttpPost("access/requests/reject")]
    public IActionResult RejectRequests([FromBody] RequestDecision body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!Select(body, out var ids, out var unknown, out var ambiguous, out var error)) return BadRequest(new { error });
        var done = _access.RejectRequests(ids, body.Ban, body.Reason);
        return Ok(new { rejected = done.Select(Short), banned = body.Ban, unknown, ambiguous });
    }

    private static string Short(string id) => id[..8];

    private static IEnumerable<AccessRequest> Filter(IEnumerable<AccessRequest> requests, string? name, int? olderThanDays)
    {
        if (!string.IsNullOrWhiteSpace(name))
            requests = requests.Where(r => (r.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase) || (r.Note ?? "").Contains(name, StringComparison.OrdinalIgnoreCase));
        if (olderThanDays is > 0)
            requests = requests.Where(r => r.RequestedAt <= DateTime.UtcNow.AddDays(-olderThanDays.Value));
        return requests;
    }

    /// <summary>
    /// Turns a decision into discovery ids: explicit ids (6+ hex characters, a prefix is enough when unique) and/or <c>all</c> narrowed by
    /// <c>name</c> and <c>older_than_days</c>. Nothing selected by accident: with neither ids nor <c>all</c> the call is refused.
    /// </summary>
    private bool Select(RequestDecision body, out List<string> ids, out List<string> unknown, out List<string> ambiguous, out string error)
    {
        ids = []; unknown = []; ambiguous = []; error = "";
        var pending = _access.Requests;
        var hasFilter = !string.IsNullOrWhiteSpace(body.Name) || body.OlderThanDays > 0;

        if (body.Ids is { Count: > 0 })
        {
            // The verification code ("3FA9-C1B2-7E40", the start of the id in groups of four) is accepted as it is shown: dashes and case do not matter.
            foreach (var wanted in body.Ids.Select(i => i.Trim().Replace("-", "").ToLowerInvariant()).Where(i => i.Length > 0).Distinct())
            {
                if (wanted.Length < 6 || !wanted.All(Uri.IsHexDigit)) { unknown.Add(wanted); continue; }
                var matches = pending.Where(r => r.Id.StartsWith(wanted, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0) unknown.Add(wanted);
                else if (matches.Count > 1) ambiguous.Add(wanted);
                else ids.Add(matches[0].Id);
            }
            return true;
        }

        if (!body.All && !hasFilter) { error = "give ids, or all (optionally with name / older_than_days)"; return false; }
        ids = Filter(pending, body.Name, body.OlderThanDays).Select(r => r.Id).ToList();
        return true;
    }

    private static bool IsDiscoveryId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit);

    private async Task DisconnectAsync(string discoveryId)
    {
        foreach (var connectionId in await _presence.GetConnectionsAsync(discoveryId.Trim().ToLowerInvariant()))
        {
            if (_sockets.Get(connectionId) is { State: System.Net.WebSockets.WebSocketState.Open } socket)
            {
                try { await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "access_refused", CancellationToken.None); }
                catch (System.Net.WebSockets.WebSocketException) { /* gone meanwhile */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Auth
    // ------------------------------------------------------------------

    /// <summary>Secret AND local machine (see <see cref="FreeSpeech.Ops.AdminGuard"/>): the admin API is never reachable from the internet, whatever the proxy does.</summary>
    private bool IsAuthorized() => FreeSpeech.Ops.AdminGuard.IsAuthorized(HttpContext, _configuration);
}

public class SetStatusRequest
{
    [JsonPropertyName("is_public")] public bool IsPublic { get; set; }
}

public class DrainRequest
{
    public int Count { get; set; }
}

/// <summary>New volume limits, in bytes; a missing one stays as it is, 0 turns it off.</summary>
public class LimitsRequest
{
    [JsonPropertyName("identity_per_day")] public long? IdentityPerDay { get; set; }
    [JsonPropertyName("identity_per_10_minutes")] public long? IdentityPer10Minutes { get; set; }
    [JsonPropertyName("address_per_day")] public long? AddressPerDay { get; set; }
}

public class ProvisionerRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("enc_key")] public string? EncKey { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("sign_key")] public string? SignKey { get; set; }
}

public class UrlRequest
{
    public string Url { get; set; } = "";
}

public class AccessModeRequest
{
    public string Mode { get; set; } = "";
}

public class ClientRequest
{
    public string Id { get; set; } = "";
    public string? Note { get; set; }
    public string? Reason { get; set; }
}

public class RequestsOpenRequest
{
    public bool Open { get; set; }
}

/// <summary>Decision on access requests: explicit ids (prefixes allowed), or <c>all</c> narrowed by <c>name</c> / <c>older_than_days</c>.</summary>
public class RequestDecision
{
    public List<string>? Ids { get; set; }
    public bool All { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("older_than_days")] public int OlderThanDays { get; set; }
    public bool Ban { get; set; }
    public string? Reason { get; set; }
}

public class InvitationRequest
{
    public string? Note { get; set; }
    public int Days { get; set; }
}

public class BanRequest
{
    public string Url { get; set; } = "";
    public string? Reason { get; set; }
}
