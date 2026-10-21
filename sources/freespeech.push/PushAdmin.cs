/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using FreeSpeech.Push.Providers;

namespace FreeSpeech.Push;

/// <summary>
/// Local admin API of the push gateway (<c>/admin/...</c>): same rules as the relay server's — the admin secret and a call from the
/// machine itself — and the same command line (<c>FreeSpeech.Push admin ...</c>). Everything applies at once, no restart.
/// </summary>
public static class PushAdmin
{
    public sealed record SettingsRequest(int? MinIntervalSeconds, int? MaxConcurrent, bool? Maintenance);

    public static void Map(WebApplication app, PeerStore peers, PushSettings settings, PushStats stats, IReadOnlyDictionary<string, IPushProvider> providers, Func<int> inFlight)
    {
        IResult? Guard(HttpContext c) => AdminGuard.IsAuthorized(c, app.Configuration) ? null : Results.Unauthorized();

        app.MapGet("/admin/status", (HttpContext c) => Guard(c) ?? Results.Ok(new
        {
            providers = providers.Keys.Order().ToArray(),
            in_flight = inFlight(),
            started_at = stats.StartedAt,
            settings = settings.Snapshot(),
            linked_relays = peers.Accepted("relay").Count,
            totals = new
            {
                sent = stats.All.Values.Sum(s => s.Sent), failed = stats.All.Values.Sum(s => s.Failed), busy = stats.All.Values.Sum(s => s.Busy),
                rate_limited = stats.All.Values.Sum(s => s.RateLimited), gone = stats.All.Values.Sum(s => s.Gone),
            },
        }));

        // The relays accepted by this gateway, with what each one has asked of it since the start.
        app.MapGet("/admin/relays", (HttpContext c) => Guard(c) ?? Results.Ok(peers.Accepted("relay").Select(p =>
        {
            var s = stats.All.TryGetValue("relay-" + p.Id[..8], out var counter) ? counter : null;
            return new { id = p.Id, name = p.Name, url = p.Url, sent = s?.Sent ?? 0, failed = s?.Failed ?? 0, busy = s?.Busy ?? 0, rate_limited = s?.RateLimited ?? 0, last_used = s?.LastUsed };
        })));
        app.MapGet("/admin/settings", (HttpContext c) => Guard(c) ?? Results.Ok(settings.Snapshot()));

        app.MapPost("/admin/settings", (HttpContext c, SettingsRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try { settings.Update(body.MinIntervalSeconds, body.MaxConcurrent, body.Maintenance); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(settings.Snapshot());
        });
    }

    private static readonly string Usage = $"""
        FreeSpeech push gateway administration (talks to the running gateway's local admin API)

          status                                    providers, counters, limits
          relays                                    linked relay servers, with their counters
          set min-interval <seconds>                one wake-up per device token at most every N seconds
          set max-concurrent <n>                    beyond this many wake-ups at once, answer "busy"
          maintenance on | off                      answer "busy" to everything: relays use another gateway (restart without loss)
        {AdminCliKit.OpsUsage}

        Linking with relay servers (each administrator accepts the other's request):
        {PeerCli.Usage}

        Connection: --url <http://127.0.0.1:5393> or FS_ADMIN_URL (default: ASPNETCORE_URLS, then http://127.0.0.1:5000).
        Secret: FS_ADMIN_SECRET or Admin:Secret. The gateway only answers calls made from its own machine.
        """;

    public static Task<int> RunCli(string[] args) =>
        AdminCliKit.RunAsync(args, "the push gateway", Usage, "admin", async (list, api) =>
        {
            string Arg(int i, string what) => list.Count > i ? list[i] : throw new ArgumentException($"missing {what}");
            // Links with relay servers ("add relay <url>", "requests", "accept <id>"...): a gateway links with relays only, so the kind may be left out.
            if (await PeerCli.TryRunAsync(list, api, "admin", defaultKind: "relay") is { } peerCode) return peerCode;
            return list[0] switch
            {
                "status" => await api.Show("admin/status"),
                "relays" => await api.Show("admin/relays"),
                "set" => Arg(1, "min-interval | max-concurrent") switch
                {
                    "min-interval" => await api.ShowPost("admin/settings", new { minIntervalSeconds = int.Parse(Arg(2, "seconds")) }),
                    "max-concurrent" => await api.ShowPost("admin/settings", new { maxConcurrent = int.Parse(Arg(2, "number")) }),
                    _ => throw new ArgumentException("set min-interval <seconds> | set max-concurrent <n>"),
                },
                "maintenance" => await api.ShowPost("admin/settings", new { maintenance = Arg(1, "on|off") == "on" }),
                _ => null,
            };
        }, "push");
}
