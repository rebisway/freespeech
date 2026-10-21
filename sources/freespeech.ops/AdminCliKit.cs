/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FreeSpeech.Ops;

/// <summary>One connection to a local admin API, with the calls every command needs. Each call prints its result and returns the exit code.</summary>
public sealed class AdminApi(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public Task<int> Show(string path) => ShowAsync(Http.GetAsync(path));
    public Task<int> ShowPost(string path, object body) => ShowAsync(Http.PostAsJsonAsync(path, body));
    public Task<int> Post(string path, object body) => DoneAsync(Http.PostAsJsonAsync(path, body));
    public Task<int> Delete(string path) => DoneAsync(Http.DeleteAsync(path));
    public static string Q(string value) => Uri.EscapeDataString(value);

    private static async Task<int> ShowAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, body);
        if (body.Length > 0)
        {
            try { Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(body).RootElement, new JsonSerializerOptions { WriteIndented = true })); }
            catch (JsonException) { Console.WriteLine(body); }
        }
        return 0;
    }

    private static async Task<int> DoneAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        if (!response.IsSuccessStatusCode) return AdminCliKit.Fail(response, await response.Content.ReadAsStringAsync());
        Console.WriteLine("ok");
        return 0;
    }
}

/// <summary>
/// Command-line plumbing shared by every FreeSpeech server's "admin" command: where the server is (--url / FS_ADMIN_URL /
/// ASPNETCORE_URLS), the secret (FS_ADMIN_SECRET / Admin:Secret), error reporting with stable exit codes, and the "log" and
/// "trace" commands every server has. Exit codes: 0 ok, 1 usage, 2 no secret, 3 server unreachable, 4 refused or failed.
/// </summary>
public static class AdminCliKit
{
    public const string OpsUsage = $"""
          (no command)                              interactive prompt: type the commands below; Ctrl+C stops a running one; exit leaves
          version                                   version, runtime and uptime of the running server
          log                                    show log levels
          log level <category|all> <level>          change a level now (trace|debug|information|warning|error|critical|none)
          log reset                                 back to the levels of the configuration
          log tail [-n 100] [--level debug] [-f]    last lines from memory; -f keeps following, like a console
          trace <id> [--minutes 15]                 follow one identity (first 8+ hex characters; ends by itself, 60 minutes at most)
          trace list | trace stop <id|all>
        {MailCli.Usage}
        """;

    /// <param name="appName">Used in messages.</param>
    /// <param name="prompt">The name before "&gt;" at the interactive prompt: tells the programs apart when several are open.</param>
    /// <param name="usage">Full usage text for the server's own commands (the log/trace lines are added).</param>
    /// <param name="opsPrefix">Route prefix of the admin API on this server ("api/admin" or "admin").</param>
    /// <param name="commands">The server's own commands: returns null when the command is not one of them.</param>
    public static async Task<int> RunAsync(string[] args, string appName, string usage, string opsPrefix, Func<List<string>, AdminApi, Task<int?>> commands, string prompt = "freespeech")
    {
        var list = args.ToList();
        var url = TakeOption(list, "--url") ?? Environment.GetEnvironmentVariable("FS_ADMIN_URL") ?? DefaultUrl();
        // No command: an interactive prompt when a person is typing, the usage text (exit 1) in a script.
        // "--shell" forces the prompt even when input or output is piped (commands read from standard input, one per line).
        var forced = TakeFlag(list, "--shell");
        var shell = list.Count == 0 && (forced || (!Console.IsInputRedirected && !Console.IsOutputRedirected));
        if (!shell && (list.Count == 0 || list[0] is "help" or "--help" or "-h")) { Console.WriteLine(usage); return list.Count == 0 ? 1 : 0; }

        var secret = Environment.GetEnvironmentVariable("FS_ADMIN_SECRET") ?? ConfiguredSecret();
        if (string.IsNullOrEmpty(secret)) { Console.Error.WriteLine("No admin secret: set FS_ADMIN_SECRET or Admin:Secret."); return 2; }

        if (shell) return await ShellAsync(url, secret, appName, usage, opsPrefix, commands, prompt);
        using var cts = new CancellationTokenSource();
        var interrupted = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupted = true; cts.Cancel(); };
        return await ExecuteAsync(list, url, secret, appName, usage, opsPrefix, commands, shell: false, () => interrupted, cts.Token);
    }

    /// <summary>One command against the running server: returns its exit code and reports errors (never throws).</summary>
    private static async Task<int> ExecuteAsync(List<string> list, string url, string secret, string appName, string usage, string opsPrefix,
        Func<List<string>, AdminApi, Task<int?>> commands, bool shell, Func<bool> interrupted, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-Admin-Secret", secret);
        var api = new AdminApi(http);
        try
        {
            var follow = list[0] == "log" && list.Count > 1 && list[1] == "tail" && (list.Contains("-f") || list.Contains("--follow"));
            var tracing = shell && list[0] == "trace" && list.Count > 1 && list[1] is not ("list" or "stop");
            if (!follow && !tracing) http.Timeout = TimeSpan.FromSeconds(15);

            var result = await RunOpsAsync(list, api, opsPrefix, shell, ct) ?? await commands(list, api);
            if (result is { } code) return code;
            Console.Error.WriteLine($"Unknown command '{list[0]}'.{(shell ? " Type 'help'." : "\n\n" + usage)}");
            return 1;
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        catch (FormatException) { Console.Error.WriteLine("A number was expected."); return 1; }
        catch (HttpRequestException ex) { Console.Error.WriteLine($"Cannot reach {appName} at {url}: {ex.Message}"); return 3; }
        catch (OperationCanceledException)
        {
            if (interrupted()) return 0;   // Ctrl+C on a followed log
            Console.Error.WriteLine($"No answer from {url}.");
            return 3;
        }
    }

    /// <summary>
    /// The interactive prompt (like <c>asterisk -r</c> or <c>mysql</c>): each line is one of the usual commands. Ctrl+C stops a command that
    /// is running (a followed log, a trace) and keeps the prompt; "exit", "quit" or Ctrl+D leaves.
    /// </summary>
    private static async Task<int> ShellAsync(string url, string secret, string appName, string usage, string opsPrefix, Func<List<string>, AdminApi, Task<int?>> commands, string prompt)
    {
        CancellationTokenSource? running = null;
        var interrupted = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupted = true; running?.Cancel(); };

        // Greet with the version of the running server; also proves the address and the secret work.
        using (var probe = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) })
        {
            probe.DefaultRequestHeaders.Add("X-Admin-Secret", secret);
            try { Console.WriteLine(await VersionLineAsync(new AdminApi(probe), opsPrefix) ?? $"Connected to {appName} at {url}."); }
            catch (HttpRequestException ex) { Console.Error.WriteLine($"Cannot reach {appName} at {url}: {ex.Message}"); return 3; }
            catch (OperationCanceledException) { Console.Error.WriteLine($"No answer from {url}."); return 3; }
        }
        Console.WriteLine("Type 'help' for the commands, 'exit' to leave. Ctrl+C stops a running command.");

        var last = 0;
        var editor = new LineEditor(".freespeech_admin_history");
        while (true)
        {
            var line = editor.Read(prompt + "> ");
            if (line == null) { Console.WriteLine(); return last; }   // Ctrl+D / end of input
            var words = Tokenize(line);
            if (words.Count == 0) continue;
            if (words[0] is "exit" or "quit") return last;
            if (words[0] is "help" or "?") { Console.WriteLine(usage); continue; }

            interrupted = false;
            using var cts = new CancellationTokenSource();
            running = cts;
            last = await ExecuteAsync(words, url, secret, appName, usage, opsPrefix, commands, shell: true, () => interrupted, cts.Token);
            running = null;
            if (interrupted) Console.WriteLine();
        }
    }

    /// <summary>Splits a line into words; single or double quotes keep spaces together.</summary>
    internal static List<string> Tokenize(string line)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        var has = false;
        foreach (var ch in line)
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; else current.Append(ch); }
            else if (ch is '"' or '\'') { quote = ch; has = true; }
            else if (char.IsWhiteSpace(ch)) { if (has || current.Length > 0) { words.Add(current.ToString()); current.Clear(); has = false; } }
            else current.Append(ch);
        }
        if (has || current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static async Task<string?> VersionLineAsync(AdminApi api, string p)
    {
        using var response = await api.Http.GetAsync($"{p}/version");
        if (!response.IsSuccessStatusCode) return null;
        var v = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        string S(string name) => v.TryGetProperty(name, out var e) ? e.ToString() : "?";
        var up = TimeSpan.FromSeconds(v.TryGetProperty("uptime_seconds", out var u) ? u.GetInt64() : 0);
        return $"{S("program")} {S("version")}{(v.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String ? $" of {date.GetString()}" : "")} ({S("runtime")}, {S("os")}), running since {S("started_utc")[..Math.Min(19, S("started_utc").Length)]} UTC ({(int)up.TotalDays}d {up.Hours}h {up.Minutes}m)";
    }

    private static async Task<int?> RunOpsAsync(List<string> list, AdminApi api, string p, bool shell, CancellationToken ct)
    {
        string Arg(int i, string what) => list.Count > i ? list[i] : throw new ArgumentException($"missing {what}");

        if (list[0] is "smtp" or "notify" or "export" or "self" or "key" && await MailCli.TryRunAsync(list, api, p) is { } mailCode) return mailCode;

        switch (list[0])
        {
            case "version" or "--version":
                {
                    var line = await VersionLineAsync(api, p);
                    if (line == null) { Console.Error.WriteLine("Refused: wrong or missing admin secret, or not called from the machine itself."); return 4; }
                    Console.WriteLine(line);
                    return 0;
                }

            case "log":
                var sub = list.Count > 1 ? list[1] : "";
                if (sub == "") return await api.Show($"{p}/log");
                if (sub == "reset") return await api.Delete($"{p}/log/level");
                if (sub == "level") return await api.Post($"{p}/log/level", new { category = Arg(2, "category (or all)"), level = Arg(3, "level") });
                if (sub == "tail")
                {
                    var rest = list.Skip(2).ToList();
                    var lines = TakeOption(rest, "-n") ?? "100";
                    var level = TakeOption(rest, "--level") ?? "trace";
                    var follow = rest.Contains("-f") || rest.Contains("--follow");
                    return await TailAsync(api, p, lines, level, follow, null, ct);
                }
                throw new ArgumentException("log, log level <category|all> <level>, log reset, log tail");

            case "trace":
                var action = list.Count > 1 ? list[1] : "list";
                if (action == "list") return await api.Show($"{p}/trace");
                if (action == "stop") return await api.Delete($"{p}/trace?id={AdminApi.Q(list.Count > 2 ? list[2] : "all")}");
                var rest2 = list.Skip(2).ToList();
                var minutes = TakeOption(rest2, "--minutes");
                if (!shell) return await api.ShowPost($"{p}/trace", new { id = list[1], minutes = double.TryParse(minutes, out var m) ? m : 15 });

                // At the prompt a trace shows the traffic of that identity live, until Ctrl+C (which also ends the trace).
                var started = await api.Http.PostAsJsonAsync($"{p}/trace", new { id = list[1], minutes = double.TryParse(minutes, out var mm) ? mm : 15 });
                if (!started.IsSuccessStatusCode) return Fail(started, await started.Content.ReadAsStringAsync());
                var shortId = list[1].ToLowerInvariant()[..Math.Min(8, list[1].Length)];
                Console.WriteLine($"Tracing {shortId} for {(double.TryParse(minutes, out var m2) ? m2 : 15)} minute(s). Ctrl+C stops.");
                try { return await TailAsync(api, p, "0", "trace", follow: true, contains: shortId, ct); }
                finally
                {
                    using var stop = new HttpClient { BaseAddress = api.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
                    foreach (var h in api.Http.DefaultRequestHeaders) stop.DefaultRequestHeaders.Add(h.Key, h.Value);
                    try { await stop.DeleteAsync($"{p}/trace?id={AdminApi.Q(list[1])}"); } catch (Exception) { /* the trace ends by itself anyway */ }
                }
        }
        return null;
    }

    private static async Task<int> TailAsync(AdminApi api, string p, string lines, string level, bool follow, string? contains, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{p}/log/tail?lines={AdminApi.Q(lines)}&level={AdminApi.Q(level)}&follow={follow.ToString().ToLowerInvariant()}");
        using var response = await api.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return Fail(response, "");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (contains == null || line.Contains(contains, StringComparison.OrdinalIgnoreCase)) Console.WriteLine(line);
        return 0;
    }

    public static int Fail(HttpResponseMessage response, string body)
    {
        Console.Error.WriteLine(response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Refused: wrong or missing admin secret, or not called from the machine itself (the server must have Admin:Secret set).",
            System.Net.HttpStatusCode.NotFound => "Not found (nothing to remove, or a proxy hides the admin API: use the local address).",
            _ => $"Failed ({(int)response.StatusCode}): {body}",
        });
        return 4;
    }

    /// <summary>
    /// The verification code of an identity: the first 12 characters of its id, upper case, in groups of four ("3FA9-C1B2-7E40"). The person who asks for
    /// access reads it out; the administrator compares it with the one shown here before accepting. Same notation on the phone and in the console.
    /// </summary>
    public static string VerificationCode(string discoveryId)
    {
        var head = discoveryId.Length >= 12 ? discoveryId[..12].ToUpperInvariant() : discoveryId.ToUpperInvariant();
        return string.Join('-', Enumerable.Range(0, (head.Length + 3) / 4).Select(i => head.Substring(i * 4, Math.Min(4, head.Length - i * 4))));
    }

    public static string? TakeOption(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    /// <summary>True (and removes it) when the switch is present, e.g. <c>--all</c>.</summary>
    public static bool TakeFlag(List<string> args, string name) => args.Remove(name);

    public static string DefaultUrl()
    {
        var first = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first?.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("*", "127.0.0.1").Replace("+", "127.0.0.1") ?? "http://127.0.0.1:5000";
    }

    private static string? ConfiguredSecret()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Production.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config["Admin:Secret"];
    }
}
