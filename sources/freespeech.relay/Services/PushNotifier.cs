/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace FreeSpeech.Relay.Services;

/// <summary>A device's push registration: which push network and its token. The relay picks the gateway among the ones it is linked with.</summary>
public sealed record PushRegistration(string Platform, string Token);

/// <summary>Per-identity push tokens (protocol §9). In memory, like the flags: clients re-register on every connection.</summary>
public interface IPushRegistry
{
    void Set(string discoveryId, PushRegistration registration);
    bool Remove(string discoveryId);
    PushRegistration? Get(string discoveryId);
}

public sealed class PushRegistry : IPushRegistry
{
    private readonly ConcurrentDictionary<string, PushRegistration> _byId = new();

    public void Set(string discoveryId, PushRegistration registration) => _byId[discoveryId] = registration;
    public bool Remove(string discoveryId) => _byId.TryRemove(discoveryId, out _);
    public PushRegistration? Get(string discoveryId) => _byId.TryGetValue(discoveryId, out var r) ? r : null;
}

/// <summary>Asks the push gateway to wake an offline identity. Never carries content (protocol §9).</summary>
public interface IPushNotifier
{
    /// <summary>True when this server is linked with at least one push gateway that is not banned: without one, push is off.</summary>
    bool HasGateway { get; }

    /// <summary>Wakes <paramref name="discoveryId"/> if it has a token and no live connection. Rate-limited, never throws.</summary>
    Task WakeAsync(string discoveryId, CancellationToken ct = default);
}

/// <summary>
/// Calls <c>POST {gateway}/push</c> with a generic payload. Only the gateways the operator linked (<c>add push</c> /
/// <c>accept push</c>) are called; the client names none. Without a linked gateway, push is off and only polling remains.
///
/// Gateways are interchangeable (same credentials, no state worth keeping), so several can be linked: they are tried in
/// random order, at most <see cref="MaxAttempts"/> in all.
/// A gateway that is down or saturated (5xx, timeout, 429 "busy") is set aside for a while (circuit breaker), so a dead
/// one costs a single timeout, not one per wake-up. A wake-up is best effort: polling remains the fallback.
/// </summary>
public sealed class PushNotifier : IPushNotifier
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 3;
    private const int FailuresToOpen = 3;
    private static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxOpenFor = TimeSpan.FromSeconds(120);

    private readonly IPushRegistry _registry;
    private readonly IPresenceService _presence;
    private readonly IHttpClientFactory _http;
    private readonly ISiblingRegistry _siblings;
    private readonly FreeSpeech.Ops.TraceService _trace;
    private readonly ILogger<PushNotifier> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastWake = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime OpenUntil)> _breaker = new(StringComparer.OrdinalIgnoreCase);

    private readonly FreeSpeech.Ops.PeerStore _peers;
    private readonly FreeSpeech.Ops.PeerIdentity _identity;
    private readonly FreeSpeech.Ops.LinkStatus _links;

    public PushNotifier(IPushRegistry registry, IPresenceService presence, IHttpClientFactory http, IConfiguration config, ISiblingRegistry siblings, FreeSpeech.Ops.TraceService trace,
        FreeSpeech.Ops.PeerStore peers, FreeSpeech.Ops.PeerIdentity identity, FreeSpeech.Ops.LinkStatus links, ILogger<PushNotifier> logger)
    {
        _links = links;
        _peers = peers;
        _identity = identity;
        _trace = trace;
        _siblings = siblings;
        _registry = registry;
        _presence = presence;
        _http = http;
        _logger = logger;
    }

    /// <summary>The gateways this server may call right now: the push gateways both administrators accepted (<c>add push</c> / <c>accept push</c>), minus banned addresses.</summary>
    private string[] Allowed() =>
        _peers.Accepted("push").Select(p => p.Url.TrimEnd('/')).Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(g => !_siblings.IsBlacklisted(g)).ToArray();

    public bool HasGateway => Allowed().Length > 0;

    private enum Attempt { Done, TryNext, Gone }

    public async Task WakeAsync(string discoveryId, CancellationToken ct = default)
    {
        try
        {
            var reg = _registry.Get(discoveryId);
            if (reg == null) { _trace.Write(discoveryId, "wake-up not sent: no push token registered here"); return; }
            if (!HasGateway) { _trace.Write(discoveryId, "wake-up not sent: no push gateway linked on this server"); return; }
            if (await _presence.IsOnlineAsync(discoveryId)) { _trace.Write(discoveryId, "wake-up not sent: already connected"); return; }

            var now = DateTime.UtcNow;
            if (_lastWake.TryGetValue(discoveryId, out var last) && now - last < MinInterval)
            {
                _trace.Write(discoveryId, $"wake-up held back: the previous one was {(now - last).TotalSeconds:0} s ago (one per {MinInterval.TotalSeconds:0} s)");
                return;
            }
            _lastWake[discoveryId] = now;   // held during the attempt, so concurrent calls do not double it; released below if no gateway took it

            foreach (var gateway in Candidates())
            {
                var result = await TryGatewayAsync(gateway, reg, ct);
                if (result == Attempt.Gone)
                {
                    _registry.Remove(discoveryId);   // the push network no longer knows this token
                    _trace.Write(discoveryId, $"wake-up via {gateway}: the push network does not know this token any more (token dropped)");
                    _logger.LogInformation("Push token of {Id} dropped (gateway: gone)", discoveryId.Length <= 8 ? discoveryId : discoveryId[..8]);
                    return;
                }
                _trace.Write(discoveryId, $"wake-up via {gateway}: {result}");
                if (result == Attempt.Done) return;
            }
            _trace.Write(discoveryId, "wake-up failed: no gateway took it (the next one may try again at once)");
            _lastWake.TryRemove(discoveryId, out _);   // no gateway took it: the next wake-up may try again at once
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _lastWake.TryRemove(discoveryId, out _);
            _logger.LogWarning("Push gateway unreachable: {Message}", ex.Message);
        }
    }

    /// <summary>The linked gateways in random order (load is shared, none is preferred); gateways whose breaker is open are skipped.</summary>
    private IEnumerable<string> Candidates() =>
        Allowed().OrderBy(_ => Random.Shared.Next()).Where(g => !IsOpen(g)).Take(MaxAttempts).ToList();

    private bool IsOpen(string gateway) => _breaker.TryGetValue(gateway, out var b) && b.OpenUntil > DateTime.UtcNow;

    private void Failed(string gateway, TimeSpan? retryAfter = null)
    {
        var state = _breaker.AddOrUpdate(gateway, (1, DateTime.MinValue), (_, b) => (b.Failures + 1, b.OpenUntil));
        // A gateway that says "busy, retry in N s" is set aside at once; a silent failure needs a few in a row.
        if (retryAfter != null || state.Failures >= FailuresToOpen)
        {
            var until = DateTime.UtcNow + (retryAfter is { } r ? TimeSpan.FromSeconds(Math.Min(r.TotalSeconds, MaxOpenFor.TotalSeconds)) : OpenFor);
            _breaker[gateway] = (0, until);
            _logger.LogWarning("Push gateway {Gateway} set aside until {Until:HH:mm:ss}", gateway, until);
        }
    }

    private void Worked(string gateway) => _breaker.TryRemove(gateway, out _);

    private async Task<Attempt> TryGatewayAsync(string gateway, PushRegistration reg, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { platform = reg.Platform, token = reg.Token, kind = "wake" });
        using var request = new HttpRequestMessage(HttpMethod.Post, gateway + "/push") { Content = new ByteArrayContent(json) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        FreeSpeech.Ops.PeerSigning.Sign(request, _identity, "/push", json);   // the gateway knows this server by its key

        HttpResponseMessage response;
        try { response = await _http.CreateClient("Push").SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _links.Fail(gateway, "unreachable");
            Failed(gateway);
            _logger.LogWarning("Push gateway {Gateway} unreachable: {Message}", gateway, ex.Message);
            return Attempt.TryNext;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) { Worked(gateway); _links.Ok(gateway); return Attempt.Done; }
            if (response.StatusCode == HttpStatusCode.Gone) { Worked(gateway); _links.Ok(gateway); return Attempt.Gone; }
            _links.Fail(gateway, response.StatusCode == HttpStatusCode.TooManyRequests ? "busy" : (int)response.StatusCode >= 500 ? "error " + (int)response.StatusCode : "refused " + (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // "token": this very token was woken a moment ago — another gateway would only wake it twice. "busy": go elsewhere.
                if (response.Headers.TryGetValues("X-Push-Reason", out var why) && why.Contains("token")) return Attempt.Done;
                Failed(gateway, response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10));
                return Attempt.TryNext;
            }

            if ((int)response.StatusCode >= 500) Failed(gateway);   // 503 also means "no credentials for this platform here": another gateway may have them
            _logger.LogWarning("Push gateway {Gateway} answered {Status}", gateway, (int)response.StatusCode);
            return (int)response.StatusCode >= 500 ? Attempt.TryNext : Attempt.Done;   // other 4xx: the request itself is wrong, no use retrying
        }
    }
}