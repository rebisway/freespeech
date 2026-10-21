/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>
/// Linking servers (relay ↔ push gateway) by mutual consent, with no shared secret: each server has its own key pair
/// (<see cref="PeerIdentity"/>), asks the other for a link (<c>add</c>), and the other's administrator accepts or rejects
/// (<c>requests</c>, <c>accept</c>, <c>reject</c>). Everything is saved in <c>peers.json</c> next to the server's other state.
/// </summary>
public static class PeerExtensions
{
    /// <param name="kind">"relay" or "push".</param>
    /// <param name="kinds">Kinds of server this one may be linked with.</param>
    /// <param name="defaultStateDir">Where the identity and the peers are kept unless <c>Peer:StatePath</c> says otherwise.</param>
    public static IHostApplicationBuilder AddPeers(this IHostApplicationBuilder builder, string kind, string[] kinds, Func<IConfiguration, string> defaultStateDir)
    {
        var config = builder.Configuration;
        var dir = Path.GetFullPath(config["Peer:StatePath"] ?? defaultStateDir(config));
        Directory.CreateDirectory(dir);

        builder.Services.AddSingleton(new PeerOptions(kind, kinds, PeerStore.Clean(config["Peer:Name"], 64) ?? Environment.MachineName, config["Peer:PublicUrl"]?.Trim().TrimEnd('/'), PeerStore.Clean(config["Peer:Contact"], 128)));
        builder.Services.AddSingleton(PeerIdentity.LoadOrCreate(Path.Combine(dir, "peer-identity.json")));
        builder.Services.AddSingleton(sp => new PeerStore(Path.Combine(dir, "peers.json"), config.GetValue("Peer:MaxRequests", 200), TimeSpan.FromDays(Math.Max(1, config.GetValue("Peer:RequestDays", 14))), sp.GetRequiredService<ILogger<PeerStore>>()));
        builder.Services.AddHttpClient("peer", c => c.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<PeerClient>();
        builder.Services.AddHostedService<PeerRefreshService>();
        return builder;
    }

    private static readonly ConcurrentDictionary<string, (int Count, DateTime Since)> Asking = new();

    /// <summary>One address may ask this often: a request comes from a stranger, so it is throttled before anything is stored.</summary>
    private static bool TooMany(HttpContext c)
    {
        var key = c.Connection.RemoteIpAddress?.ToString() ?? "local";
        var now = DateTime.UtcNow;
        var entry = Asking.AddOrUpdate(key, (1, now), (_, e) => now - e.Since > TimeSpan.FromMinutes(10) ? (1, now) : (e.Count + 1, e.Since));
        if (Asking.Count > 5000) foreach (var (k, e) in Asking) if (now - e.Since > TimeSpan.FromMinutes(10)) Asking.TryRemove(k, out _);
        return entry.Count > 30;
    }

    /// <summary>
    /// The public side, reachable from other servers: <c>GET /peer/identity</c>, <c>POST /peer/request</c> (signed by the key it presents)
    /// and <c>POST /peer/status</c> (signed by a peer). Mounted at the root, whatever the admin prefix.
    /// </summary>
    public static IEndpointRouteBuilder MapPeers(this IEndpointRouteBuilder app)
    {
        // The address a request comes from: behind the local reverse proxy, the first address it forwarded.
        static string? SeenFrom(HttpContext c)
        {
            var remote = c.Connection.RemoteIpAddress;
            if (remote != null && System.Net.IPAddress.IsLoopback(remote) && c.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() is { Length: > 0 and <= 45 } forwarded) return forwarded;
            return remote?.ToString();
        }

        // The body of a signed call is read twice (to verify it, then by the handler): it must be rewindable.
        if (app is IApplicationBuilder builder) builder.Use(async (c, next) => { if (c.Request.Path.StartsWithSegments("/peer")) c.Request.EnableBuffering(); await next(); });

        app.MapGet("/peer/identity", (PeerOptions options, PeerIdentity identity) =>
            Results.Ok(new { software = "FreeSpeech", kind = options.Kind, name = options.Name, contact = options.Contact, public_key = identity.PublicKey, fingerprint = identity.Fingerprint }));

        app.MapPost("/peer/request", (HttpContext c, PeerStore store, PeerOptions options, PeerRequestBody body) =>
        {
            if (TooMany(c)) { Notifier.Raise(NotifyKind.Abuse, "Too many link requests", "One address sent more than 30 link requests in 10 minutes; they are answered 'throttled'."); return Results.Json(new { state = "throttled" }, statusCode: StatusCodes.Status429TooManyRequests); }
            if (body.Kind == null || !options.Kinds.Contains(body.Kind)) return Results.Json(new { state = "refused", error = "kind_not_accepted" }, statusCode: 400);
            if (!PeerIdentity.IsValidPublicKey(body.PublicKey) || string.IsNullOrEmpty(body.Sig)) return Results.Json(new { state = "refused", error = "bad_key" }, statusCode: 400);
            var url = body.Url?.Trim() ?? "";
            if (url.Length > 0 && !PeerClient.IsAcceptableUrl(url, insecure: true, out _)) return Results.Json(new { state = "refused", error = "bad_url" }, statusCode: 400);
            if (Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(body.Ts)).TotalMinutes) > 5) return Results.Json(new { state = "refused", error = "bad_time" }, statusCode: 400);
            // The key that signs is the key that is asking: nobody can ask with someone else's fingerprint.
            if (!PeerIdentity.Verify(body.PublicKey!, PeerSigning.RequestText(body.Kind, body.Name, body.Url ?? "", body.Ts, body.Contact), body.Sig)) return Results.Json(new { state = "refused", error = "bad_signature" }, statusCode: 400);

            var seen = SeenFrom(c);
            var received = store.Receive(body.Kind, body.Name, url, body.PublicKey!, body.Contact, seen);
            if (received == PeerRequestResult.Stored)
                Notifier.Raise(NotifyKind.Peers, "Link request waiting", $"A {body.Kind} server asks for a link.\nName: {PeerStore.Clean(body.Name, 64)}\nAddress: {url}\nContact it gives: {PeerStore.Clean(body.Contact, 128) ?? "(none)"}\nRequest seen from: {seen ?? "(unknown)"}\nFingerprint: {PeerIdentity.FingerprintOf(body.PublicKey!)}\nThe name, the address and the contact are only what the sender declares: reach its administrator by another way than this request, ask them to read the fingerprint with 'peer-id', and compare it with the one above. Then: admin requests, admin accept <id>.");
            return received switch
            {
                PeerRequestResult.Stored or PeerRequestResult.Updated => Results.Json(new { state = "pending" }, statusCode: StatusCodes.Status202Accepted),
                PeerRequestResult.AlreadyAccepted => Results.Ok(new { state = "accepted" }),
                PeerRequestResult.Banned => Results.Json(new { state = "banned" }, statusCode: StatusCodes.Status403Forbidden),
                PeerRequestResult.Closed => Results.Json(new { state = "closed" }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(new { state = "full" }, statusCode: StatusCodes.Status429TooManyRequests),
            };
        });

        app.MapPost("/peer/status", async (HttpContext c, PeerStore store, PeerOptions options) =>
        {
            if (TooMany(c)) return Results.Json(new { state = "throttled" }, statusCode: StatusCodes.Status429TooManyRequests);
            foreach (var kind in options.Kinds)
            {
                var (outcome, peer) = await PeerSigning.VerifyAsync(c.Request, store, kind, anyState: true);
                if (outcome == PeerSigning.Outcome.Ok) return Results.Ok(new { state = peer!.State == PeerState.Accepted ? "accepted" : "pending" });
                if (outcome == PeerSigning.Outcome.Banned) return Results.Json(new { state = "banned" }, statusCode: StatusCodes.Status403Forbidden);
                if (outcome is PeerSigning.Outcome.BadSignature or PeerSigning.Outcome.Replay) Notifier.Raise(NotifyKind.Abuse, "Forged or replayed server call", "A call to /peer/status carried a signature that does not verify or that was already used.");
                if (outcome is PeerSigning.Outcome.BadSignature or PeerSigning.Outcome.BadTime or PeerSigning.Outcome.Replay) return Results.Json(new { state = "refused" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            return Results.Json(new { state = "unknown" }, statusCode: StatusCodes.Status404NotFound);
        });
        return app;
    }

    // ------------------------------------------------------------------ admin (local, secret + this machine)

    /// <summary>The local admin routes <c>{prefix}/peers...</c>: identity, list, add, requests, accept, reject, remove, ban, open/close the queue, refresh.</summary>
    public static IEndpointRouteBuilder MapPeerAdmin(this IEndpointRouteBuilder app, string prefix)
    {
        IResult? Guard(HttpContext c) => AdminGuard.IsAuthorized(c, c.RequestServices.GetRequiredService<IConfiguration>()) ? null : Results.Unauthorized();
        object View(Peer p) => new { id = p.Id, kind = p.Kind, name = p.Name, url = p.Url, contact = p.Contact, seen_from = p.SeenFrom, state = p.State, since = p.Since };

        app.MapGet($"{prefix}/peers/identity", (HttpContext c, PeerOptions options, PeerIdentity identity) =>
            Guard(c) ?? Results.Ok(new { kind = options.Kind, name = options.Name, public_url = options.PublicUrl, contact = options.Contact, fingerprint = identity.Fingerprint }));

        app.MapGet($"{prefix}/peers", (HttpContext c, PeerStore store, string? kind) =>
            Guard(c) ?? Results.Ok(new { requests_open = store.RequestsOpen, pending_requests = store.Requests().Count, peers = store.Peers(kind).Select(View) }));

        app.MapGet($"{prefix}/peers/requests", (HttpContext c, PeerStore store, string? kind, string? name, int? older_than_days, int? limit, int? offset) =>
        {
            if (Guard(c) is { } denied) return denied;
            var all = Filter(store.Requests(kind), name, older_than_days).ToList();
            return Results.Ok(new { open = store.RequestsOpen, total = all.Count, requests = all.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 500)).Select(View) });
        });

        app.MapPost($"{prefix}/peers", async (HttpContext c, PeerClient client, AddPeerBody body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try
            {
                var peer = await client.AddAsync(body.Kind ?? "", body.Url ?? "", body.Fingerprint, body.Insecure, c.RequestAborted);
                return Results.Ok(new { id = peer.Id, fingerprint = peer.Id, name = peer.Name, url = peer.Url, state = peer.State });
            }
            catch (PeerClient.PeerException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status); }
        });

        app.MapPost($"{prefix}/peers/accept", async (HttpContext c, PeerStore store, PeerClient client, PeerOptions options, PeerDecision body) =>
        {
            if (Guard(c) is { } denied) return denied;
            if (!Select(store, body, out var ids, out var unknown, out var ambiguous, out var error)) return Results.BadRequest(new { error });
            var accepted = new List<string>(); var mismatch = new List<string>(); var unverified = new List<string>(); var noAddress = new List<string>();
            foreach (var id in ids)
            {
                var peer = store.Find(id);
                if (peer == null) continue;
                // A relay calls the servers it accepts: one that gave no address could never be reached.
                if (options.Kind is "relay" or "directory" && peer.Url.Length == 0) { noAddress.Add(id); continue; }
                var claim = await client.VerifyClaimAsync(peer, c.RequestAborted);
                if (claim == PeerClient.Claim.Mismatch) { mismatch.Add(id); continue; }
                if (claim == PeerClient.Claim.Unverified) unverified.Add(id);
                accepted.AddRange(store.Approve([id]));
            }
            return Results.Ok(new { accepted = accepted.Select(Short), mismatch = mismatch.Select(Short), unverified = unverified.Select(Short), no_address = noAddress.Select(Short), unknown, ambiguous });
        });

        app.MapPost($"{prefix}/peers/reject", (HttpContext c, PeerStore store, PeerDecision body) =>
        {
            if (Guard(c) is { } denied) return denied;
            if (!Select(store, body, out var ids, out var unknown, out var ambiguous, out var error)) return Results.BadRequest(new { error });
            return Results.Ok(new { rejected = store.Reject(ids, body.Ban).Select(Short), banned = body.Ban, unknown, ambiguous });
        });

        app.MapDelete($"{prefix}/peers", (HttpContext c, PeerStore store, string id) =>
        {
            if (Guard(c) is { } denied) return denied;
            return Resolve(store, id, out var found, out var problem) ? (store.Remove(found!.Id) ? Results.Ok() : Results.NotFound()) : problem!;
        });

        app.MapPost($"{prefix}/peers/ban", (HttpContext c, PeerStore store, PeerIdBody body) =>
        {
            if (Guard(c) is { } denied) return denied;
            return Resolve(store, body.Id ?? "", out var found, out var problem) ? (store.Ban(found!.Id) ? Results.Ok() : Results.NotFound()) : problem!;
        });

        app.MapDelete($"{prefix}/peers/ban", (HttpContext c, PeerStore store, string id) =>
        {
            if (Guard(c) is { } denied) return denied;
            return Resolve(store, id, out var found, out var problem) ? (store.Unban(found!.Id) ? Results.Ok() : Results.NotFound()) : problem!;
        });

        app.MapPost($"{prefix}/peers/open", (HttpContext c, PeerStore store, PeerOpenBody body) =>
        {
            if (Guard(c) is { } denied) return denied;
            store.SetRequestsOpen(body.Open);
            return Results.Ok();
        });

        app.MapPost($"{prefix}/peers/refresh", async (HttpContext c, PeerClient client, PeerStore store) =>
        {
            if (Guard(c) is { } denied) return denied;
            await client.RefreshAsync(includeAccepted: true, c.RequestAborted);
            return Results.Ok(new { peers = store.Peers().Select(View) });
        });
        return app;
    }

    private static string Short(string id) => id.Length <= 12 ? id : id[..12];

    private static IEnumerable<Peer> Filter(IEnumerable<Peer> peers, string? name, int? olderThanDays)
    {
        if (!string.IsNullOrWhiteSpace(name)) peers = peers.Where(p => (p.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase) || p.Url.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (olderThanDays is > 0) peers = peers.Where(p => p.Since <= DateTime.UtcNow.AddDays(-olderThanDays.Value));
        return peers;
    }

    private static bool Resolve(PeerStore store, string prefix, out Peer? found, out IResult? problem)
    {
        found = null; problem = null;
        prefix = PeerStore.Norm(prefix);
        if (prefix.Length < 6 || !prefix.All(Uri.IsHexDigit)) { problem = Results.BadRequest(new { error = "give at least 6 hexadecimal characters of the id" }); return false; }
        var matches = store.ByPrefix(prefix);
        if (matches.Count == 0) { problem = Results.NotFound(); return false; }
        if (matches.Count > 1) { problem = Results.Conflict(new { error = "ambiguous prefix, give more characters" }); return false; }
        found = matches[0];
        return true;
    }

    private static bool Select(PeerStore store, PeerDecision body, out List<string> ids, out List<string> unknown, out List<string> ambiguous, out string error)
    {
        ids = []; unknown = []; ambiguous = []; error = "";
        var pending = store.Requests(body.Kind);
        if (body.Ids is { Count: > 0 })
        {
            foreach (var wanted in body.Ids.Select(i => i.Trim().ToLowerInvariant()).Where(i => i.Length > 0).Distinct())
            {
                if (wanted.Length < 6 || !wanted.All(Uri.IsHexDigit)) { unknown.Add(wanted); continue; }
                var matches = pending.Where(p => p.Id.StartsWith(wanted, StringComparison.Ordinal)).ToList();
                if (matches.Count == 0) unknown.Add(wanted);
                else if (matches.Count > 1) ambiguous.Add(wanted);
                else ids.Add(matches[0].Id);
            }
            return true;
        }
        if (!body.All && string.IsNullOrWhiteSpace(body.Name) && body.OlderThanDays <= 0) { error = "give ids, or all (optionally with name / older_than_days)"; return false; }
        ids = Filter(pending, body.Name, body.OlderThanDays).Select(p => p.Id).ToList();
        return true;
    }

    public sealed class AddPeerBody
    {
        public string? Kind { get; set; }
        public string? Url { get; set; }
        public string? Fingerprint { get; set; }
        public bool Insecure { get; set; }
    }

    public sealed class PeerDecision
    {
        public string? Kind { get; set; }
        public List<string>? Ids { get; set; }
        public bool All { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("older_than_days")] public int OlderThanDays { get; set; }
        public bool Ban { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class PeerIdBody { public string? Id { get; set; } }
    public sealed class PeerOpenBody { public bool Open { get; set; } }
}
