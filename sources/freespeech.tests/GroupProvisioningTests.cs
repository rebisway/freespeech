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
using FreeSpeech.Client;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>
/// Groups of a private server: the administrator's tool (the console) is an identity the server names; it builds a group from the public keys the server kept
/// for its allowed members, and each member gets an invitation from someone they have never met, to accept with one gesture.
/// </summary>
public class GroupProvisioningTests : IClassFixture<ServerFixture>
{
    private const string Secret = "admin-test-secret";
    private readonly ServerFixture _server;

    public GroupProvisioningTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<HttpResponseMessage> Admin(HttpMethod method, string path, object? body = null)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(method, _server.Http + path) { Content = body == null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Admin-Secret", Secret);
        return await http.SendAsync(request);
    }

    private FreeSpeechClient NewClient(string dir, string name, string tag)
    {
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(dir, name), ServerUrl = _server.Http });
        c.CreateIdentity(Pad(tag + " 1"), Pad(tag + " 2"), Pad(tag + " 3"), name);
        c.Initialize();
        return c;
    }

    [Fact]
    public async Task TheAdministratorToolBuildsAGroup_andTheMemberAcceptsItWithoutKnowingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-prov-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/access/mode", new { mode = "allowlist" })).StatusCode);
        try
        {
            await using var console = NewClient(dir, "Administration", "prov console");
            await using var anna = NewClient(dir, "Anna", "prov anna");
            await using var stranger = NewClient(dir, "Stranger", "prov stranger");
            var annaId = anna.Identity.DiscoveryId;

            // The server allows Anna, and names the console's identity as its administrator tool (which also lets it register).
            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients", new { id = annaId, note = "Anna" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/provisioners", new { id = console.Identity.DiscoveryId })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/clients", new { id = stranger.Identity.DiscoveryId, note = "Stranger" })).StatusCode);

            // Before Anna has connected, the server has no keys for her; once she has, it keeps them (public data) for the administrator's tools.
            Assert.DoesNotContain(annaId, await (await Admin(HttpMethod.Get, "/api/admin/clients/keys")).Content.ReadAsStringAsync());
            await anna.ConnectAsync(); await console.ConnectAsync(); await stranger.ConnectAsync();
            var keys = (await (await Admin(HttpMethod.Get, "/api/admin/clients/keys")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
            var entry = keys.Single(k => k.GetProperty("id").GetString() == annaId);

            var member = new GroupMember
            {
                DiscoveryId = annaId, Nickname = "Anna",
                EncryptionPublicKey = entry.GetProperty("enc_key").GetString()!, SigningPublicKey = entry.GetProperty("sign_key").GetString()!,
                Servers = [new ServerEntry { Url = _server.Http, Priority = 0 }],
            };
            var group = await console.Groups.CreateAsync("Accounting", [member]);

            // Anna is invited by someone she does not know, but whom her server names: the invitation waits for her answer, marked as coming from the administrator.
            Assert.True(await UntilAsync(() => anna.Groups.PendingGroupInvites.Count == 1), "the invitation did not arrive");
            var invite = anna.Groups.PendingGroupInvites.Single();
            Assert.True(invite.FromAdministrator);
            Assert.Equal("Accounting", invite.Name);
            Assert.Empty(anna.Groups.Store.All);   // nothing joined before she answers

            var joined = await anna.Groups.AcceptInvitationAsync(group.GroupId);
            Assert.Equal("Accounting", joined.Name);
            Assert.True(anna.Groups.IsMember(group.GroupId, annaId));

            // Somebody who is neither a contact nor named by the server is still ignored: Anna hears no invitation from the stranger.
            var annaMember = member;
            var strangerGroup = await stranger.Groups.CreateAsync("Not welcome", [annaMember]);
            await Task.Delay(1500);
            Assert.DoesNotContain(anna.Groups.PendingGroupInvites, i => i.GroupId == strangerGroup.GroupId);
        }
        finally
        {
            await Admin(HttpMethod.Post, "/api/admin/access/mode", new { mode = "open" });
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
