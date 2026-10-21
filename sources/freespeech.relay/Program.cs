/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using FreeSpeech.Relay.Services;

// "admin ..." manages a running server through its local admin API instead of starting one (see AdminCli).
if (args.Length > 0 && args[0] == "admin")
{
    Environment.ExitCode = await FreeSpeech.Relay.AdminCli.RunAsync(args[1..]);
    return;
}

// "check" says what the installation still lacks (secrets) and exits 1 when something is missing: the update script runs it.
if (args.Length > 0 && args[0] == "check")
{
    Environment.ExitCode = ConfigCheck.Run(FreeSpeech.Relay.ServerChecks.Name, FreeSpeech.Relay.ServerChecks.Issues(ConfigCheck.Load()));
    return;
}

// "import <file>" puts back the state saved by "admin export" (service stopped); see StateArchive.
if (args.Length > 0 && args[0] == "import")
{
    Environment.ExitCode = StateCli.RunImport(args[1..], FreeSpeech.Relay.ServerState.Files(ConfigCheck.Load()));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// The secret (Admin:Secret) of this installation live here, next to the program and outside the repository; the published
// appsettings.json is replaced at every update, this file never is. Environment variables still win over it.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// Log levels changeable at run time, in-memory log ring and per-identity trace (admin "log" and "trace" commands).
builder.AddOps(new Dictionary<string, string[]>
{
    ["relay"] = ["FreeSpeech.Relay.Services.RelaySocketHandler", "FreeSpeech.Relay.Services.PresenceService", "FreeSpeech.Relay.Services.MessageDeliveryService"],
    ["s2s"] = ["FreeSpeech.Relay.Controllers.S2SController", "FreeSpeech.Relay.Services.S2S", "FreeSpeech.Relay.Services.SiblingRegistryService", "FreeSpeech.Relay.Services.RedirectService"],
    ["push"] = ["FreeSpeech.Relay.Services.PushNotifier"],
    ["access"] = ["FreeSpeech.Relay.Services.ClientAccessService"],
    ["flags"] = ["FreeSpeech.Relay.Controllers.FlagsController", "FreeSpeech.Relay.Services.PendingFlagService", "FreeSpeech.Relay.Services.FlagExpiryService"],
    ["admin"] = ["FreeSpeech.Relay.Controllers.AdminController"],
    ["http"] = ["Microsoft.AspNetCore"],
});
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<IPendingFlagService, PendingFlagService>();
builder.Services.AddSingleton<IPresenceService, PresenceService>();
builder.Services.AddSingleton<IHttpSessionService, HttpSessionService>();
builder.Services.AddSingleton<ISocketRegistry, SocketRegistry>();
builder.Services.AddSingleton<IMessageDeliveryService, MessageDeliveryService>();
builder.Services.AddSingleton<ISiblingRegistry, SiblingRegistryService>();
builder.Services.AddSingleton<RelaySettings>();   // tunables changeable while the server runs (admin settings)
builder.Services.AddSingleton<IClientAccess, ClientAccessService>();

// Links with push gateways and sibling relays by mutual consent (add / requests / accept): saved next to access.json.
builder.AddPeers("relay", ["push", "relay", "directory"], c => FreeSpeech.Relay.ServerState.PeerDirectory(c));
builder.AddMail();   // SMTP account and notifications to the administrator (admin smtp / notify)
builder.Services.AddSingleton(sp => FreeSpeech.Relay.ServerState.Files(sp.GetRequiredService<IConfiguration>()));   // what admin export saves
builder.Services.AddSingleton<IS2SClient, S2SClient>();
builder.Services.AddSingleton<IRedirectService, RedirectService>();
builder.Services.AddSingleton<IPushRegistry, PushRegistry>();
builder.Services.AddSingleton<IPushNotifier, PushNotifier>();
builder.Services.AddScoped<RelaySocketHandler>();
builder.Services.AddHostedService<FlagExpiryService>();
builder.Services.AddHostedService<DirectoryHeartbeatService>();   // how this relay is, told to the directories it is linked with

// S2S calls to a sibling (protocol §10) must never hang the request that
// triggered them (a "send" with no local recipient, or a flag propagation)
// on a dead or slow sibling — a short timeout per attempt.
builder.Services.AddHttpClient("S2S", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<S2SRateLimiter>();
builder.Services.AddSingleton<ITrafficLimiter>(sp => new TrafficLimiter(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddHttpClient("Push", client => client.Timeout = TimeSpan.FromSeconds(5));   // wake-up calls to the push gateway (§9)

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // nginx runs on the same machine; only it may set these headers.
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();

// The alarm: a missing secret is shouted at every start, at a level no installation hides.
{
    var issues = FreeSpeech.Relay.ServerChecks.Issues(app.Configuration);
    ConfigCheck.LogAtStartup(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Config"), FreeSpeech.Relay.ServerChecks.Name, issues);
    app.Services.NotifyIssues(FreeSpeech.Relay.ServerChecks.Name, issues);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Behind nginx (TLS ends there, which also does the http -> https redirect): trust the proxy's forwarded headers, nothing else.
app.UseForwardedHeaders();
app.UseMiddleware<S2SAuthMiddleware>();   // signature, replay and quota checks for /s2s/* (§10)
app.UseAuthorization();
app.MapControllers();
// A private relay takes no part in server-to-server traffic: linking with another relay would do nothing until it is made public.
app.Services.GetRequiredService<FreeSpeech.Ops.PeerClient>().Precheck = kind =>
    kind == "relay" && !app.Services.GetRequiredService<ISiblingRegistry>().IsPublic
        ? "this relay is private (see 'admin private'): it takes no part in server-to-server traffic. Make it public first: 'admin public'"
        : null;
app.MapPeers();                  // public: GET /peer/identity, POST /peer/request, POST /peer/status
app.MapPeerAdmin("api/admin");   // local admin routes /api/admin/peers...
app.MapOpsAdmin("/api/admin");

// Raw WebSocket relay transport (protocol §11) — deliberately not
// SignalR, so any language with a plain WebSocket client can connect.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(45) });   // pings keep idle connections alive through nginx; no answer in 45 s (phone switched off, network gone): the connection is closed, so the identity counts as offline and gets its push wake-up
app.Map("/ws/relay", async (HttpContext context, RelaySocketHandler handler) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(socket, context.RequestAborted, context.Connection.RemoteIpAddress?.ToString());
});

app.Run();
