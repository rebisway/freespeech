/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreeSpeech.Tests;

/// <summary>
/// What the console changes while a relay runs, and what must survive a restart: the service settings, the name and the public address it
/// presents to other servers.
/// </summary>
public class RelaySettingsTests : IDisposable
{
    private const string Admin = "settings-admin-secret";
    private static readonly string Dll = TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private Dictionary<string, string> Env()
    {
        var state = Path.Combine(_root, "state");
        var content = Path.Combine(_root, "content");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(content);
        return new()
        {
            ["Admin__Secret"] = Admin, ["ASPNETCORE_CONTENTROOT"] = content, ["Server__MaxConnections"] = "50",
            ["Server__AccessFilePath"] = Path.Combine(state, "access.json"), ["Server__SiblingsFilePath"] = Path.Combine(state, "siblings.json"),
            ["Server__LimitsFilePath"] = Path.Combine(state, "limits.json"), ["Server__SettingsFilePath"] = Path.Combine(state, "settings.json"),
        };
    }

    private static async Task<JsonElement> Call(TestProcess server, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(method, server.Http + path);
        request.Headers.Add("X-Admin-Secret", Admin);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Settings_change_at_once_stay_after_a_restart_and_reset_to_the_configuration()
    {
        var env = Env();
        using (var relay = await TestProcess.StartAsync(Dll, env, "/api/time"))
        {
            var start = await Call(relay, HttpMethod.Get, "/api/admin/settings");
            Assert.Equal(50, start.GetProperty("max_connections").GetInt32());   // the configuration
            Assert.Empty(start.GetProperty("changed").EnumerateArray());

            var changed = await Call(relay, HttpMethod.Post, "/api/admin/settings", new { max_connections = 7, source_url = "https://git.example.org/freespeech" });
            Assert.Equal(7, changed.GetProperty("max_connections").GetInt32());
            Assert.Equal(["max_connections", "source_url"], changed.GetProperty("changed").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(7, (await Call(relay, HttpMethod.Get, "/api/admin/status")).GetProperty("max_connections").GetInt32());   // used by the server, not just stored
            Assert.Equal("https://git.example.org/freespeech", (await Call(relay, HttpMethod.Get, "/api/info")).GetProperty("source").GetString());

            Assert.Equal("CA", (await Call(relay, HttpMethod.Post, "/api/admin/settings", new { country = "ca" })).GetProperty("country").GetString());   // a code, told to the directories
            await Call(relay, HttpMethod.Post, "/api/admin/settings", new { country = "Canada" }, HttpStatusCode.BadRequest);
            await Call(relay, HttpMethod.Post, "/api/admin/settings", new { max_message_bytes = 10 }, HttpStatusCode.BadRequest);
            await Call(relay, HttpMethod.Post, "/api/admin/settings", new { source_url = "ftp://nope" }, HttpStatusCode.BadRequest);
            Assert.Equal(7, (await Call(relay, HttpMethod.Get, "/api/admin/settings")).GetProperty("max_connections").GetInt32());   // a refused change changes nothing
        }

        using (var again = await TestProcess.StartAsync(Dll, env, "/api/time"))
        {
            var kept = await Call(again, HttpMethod.Get, "/api/admin/settings");
            Assert.Equal(7, kept.GetProperty("max_connections").GetInt32());   // kept across the restart
            Assert.Equal(50, kept.GetProperty("defaults").GetProperty("max_connections").GetInt32());

            var (code, output) = await TestProcess.AdminCliAsync(Dll, again.Http, Admin, "settings", "set", "max-connections", "9");
            Assert.True(code == 0, output);
            Assert.Equal(9, (await Call(again, HttpMethod.Get, "/api/admin/settings")).GetProperty("max_connections").GetInt32());

            var reset = await Call(again, HttpMethod.Delete, "/api/admin/settings");
            Assert.Equal(50, reset.GetProperty("max_connections").GetInt32());
            Assert.Empty(reset.GetProperty("changed").EnumerateArray());
        }
    }

    [Fact]
    public async Task The_name_and_public_address_change_at_once_and_stay_after_a_restart()
    {
        var env = Env();
        using (var relay = await TestProcess.StartAsync(Dll, env, "/api/time"))
        {
            var (keyCode, keyOutput) = await TestProcess.AdminCliAsync(Dll, relay.Http, Admin, "key");
            Assert.Equal(0, keyCode);
            Assert.Equal((await Call(relay, HttpMethod.Get, "/peer/identity")).GetProperty("public_key").GetString(), keyOutput.Trim());   // the key alone, as the other servers read it

            var self = await Call(relay, HttpMethod.Post, "/api/admin/self", new { name = "fs2", publicUrl = "https://fs2.example.org/" });
            Assert.Equal("fs2", self.GetProperty("name").GetString());
            Assert.Equal("https://fs2.example.org", self.GetProperty("public_url").GetString());
            Assert.Equal("https://fs2.example.org", (await Call(relay, HttpMethod.Get, "/api/admin/status")).GetProperty("public_url").GetString());   // the status says it at once
            Assert.Equal("fs2", (await Call(relay, HttpMethod.Get, "/peer/identity")).GetProperty("name").GetString());                                // and so do the other servers

            await Call(relay, HttpMethod.Post, "/api/admin/self", new { publicUrl = "http://far.example.org" }, HttpStatusCode.BadRequest);   // https, or a private network
            await Call(relay, HttpMethod.Post, "/api/admin/self", new { name = "" }, HttpStatusCode.BadRequest);
        }

        using (var again = await TestProcess.StartAsync(Dll, env, "/api/time"))
        {
            var self = await Call(again, HttpMethod.Get, "/api/admin/self");
            Assert.Equal("fs2", self.GetProperty("name").GetString());
            Assert.Equal("https://fs2.example.org", self.GetProperty("public_url").GetString());
        }
    }
}
