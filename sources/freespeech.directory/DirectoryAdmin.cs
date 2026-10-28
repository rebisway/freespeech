/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;

namespace FreeSpeech.Directory;

/// <summary>Local admin API of the directory (<c>/admin/...</c>) and its command line (<c>FreeSpeech.Directory admin ...</c>).</summary>
public static class DirectoryAdmin
{
    public static void Map(WebApplication app, DirectoryRegistry registry, DirectorySettings settings, PeerStore peers)
    {
        IResult? Guard(HttpContext c) => AdminGuard.IsAuthorized(c, app.Configuration) ? null : Results.Unauthorized();

        // What the console shows on the server's card: how many relays are linked, known (spoke recently) and offered to newcomers right now.
        app.MapGet("/admin/status", (HttpContext c) => Guard(c) ?? Results.Ok(new
        {
            linked_relays = peers.Accepted("relay").Count,
            known_relays = registry.Known().Count,
            listed_relays = registry.Known().Count(e => e.Reachable && e.Beat.AcceptsNew && e.Beat.Free != 0),
            settings = settings.View(),
        }));

        // The relays accepted that spoke recently; "listed" says whether a newcomer is offered this relay right now.
        app.MapGet("/admin/servers", (HttpContext c) => Guard(c) ?? Results.Ok(registry.Known().Select(e => new
        {
            id = e.Peer.Id, name = e.Peer.Name, url = e.Peer.Url, country = e.Beat.Country, free = e.Beat.Free, accepts_new = e.Beat.AcceptsNew,
            version = e.Beat.Version, date = e.Beat.Date, reachable = e.Reachable, last_heartbeat = e.Beat.At,
            listed = e.Reachable && e.Beat.AcceptsNew && e.Beat.Free != 0,
        })));

        app.MapGet("/admin/settings", (HttpContext c) => Guard(c) ?? Results.Ok(settings.View()));
        app.MapPost("/admin/settings", (HttpContext c, SettingsRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try { settings.Set(body); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(settings.View());
        });
        app.MapDelete("/admin/settings", (HttpContext c) =>
        {
            if (Guard(c) is { } denied) return denied;
            settings.Reset();
            return Results.Ok(settings.View());
        });
    }

    private static readonly string Usage = $"""
        FreeSpeech directory administration (talks to the running directory's local admin API)

          status                                    relays linked, known (spoke recently) and offered to newcomers now, and the settings in force
          servers                                   relays accepted that spoke recently: country, places left, version, whether they are offered
          settings                                  the settings in force (expiry, address check, list size) and those of the configuration
          settings set <expiry-minutes|probe-minutes|list-size> <n>
                                                    change one at once and keep it across restarts
          settings reset                            back to the settings of the configuration
        {AdminCliKit.OpsUsage}

        Linking with relays (each administrator accepts the other's request; the kind may be left out):
        {PeerCli.Usage}

        Connection: --url <http://127.0.0.1:5294> or FS_ADMIN_URL (default: ASPNETCORE_URLS, then http://127.0.0.1:5000).
        Secret: FS_ADMIN_SECRET or Admin:Secret. The directory only answers calls made from its own machine.
        """;

    private static readonly Dictionary<string, string> SettingNames = new() { ["expiry-minutes"] = "expiry_minutes", ["probe-minutes"] = "probe_minutes", ["list-size"] = "list_size" };

    public static Task<int> RunCli(string[] args) =>
        AdminCliKit.RunAsync(args, "the directory", Usage, "admin", async (list, api) =>
        {
            if (await PeerCli.TryRunAsync(list, api, "admin", defaultKind: "relay") is { } peerCode) return peerCode;
            switch (list[0])
            {
                case "status": return await api.Show("admin/status");
                case "servers": return await api.Show("admin/servers");
                case "settings":
                    if (list.Count == 1) return await api.Show("admin/settings");
                    if (list[1] == "reset") return await api.Delete("admin/settings");
                    if (list[1] != "set" || list.Count != 4 || !SettingNames.TryGetValue(list[2], out var key))
                        throw new ArgumentException("settings | settings set <" + string.Join("|", SettingNames.Keys) + "> <n> | settings reset");
                    return await api.ShowPost("admin/settings", new Dictionary<string, int> { [key] = int.Parse(list[3]) });
            }
            return null;
        }, "directory");
}
