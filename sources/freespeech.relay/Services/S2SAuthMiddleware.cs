/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Authenticates every /s2s request before any controller sees it (protocol §10). The caller must be a relay both administrators
/// accepted and sign its call with its key (<see cref="PeerSigning"/>). Any failure — private server, unknown or banned relay, bad or stale
/// signature, replayed call, oversized body — gets the same 404 as a route that does not exist, so a refusal reveals nothing. Only an
/// authenticated sibling that exceeds its quota gets a 429. On success the sibling is put in HttpContext.Items["s2s.sibling"] and the body
/// is made re-readable.
/// </summary>
public class S2SAuthMiddleware
{
    public const string SiblingItem = "s2s.sibling";

    private readonly RequestDelegate _next;

    public S2SAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ISiblingRegistry siblings, S2SRateLimiter limiter, RelaySettings settings, PeerStore peers, LinkStatus links, ILogger<S2SAuthMiddleware> logger)
    {
        if (!context.Request.Path.StartsWithSegments("/s2s"))
        {
            await _next(context);
            return;
        }

        if (!siblings.IsPublic) { Refuse(context); return; }

        var maxBody = settings.Current.MaxMessageBytes + 16 * 1024;
        var body = await ReadBodyAsync(context.Request, maxBody, context.RequestAborted);
        if (body == null) { Refuse(context); return; }
        context.Request.Body = new MemoryStream(body);   // seekable: the signature covers the body

        var (outcome, peer) = await PeerSigning.VerifyAsync(context.Request, peers, "relay");
        if (outcome is PeerSigning.Outcome.BadSignature or PeerSigning.Outcome.Replay)
            Notifier.Raise(NotifyKind.Abuse, "Forged or replayed server call", "A server-to-server call carried a signature that does not verify or that was already used.");
        if (outcome != PeerSigning.Outcome.Ok || peer == null || siblings.IsBlacklisted(peer.Url)) { Refuse(context); return; }

        if (!limiter.Allow("peer:" + peer.Id, settings.Current.S2SRatePerMinute))
        {
            logger.LogWarning("S2S quota exceeded by {Sibling}", peer.Url);
            Notifier.Raise(NotifyKind.Abuse, "Sibling over its quota", $"The linked server {peer.Url} sent more requests than Server:S2SRatePerMinute allows; its requests are answered 429.");
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            return;
        }

        links.Ok(peer.Url);   // a call received from the sibling counts as an exchange that worked
        context.Items[SiblingItem] = new Sibling { Url = peer.Url.TrimEnd('/') };
        await _next(context);
    }

    private static void Refuse(HttpContext context) => context.Response.StatusCode = StatusCodes.Status404NotFound;

    /// <summary>Reads the whole body, or null if it is larger than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes) return null;
        }
        return ms.ToArray();
    }
}
