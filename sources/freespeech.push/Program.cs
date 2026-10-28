/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.Concurrent;
using FreeSpeech.Ops;
using FreeSpeech.Push;
using FreeSpeech.Push.Providers;

// "admin ..." manages a running gateway through its local admin API instead of starting one (see PushAdmin).
if (args.Length > 0 && args[0] == "admin")
{
    Environment.ExitCode = await PushAdmin.RunCli(args[1..]);
    return;
}

// "check" says what the installation still lacks (secrets, credentials file) and exits 1 when something is missing: the update script runs it.
if (args.Length > 0 && args[0] == "check")
{
    Environment.ExitCode = ConfigCheck.Run(PushChecks.Name, PushChecks.IssuesFromDisk(ConfigCheck.Load()));
    return;
}

// "import <file>" puts back the state saved by "admin export" (service stopped); see StateArchive.
if (args.Length > 0 && args[0] == "import")
{
    Environment.ExitCode = StateCli.RunImport(args[1..], PushStateFiles.Files(ConfigCheck.Load()));
    return;
}

// FreeSpeech push gateway (protocol §9): the only component holding FCM/APNs credentials. Relay servers call
// POST /push with a token; the gateway sends a generic wake-up through the matching push network.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);   // credentials live here, never in the repository
builder.AddOps(new Dictionary<string, string[]>
{
    ["push"] = ["Push", "FreeSpeech.Push.Providers"],
    ["relays"] = ["FreeSpeech.Ops.PeerStore", "FreeSpeech.Push.PushSettings"],
    ["fcm"] = ["FreeSpeech.Push.Providers.FcmProvider"],
    ["apns"] = ["FreeSpeech.Push.Providers.ApnsProvider"],
    ["http"] = ["Microsoft.AspNetCore"],
});

var config = builder.Configuration;
// Links with relay servers by mutual consent, no shared secret (add relay / requests relay / accept relay): saved next to relays.json.
builder.AddPeers("push", ["relay"], c => PushStateFiles.PeerDirectory(c));
builder.Services.AddSingleton(new LinkStatus("relay"));   // when each relay last called, shown by "list"
builder.AddMail();   // SMTP account and notifications to the administrator (admin smtp / notify)
builder.Services.AddSingleton(sp => PushStateFiles.Files(sp.GetRequiredService<IConfiguration>()));   // what admin export saves
builder.Services.AddHttpClient("Push", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8 * 1024);   // a wake-up request is a few hundred bytes

// Behind nginx every request arrives from 127.0.0.1: the admin API must see the real caller, or "from this machine" would be true for
// the whole internet. Only the proxy on this machine may set these headers.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();
app.UseForwardedHeaders();
var log = app.Services.GetRequiredService<ILoggerFactory>();
var http = app.Services.GetRequiredService<IHttpClientFactory>();

// One provider per network. A network without credentials is simply absent: its requests get 503.
var providers = new Dictionary<string, IPushProvider>();
void Add(IPushProvider p) => providers[p.Platform] = p;

if (config["Fcm:ServiceAccountFile"] is { Length: > 0 } fcm && File.Exists(fcm))
    Add(new FcmProvider(http.CreateClient("Push"), fcm, log.CreateLogger<FcmProvider>()));
if (config["Apns:KeyFile"] is { Length: > 0 } key && File.Exists(key))
    Add(new ApnsProvider(http.CreateClient("Push"), key, config["Apns:KeyId"]!, config["Apns:TeamId"]!, config["Apns:BundleId"]!, config.GetValue<bool>("Apns:Production"), log.CreateLogger<ApnsProvider>()));
if (config.GetValue<bool>("Push:Fake"))
    foreach (var platform in new[] { "fcm", "apns" })
        if (!providers.ContainsKey(platform)) Add(new LogProvider(platform, log.CreateLogger<LogProvider>()));

// Runtime state, changed through the admin API without a restart: the limits and the counters. The relays allowed to call are the peers
// both administrators accepted (peers.json). Saved next to the gateway ("Push:StatePath", default "push-state").
var statePath = config["Push:StatePath"] ?? "push-state";
Directory.CreateDirectory(statePath);
var peers = app.Services.GetRequiredService<PeerStore>();
var settings = new PushSettings(Path.Combine(statePath, "settings.json"), config.GetValue("Push:MinIntervalSeconds", 15), config.GetValue("Push:MaxConcurrent", 100), log.CreateLogger<PushSettings>());   // saturation: beyond MaxConcurrent, "busy" so the relay tries another gateway
var stats = new PushStats();

var lastSent = new ConcurrentDictionary<string, DateTime>();   // per-token rate limit
var inFlight = 0;
var logger = log.CreateLogger("Push");
logger.LogInformation("Providers: {Providers}", providers.Count == 0 ? "none" : string.Join(", ", providers.Keys));
// Fail closed: with no accepted relay every request is refused (only a development run, with the log provider, may go without).
var open = peers.Accepted("relay").Count == 0;
var dev = app.Environment.IsDevelopment() || config.GetValue<bool>("Push:Fake");
{
    var issues = PushChecks.Issues(config, !open);
    ConfigCheck.LogAtStartup(log.CreateLogger("Config"), PushChecks.Name, issues);   // the alarm: shouted at every start, at a level no installation hides
    app.Services.NotifyIssues(PushChecks.Name, issues);   // and sent by mail when notifications are on
}
if (open && dev)
    logger.LogWarning("No relay accepted: anyone who can reach this gateway can send wake-ups (development only).");

app.MapGet("/health", () => Results.Ok(new { ok = true }));   // says nothing about what is configured

PushAdmin.Map(app, peers, settings, stats, providers, () => inFlight);
app.MapOpsAdmin("/admin");
app.MapPeers();                 // public: GET /peer/identity, POST /peer/request, POST /peer/status
app.MapPeerAdmin("/admin");     // local admin routes /admin/peers... (nginx keeps /admin closed)
// A signed call is read twice (to check the signature, then by the handler): its body must be rewindable.
app.Use(async (c, next) => { if (c.Request.Path == "/push") c.Request.EnableBuffering(); await next(); });

app.MapPost("/push", async (HttpRequest request, HttpResponse response, PushRequest body, CancellationToken ct) =>
{
    // Who is calling: a relay both administrators agreed on (signed call) or, in development with no relay accepted at all, anybody.
    string keyId;
    if (request.Headers.ContainsKey(PeerSigning.IdHeader))
    {
        var (outcome, peer) = await PeerSigning.VerifyAsync(request, peers, "relay");
        if (outcome is PeerSigning.Outcome.BadSignature or PeerSigning.Outcome.Replay)
            Notifier.Raise(NotifyKind.Abuse, "Forged or replayed relay call", "A call to /push carried a signature that does not verify or that was already used.");
        if (outcome == PeerSigning.Outcome.Banned) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (outcome != PeerSigning.Outcome.Ok) return Results.Unauthorized();
        app.Services.GetRequiredService<LinkStatus>().Ok(peer!.Url);
        keyId = "relay-" + peer.Id[..8];
    }
    else if (dev && peers.Accepted("relay").Count == 0) keyId = "open";   // development only, and only while no relay is accepted
    else return Results.Unauthorized();
    var counter = stats.For(keyId);
    counter.LastUsed = DateTime.UtcNow;

    // Maintenance or saturation: "busy", so the relay tries another gateway. Nothing is lost, wake-ups are best effort anyway.
    var busy = settings.Maintenance;
    var counted = false;
    if (!busy) { counted = true; busy = Interlocked.Increment(ref inFlight) > settings.MaxConcurrent; }
    if (busy)
    {
        if (counted) Interlocked.Decrement(ref inFlight);
        Interlocked.Increment(ref counter.Busy);
        response.Headers["X-Push-Reason"] = "busy";
        response.Headers.RetryAfter = "5";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    try
    {
        if (string.IsNullOrWhiteSpace(body.Token) || body.Token.Length > 4096 || body.Kind != "wake") return Results.BadRequest();
        if (!providers.TryGetValue(body.Platform ?? "", out var provider)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var now = DateTime.UtcNow;
        var tokenKey = provider.Platform + ":" + body.Token;
        if (lastSent.TryGetValue(tokenKey, out var last) && now - last < settings.MinInterval)
        {
            Interlocked.Increment(ref counter.RateLimited);
            response.Headers["X-Push-Reason"] = "token";   // not saturation: this token was woken a moment ago
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        lastSent[tokenKey] = now;   // held during the send, released below if the push network did not take it
        if (lastSent.Count > 10_000) foreach (var (k, t) in lastSent) if (now - t > settings.MinInterval) lastSent.TryRemove(k, out _);

        var outcome = await provider.SendWakeAsync(body.Token, ct);
        if (outcome != PushOutcome.Sent) lastSent.TryRemove(tokenKey, out _);   // a retry is not a duplicate wake-up
        switch (outcome)
        {
            case PushOutcome.Sent: Interlocked.Increment(ref counter.Sent); break;
            case PushOutcome.InvalidToken: Interlocked.Increment(ref counter.Gone); break;
            default: Interlocked.Increment(ref counter.Failed); break;
        }
        logger.LogDebug("Wake-up {Platform} for relay key {Key}: {Outcome}", provider.Platform, keyId, outcome);
        return outcome switch
        {
            PushOutcome.Sent => Results.Ok(),
            PushOutcome.InvalidToken => Results.StatusCode(StatusCodes.Status410Gone),   // the relay server drops the token
            _ => Results.StatusCode(StatusCodes.Status502BadGateway),
        };
    }
    finally { Interlocked.Decrement(ref inFlight); }
});

app.Run();

/// <summary>Body of POST /push. Deliberately nothing else: no sender, no recipient identity, no content.</summary>
public sealed record PushRequest(string? Platform, string? Token, string? Kind);
