/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Json;
using System.Text.Json;

namespace FreeSpeech.Ops;

/// <summary>
/// The "admin" commands that link servers (relay ↔ push gateway), the same on every server:
/// <c>add</c>, <c>list</c>, <c>requests</c>, <c>accept</c>, <c>reject</c>, <c>remove</c>, <c>ban</c>, <c>unban</c>, <c>peer-id</c>, <c>peer-refresh</c>.
/// A command takes the kind of server first (<c>push</c> or <c>relay</c>); a server that has a single possible kind may leave it out.
/// </summary>
public static class PeerCli
{
    public const string Usage = """
          peer-id                                   this server's fingerprint (give it to the other administrator to compare)
          add <push|relay|directory> <url> [--fingerprint f] [--insecure]
                                                    ask a server for a link; its administrator must accept (https; http only on a private network)
          list [push|relay|directory]               every server linked, asked, refused or banned (all kinds when none is given)
          requests <push|relay|directory> [--name t] [--limit N] [--offset N] [--json]
                                                    requests from other servers waiting for you
          accept <push|relay|directory> <id…> | --all [--name t] [--older-than DAYS] [--yes]
          reject <push|relay|directory> <id…> | --all [--name t] [--older-than DAYS] [--ban] [--yes]
          remove <push|relay|directory> <id>                  forget a server (it may ask again)
          ban <push|relay|directory> <id> | unban <push|relay|directory> <id>
          peer-requests open | close                take requests from other servers, or not
          peer-refresh                              ask now where the requests you sent stand
        """;

    private static readonly string[] Verbs = ["add", "list", "requests", "accept", "reject", "remove", "ban", "unban"];

    /// <summary>
    /// Runs the command when it is one of ours (returns its exit code), otherwise returns null so the server's own commands get their turn.
    /// <paramref name="defaultKind"/> is the kind to assume when none is given (a gateway links with relays only); null = the kind is required.
    /// </summary>
    public static async Task<int?> TryRunAsync(List<string> list, AdminApi api, string prefix, string? defaultKind)
    {
        var verb = list[0];
        if (verb == "peer-id") return await api.Show($"{prefix}/peers/identity");
        if (verb == "peer-refresh") return await api.ShowPost($"{prefix}/peers/refresh", new { });
        if (verb == "peer-requests") return await api.Post($"{prefix}/peers/open", new { open = (list.Count > 1 ? list[1] : "") switch { "open" => true, "close" => false, _ => throw new ArgumentException("peer-requests open | close") } });
        if (!Verbs.Contains(verb)) return null;

        var rest = list.Skip(1).ToList();
        if (verb == "list" && rest.Count == 0) return await List(api, prefix, null);
        string? kind = null;
        if (rest.Count > 0 && rest[0] is "push" or "relay" or "directory") { kind = rest[0]; rest.RemoveAt(0); }
        else if (defaultKind == null) return null;   // not a peer command for this server (e.g. the relay's own "accept <id>")
        kind ??= defaultKind;
        if (kind == null) return null;

        return verb switch
        {
            "add" => await Add(api, prefix, kind, rest),
            "list" => await List(api, prefix, kind),
            "requests" => await Requests(api, prefix, kind, rest),
            "accept" or "reject" => await Decide(api, prefix, kind, verb == "accept", rest),
            "remove" => await api.Delete($"{prefix}/peers?id={AdminApi.Q(rest.FirstOrDefault() ?? throw new ArgumentException("missing id"))}"),
            "ban" => await api.Post($"{prefix}/peers/ban", new { id = rest.FirstOrDefault() ?? throw new ArgumentException("missing id") }),
            "unban" => await api.Delete($"{prefix}/peers/ban?id={AdminApi.Q(rest.FirstOrDefault() ?? throw new ArgumentException("missing id"))}"),
            _ => null,
        };
    }

    private static async Task<int> Add(AdminApi api, string prefix, string kind, List<string> rest)
    {
        var fingerprint = AdminCliKit.TakeOption(rest, "--fingerprint");
        var insecure = AdminCliKit.TakeFlag(rest, "--insecure");
        var url = rest.FirstOrDefault() ?? throw new ArgumentException($"add {kind} <url>");

        using var response = await api.Http.PostAsJsonAsync($"{prefix}/peers", new { kind, url, fingerprint, insecure });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(TryField(body, "error") ?? body);
            return response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? AdminCliKit.Fail(response, body) : 4;
        }
        var root = JsonDocument.Parse(body).RootElement;
        var state = root.GetProperty("state").GetString();
        Console.WriteLine($"{kind} server {root.GetProperty("url").GetString()}  ({root.GetProperty("name").GetString() ?? "no name"})");
        Console.WriteLine($"  fingerprint: {root.GetProperty("id").GetString()}");
        Console.WriteLine(state == PeerState.Accepted
            ? "  Linked: agreed on both sides (that server had already asked for this link, or the link already existed); its address is now the one given here, no new request is sent."
            : "  Request sent: its administrator has to accept it (they see your fingerprint with 'peer-id'). 'list' shows where it stands.");
        return 0;
    }

    private static async Task<int> List(AdminApi api, string prefix, string? kind)
    {
        using var response = await api.Http.GetAsync($"{prefix}/peers" + (kind == null ? "" : $"?kind={kind}"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);
        var root = JsonDocument.Parse(body).RootElement;
        var peers = root.GetProperty("peers").EnumerateArray().ToList();
        if (peers.Count == 0) Console.WriteLine($"No {kind ?? "server"} linked or asked yet.");
        else
        {
            Console.WriteLine($"{"ID",-12}  {"KIND",-9}  {"STATE",-8}  NAME / ADDRESS / CONTACT");
            foreach (var p in peers)
                Console.WriteLine($"{p.GetProperty("id").GetString()![..12],-12}  {p.GetProperty("kind").GetString(),-9}  {Describe(p.GetProperty("state").GetString()),-8}  {p.GetProperty("name").GetString() ?? "(no name)"}  {p.GetProperty("url").GetString()}  {Contact(p)}");
        }
        var waiting = root.GetProperty("pending_requests").GetInt32();
        if (waiting > 0) Console.WriteLine($"{waiting} request(s) from other servers waiting: 'requests {kind ?? "<kind>"}'.");
        return 0;
    }

    private static string Contact(JsonElement p) => p.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.String ? "<" + c.GetString() + ">" : "";

    private static string Describe(string? state) => state switch
    {
        PeerState.Accepted => "linked",
        PeerState.PendingOut => "asked",
        PeerState.Refused => "refused",
        PeerState.Banned => "banned",
        _ => state ?? "?",
    };

    private static async Task<int> Requests(AdminApi api, string prefix, string kind, List<string> rest)
    {
        var name = AdminCliKit.TakeOption(rest, "--name");
        var limit = AdminCliKit.TakeOption(rest, "--limit") ?? "50";
        var offset = AdminCliKit.TakeOption(rest, "--offset") ?? "0";
        var json = AdminCliKit.TakeFlag(rest, "--json");
        var query = $"kind={kind}&limit={AdminApi.Q(limit)}&offset={AdminApi.Q(offset)}" + (name != null ? "&name=" + AdminApi.Q(name) : "");
        if (json) return await api.Show($"{prefix}/peers/requests?{query}");

        using var response = await api.Http.GetAsync($"{prefix}/peers/requests?{query}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);
        var root = JsonDocument.Parse(body).RootElement;
        if (!root.GetProperty("open").GetBoolean()) Console.WriteLine("(the queue is closed: new requests are not taken — 'peer-requests open' to reopen)");
        var total = root.GetProperty("total").GetInt32();
        if (total == 0) { Console.WriteLine($"No {kind} request waiting."); return 0; }

        Console.WriteLine($"{"ID",-12}  {"REQUESTED (UTC)",-16}  NAME / ADDRESS");
        foreach (var r in root.GetProperty("requests").EnumerateArray())
            {
            Console.WriteLine($"{r.GetProperty("id").GetString()![..12],-12}  {r.GetProperty("since").GetDateTime():yyyy-MM-dd HH:mm}  {r.GetProperty("name").GetString() ?? "(no name)"}  {r.GetProperty("url").GetString()}");
            Console.WriteLine($"{"",-12}  contact: {(r.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "(none)")}   seen from: {(r.TryGetProperty("seen_from", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "(unknown)")}   (declared by the sender, to be checked by another way)");
        }
        Console.WriteLine($"{root.GetProperty("requests").GetArrayLength()} shown, {total} matching. Compare the id with the fingerprint the other administrator reads with 'peer-id', then 'accept {kind} <id>'.");
        return 0;
    }

    private static async Task<int> Decide(AdminApi api, string prefix, string kind, bool accept, List<string> rest)
    {
        var all = AdminCliKit.TakeFlag(rest, "--all");
        var yes = AdminCliKit.TakeFlag(rest, "--yes");
        var ban = AdminCliKit.TakeFlag(rest, "--ban");
        var name = AdminCliKit.TakeOption(rest, "--name");
        var older = AdminCliKit.TakeOption(rest, "--older-than");
        var olderDays = string.IsNullOrEmpty(older) ? 0 : int.Parse(older.TrimEnd('d', 'D'));
        if (accept && ban) throw new ArgumentException("--ban only goes with reject");

        var bulk = rest.Count == 0;
        if (bulk && !all && name == null && olderDays == 0) throw new ArgumentException($"{(accept ? "accept" : "reject")} {kind}: give ids, or --all (optionally with --name / --older-than)");
        if (!bulk && (all || name != null || olderDays > 0)) throw new ArgumentException("give either ids or --all with filters, not both");

        if (bulk && !yes)
        {
            using var preview = await api.Http.GetAsync($"{prefix}/peers/requests?kind={kind}&limit=1&older_than_days={olderDays}" + (name != null ? "&name=" + AdminApi.Q(name) : ""));
            var text = await preview.Content.ReadAsStringAsync();
            if (!preview.IsSuccessStatusCode) return AdminCliKit.Fail(preview, text);
            var total = JsonDocument.Parse(text).RootElement.GetProperty("total").GetInt32();
            if (total == 0) { Console.WriteLine("No request matches."); return 0; }
            if (Console.IsInputRedirected) { Console.Error.WriteLine($"{total} request(s) match: add --yes to confirm in a script."); return 1; }
            Console.Write($"{(accept ? "Accept" : ban ? "Reject and BAN" : "Reject")} {total} {kind} request(s)? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Nothing done."); return 0; }
        }

        using var response = await api.Http.PostAsJsonAsync($"{prefix}/peers/{(accept ? "accept" : "reject")}", new { kind, ids = bulk ? null : rest, all = bulk, name, older_than_days = olderDays, ban });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);
        var root = JsonDocument.Parse(body).RootElement;

        List<string?> Names(string field) => root.TryGetProperty(field, out var a) ? a.EnumerateArray().Select(e => e.GetString()).ToList() : [];
        var done = Names(accept ? "accepted" : "rejected");
        Console.WriteLine($"{(accept ? "Accepted" : ban ? "Rejected and banned" : "Rejected")}: {done.Count}" + (done.Count is > 0 and <= 20 ? " (" + string.Join(", ", done) + ")" : ""));
        var unverified = Names("unverified"); var mismatch = Names("mismatch"); var unknown = Names("unknown"); var ambiguous = Names("ambiguous");
        if (unverified.Count > 0) Console.WriteLine("Accepted but not verified (its address did not answer): " + string.Join(", ", unverified));
        var noAddress = Names("no_address");
        if (noAddress.Count > 0) Console.Error.WriteLine("NOT accepted: they gave no address, so this server could never call them (they must set Peer:PublicUrl and ask again): " + string.Join(", ", noAddress));
        if (mismatch.Count > 0) Console.Error.WriteLine("NOT accepted: the address they give does not show the key they asked with: " + string.Join(", ", mismatch));
        if (unknown.Count > 0) Console.Error.WriteLine("Not waiting (already handled, expired or mistyped): " + string.Join(", ", unknown));
        if (ambiguous.Count > 0) Console.Error.WriteLine("Ambiguous prefix, give more characters: " + string.Join(", ", ambiguous));
        return mismatch.Count + noAddress.Count + unknown.Count + ambiguous.Count > 0 ? 4 : 0;
    }

    private static string? TryField(string json, string field)
    {
        try { return JsonDocument.Parse(json).RootElement.TryGetProperty(field, out var v) ? v.GetString() : null; }
        catch (JsonException) { return null; }
    }
}
