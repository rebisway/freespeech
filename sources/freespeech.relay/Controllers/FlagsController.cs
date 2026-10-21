/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Relay.Models;
using FreeSpeech.Relay.Services;
using Microsoft.AspNetCore.Mvc;

namespace FreeSpeech.Relay.Controllers;

// ===========================================================================
// TimeController
// ===========================================================================

/// <summary>
/// Exposes the server's UTC clock so clients compute rendezvous times from a
/// single authoritative source, avoiding device clock drift (protocol §5).
/// </summary>
[ApiController]
[Route("api/time")]
public class TimeController : ControllerBase
{
    /// <summary>Returns the current UTC time on this server.</summary>
    [HttpGet]
    public IActionResult GetServerTime() => Ok(new { utc = DateTime.UtcNow });
}

// ===========================================================================
// FlagsController
// ===========================================================================

/// <summary>
/// HTTP endpoints for the WAITING/READY rendezvous flag protocol (§9).
///
/// CLIENT ENDPOINTS (X-Discovery-Id header identifies the caller):
///   GET    /api/flags/{discoveryId}   - poll flags addressed to me
///   POST   /api/flags                  - set or update my flag
///   DELETE /api/flags/{from}/{to}      - remove a flag after rendezvous
///
/// The caller must both claim the identity via the header AND actually be
/// connected as it on the realtime hub (<see cref="Hubs.RelayHub"/>) — this
/// is what stops a stranger from reading or forging another identity's
/// flags with nothing but a guessed discovery id.
///
/// S2S propagation (protocol §10): a flag set here is also fire-and-
/// forget propagated to every whitelisted sibling when this server is
/// public, so the rendezvous still works if the recipient later shows up on
/// a sibling instead of this exact server.
/// </summary>
[ApiController]
[Route("api/flags")]
public class FlagsController : ControllerBase
{
    private readonly IPendingFlagService _flags;
    private readonly IPresenceService _presence;
    private readonly ISiblingRegistry _siblings;
    private readonly IS2SClient _s2sClient;
    private readonly IPushNotifier _push;
    private readonly FreeSpeech.Ops.TraceService _trace;
    private readonly ILogger<FlagsController> _logger;

    private readonly IHttpSessionService _sessions;

    public FlagsController(IPendingFlagService flags, IPresenceService presence, ISiblingRegistry siblings, IS2SClient s2sClient, IPushNotifier push, IHttpSessionService sessions, FreeSpeech.Ops.TraceService trace, ILogger<FlagsController> logger)
    {
        _trace = trace;
        _sessions = sessions;
        _push = push;
        _flags = flags;
        _presence = presence;
        _siblings = siblings;
        _s2sClient = s2sClient;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // GET /api/flags/{discoveryId}
    // ------------------------------------------------------------------

    /// <summary>Returns all active flags addressed TO the given discovery id.</summary>
    [HttpGet("{discoveryId}")]
    public async Task<IActionResult> GetFlags(string discoveryId)
    {
        if (!await IsAuthorizedCallerAsync(discoveryId))
            return Unauthorized(new { error = "Not connected as this identity" });

        return Ok(_flags.GetFlagsFor(discoveryId));
    }

    // ------------------------------------------------------------------
    // POST /api/flags
    // ------------------------------------------------------------------

    /// <summary>Creates or updates a WAITING or READY flag. Caller must be the From identity.</summary>
    [HttpPost]
    public async Task<IActionResult> SetFlag([FromBody] PendingFlag flag)
    {
        if (!await IsAuthorizedCallerAsync(flag.From))
            return Unauthorized(new { error = "Not connected as this identity" });

        if (flag.State != PendingFlagState.Waiting && flag.State != PendingFlagState.Ready)
            return BadRequest(new { error = "State must be WAITING or READY" });

        if (flag.State == PendingFlagState.Ready)
        {
            if (!flag.NextConnectAt.HasValue)
                return BadRequest(new { error = "A READY flag must include next_connect_at" });

            if (flag.NextConnectAt.Value <= DateTime.UtcNow)
                return BadRequest(new { error = "next_connect_at must be in the future (server UTC time)" });
        }

        var saved = _flags.SetFlag(flag);

        // Wake-up (protocol §9): a WAITING flag means the recipient has a message to fetch and is not here.
        if (saved.State == PendingFlagState.Waiting)
        {
            _trace.Write(saved.From, $"WAITING flag raised towards {FreeSpeech.Ops.TraceService.Short(saved.To)}: waking them");
            _trace.Write(saved.To, $"WAITING flag from {FreeSpeech.Ops.TraceService.Short(saved.From)}: they have a letter for you");
            _ = _push.WakeAsync(saved.To);
        }

        if (_siblings.IsPublic)
        {
            // Deliberately CancellationToken.None: this must keep running in
            // the background after the HTTP response returns, not be
            // cancelled when the request that triggered it completes.
            foreach (var sibling in _siblings.Whitelist)
                _ = _s2sClient.PropagateFlagAsync(sibling, saved, CancellationToken.None);
        }

        return Ok(new { updated_at = saved.UpdatedAt });
    }

    // ------------------------------------------------------------------
    // DELETE /api/flags/{from}/{to}
    // ------------------------------------------------------------------

    /// <summary>Removes a flag after a successful rendezvous. Either party may remove it.</summary>
    [HttpDelete("{from}/{to}")]
    public async Task<IActionResult> RemoveFlag(string from, string to)
    {
        var callerId = Request.Headers["X-Discovery-Id"].FirstOrDefault() ?? "";
        if (callerId != from && callerId != to)
            return Unauthorized(new { error = "Not authorized to remove this flag" });

        if (!HasSessionToken(callerId) || !await _presence.IsOnlineAsync(callerId))
            return Unauthorized(new { error = "Not connected as this identity" });

        _flags.RemoveFlag(from, to);
        return Ok();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>The caller presents the token handed out when this identity registered over the WebSocket (§11): knowing the id is not enough.</summary>
    private bool HasSessionToken(string discoveryId) => _sessions.IsValid(discoveryId, Request.Headers["X-Flag-Token"].FirstOrDefault());

    /// <summary>
    /// True only when the caller both claims <paramref name="claimedId"/> via
    /// the X-Discovery-Id header AND has an active realtime connection under
    /// that same identity.
    /// </summary>
    private async Task<bool> IsAuthorizedCallerAsync(string claimedId)
    {
        var headerId = Request.Headers["X-Discovery-Id"].FirstOrDefault() ?? "";
        return headerId == claimedId && HasSessionToken(claimedId) && await _presence.IsOnlineAsync(claimedId);
    }
}
