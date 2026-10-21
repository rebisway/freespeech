/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpeech.Ops;

/// <summary>The admin routes of the SMTP account, the notifications and the state export, the same on every server (mounted by <c>MapOpsAdmin</c>).</summary>
public static class MailAdmin
{
    public sealed record TestRequest(string? To);
    public sealed record NotifyRequest(bool? Enabled, string? Kind, bool? On);
    public sealed record SendRequest(string? Message);
    public sealed record ExportRequest(string? Passphrase);
    public sealed record SelfRequest(string? Name, string? PublicUrl, string? Contact);

    private static object Self(HttpContext c)
    {
        var o = c.RequestServices.GetRequiredService<PeerOptions>();
        return new { kind = o.Kind, name = o.Name, public_url = o.PublicUrl, contact = o.Contact, fingerprint = c.RequestServices.GetRequiredService<PeerIdentity>().Fingerprint, public_key = c.RequestServices.GetRequiredService<PeerIdentity>().PublicKey };
    }

    public static void Map(IEndpointRouteBuilder app, string prefix)
    {
        // Services are looked up inside the handlers: a server that registered none of them answers 404 on these routes and nothing else changes.
        IResult? Guard(HttpContext c) => AdminGuard.IsAuthorized(c, c.RequestServices.GetRequiredService<IConfiguration>()) ? null : Results.Unauthorized();
        static MailSettings Settings(HttpContext c) => c.RequestServices.GetRequiredService<MailSettings>();

        app.MapGet($"{prefix}/smtp", (HttpContext c) => Guard(c) ?? Results.Ok(Settings(c).Snapshot()));

        app.MapPost($"{prefix}/smtp", (HttpContext c, SmtpRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try { Settings(c).Update(body); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(Settings(c).Snapshot());
        });

        app.MapDelete($"{prefix}/smtp", (HttpContext c) =>
        {
            if (Guard(c) is { } denied) return denied;
            Settings(c).Clear();
            return Results.Ok();
        });

        // A mail sent now with the account as it is, and the answer of the SMTP server: where it stopped if it did not go through.
        app.MapPost($"{prefix}/smtp/test", async (HttpContext c, TestRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            var result = await c.RequestServices.GetRequiredService<Notifier>().SendNowAsync("SMTP test", "This is a test mail. If you read it, the SMTP settings of this server work.", body.To, c.RequestAborted);
            return Results.Ok(result);
        });

        app.MapGet($"{prefix}/notify", (HttpContext c) => Guard(c) ?? Results.Ok(((dynamic)Settings(c).Snapshot()).notify));

        app.MapPost($"{prefix}/notify", (HttpContext c, NotifyRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            NotifyKind? kind = null;
            if (body.Kind != null)
            {
                if (!Enum.TryParse<NotifyKind>(body.Kind, ignoreCase: true, out var k) || int.TryParse(body.Kind, out _)) return Results.BadRequest(new { error = "kind must be peers, abuse, access or system" });
                kind = k;
            }
            if ((body.On ?? body.Enabled) is not { } on) return Results.BadRequest(new { error = "on or off is missing" });
            Settings(c).SetNotify(kind, on);
            return Results.Ok(((dynamic)Settings(c).Snapshot()).notify);
        });

        app.MapPost($"{prefix}/notify/send", async (HttpContext c, SendRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 2000) return Results.BadRequest(new { error = "message must be 1 to 2000 characters" });
            return Results.Ok(await c.RequestServices.GetRequiredService<Notifier>().SendNowAsync("Message from the administrator", body.Message, null, c.RequestAborted));
        });

        // How this server presents itself to other servers: its name and its public address. Changed while it runs and kept in appsettings.Local.json.
        app.MapGet($"{prefix}/self", (HttpContext c) => Guard(c) ?? Results.Ok(Self(c)));
        app.MapPost($"{prefix}/self", (HttpContext c, SelfRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            var options = c.RequestServices.GetRequiredService<PeerOptions>();
            var name = body.Name == null ? null : PeerStore.Clean(body.Name, 64);
            if (body.Name != null && name == null) return Results.BadRequest(new { error = "name must be 1 to 64 characters" });
            var url = body.PublicUrl?.Trim().TrimEnd('/');
            if (url is { Length: > 0 } && !PeerClient.IsAcceptableUrl(url, insecure: false, out _)) return Results.BadRequest(new { error = "public_url must be https:// (http:// only for a private network), at most 200 characters" });
            var contact = body.Contact == null ? null : PeerStore.Clean(body.Contact, 128) ?? "";
            var changes = new Dictionary<string, object?>();
            if (contact != null) { options.Contact = contact.Length == 0 ? null : contact; changes["Peer:Contact"] = contact.Length == 0 ? null : contact; }
            if (name != null) { options.Name = name; changes["Peer:Name"] = name; }
            if (url != null) { options.PublicUrl = url.Length == 0 ? null : url; changes["Peer:PublicUrl"] = url.Length == 0 ? null : url; }
            if (changes.Count > 0) c.RequestServices.GetRequiredService<LocalConfigFile>().Set(changes);
            return Results.Ok(Self(c));
        });

        // The state of the server in one encrypted file. The passphrase travels in the body of a local, secret-protected call.
        app.MapPost($"{prefix}/export", (HttpContext c, ExportRequest body) =>
        {
            if (Guard(c) is { } denied) return denied;
            try { return Results.File(StateArchive.Pack(c.RequestServices.GetRequiredService<StateFiles>(), body.Passphrase ?? ""), "application/octet-stream"); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
}

/// <summary>The <c>smtp</c>, <c>notify</c> and <c>export</c> commands, the same on every server.</summary>
public static class MailCli
{
    public const string Usage = """
          smtp show                                 SMTP account and notification switches (the password is never shown)
          smtp set host|port|tls|user|from|to|prefix <value>
                                                    tls = starttls | implicit | none; to = addresses separated by commas;
                                                    prefix = start of the subject, with {kind} and {host} (default "[{kind}] {host}")
          smtp set password                         read without echo (or from standard input, or FS_SMTP_PASSWORD); written to appsettings.Local.json
          smtp clear                                forget the account and turn the notifications off
          smtp test [--to address]                  send a mail now and print the answer of the SMTP server (exit 4 when it fails)
          notify                                    which notifications are on
          notify on | off [peers|abuse|access|system]
                                                    all of them, or one kind: link requests, abuses, clients waiting, configuration problems
          notify send "<message>"                   send a mail to the administrator now
          self                                      how this server presents itself to the others: kind, name, public address, contact, fingerprint, public key
          key                                       print the public key alone, on one line (the value to copy where this server's key is asked for)
          self set name|public-url|contact <value>  change them now (a public address is https://; the contact is how another administrator can check who you are, e.g. a mail address; empty = none); kept in appsettings.Local.json
          export <file> [--force]                   save the state of the server (keys, links, access) in a file encrypted with a passphrase;
                                                    put it back on a stopped server with: dotnet <program>.dll import <file> [--force]
        """;

    /// <summary>A mail attempt can take as long as the server allows it (Smtp:TimeoutSeconds, at most 120): the command waits for the answer instead of giving up first.</summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(130);

    private static readonly string[] Fields = ["host", "port", "tls", "user", "from", "to", "prefix"];

    public static async Task<int?> TryRunAsync(List<string> list, AdminApi api, string p)
    {
        string Arg(int i, string what) => list.Count > i ? list[i] : throw new ArgumentException($"missing {what}");

        switch (list[0])
        {
            case "smtp":
                switch (list.Count > 1 ? list[1] : "show")
                {
                    case "show": return await api.Show($"{p}/smtp");
                    case "clear": return await api.Delete($"{p}/smtp");
                    case "test": return await Test(api, p, list.Skip(2).ToList());
                    case "set":
                        var field = Arg(2, "host | port | tls | user | password | from | to | prefix").ToLowerInvariant();
                        if (field == "password")
                            return await api.Post($"{p}/smtp", new { password = PromptKit.ReadSecret("SMTP password: ", "FS_SMTP_PASSWORD", confirm: false) });
                        if (!Fields.Contains(field)) throw new ArgumentException("smtp set host|port|tls|user|password|from|to|prefix <value>");
                        var value = list.Count > 3 ? string.Join(' ', list.Skip(3)) : (field is "user" or "prefix" or "to" ? "" : throw new ArgumentException($"missing the value of {field}"));
                        object body = field == "port" ? new { port = int.Parse(value) } : new Dictionary<string, string> { [field] = value };
                        return await api.ShowPost($"{p}/smtp", body);
                }
                throw new ArgumentException("smtp show | set <field> <value> | clear | test");

            case "notify":
                var sub = list.Count > 1 ? list[1] : "";
                if (sub == "") return await api.Show($"{p}/notify");
                if (sub == "send") return await Send(api, p, string.Join(' ', list.Skip(2)));
                if (sub is "on" or "off") return await api.ShowPost($"{p}/notify", new { on = sub == "on", kind = list.Count > 2 ? list[2] : null });
                throw new ArgumentException("notify | notify on|off [peers|abuse|access|system] | notify send \"<message>\"");

            case "export":
                return await Export(api, p, list.Skip(1).ToList());

            case "key":
                using (var response = await api.Http.GetAsync($"{p}/self"))
                {
                    if (!response.IsSuccessStatusCode) return await Print(response, "");
                    Console.WriteLine((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("public_key").GetString());
                    return 0;
                }

            case "self":
                if (list.Count == 1) return await api.Show($"{p}/self");
                if (list[1] != "set") throw new ArgumentException("self | self set name|public-url|contact <value>");
                var what = Arg(2, "name | public-url | contact");
                var given = list.Count > 3 ? string.Join(' ', list.Skip(3)) : (what is "public-url" or "contact" ? "" : throw new ArgumentException("missing the value"));
                return what switch
                {
                    "name" => await api.ShowPost($"{p}/self", new { name = given }),
                    "public-url" => await api.ShowPost($"{p}/self", new { publicUrl = given }),
                    "contact" => await api.ShowPost($"{p}/self", new { contact = given }),
                    _ => throw new ArgumentException("self set name|public-url|contact <value>"),
                };
        }
        return null;
    }

    private static async Task<int> Test(AdminApi api, string p, List<string> rest)
    {
        var to = AdminCliKit.TakeOption(rest, "--to");
        api.Http.Timeout = Patient;
        return await Print(await api.Http.PostAsJsonAsync($"{p}/smtp/test", new { to }), "sent");
    }

    private static async Task<int> Send(AdminApi api, string p, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("notify send \"<message>\"");
        api.Http.Timeout = Patient;
        return await Print(await api.Http.PostAsJsonAsync($"{p}/notify/send", new { message }), "sent");
    }

    /// <summary>Prints the outcome of a mail attempt: what the SMTP server said and the stage that failed. Exit 0 when the mail went, 4 when not.</summary>
    private static async Task<int> Print(HttpResponseMessage response, string okWord)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, await response.Content.ReadAsStringAsync());
            var r = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var ok = r.GetProperty("ok").GetBoolean();
            var message = r.GetProperty("message").GetString();
            if (ok) { Console.WriteLine($"ok: {message}"); return 0; }
            Console.Error.WriteLine($"FAILED at {r.GetProperty("stage").GetString()}: {message}");
            return 4;
        }
    }

    private static async Task<int> Export(AdminApi api, string p, List<string> rest)
    {
        var force = AdminCliKit.TakeFlag(rest, "--force");
        if (rest.Count != 1) throw new ArgumentException("export <file> [--force]");
        if (File.Exists(rest[0]) && !force) { Console.Error.WriteLine($"{rest[0]} exists: use --force to replace it."); return 1; }

        var passphrase = PromptKit.ReadSecret("Passphrase for the archive (at least 8 characters, keep it: nothing else can open the file): ", "FS_STATE_PASSPHRASE", confirm: true);
        using var response = await api.Http.PostAsJsonAsync($"{p}/export", new { passphrase });
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, await response.Content.ReadAsStringAsync());
        AtomicFile.WriteAllBytes(rest[0], await response.Content.ReadAsByteArrayAsync());
        Console.WriteLine($"saved {rest[0]}");
        return 0;
    }
}
