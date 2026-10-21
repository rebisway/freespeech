/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>A tiny SMTP server for the tests: keeps the messages it is given, and can refuse the recipient.</summary>
public sealed class FakeSmtp : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Messages { get; } = [];
    public bool RefuseRecipient { get; set; }
    /// <summary>Accepts the connection and says nothing: a host that does not answer.</summary>
    public bool Silent { get; set; }

    public FakeSmtp()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Task.Run(() => Serve(client)); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
            }
        });
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        if (Silent) { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token); return; }
        await writer.WriteLineAsync("220 fake ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO": await writer.WriteLineAsync("250-fake"); await writer.WriteLineAsync("250 AUTH PLAIN LOGIN"); break;
                case "AUTH": await writer.WriteLineAsync("235 2.7.0 ok"); break;
                case "RCPT": await writer.WriteLineAsync(RefuseRecipient ? "550 5.1.1 no such mailbox here" : "250 ok"); break;
                case "DATA":
                    await writer.WriteLineAsync("354 go");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } l && l != ".") data.AppendLine(l);
                    lock (Messages) Messages.Add(data.ToString());
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                default: await writer.WriteLineAsync("250 ok"); break;
            }
        }
    }

    public async Task<string> NextAsync(int already, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            lock (Messages) if (Messages.Count > already) return Messages[already];
            await Task.Delay(50);
        }
        throw new TimeoutException("no mail arrived");
    }

    public int Count { get { lock (Messages) return Messages.Count; } }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

/// <summary>
/// The SMTP account and the notifications of a server, and the export/import of its state, driven through the push gateway (the relay shares the code):
/// a password that is written and never shown, a test mail whose answer says where it failed, one mail per kind and per interval, and a state that
/// comes back on another machine.
/// </summary>
public class NotifyTests : IAsyncLifetime
{
    private const string Admin = "notify-admin-secret";
    private static readonly string Dll = TestProcess.Dll("freespeech.push", "FreeSpeech.Push.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-notify-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_root, "state");
    private string Content => Path.Combine(_root, "content");
    private readonly FakeSmtp _smtp = new();
    private TestProcess _gateway = null!;

    private Dictionary<string, string> Env(string? state = null) => new()
    {
        ["Push__Fake"] = "true", ["Push__StatePath"] = state ?? State, ["Admin__Secret"] = Admin, ["ASPNETCORE_CONTENTROOT"] = Content,
        ["Peer__PublicUrl"] = "https://push2.example.org", ["Notify__MinIntervalSeconds"] = "3", ["Smtp__TimeoutSeconds"] = "3",
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Content);
        File.WriteAllText(Path.Combine(Content, "appsettings.Local.json"), """{ "Keep": { "Me": "yes" } }""");
        _gateway = await TestProcess.StartAsync(Dll, Env());
    }

    public Task DisposeAsync()
    {
        _gateway.Dispose();
        _smtp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> Call(HttpMethod method, string path, object? body = null)
    {
        using var client = new HttpClient();
        var req = new HttpRequestMessage(method, _gateway.Http + path);
        req.Headers.Add("X-Admin-Secret", Admin);
        if (body != null) req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task ConfigureSmtp(string password = "correct-horse-battery") =>
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/admin/smtp", new { host = "127.0.0.1", port = _smtp.Port, tls = "none", user = "robot", password, from = "freespeech@example.org", to = "admin@example.org" })).StatusCode);

    /// <summary>A relay asks the gateway for a link (a signed request, no address): this is what the administrator is told about.</summary>
    private async Task AskForLink(string name)
    {
        var who = PeerIdentity.LoadOrCreate(Path.Combine(_root, name + ".key"));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new PeerRequestBody { Kind = "relay", Name = name, Url = "", PublicKey = who.PublicKey, Ts = ts, Sig = who.Sign(PeerSigning.RequestText("relay", name, "", ts)) };
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(_gateway.Http + "/peer/request", body)).StatusCode);
    }

    [Fact]
    public async Task The_password_is_written_to_the_local_file_and_never_shown()
    {
        await ConfigureSmtp("never-shown-password");
        var shown = await (await Call(HttpMethod.Get, "/admin/smtp")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("never-shown-password", shown);
        var snapshot = JsonDocument.Parse(shown).RootElement;
        Assert.True(snapshot.GetProperty("password_set").GetBoolean());
        Assert.True(snapshot.GetProperty("configured").GetBoolean());
        Assert.Equal("[PUSH] push2.example.org Link request waiting", snapshot.GetProperty("subject_example").GetString());

        var file = File.ReadAllText(Path.Combine(Content, "appsettings.Local.json"));
        Assert.Contains("never-shown-password", file);   // the one place of the secrets
        Assert.Contains("\"Me\": \"yes\"", file);        // the other keys are kept

        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/admin/smtp", new { tls = "ssl3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/admin/smtp", new { prefix = "two\nlines" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/admin/smtp", new { to = "not-an-address" })).StatusCode);
    }

    [Fact]
    public async Task The_test_mail_answers_where_it_stopped()
    {
        await ConfigureSmtp();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/admin/smtp", new { prefix = "[relay.exemple.org]" })).StatusCode);

        var ok = await Json(await Call(HttpMethod.Post, "/admin/smtp/test", new { }));
        Assert.True(ok.GetProperty("ok").GetBoolean(), ok.ToString());
        var mail = await _smtp.NextAsync(0);
        Assert.Contains("Subject: [relay.exemple.org] SMTP test", mail);
        Assert.Contains("admin@example.org", mail);

        _smtp.RefuseRecipient = true;
        var refused = await Json(await Call(HttpMethod.Post, "/admin/smtp/test", new { to = "someone@example.org" }));
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("send", refused.GetProperty("stage").GetString());
        Assert.Contains("550", refused.GetProperty("message").GetString());

        await Call(HttpMethod.Post, "/admin/smtp", new { port = 1 });
        var unreachable = await Json(await Call(HttpMethod.Post, "/admin/smtp/test", new { }));
        Assert.False(unreachable.GetProperty("ok").GetBoolean());
        Assert.Equal("connect", unreachable.GetProperty("stage").GetString());

        // The command line prints the same and exits 4 when the mail did not go.
        var (code, output) = await TestProcess.AdminCliAsync(Dll, _gateway.Http, Admin, "smtp", "test");
        Assert.Equal(4, code);
        Assert.Contains("FAILED at connect", output);

        // Nothing configured: the answer says what is missing instead of failing obscurely.
        await Call(HttpMethod.Delete, "/admin/smtp");
        var none = await Json(await Call(HttpMethod.Post, "/admin/smtp/test", new { }));
        Assert.Equal("settings", none.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task A_host_that_does_not_answer_is_reported_instead_of_hanging()
    {
        using var silent = new FakeSmtp { Silent = true };
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/admin/smtp", new { host = "127.0.0.1", port = silent.Port, tls = "none", from = "freespeech@example.org", to = "admin@example.org" })).StatusCode);

        var started = DateTime.UtcNow;
        var result = await Json(await Call(HttpMethod.Post, "/admin/smtp/test", new { }));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("connect", result.GetProperty("stage").GetString());
        Assert.Contains("no answer", result.GetProperty("message").GetString());
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 2, 10);   // Smtp:TimeoutSeconds, not forever

        var (code, output) = await TestProcess.AdminCliAsync(Dll, _gateway.Http, Admin, "smtp", "test");
        Assert.Equal(4, code);
        Assert.Contains("FAILED at connect: no answer", output);   // the command line waits for that answer
    }

    [Fact]
    public async Task A_link_request_is_mailed_once_per_interval_and_only_when_asked_for()
    {
        await ConfigureSmtp();

        await AskForLink("quiet");   // notifications are off: nothing is sent
        await Task.Delay(1000);
        Assert.Equal(0, _smtp.Count);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/admin/notify", new { on = true })).StatusCode);
        await AskForLink("first");
        var first = await _smtp.NextAsync(0);
        Assert.Contains("Subject: [PUSH] push2.example.org Link request waiting", first);
        Assert.Contains("Name: first", first);
        Assert.Contains("Fingerprint:", first);
        Assert.Contains("Contact it gives:", first);   // the mail says how to check who is asking
        Assert.Contains("Request seen from:", first);

        await AskForLink("second");
        await AskForLink("third");
        var digest = await _smtp.NextAsync(1, seconds: 15);   // after the interval, the two others in one mail
        Assert.Contains("(+2 more)", digest);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/admin/notify", new { on = false, kind = "peers" })).StatusCode);
        var settled = _smtp.Count;
        await Task.Delay(3500);
        await AskForLink("fourth");
        await Task.Delay(1500);
        Assert.Equal(settled, _smtp.Count);   // that kind is off

        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/admin/notify", new { on = true, kind = "everything" })).StatusCode);
    }

    [Fact]
    public async Task The_state_is_exported_encrypted_and_imported_on_another_machine()
    {
        await AskForLink("someone");   // a peer request is part of the state (peers.json)
        using var response = await Call(HttpMethod.Post, "/admin/export", new { passphrase = "a long passphrase" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var archive = await response.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("peer-identity", Encoding.UTF8.GetString(archive));   // encrypted: nothing readable in it
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/admin/export", new { passphrase = "short" })).StatusCode);

        var file = Path.Combine(_root, "state.fsstate");
        File.WriteAllBytes(file, archive);
        var other = Path.Combine(_root, "other-state");
        var env = Env(other);
        env["FS_STATE_PASSPHRASE"] = "a long passphrase";

        var (code, output) = await Run(env, "import", file);
        Assert.True(code == 0, output);
        Assert.Equal(File.ReadAllText(Path.Combine(State, "peer-identity.json")), File.ReadAllText(Path.Combine(other, "peer-identity.json")));
        Assert.Equal(File.ReadAllText(Path.Combine(State, "peers.json")), File.ReadAllText(Path.Combine(other, "peers.json")));

        Assert.Equal(4, (await Run(env, "import", file)).Code);                 // a state is there: not replaced
        Assert.Equal(0, (await Run(env, "import", file, "--force")).Code);     // unless asked
        Assert.True(File.Exists(Path.Combine(other, "peers.json.before-import")));

        env["FS_STATE_PASSPHRASE"] = "another passphrase";
        Assert.Equal(4, (await Run(env, "import", file, "--force")).Code);     // wrong passphrase
    }

    [Fact]
    public async Task The_relay_exports_its_state_too()
    {
        var state = Path.Combine(_root, "relay-state");
        Directory.CreateDirectory(state);
        using var relay = await TestProcess.StartAsync(TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll"), new Dictionary<string, string>
        {
            ["Admin__Secret"] = Admin, ["Server__AccessFilePath"] = Path.Combine(state, "access.json"), ["Server__SiblingsFilePath"] = Path.Combine(state, "siblings.json"),
            ["Server__LimitsFilePath"] = Path.Combine(state, "limits.json"), ["ASPNETCORE_CONTENTROOT"] = Content,
        }, healthPath: "/api/time");

        using var client = new HttpClient();
        HttpRequestMessage Req(object body) { var r = new HttpRequestMessage(HttpMethod.Post, relay.Http + "/api/admin/export") { Content = JsonContent.Create(body) }; r.Headers.Add("X-Admin-Secret", Admin); return r; }
        using var ok = await client.SendAsync(Req(new { passphrase = "a long passphrase" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await ok.Content.ReadAsByteArrayAsync()).Length > 100);
        Assert.True(File.Exists(Path.Combine(state, "peer-identity.json")));   // the key of the relay is part of what is saved
        using var weak = await client.SendAsync(Req(new { passphrase = "short" }));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    private static async Task<(int Code, string Output)> Run(Dictionary<string, string> env, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Dll);
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (k, v) in env) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
