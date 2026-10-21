/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using FreeSpeech.Directory;
using FreeSpeech.Ops;

// "admin ..." manages a running directory through its local admin API instead of starting one (see DirectoryAdmin).
if (args.Length > 0 && args[0] == "admin")
{
    Environment.ExitCode = await DirectoryAdmin.RunCli(args[1..]);
    return;
}

// "check" says what the installation still lacks and exits 1 when something is missing: the update script runs it.
if (args.Length > 0 && args[0] == "check")
{
    Environment.ExitCode = ConfigCheck.Run(DirectoryChecks.Name, DirectoryChecks.IssuesFromDisk(ConfigCheck.Load()));
    return;
}

// "import <file>" puts back the state saved by "admin export" (service stopped); see StateArchive.
if (args.Length > 0 && args[0] == "import")
{
    Environment.ExitCode = StateCli.RunImport(args[1..], DirectoryState.Files(ConfigCheck.Load()));
    return;
}

// FreeSpeech directory: tells a client that knows no relay which relays take new accounts. Relays say how they are by signed heartbeats (they are
// linked by keys and by the agreement of both administrators, like every server link); the answer to a client is signed by this server's key, which the
// client has built in. It keeps nothing about the clients that ask and logs no address.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.AddOps(new Dictionary<string, string[]>
{
    ["directory"] = ["FreeSpeech.Directory", "FreeSpeech.Ops.DirectoryRegistry"],
    ["http"] = ["Microsoft.AspNetCore"],
});
var config = builder.Configuration;
builder.AddPeers("directory", ["relay"], c => DirectoryState.PeerDirectory(c));
builder.AddMail();   // SMTP account and notifications to the administrator (admin smtp / notify)
builder.Services.AddSingleton(sp => DirectoryState.Files(sp.GetRequiredService<IConfiguration>()));   // what admin export saves
builder.Services.AddSingleton<DirectorySettings>();
builder.Services.AddSingleton(sp => new DirectoryRegistry(sp.GetRequiredService<PeerStore>(), () => TimeSpan.FromMinutes(sp.GetRequiredService<DirectorySettings>().Current.ExpiryMinutes)));
builder.Services.AddSingleton<DirectoryProbe>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectoryProbe>());
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024);   // a heartbeat is a few dozen bytes

// Behind nginx every request arrives from 127.0.0.1: only the proxy on this machine may set the forwarded headers.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();
app.UseForwardedHeaders();
var peers = app.Services.GetRequiredService<PeerStore>();
var identity = app.Services.GetRequiredService<PeerIdentity>();
var registry = app.Services.GetRequiredService<DirectoryRegistry>();
var settings = app.Services.GetRequiredService<DirectorySettings>();
var probe = app.Services.GetRequiredService<DirectoryProbe>();
{
    var issues = DirectoryChecks.Issues(config, peers.Accepted("relay").Count > 0);
    ConfigCheck.LogAtStartup(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Config"), DirectoryChecks.Name, issues);
    app.Services.NotifyIssues(DirectoryChecks.Name, issues);
}

app.MapGet("/health", () => Results.Ok(new { ok = true }));
DirectoryAdmin.Map(app, registry, settings, peers);
app.MapOpsAdmin("/admin");
app.MapPeers();                 // public: GET /peer/identity (shows the key clients pin), POST /peer/request, POST /peer/status
app.MapPeerAdmin("/admin");
app.Use(async (c, next) => { if (c.Request.Path == "/directory/heartbeat") c.Request.EnableBuffering(); await next(); });

// A relay accepted by both administrators says how it is.
app.MapPost("/directory/heartbeat", async (HttpRequest request, HeartbeatBody body) =>
{
    var (outcome, peer) = await PeerSigning.VerifyAsync(request, peers, "relay");
    if (outcome is PeerSigning.Outcome.BadSignature or PeerSigning.Outcome.Replay)
        Notifier.Raise(NotifyKind.Abuse, "Forged or replayed relay call", "A call to /directory/heartbeat carried a signature that does not verify or that was already used.");
    if (outcome == PeerSigning.Outcome.Banned) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (outcome != PeerSigning.Outcome.Ok) return Results.Unauthorized();
    registry.Record(peer!.Id, body);
    if (!registry.IsProbed(peer.Id)) _ = probe.ProbeAsync(peer);   // the first heartbeat of a relay: look at its address now, not in half an hour
    return Results.Ok();
});

// A client asks for relays: signed, so that nobody between the client and the directory can substitute another list.
var asking = new ConcurrentDictionary<string, (int Count, DateTime Since)>();
app.MapGet("/directory/servers", (HttpContext c, string? country, int? count) =>
{
    var now = DateTime.UtcNow;
    var key = c.Connection.RemoteIpAddress?.ToString() ?? "local";
    var entry = asking.AddOrUpdate(key, (1, now), (_, e) => now - e.Since > TimeSpan.FromMinutes(1) ? (1, now) : (e.Count + 1, e.Since));
    if (asking.Count > 5000) foreach (var (k, e) in asking) if (now - e.Since > TimeSpan.FromMinutes(1)) asking.TryRemove(k, out _);
    if (entry.Count > 30)
    {
        Notifier.Raise(NotifyKind.Abuse, "Too many list requests", "One address asked for the list of relays more than 30 times in a minute; it is answered 429.");
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    return Results.Ok(SignedRelayList.Create(identity, registry.Pick(country, Math.Clamp(count ?? settings.Current.ListSize, 1, 10))));
});

app.Run();
