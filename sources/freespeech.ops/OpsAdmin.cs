/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>
/// Who may use an admin API. Two conditions, both required: the caller presents the configured secret (<c>Admin:Secret</c>; empty
/// means every call is refused) AND it connects from the machine itself — loopback — unless <c>Admin:AllowRemote</c> is true.
/// A reverse proxy in front does not weaken this: the forwarded client address is what is checked.
/// </summary>
public static class AdminGuard
{
    public static bool IsAuthorized(HttpContext context, IConfiguration config)
    {
        var configured = config["Admin:Secret"];
        if (string.IsNullOrEmpty(configured)) return false;
        if (!config.GetValue("Admin:AllowRemote", false) && !IsLocal(context)) return false;

        var presented = context.Request.Headers["X-Admin-Secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured));
    }

    private static bool IsLocal(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip == null || System.Net.IPAddress.IsLoopback(ip);   // null: in-process test host
    }
}

/// <summary>Registration of log levels, the in-memory log ring and tracing, plus their admin routes.</summary>
public static class OpsExtensions
{
    /// <summary>
    /// Replaces the static logging rules by <see cref="LogControl"/> (levels start from <c>Logging:LogLevel</c> and can then be changed
    /// at run time) and adds the <see cref="LogRing"/> provider. Call before <c>Build()</c>.
    /// </summary>
    public static IHostApplicationBuilder AddOps(this IHostApplicationBuilder builder, IReadOnlyDictionary<string, string[]>? aliases = null)
    {
        var control = new LogControl(builder.Configuration, aliases);
        var ring = new LogRing();
        builder.Services.AddSingleton(control);
        builder.Services.AddSingleton(ring);
        builder.Services.AddSingleton(new TraceService(ring));
        builder.Logging.AddProvider(new LogRingProvider(ring));
        builder.Services.Configure<LoggerFilterOptions>(o =>
        {
            o.Rules.Clear();
            o.MinLevel = LogLevel.Trace;
            o.Rules.Add(new LoggerFilterRule(null, null, null, (_, category, level) => control.IsEnabled(category, level)));
        });
        return builder;
    }

    /// <summary>Maps <c>{prefix}/log</c>, <c>{prefix}/log/level</c>, <c>{prefix}/log/tail</c> and <c>{prefix}/trace</c>.</summary>
    public static IEndpointRouteBuilder MapOpsAdmin(this IEndpointRouteBuilder app, string prefix)
    {
        IResult? Guard(HttpContext c) => AdminGuard.IsAuthorized(c, c.RequestServices.GetRequiredService<IConfiguration>()) ? null : Results.Unauthorized();

        // What is actually running: the version of the live process, not of the files on disk.
        var started = DateTimeOffset.UtcNow;
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        app.MapGet($"{prefix}/version", (HttpContext c) => Guard(c) ?? Results.Ok(new
        {
            program = entry?.GetName().Name,
            version = ReleaseInfo.Version,
            date = ReleaseInfo.Date,
            license = "AGPL-3.0-or-later",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            started_utc = started,
            uptime_seconds = (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
        }));

        app.MapGet($"{prefix}/log", (HttpContext c, LogControl control) => Guard(c) ?? Results.Ok(control.Snapshot()));

        app.MapPost($"{prefix}/log/level", (HttpContext c, LogControl control, LevelRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            if (!Enum.TryParse<LogLevel>(body.Level, ignoreCase: true, out var level) || int.TryParse(body.Level, out _))
                return Results.BadRequest(new { error = "level must be trace, debug, information, warning, error, critical or none" });
            try { control.Set(body.Category ?? "", level); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok();
        });

        app.MapDelete($"{prefix}/log/level", (HttpContext c, LogControl control) =>
        {
            if (Guard(c) is { } denied) return denied;
            control.Reset();
            return Results.Ok();
        });

        // Plain text, one line per entry. With follow=true the response stays open and streams new lines until the client disconnects.
        app.MapGet($"{prefix}/log/tail", async (HttpContext c, LogRing ring, int? lines, string? level, bool? follow) =>
        {
            if (Guard(c) is { } denied) { await denied.ExecuteAsync(c); return; }
            var min = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var l) ? l : LogLevel.Trace;
            c.Response.ContentType = "text/plain; charset=utf-8";
            // Subscribe before reading the past or flushing the headers: a line logged in between is neither lost nor shown twice-late.
            using var subscription = follow == true ? ring.Subscribe(min) : null;
            foreach (var entry in ring.Last(Math.Clamp(lines ?? 100, 0, 2000), min)) await c.Response.WriteAsync(entry + "\n");
            await c.Response.Body.FlushAsync();
            if (subscription == null) return;

            try
            {
                await foreach (var entry in subscription.ReadAsync(c.RequestAborted))
                {
                    await c.Response.WriteAsync(entry + "\n", c.RequestAborted);
                    await c.Response.Body.FlushAsync(c.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* the client went away */ }
        });

        app.MapGet($"{prefix}/trace", (HttpContext c, TraceService trace) =>
            Guard(c) ?? Results.Ok(trace.Active().Select(a => new { id = a.Id, until = a.Until })));

        app.MapPost($"{prefix}/trace", (HttpContext c, TraceService trace, TraceRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try { trace.Start(body.Id ?? "", TimeSpan.FromMinutes(body.Minutes)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(new { minutes = Math.Clamp(body.Minutes <= 0 ? TraceService.MaxDuration.TotalMinutes : body.Minutes, 1, TraceService.MaxDuration.TotalMinutes) });
        });

        app.MapDelete($"{prefix}/trace", (HttpContext c, TraceService trace, string? id) =>
        {
            if (Guard(c) is { } denied) return denied;
            if (string.IsNullOrEmpty(id) || id == "all") { trace.StopAll(); return Results.Ok(); }
            return trace.Stop(id) ? Results.Ok() : Results.NotFound();
        });

        MailAdmin.Map(app, prefix);
        return app;
    }

    public sealed record LevelRequest(string? Category, string? Level);
    public sealed record TraceRequest(string? Id, double Minutes);
}
