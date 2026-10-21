/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Json;
using FreeSpeech.Ops;

namespace FreeSpeech.Relay;

/// <summary>
/// <c>dotnet FreeSpeech.Relay.dll admin ...</c> — manages a running server through its local admin API (<c>/api/admin</c>), so the
/// operator needs no web interface and the API never has to be exposed: run it on the server itself (or through an SSH tunnel).
/// Every change applies at once and is persisted by the server; nothing is restarted.
/// </summary>
public static class AdminCli
{
    private static readonly string Usage = $"""
        FreeSpeech relay server administration (talks to the running server's local admin API)

          status                                    mode, counters
          public | private                          take part in server-to-server traffic or not
          access open | allowlist                   who may register: everybody not banned, or only allowed/invited identities
          clients                                   list allowed and banned identities and pending invitations
          connected                                 the identities connected right now (memory only), allowed or banned or neither
          allow-connected                           allow every identity connected right now (before switching to "allowlist")
          pending [--name t] [--limit N] [--offset N] [--json]
                                                    access requests waiting for a decision (oldest first)
          accept <id…> | --all [--name t] [--older-than DAYS] [--yes]
                                                    allow requesters (ids or 6+ hex prefixes; --all needs --yes in scripts)
          reject <id…> | --all [--name t] [--older-than DAYS] [--ban] [--reason t] [--yes]
                                                    turn requests down, and ban them with --ban
          requests open | close                     let unknown identities leave a request, or not
          allow <id> [note]                         allow an identity (64-hex discovery id)
          remove <id>                               remove it from the allow-list and cut its connections
          ban <id> [reason]                         refuse it everywhere and cut its connections
          unban <id>
          invite [note] [--days N]                  create a single-use invitation token (shown once)
          revoke <invitation-id>
          siblings | candidates | blacklist         list (siblings: the relays linked with this one)
          dismiss <url>                             forget a suggested candidate
          server-ban <url> [reason]                 ban an address (sibling or push gateway)
          server-unban <url>
          drain <count>                             ask up to <count> clients to move to a sibling
          limits                                    the volume limits in force (what one identity or address may send)
          limits set [--identity-day SIZE] [--identity-10min SIZE] [--address-day SIZE]
                                                    change them at once and keep them across restarts (SIZE: 1G, 300M, 500K or bytes; 0 or off = no limit)
          limits reset                              back to the limits of the configuration
          settings                                  the service settings in force (connections, message size, quota between servers, source address, access requests)
          settings set <name> <value>               change one at once and keep it across restarts; names: max-connections (0 = no limit), max-message-bytes,
                                                    s2s-rate, source-url (empty = none), country (two-letter code told to the directories), max-access-requests, access-request-days
          settings reset                            back to the settings of the configuration
          traffic [--limit N]                       who sent the most in the last 24 hours: identities and addresses, most first
        {AdminCliKit.OpsUsage}

        Linking with a push gateway or a sibling relay (each administrator accepts the other's request):
        {PeerCli.Usage}

        Connection: --url <http://127.0.0.1:5290> or FS_ADMIN_URL (default: ASPNETCORE_URLS, then http://127.0.0.1:5000).
        Secret: FS_ADMIN_SECRET or Admin:Secret (appsettings*.json, Admin__Secret). The server only answers calls made from its own machine.
        """;

    public static Task<int> RunAsync(string[] args) =>
        AdminCliKit.RunAsync(args, "the relay server", Usage, "api/admin", async (list, api) =>
        {
            string Arg(int i, string what) => list.Count > i ? list[i] : throw new ArgumentException($"missing {what}");
            string? Rest(int i) => list.Count > i ? string.Join(' ', list.Skip(i)) : null;
            // Links with other servers: "add push <url>", "accept push <id>"... (the clients' own "accept <id>" has no kind, so it falls through).
            if (await PeerCli.TryRunAsync(list, api, "api/admin", defaultKind: null) is { } peerCode) return peerCode;

            var days = AdminCliKit.TakeOption(list, "--days");

            return list[0] switch
            {
                "status" => await api.Show("api/admin/status"),
                "public" => await api.Post("api/admin/status", new { is_public = true }),
                "private" => await api.Post("api/admin/status", new { is_public = false }),
                "access" => await api.Post("api/admin/access/mode", new { mode = Arg(1, "mode (open|allowlist)") }),
                "clients" => await api.Show("api/admin/access"),
                "connected" => await api.Show("api/admin/connected"),
                "allow-connected" => await api.ShowPost("api/admin/clients/allow-connected", new { }),
                "pending" => await Pending(list, api),
                "accept" => await Decide(list, api, accept: true),
                "reject" => await Decide(list, api, accept: false),
                "requests" => await api.Post("api/admin/access/requests/open", new { open = Arg(1, "open|close") switch { "open" => true, "close" => false, _ => throw new ArgumentException("requests open | close") } }),
                "allow" => await api.Post("api/admin/clients", new { id = Arg(1, "id"), note = Rest(2) }),
                "remove" => await api.Delete("api/admin/clients?id=" + AdminApi.Q(Arg(1, "id"))),
                "ban" => await api.Post("api/admin/clients/ban", new { id = Arg(1, "id"), reason = Rest(2) }),
                "unban" => await api.Delete("api/admin/clients/ban?id=" + AdminApi.Q(Arg(1, "id"))),
                "invite" => await api.ShowPost("api/admin/invitations", new { note = Rest(1), days = int.TryParse(days, out var d) ? d : 0 }),
                "revoke" => await api.Delete("api/admin/invitations?id=" + AdminApi.Q(Arg(1, "invitation id"))),
                "siblings" => await api.Show("api/admin/siblings"),
                "candidates" => await api.Show("api/admin/candidates"),
                "blacklist" => await api.Show("api/admin/blacklist"),
                "dismiss" => await api.Delete("api/admin/candidates?url=" + AdminApi.Q(Arg(1, "url"))),
                "server-ban" => await api.Post("api/admin/blacklist", new { url = Arg(1, "url"), reason = Rest(2) }),
                "server-unban" => await api.Delete("api/admin/blacklist?url=" + AdminApi.Q(Arg(1, "url"))),
                "drain" => await api.ShowPost("api/admin/drain", new { count = int.Parse(Arg(1, "count")) }),
                "limits" => await Limits(list, api),
                "settings" => await Settings(list, api),
                "traffic" => await Traffic(list, api),
                _ => null,
            };
        }, "relay");

    // ------------------------------------------------------------------
    // Volume limits
    // ------------------------------------------------------------------

    /// <summary>A size as typed: "1G", "300M", "500K", plain bytes; "0" or "off" for no limit. Binary units (1G = 1,073,741,824 bytes).</summary>
    internal static long ParseSize(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        if (s is "off" or "none" or "no") return 0;
        var unit = s.EndsWith("ib") ? s[^3..^2] : s.EndsWith('b') ? s[^2..^1] : s[^1..];
        var factor = unit switch { "k" => 1024L, "m" => 1024L * 1024, "g" => 1024L * 1024 * 1024, "t" => 1024L * 1024 * 1024 * 1024, _ => 1L };
        var number = factor == 1 ? s.TrimEnd('b') : s[..s.LastIndexOfAny(['k', 'm', 'g', 't'])];
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new ArgumentException($"not a size: '{text}' (examples: 1G, 300M, 500K, 0, off)");
        return (long)(value * factor);
    }

    /// <summary>A size for a person: "off", "1 GiB", "300 MiB".</summary>
    internal static string FormatSize(long bytes, bool zeroIsOff = true)
    {
        if (bytes == 0) return zeroIsOff ? "off" : "0 B";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v.ToString(i == 0 || v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[i]}";
    }

    private static readonly Dictionary<string, string> SettingNames = new()
    {
        ["max-connections"] = "max_connections", ["max-message-bytes"] = "max_message_bytes", ["s2s-rate"] = "s2s_rate_per_minute",
        ["source-url"] = "source_url", ["country"] = "country", ["max-access-requests"] = "max_access_requests", ["access-request-days"] = "access_request_days",
    };

    private static async Task<int> Settings(List<string> list, AdminApi api)
    {
        var rest = list.Skip(1).ToList();
        if (rest.Count == 0) return await api.Show("api/admin/settings");
        if (rest[0] == "reset") return await api.Delete("api/admin/settings");
        if (rest[0] != "set" || rest.Count < 2 || !SettingNames.TryGetValue(rest[1], out var key))
            throw new ArgumentException("settings | settings set <" + string.Join("|", SettingNames.Keys) + "> <value> | settings reset");
        var text = rest.Count > 2 ? string.Join(' ', rest.Skip(2)) : (key is "source_url" or "country" ? "" : throw new ArgumentException($"missing the value of {rest[1]}"));
        return await api.ShowPost("api/admin/settings", new Dictionary<string, object> { [key] = key is "source_url" or "country" ? text : int.Parse(text) });
    }

    private static async Task<int> Limits(List<string> list, AdminApi api)
    {
        var rest = list.Skip(1).ToList();
        if (rest.Count == 0) return await ShowLimits(api, "GET", null);
        if (rest[0] == "reset") return await ShowLimits(api, "DELETE", null);
        if (rest[0] != "set") throw new ArgumentException("limits | limits set ... | limits reset");

        rest.RemoveAt(0);
        long? Take(string option) => AdminCliKit.TakeOption(rest, option) is { } v ? ParseSize(v) : null;
        var day = Take("--identity-day");
        var burst = Take("--identity-10min");
        var address = Take("--address-day");
        if (rest.Count > 0) throw new ArgumentException($"unknown argument: {rest[0]}");
        if (day == null && burst == null && address == null) throw new ArgumentException("limits set: give at least one of --identity-day, --identity-10min, --address-day");
        return await ShowLimits(api, "POST", new { identity_per_day = day, identity_per_10_minutes = burst, address_per_day = address });
    }

    private static async Task<int> ShowLimits(AdminApi api, string method, object? body)
    {
        using var response = method switch
        {
            "POST" => await api.Http.PostAsJsonAsync("api/admin/limits", body),
            "DELETE" => await api.Http.DeleteAsync("api/admin/limits"),
            _ => await api.Http.GetAsync("api/admin/limits"),
        };
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, text);

        var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
        var changed = root.GetProperty("changed").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        var defaults = root.GetProperty("defaults");
        void Line(string label, string key)
        {
            var now = root.GetProperty(key).GetInt64();
            var def = defaults.GetProperty(key).GetInt64();
            Console.WriteLine($"{label,-34} {FormatSize(now),-10}" + (changed.Contains(key) ? $"  (changed; the configuration says {FormatSize(def)})" : "  (configuration)"));
        }
        Line("per identity, per 24 hours", "identity_per_day");
        Line("per identity, per 10 minutes", "identity_per_10_minutes");
        Line("per address, per 24 hours", "address_per_day");
        return 0;
    }

    private static async Task<int> Traffic(List<string> list, AdminApi api)
    {
        var rest = list.Skip(1).ToList();
        var limit = AdminCliKit.TakeOption(rest, "--limit") ?? "10";
        using var response = await api.Http.GetAsync("api/admin/traffic?limit=" + AdminApi.Q(limit));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, text);

        var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
        void Table(string title, string key, string name)
        {
            var rows = root.GetProperty(key).EnumerateArray().ToList();
            Console.WriteLine(title);
            if (rows.Count == 0) { Console.WriteLine("  (nothing sent)"); return; }
            foreach (var r in rows) Console.WriteLine($"  {FormatSize(r.GetProperty("bytes_24h").GetInt64(), zeroIsOff: false),-10} {r.GetProperty(name).GetString()}");
        }
        Table("Identities, last 24 hours:", "identities", "id");
        Table("Addresses, last 24 hours:", "addresses", "address");
        return 0;
    }

    /// <summary>Days from "7" or "7d".</summary>
    private static int Days(string? text) => string.IsNullOrEmpty(text) ? 0 : int.Parse(text.TrimEnd('d', 'D'));

    private static async Task<int> Pending(List<string> list, AdminApi api)
    {
        var rest = list.Skip(1).ToList();
        var name = AdminCliKit.TakeOption(rest, "--name");
        var limit = AdminCliKit.TakeOption(rest, "--limit") ?? "50";
        var offset = AdminCliKit.TakeOption(rest, "--offset") ?? "0";
        var json = AdminCliKit.TakeFlag(rest, "--json");

        var query = $"limit={AdminApi.Q(limit)}&offset={AdminApi.Q(offset)}" + (name != null ? "&name=" + AdminApi.Q(name) : "");
        if (json) return await api.Show("api/admin/access/requests?" + query);

        using var response = await api.Http.GetAsync("api/admin/access/requests?" + query);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        var total = root.GetProperty("total").GetInt32();
        if (!root.GetProperty("open").GetBoolean()) Console.WriteLine("(the queue is closed: new requests are not taken — 'requests open' to reopen)");
        if (total == 0) { Console.WriteLine("No request waiting."); return 0; }

        Console.WriteLine($"{"VERIFICATION CODE",-16}  {"REQUESTED (UTC)",-16}  NAME / NOTE (declared by the person: not checked)");
        foreach (var r in root.GetProperty("requests").EnumerateArray())
        {
            var who = r.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : "(no name)";
            var note = r.TryGetProperty("note", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? " — " + t.GetString() : "";
            Console.WriteLine($"{AdminCliKit.VerificationCode(r.GetProperty("id").GetString()!),-16}  {r.GetProperty("requested_at").GetDateTime():yyyy-MM-dd HH:mm}  {who}{note}");
        }
        var shown = root.GetProperty("requests").GetArrayLength();
        Console.WriteLine($"{shown} shown, {total} matching. Ask the person to read the code shown on their phone and compare it, then 'accept <code>' or 'reject <code>' (the code as shown, or its first 6 characters).");
        return 0;
    }

    private static async Task<int> Decide(List<string> list, AdminApi api, bool accept)
    {
        var rest = list.Skip(1).ToList();
        var all = AdminCliKit.TakeFlag(rest, "--all");
        var yes = AdminCliKit.TakeFlag(rest, "--yes");
        var ban = AdminCliKit.TakeFlag(rest, "--ban");
        var name = AdminCliKit.TakeOption(rest, "--name");
        var older = Days(AdminCliKit.TakeOption(rest, "--older-than"));
        var reason = AdminCliKit.TakeOption(rest, "--reason");
        if (accept && (ban || reason != null)) throw new ArgumentException("--ban and --reason only go with reject");

        var bulk = rest.Count == 0;
        if (bulk && !all && name == null && older == 0) throw new ArgumentException($"{(accept ? "accept" : "reject")}: give ids, or --all (optionally with --name / --older-than)");
        if (!bulk && (all || name != null || older > 0)) throw new ArgumentException("give either ids or --all with filters, not both");

        if (bulk && !yes)
        {
            using var preview = await api.Http.GetAsync($"api/admin/access/requests?limit=1&older_than_days={older}" + (name != null ? "&name=" + AdminApi.Q(name) : ""));
            var text = await preview.Content.ReadAsStringAsync();
            if (!preview.IsSuccessStatusCode) return AdminCliKit.Fail(preview, text);
            var total = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("total").GetInt32();
            if (total == 0) { Console.WriteLine("No request matches."); return 0; }
            if (Console.IsInputRedirected) { Console.Error.WriteLine($"{total} request(s) match: add --yes to confirm in a script."); return 1; }
            Console.Write($"{(accept ? "Accept" : ban ? "Reject and BAN" : "Reject")} {total} request(s)? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Nothing done."); return 0; }
        }

        var path = accept ? "api/admin/access/requests/accept" : "api/admin/access/requests/reject";
        using var response = await api.Http.PostAsJsonAsync(path, new { ids = bulk ? null : rest, all = bulk, name, older_than_days = older, ban, reason });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);

        var root = System.Text.Json.JsonDocument.Parse(body).RootElement;
        var done = root.GetProperty(accept ? "accepted" : "rejected").EnumerateArray().Select(e => e.GetString()).ToList();
        Console.WriteLine($"{(accept ? "Accepted" : ban ? "Rejected and banned" : "Rejected")}: {done.Count}" + (done.Count is > 0 and <= 20 ? " (" + string.Join(", ", done) + ")" : ""));
        var unknown = root.GetProperty("unknown").EnumerateArray().Select(e => e.GetString()).ToList();
        var ambiguous = root.GetProperty("ambiguous").EnumerateArray().Select(e => e.GetString()).ToList();
        if (unknown.Count > 0) Console.Error.WriteLine("Not waiting (already handled, expired or mistyped): " + string.Join(", ", unknown));
        if (ambiguous.Count > 0) Console.Error.WriteLine("Ambiguous prefix, give more characters: " + string.Join(", ", ambiguous));
        return unknown.Count + ambiguous.Count > 0 ? 4 : 0;
    }
}
