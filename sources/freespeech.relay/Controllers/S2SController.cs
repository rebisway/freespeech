/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;
using FreeSpeech.Relay.Models;
using FreeSpeech.Relay.Services;
using Microsoft.AspNetCore.Mvc;

namespace FreeSpeech.Relay.Controllers;

/// <summary>
/// Incoming S2S requests from a whitelisted sibling (protocol §10):
/// cascade discovery lookup, envelope forwarding, and flag propagation.
/// Every route requires a valid request signature from a whitelisted sibling
/// (checked by S2SAuthMiddleware, see S2SAuth). A private server, or any unauthenticated/unknown caller, gets
/// the exact same 404 as a route that doesn't exist — refusing must never
/// reveal whether this server exists, is private, or just doesn't know the
/// caller (protocol §10: "un refus ne doit rien laisser transparaître").
/// </summary>
[ApiController]
[Route("s2s")]
public class S2SController : ControllerBase
{
    private readonly ISiblingRegistry _siblings;
    private readonly IMessageDeliveryService _delivery;
    private readonly IPendingFlagService _flags;
    private readonly IPresenceService _presence;
    private readonly IPushNotifier _push;
    private readonly IRedirectService _redirect;
    private readonly ILogger<S2SController> _logger;

    public S2SController(ISiblingRegistry siblings, IMessageDeliveryService delivery, IPendingFlagService flags, IPresenceService presence, IPushNotifier push, IRedirectService redirect, ILogger<S2SController> logger)
    {
        _redirect = redirect;
        _push = push;
        _siblings = siblings;
        _delivery = delivery;
        _flags = flags;
        _presence = presence;
        _logger = logger;
    }

    [HttpPost("lookup")]
    public async Task<IActionResult> Lookup([FromBody] LookupRequest body)
    {
        var sibling = Authenticate();
        if (sibling == null) return NotFound();
        if (string.IsNullOrWhiteSpace(body.DiscoveryId)) return NotFound();

        // Deliberately single-hop: this only answers for OUR own local
        // presence, never asks our own siblings in turn — keeps the S2S
        // graph flat and loop-free rather than a full cascade.
        var online = await _presence.IsOnlineAsync(body.DiscoveryId);
        return Ok(new { online });
    }

    /// <summary>How many more clients this server can take (-1 = unlimited, 0 = full): a sibling asks before redirecting a client here.</summary>
    [HttpPost("capacity")]
    public async Task<IActionResult> Capacity()
    {
        if (Authenticate() == null) return NotFound();
        return Ok(new { free_slots = await _redirect.FreeSlotsAsync() });
    }

    [HttpPost("relay")]
    public async Task<IActionResult> Relay([FromBody] RelayRequest body, CancellationToken ct)
    {
        var sibling = Authenticate();
        if (sibling == null) return NotFound();
        if (body.Envelope == null || string.IsNullOrWhiteSpace(body.Envelope.To)) return NotFound();

        var delivered = await _delivery.DeliverLocallyAsync(body.Envelope, excludeConnectionId: null, ct);
        if (delivered)
            _logger.LogDebug("Envelope forwarded from sibling {Sibling} delivered locally to {To}", sibling.Url, FreeSpeech.Ops.TraceService.Short(body.Envelope.To));

        return Ok(new { delivered });
    }

    [HttpPost("flag")]
    public IActionResult PropagateFlag([FromBody] PendingFlag flag)
    {
        var sibling = Authenticate();
        if (sibling == null) return NotFound();
        if (flag.State != PendingFlagState.Waiting && flag.State != PendingFlagState.Ready) return NotFound();

        // Trust boundary is at the sibling, not the individual client: we
        // don't re-check that "From" is actually connected here (it isn't —
        // it's on the ORIGIN server), only that a whitelisted sibling is
        // vouching for this flag.
        _flags.SetFlag(flag);
        if (flag.State == PendingFlagState.Waiting) _ = _push.WakeAsync(flag.To);   // the recipient's token may be registered here, not on the origin (§9)
        _logger.LogDebug("Flag propagated from sibling {Sibling}: {From} -> {To} state={State}", sibling.Url, FreeSpeech.Ops.TraceService.Short(flag.From), FreeSpeech.Ops.TraceService.Short(flag.To), flag.State);
        return Ok();
    }

    [HttpPost("siblings/sync")]
    public IActionResult SyncSiblings([FromBody] SyncSiblingsRequest body)
    {
        var sibling = Authenticate();
        if (sibling == null) return NotFound();

        foreach (var url in body.Siblings ?? [])
        {
            if (string.IsNullOrWhiteSpace(url) || url == sibling.Url) continue;
            _siblings.SuggestCandidate(url, suggestedBy: sibling.Url);
        }

        return Ok();
    }

    // ------------------------------------------------------------------
    // Auth
    // ------------------------------------------------------------------

    /// <summary>The sibling authenticated by <see cref="S2SAuthMiddleware"/> (signature, timestamp, nonce, quota already checked); null if the middleware did not run.</summary>
    private Sibling? Authenticate() => HttpContext.Items[S2SAuthMiddleware.SiblingItem] as Sibling;
}

public class LookupRequest
{
    [JsonPropertyName("discovery_id")] public string DiscoveryId { get; set; } = "";
}

public class RelayRequest
{
    public Envelope? Envelope { get; set; }
}

public class SyncSiblingsRequest
{
    public List<string>? Siblings { get; set; }
}
