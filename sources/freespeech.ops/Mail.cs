/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FreeSpeech.Ops;

/// <summary>
/// <c>appsettings.Local.json</c>, the one place of the local settings and secrets, edited by the admin commands that must change a
/// setting from a distance (the SMTP account). The other keys are kept; comments of the file are not (the file is rewritten).
/// </summary>
public sealed class LocalConfigFile(string path)
{
    private static readonly object Lock = new();
    public string Path { get; } = path;

    /// <summary>Sets (or, with a null value, removes) keys written <c>Section:Key</c>; the file is replaced in one piece, rights 600.</summary>
    public void Set(IReadOnlyDictionary<string, object?> values)
    {
        lock (Lock)
        {
            var root = File.Exists(Path)
                ? JsonNode.Parse(File.ReadAllText(Path), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject()
                : new JsonObject();
            foreach (var (key, value) in values)
            {
                var parts = key.Split(':');
                var node = root;
                foreach (var part in parts[..^1])
                    node = node[part] as JsonObject ?? (JsonObject)(node[part] = new JsonObject());
                if (value == null) node.Remove(parts[^1]);
                else node[parts[^1]] = value switch { string s => JsonValue.Create(s), int i => JsonValue.Create(i), bool b => JsonValue.Create(b), _ => JsonValue.Create(value.ToString()) };
            }
            AtomicFile.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the folder rights still apply */ }
        }
    }
}

/// <summary>What an administrator can be told about by mail; each kind can be turned on or off.</summary>
public enum NotifyKind { Peers, Abuse, Access, System }

/// <summary>Body of POST {admin}/smtp: the fields to change (null = unchanged; an empty text clears <c>user</c> and <c>password</c>).</summary>
public sealed record SmtpRequest(string? Host, int? Port, string? Tls, string? User, string? Password, string? From, string? To, string? Prefix);

/// <summary>Result of a mail attempt: where it stopped (<c>connect</c>, <c>authenticate</c>, <c>send</c>, <c>settings</c>) and what the SMTP server said.</summary>
public sealed record MailResult(bool Ok, string Stage, string Message);

/// <summary>
/// The SMTP account and the notification switches of one server, in memory and in <c>appsettings.Local.json</c> (<c>Smtp:*</c>, <c>Notify:*</c>):
/// changed by the admin commands without a restart. The password is written there and never read back or shown.
/// </summary>
public sealed class MailSettings
{
    public const string DefaultPrefix = "[{kind}] {host}";
    private static readonly string[] TlsModes = ["starttls", "implicit", "none"];

    private readonly object _lock = new();
    private readonly LocalConfigFile _file;
    private readonly ILogger<MailSettings> _logger;
    private string _host = "", _tls = "starttls", _user = "", _password = "", _from = "", _to = "", _prefix = DefaultPrefix;
    private int _port = 587;
    private bool _enabled;
    private readonly Dictionary<NotifyKind, bool> _kinds = Enum.GetValues<NotifyKind>().ToDictionary(k => k, _ => true);

    /// <summary>How this server calls itself in a subject: <c>RELAY</c> or <c>PUSH</c>; and its name (the host of <c>Peer:PublicUrl</c>, else the machine).</summary>
    public string KindLabel { get; }
    public string HostLabel => Uri.TryCreate(_peer.PublicUrl, UriKind.Absolute, out var u) ? u.Host : Environment.MachineName;
    private readonly PeerOptions _peer;

    public MailSettings(IConfiguration config, LocalConfigFile file, PeerOptions peer, ILogger<MailSettings> logger)
    {
        _file = file;
        _logger = logger;
        _peer = peer;
        KindLabel = peer.Kind.ToUpperInvariant();
        _host = config["Smtp:Host"] ?? "";
        _port = config.GetValue("Smtp:Port", 587);
        _tls = (config["Smtp:Tls"] ?? "starttls").ToLowerInvariant();
        _user = config["Smtp:User"] ?? "";
        _password = config["Smtp:Password"] ?? "";
        _from = config["Smtp:From"] ?? "";
        _to = config["Smtp:To"] ?? "";
        _prefix = config["Smtp:SubjectPrefix"] is { Length: > 0 } p ? p : DefaultPrefix;
        _enabled = config.GetValue("Notify:Enabled", false);
        foreach (var kind in Enum.GetValues<NotifyKind>()) _kinds[kind] = config.GetValue($"Notify:{kind}", true);
    }

    public sealed record Account(string Host, int Port, string Tls, string User, string Password, string From, IReadOnlyList<string> To);

    /// <summary>The account as it is now; null while host, sender or recipient is missing, or a user is named without a password.</summary>
    public Account? Current()
    {
        lock (_lock)
        {
            var to = Recipients(_to);
            if (_host.Length == 0 || _from.Length == 0 || to.Count == 0 || (_user.Length > 0 && _password.Length == 0)) return null;
            return new Account(_host, _port, _tls, _user, _password, _from, to);
        }
    }

    /// <summary>An address with a name before the @ and a domain after it (MimeKit alone also accepts a bare word).</summary>
    public static bool IsEmail(string text) => MailboxAddress.TryParse(text, out var m) && m.Address.IndexOf('@') is > 0 && !m.Address.EndsWith('@');

    public static IReadOnlyList<string> Recipients(string text) => text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The subject: the prefix (<c>{kind}</c> and <c>{host}</c> replaced) and the event, on one line.</summary>
    public string Subject(string title)
    {
        string prefix;
        lock (_lock) prefix = _prefix.Replace("{kind}", KindLabel).Replace("{host}", HostLabel);
        return (prefix + " " + title).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    /// <summary>True when a mail about <paramref name="kind"/> should be sent: notifications on, that kind on, and an account that works on paper.</summary>
    public bool Wants(NotifyKind kind) { lock (_lock) return _enabled && _kinds[kind] && Current() != null; }

    public object Snapshot()
    {
        lock (_lock)
            return new
            {
                host = _host, port = _port, tls = _tls, user = _user, password_set = _password.Length > 0, from = _from, to = Recipients(_to), prefix = _prefix,
                subject_example = Subject("Link request waiting"), configured = Current() != null,
                notify = new { enabled = _enabled, peers = _kinds[NotifyKind.Peers], abuse = _kinds[NotifyKind.Abuse], access = _kinds[NotifyKind.Access], system = _kinds[NotifyKind.System] },
            };
    }

    public void Update(SmtpRequest r)
    {
        if (r.Host != null && (r.Host.Length > 253 || r.Host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']')))) throw new ArgumentException("host must be a name or an address");
        if (r.Port is < 1 or > 65535) throw new ArgumentException("port must be between 1 and 65535");
        if (r.Tls != null && !TlsModes.Contains(r.Tls.ToLowerInvariant())) throw new ArgumentException("tls must be starttls, implicit or none");
        if (r.User != null && (r.User.Length > 256 || r.User.Any(char.IsControl))) throw new ArgumentException("user is not valid");
        if (r.Password != null && (r.Password.Length > 256 || r.Password.Any(char.IsControl))) throw new ArgumentException("password is not valid");
        if (r.From != null && r.From.Length > 0 && !IsEmail(r.From)) throw new ArgumentException("from must be an e-mail address");
        if (r.To != null && Recipients(r.To).Any(a => !IsEmail(a))) throw new ArgumentException("to must be e-mail addresses separated by commas");
        if (r.Prefix != null && (r.Prefix.Length > 80 || r.Prefix.Any(char.IsControl))) throw new ArgumentException("prefix must be one line of at most 80 characters");

        var changes = new Dictionary<string, object?>();
        lock (_lock)
        {
            if (r.Host != null) { _host = r.Host; changes["Smtp:Host"] = r.Host.Length == 0 ? null : r.Host; }
            if (r.Port is { } port) { _port = port; changes["Smtp:Port"] = port; }
            if (r.Tls != null) { _tls = r.Tls.ToLowerInvariant(); changes["Smtp:Tls"] = _tls; }
            if (r.User != null) { _user = r.User; changes["Smtp:User"] = r.User.Length == 0 ? null : r.User; }
            if (r.Password != null) { _password = r.Password; changes["Smtp:Password"] = r.Password.Length == 0 ? null : r.Password; }
            if (r.From != null) { _from = r.From; changes["Smtp:From"] = r.From.Length == 0 ? null : r.From; }
            if (r.To != null) { _to = string.Join(", ", Recipients(r.To)); changes["Smtp:To"] = _to.Length == 0 ? null : _to; }
            if (r.Prefix != null) { _prefix = r.Prefix.Length == 0 ? DefaultPrefix : r.Prefix; changes["Smtp:SubjectPrefix"] = r.Prefix.Length == 0 ? null : r.Prefix; }
            _file.Set(changes);
        }
        _logger.LogInformation("SMTP settings changed: {Fields}", string.Join(", ", changes.Keys.Select(k => k.Replace("Smtp:", "").ToLowerInvariant())));
    }

    /// <summary>Forgets the whole account and turns the notifications off.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            (_host, _tls, _user, _password, _from, _to, _prefix, _port, _enabled) = ("", "starttls", "", "", "", "", DefaultPrefix, 587, false);
            _file.Set(new Dictionary<string, object?>
            {
                ["Smtp:Host"] = null, ["Smtp:Port"] = null, ["Smtp:Tls"] = null, ["Smtp:User"] = null, ["Smtp:Password"] = null,
                ["Smtp:From"] = null, ["Smtp:To"] = null, ["Smtp:SubjectPrefix"] = null, ["Notify:Enabled"] = null,
            });
        }
        _logger.LogInformation("SMTP settings cleared");
    }

    /// <summary>Turns the notifications on or off as a whole (<paramref name="kind"/> null) or one kind of them.</summary>
    public void SetNotify(NotifyKind? kind, bool on)
    {
        lock (_lock)
        {
            if (kind is { } k) { _kinds[k] = on; _file.Set(new Dictionary<string, object?> { [$"Notify:{k}"] = on }); }
            else { _enabled = on; _file.Set(new Dictionary<string, object?> { ["Notify:Enabled"] = on }); }
        }
        _logger.LogInformation("Notifications {What} {State}", kind?.ToString().ToLowerInvariant() ?? "(all)", on ? "on" : "off");
    }
}

/// <summary>Sends one mail with the account of <see cref="MailSettings"/> and says where it went wrong, if it did. Never puts the password in a message.</summary>
public sealed class Mailer(ILogger<Mailer> logger, IConfiguration config)
{
    /// <summary>The longest a whole attempt (connection, TLS, login, sending) may take: <c>Smtp:TimeoutSeconds</c>, 20 by default.</summary>
    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Smtp:TimeoutSeconds", 20), 1, 120));

    public async Task<MailResult> SendAsync(MailSettings settings, string title, string body, string? to = null, CancellationToken ct = default)
    {
        var account = settings.Current();
        if (account == null) return new MailResult(false, "settings", "host, from and to must be set (and a password when a user is named): see 'smtp show'");
        IReadOnlyList<string> recipients = to is { Length: > 0 } ? [to] : account.To;

        MimeMessage message;
        try
        {
            message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(account.From));
            foreach (var r in recipients) message.To.Add(MailboxAddress.Parse(r));
            message.Subject = settings.Subject(title);
            message.Body = new TextPart("plain") { Text = body };
        }
        catch (ParseException ex) { return new MailResult(false, "settings", "not an e-mail address: " + ex.Message); }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        var caller = ct;
        ct = limit.Token;
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        var stage = "connect";
        try
        {
            var security = account.Tls switch { "implicit" => SecureSocketOptions.SslOnConnect, "none" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
            await client.ConnectAsync(account.Host, account.Port, security, ct);
            stage = "authenticate";
            if (account.User.Length > 0) await client.AuthenticateAsync(account.User, account.Password, ct);
            stage = "send";
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, CancellationToken.None);
            return new MailResult(true, "send", $"sent to {string.Join(", ", recipients)}");
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            var silent = $"no answer from {account.Host}:{account.Port} within {Timeout.TotalSeconds:0} s (wrong host or port, or a firewall?)";
            logger.LogWarning("Mail failed at {Stage}: {Message}", stage, silent);
            return new MailResult(false, stage, silent);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var what = ex switch
            {
                TimeoutException => $"no answer from {account.Host}:{account.Port} (timed out: wrong host or port, or a firewall?)",
                SslHandshakeException => "TLS handshake failed (wrong port or tls mode?): " + ex.Message,
                AuthenticationException => "authentication refused: " + ex.Message,
                SmtpCommandException c => $"the SMTP server answered {(int)c.StatusCode}: {c.Message}",
                _ => ex.Message,
            };
            logger.LogWarning("Mail failed at {Stage}: {Message}", stage, what);
            return new MailResult(false, stage, what);
        }
    }
}

/// <summary>
/// Mails to the administrator about what needs them: a request for a link (<c>peers</c>), a detected abuse (<c>abuse</c>), a client waiting for
/// approval on a private server (<c>access</c>), a configuration problem (<c>system</c>). A mail never holds an identifier of a client. To keep a flood
/// of events from becoming a flood of mails, each kind sends at most one mail per <c>Notify:MinIntervalSeconds</c> (default 3600): the first event at
/// once, the following ones counted and sent together when the interval is over.
/// </summary>
public sealed class Notifier : BackgroundService
{
    private sealed class Slot { public DateTime LastSent = DateTime.MinValue; public int Pending; public string? LastTitle, LastDetail; }

    private readonly MailSettings _settings;
    private readonly Mailer _mailer;
    private readonly ILogger<Notifier> _logger;
    private readonly TimeSpan _interval;
    private readonly Dictionary<NotifyKind, Slot> _slots = Enum.GetValues<NotifyKind>().ToDictionary(k => k, _ => new Slot());

    /// <summary>The notifier of this process, if the server has one; events raised before it exists are dropped.</summary>
    public static Notifier? Current { get; private set; }

    public Notifier(MailSettings settings, Mailer mailer, IConfiguration config, ILogger<Notifier> logger)
    {
        (_settings, _mailer, _logger) = (settings, mailer, logger);
        _interval = TimeSpan.FromSeconds(Math.Max(0, config.GetValue("Notify:MinIntervalSeconds", 3600)));
        Current = this;
    }

    /// <summary>Reports an event. Cheap and safe to call from anywhere: nothing happens unless notifications are on for this kind and the account is set.</summary>
    public static void Raise(NotifyKind kind, string title, string detail) => Current?.RaiseCore(kind, title, detail);

    private void RaiseCore(NotifyKind kind, string title, string detail)
    {
        if (!_settings.Wants(kind)) return;
        var slot = _slots[kind];
        bool now;
        lock (slot)
        {
            now = DateTime.UtcNow - slot.LastSent >= _interval;
            if (now) { slot.LastSent = DateTime.UtcNow; slot.Pending = 0; }
            else { slot.Pending++; slot.LastTitle = title; slot.LastDetail = detail; }
        }
        if (now) _ = SendAsync(title, detail);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = _interval < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
            foreach (var (kind, slot) in _slots)
            {
                string? title = null, detail = null;
                lock (slot)
                    if (slot.Pending > 0 && DateTime.UtcNow - slot.LastSent >= _interval)
                    {
                        (title, detail) = ($"{slot.LastTitle} (+{slot.Pending} more)", $"{slot.Pending} more event(s) of this kind since the last mail. Last one:\n{slot.LastDetail}");
                        slot.LastSent = DateTime.UtcNow; slot.Pending = 0;
                    }
                if (title != null && _settings.Wants(kind)) await SendAsync(title, detail!);
            }
        }
    }

    private async Task SendAsync(string title, string detail)
    {
        try { await _mailer.SendAsync(_settings, title, Body(detail)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning("Notification not sent: {Message}", ex.Message); }
    }

    /// <summary>A mail from the administrator's command line (<c>notify send</c>): not counted, not held back.</summary>
    public Task<MailResult> SendNowAsync(string title, string message, string? to = null, CancellationToken ct = default) => _mailer.SendAsync(_settings, title, Body(message), to, ct);

    private string Body(string detail) => $"{detail}\n\n--\n{_settings.KindLabel} {_settings.HostLabel}, version {ReleaseInfo.Describe()}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n";
}

/// <summary>What the SMTP settings need to work, for the <c>check</c> command and the alarm at start-up.</summary>
public static class MailChecks
{
    public static IReadOnlyList<ConfigIssue> Issues(IConfiguration c)
    {
        var issues = new List<ConfigIssue>();
        if (!string.IsNullOrWhiteSpace(c["Smtp:User"]) && string.IsNullOrEmpty(c["Smtp:Password"]))
            issues.Add(new ConfigIssue("Smtp:Password", "is not set although Smtp:User is — no mail can be sent", "admin smtp set password"));
        if (c.GetValue("Notify:Enabled", false) && (string.IsNullOrWhiteSpace(c["Smtp:Host"]) || string.IsNullOrWhiteSpace(c["Smtp:From"]) || string.IsNullOrWhiteSpace(c["Smtp:To"])))
            issues.Add(new ConfigIssue("Smtp", "notifications are on but host, from or to is missing — nothing is sent", "admin smtp set host|from|to <value>, then admin smtp test"));
        return issues;
    }
}

public static class MailExtensions
{
    /// <summary>Tells the administrator by mail (kind <c>system</c>) what the start-up check found, when notifications are on.</summary>
    public static void NotifyIssues(this IServiceProvider services, string appName, IReadOnlyList<ConfigIssue> issues)
    {
        if (issues.Count == 0) return;
        _ = services.GetRequiredService<Notifier>();   // created now so that the event is not dropped
        Notifier.Raise(NotifyKind.System, "Configuration problem", $"{appName} is not fully configured:\n" + string.Join("\n", issues.Select(i => $"- {i.Key} {i.Problem}. To fix: {i.Fix}")));
    }

    /// <summary>Registers the SMTP settings, the mailer and the notifier of a server. Call after <c>AddPeers</c> (the subject names the kind of server).</summary>
    public static IHostApplicationBuilder AddMail(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(new LocalConfigFile(System.IO.Path.Combine(builder.Environment.ContentRootPath, ConfigCheck.LocalFile)));
        builder.Services.AddSingleton<MailSettings>();
        builder.Services.AddSingleton<Mailer>();
        builder.Services.AddSingleton<Notifier>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
        return builder;
    }
}
