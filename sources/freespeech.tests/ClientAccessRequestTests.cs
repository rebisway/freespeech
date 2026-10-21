/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>
/// Access requests (PROTOCOL §10 "Access requests"): an unknown identity of a private server leaves its name after the proof of its
/// keys, waits in a queue, and the administrator accepts or rejects it — one by one, in bulk, by command line or API.
/// </summary>
public partial class ClientAccessTests
{
    private async Task ClearRequests() => await Admin(HttpMethod.Post, "/api/admin/access/requests/reject", new { all = true });

    private async Task<JsonElement> Requests(string query = "")
    {
        var response = await Admin(HttpMethod.Get, "/api/admin/access/requests" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> Waiting() => (await Requests()).GetProperty("total").GetInt32();

    private async Task<JsonElement> Decide(string action, object body)
    {
        var response = await Admin(HttpMethod.Post, "/api/admin/access/requests/" + action, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task Ask(UserIdentity who, string name, string? note = null) =>
        Assert.Equal("access_pending", Message(await RegisterAsync(who, ask: true, name: name, note: note)));

    [Fact]
    public async Task AccessRequest_isQueuedOnce_andNothingIsStoredUnlessAsked()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var anna = NewIdentity("req-anna");

            Assert.Equal("private_server", Message(await RegisterAsync(anna)));   // no consent, nothing kept
            Assert.Equal(0, await Waiting());

            await Ask(anna, "Anna", "from accounting");
            Assert.Equal("access_pending", Message(await RegisterAsync(anna)));   // the server remembers: no need to send the name again
            var list = await Requests();
            Assert.Equal(1, list.GetProperty("total").GetInt32());
            var first = list.GetProperty("requests")[0];
            Assert.Equal(anna.DiscoveryId, first.GetProperty("id").GetString());
            Assert.Equal("Anna", first.GetProperty("name").GetString());
            Assert.Equal("from accounting", first.GetProperty("note").GetString());

            // The app was closed and reopened and asks again, with a corrected name: the same request is updated, not doubled.
            await Ask(anna, "Anna Martin", null);
            list = await Requests();
            Assert.Equal(1, list.GetProperty("total").GetInt32());
            Assert.Equal("Anna Martin", list.GetProperty("requests")[0].GetProperty("name").GetString());
            Assert.Equal("from accounting", list.GetProperty("requests")[0].GetProperty("note").GetString());   // no new note: the old one stays
            Assert.Contains("\"pending_request_count\":1", await (await Admin(HttpMethod.Get, "/api/admin/status")).Content.ReadAsStringAsync());
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task AccessRequest_needsTheProofOfTheKeys()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var victim = NewIdentity("req-victim");
            var forger = NewIdentity("req-forger");

            // Asks in the name of someone else's identity but signs with its own key: refused, nothing queued.
            using var c = await RawClient.ConnectAsync(_s.WsUri);
            await c.SendAsync(new { type = "register", discovery_id = victim.DiscoveryId, enc_key = Convert.ToBase64String(victim.EncryptionPublicKey), sign_key = Convert.ToBase64String(victim.SigningPublicKey), request_access = true, name = "Victim", request_id = "1" });
            var challenge = await c.ReceiveAsync();
            var data = System.Text.Encoding.UTF8.GetBytes($"freespeech-register-v1|{challenge!.Value.GetProperty("nonce").GetString()}|{victim.DiscoveryId}");
            await c.SendAsync(new { type = "register_proof", signature = Convert.ToBase64String(Primitives.Ed25519Sign(forger.SigningPrivateKey, data)), request_id = "2" });
            Assert.Equal("Invalid signature", Message(await c.ReceiveAsync()));
            Assert.Equal(0, await Waiting());

            // Keys that do not hash to the id are refused before any challenge.
            using var d = await RawClient.ConnectAsync(_s.WsUri);
            await d.SendAsync(new { type = "register", discovery_id = victim.DiscoveryId, enc_key = Convert.ToBase64String(forger.EncryptionPublicKey), sign_key = Convert.ToBase64String(forger.SigningPublicKey), request_access = true, name = "Victim", request_id = "1" });
            Assert.Equal("error", Type(await d.ReceiveAsync()));
            Assert.Equal(0, await Waiting());
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task AccessRequest_textFromAStrangerIsCleanedAndBounded()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var eve = NewIdentity("req-eve");
            await Ask(eve, "Eve\r\n[ERROR] forged line\u001b[31m" + new string('x', 200), new string('n', 500) + "\0");
            var entry = (await Requests()).GetProperty("requests")[0];
            var name = entry.GetProperty("name").GetString()!;
            var note = entry.GetProperty("note").GetString()!;
            Assert.DoesNotContain(name, c => char.IsControl(c));
            Assert.True(name.Length <= 64);
            Assert.StartsWith("Eve[ERROR]", name);
            Assert.True(note.Length <= 200);
            Assert.DoesNotContain(note, c => char.IsControl(c));
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task AcceptingARequest_letsTheIdentityIn_byIdOrByPrefix()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var bob = NewIdentity("req-bob");
            var carl = NewIdentity("req-carl");
            await Ask(bob, "Bob");
            await Ask(carl, "Carl");

            var done = await Decide("accept", new { ids = new[] { bob.DiscoveryId[..8] } });
            Assert.Single(done.GetProperty("accepted").EnumerateArray());
            Assert.Equal("registered", Type(await RegisterAsync(bob)));
            Assert.Equal("access_pending", Message(await RegisterAsync(carl)));   // only the chosen one
            Assert.Equal(1, await Waiting());
            Assert.Contains("Bob", await (await Admin(HttpMethod.Get, "/api/admin/access")).Content.ReadAsStringAsync());   // the name becomes the note of the allowed client

            // Already handled, too short, not hex: reported, nothing else happens.
            var again = await Decide("accept", new { ids = new[] { bob.DiscoveryId[..8], "abc", "zzzzzzzz" } });
            Assert.Empty(again.GetProperty("accepted").EnumerateArray());
            Assert.Equal(3, again.GetProperty("unknown").GetArrayLength());
            Assert.Equal(1, await Waiting());
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task RejectingARequest_canBanTheIdentity()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var dan = NewIdentity("req-dan");
            var fay = NewIdentity("req-fay");
            await Ask(dan, "Dan");
            await Ask(fay, "Fay");

            await Decide("reject", new { ids = new[] { dan.DiscoveryId[..10] } });
            Assert.Equal("private_server", Message(await RegisterAsync(dan)));   // turned down, may ask again
            await Ask(dan, "Dan, again");

            await Decide("reject", new { ids = new[] { fay.DiscoveryId[..10] }, ban = true, reason = "not an employee" });
            Assert.Equal("access_refused", Message(await RegisterAsync(fay)));
            Assert.Equal("access_refused", Message(await RegisterAsync(fay, ask: true, name: "Fay")));   // a banned identity cannot queue up again
            Assert.Contains("not an employee", await (await Admin(HttpMethod.Get, "/api/admin/access")).Content.ReadAsStringAsync());
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task BulkDecisions_followTheFilters_andNeverHappenByAccident()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var team = new[] { NewIdentity("req-team-1"), NewIdentity("req-team-2"), NewIdentity("req-team-3") };
            var boss = NewIdentity("req-boss");
            for (var i = 0; i < team.Length; i++) await Ask(team[i], $"Team A {i + 1}");
            await Ask(boss, "Boss", "needs Team A access");   // the filter looks at the note too — on purpose, so a name is easy to find

            // Nothing selected: refused, not "everything".
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/access/requests/accept", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/admin/access/requests/reject", new { ids = Array.Empty<string>() })).StatusCode);
            Assert.Equal(4, await Waiting());

            Assert.Equal(1, (await Requests("?name=boss")).GetProperty("total").GetInt32());
            Assert.Equal(4, (await Requests("?name=team")).GetProperty("total").GetInt32());
            Assert.Equal(2, (await Requests("?limit=2")).GetProperty("requests").GetArrayLength());
            Assert.Equal(4, (await Requests("?limit=2")).GetProperty("total").GetInt32());   // the total is the number matching, not the page
            Assert.Equal(1, (await Requests("?limit=2&offset=3")).GetProperty("requests").GetArrayLength());

            Assert.Empty((await Decide("accept", new { all = true, older_than_days = 1 })).GetProperty("accepted").EnumerateArray());   // nothing is a day old
            Assert.Equal(4, await Waiting());

            var accepted = await Decide("accept", new { all = true, name = "Team A 2" });
            Assert.Single(accepted.GetProperty("accepted").EnumerateArray());
            Assert.Equal("registered", Type(await RegisterAsync(team[1])));

            var rest = await Decide("accept", new { all = true });
            Assert.Equal(3, rest.GetProperty("accepted").GetArrayLength());
            Assert.Equal(0, await Waiting());
            Assert.Equal("registered", Type(await RegisterAsync(boss)));
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task ClosingTheQueue_stopsNewRequests_butKeepsTheWaitingOnes()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var early = NewIdentity("req-early");
            var late = NewIdentity("req-late");
            await Ask(early, "Early");

            Assert.Equal(HttpStatusCode.OK, (await Admin(HttpMethod.Post, "/api/admin/access/requests/open", new { open = false })).StatusCode);
            Assert.False((await Requests()).GetProperty("open").GetBoolean());
            Assert.Equal("private_server", Message(await RegisterAsync(late, ask: true, name: "Late")));
            Assert.Equal("access_pending", Message(await RegisterAsync(early)));   // already waiting: still waiting
            Assert.Equal(1, await Waiting());

            await Admin(HttpMethod.Post, "/api/admin/access/requests/open", new { open = true });
            await Ask(late, "Late");
            Assert.Equal(2, await Waiting());
        }
        finally
        {
            await Admin(HttpMethod.Post, "/api/admin/access/requests/open", new { open = true });
            await ClearRequests();
            await SetMode("open");
        }
    }

    [Fact]
    public async Task AllowingOrBanningDirectly_alsoClosesThePendingRequest()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var gus = NewIdentity("req-gus");
            var hal = NewIdentity("req-hal");
            await Ask(gus, "Gus");
            await Ask(hal, "Hal");

            await Admin(HttpMethod.Post, "/api/admin/clients", new { id = gus.DiscoveryId });
            await Admin(HttpMethod.Post, "/api/admin/clients/ban", new { id = hal.DiscoveryId });
            Assert.Equal(0, await Waiting());
            Assert.Equal("registered", Type(await RegisterAsync(gus)));
            Assert.Equal("access_refused", Message(await RegisterAsync(hal)));
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task AccessRequestRoutes_refuseAWrongSecret()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Get, "/api/admin/access/requests", secret: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Post, "/api/admin/access/requests/accept", new { all = true }, secret: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Post, "/api/admin/access/requests/reject", new { all = true }, secret: "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(HttpMethod.Post, "/api/admin/access/requests/open", new { open = false }, secret: "nope")).StatusCode);
    }

    [Fact]
    public async Task AccessRequest_isAcceptedByItsVerificationCode_asItIsShown()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var me = NewIdentity("req-code");
            await Ask(me, "Anna", "accounting");

            var request = (await Requests()).GetProperty("requests").EnumerateArray().First(r => r.GetProperty("id").GetString() == me.DiscoveryId);
            Assert.Equal("Anna", request.GetProperty("name").GetString());
            Assert.Equal("accounting", request.GetProperty("note").GetString());

            // The administrator types the code as the person read it out: groups of four, any case.
            var code = FreeSpeech.Ops.AdminCliKit.VerificationCode(me.DiscoveryId).ToLowerInvariant();
            Assert.Equal(1, (await Decide("accept", new { ids = new[] { code } })).GetProperty("accepted").GetArrayLength());
            Assert.Equal("registered", Type(await RegisterAsync(me)));
        }
        finally { await SetMode("open"); }
    }

    [Fact]
    public async Task CommandLine_listsAcceptsAndRejectsRequests()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var people = Enumerable.Range(1, 6).Select(i => NewIdentity($"req-cli-{i}")).ToArray();
            for (var i = 0; i < people.Length; i++) await Ask(people[i], i < 4 ? $"Sales {i + 1}" : $"Support {i - 3}", "via the CLI test");

            var pending = await Cli(_s.Http, Secret, "pending");
            Assert.Equal(0, pending.Code);
            Assert.Contains("Sales 1", pending.Output);
            Assert.Contains("Support 2", pending.Output);
            Assert.Contains(FreeSpeech.Ops.AdminCliKit.VerificationCode(people[0].DiscoveryId), pending.Output);   // the code to compare with the person's phone
            Assert.Contains("6 matching", pending.Output);

            var support = await Cli(_s.Http, Secret, "pending", "--name", "Support");
            Assert.Contains("2 matching", support.Output);
            Assert.DoesNotContain("Sales", support.Output);
            Assert.Equal(2, JsonDocument.Parse((await Cli(_s.Http, Secret, "pending", "--limit", "2", "--json")).Output).RootElement.GetProperty("requests").GetArrayLength());

            // One by one, and several at once, by prefix.
            Assert.Equal(0, (await Cli(_s.Http, Secret, "accept", people[0].DiscoveryId[..8])).Code);
            Assert.Equal("registered", Type(await RegisterAsync(people[0])));
            var two = await Cli(_s.Http, Secret, "accept", people[1].DiscoveryId[..8], people[2].DiscoveryId[..8]);
            Assert.Equal(0, two.Code);
            Assert.Contains("Accepted: 2", two.Output);

            // A prefix that matches nothing: reported, exit code 4, the valid one still done.
            var mixed = await Cli(_s.Http, Secret, "accept", people[3].DiscoveryId[..8], "deadbeef0");
            Assert.Equal(4, mixed.Code);
            Assert.Contains("Accepted: 1", mixed.Output);
            Assert.Contains("deadbeef0", mixed.Output);
            Assert.Equal(2, await Waiting());

            // "--all" never runs unattended without --yes; here no keyboard is attached.
            var refused = await Cli(_s.Http, Secret, "reject", "--all");
            Assert.Equal(1, refused.Code);
            Assert.Contains("--yes", refused.Output);
            Assert.Equal(2, await Waiting());

            Assert.Equal(1, (await Cli(_s.Http, Secret, "accept")).Code);   // nothing named: usage, not "everything"
            Assert.Equal(1, (await Cli(_s.Http, Secret, "accept", people[4].DiscoveryId[..8], "--all")).Code);
            Assert.Equal(1, (await Cli(_s.Http, Secret, "accept", "--all", "--ban", "--yes")).Code);   // --ban only goes with reject

            var banned = await Cli(_s.Http, Secret, "reject", "--all", "--name", "Support 2", "--ban", "--reason", "test", "--yes");
            Assert.Equal(0, banned.Code);
            Assert.Contains("Rejected and banned: 1", banned.Output);
            Assert.Equal("access_refused", Message(await RegisterAsync(people[5])));

            var none = await Cli(_s.Http, Secret, "reject", "--all", "--older-than", "30d", "--yes");
            Assert.Equal(0, none.Code);
            Assert.Contains("Rejected: 0", none.Output);
            Assert.Equal(1, await Waiting());

            Assert.Equal(0, (await Cli(_s.Http, Secret, "accept", "--all", "--yes")).Code);
            Assert.Contains("No request waiting", (await Cli(_s.Http, Secret, "pending")).Output);

            Assert.Equal(0, (await Cli(_s.Http, Secret, "requests", "close")).Code);
            Assert.Contains("queue is closed", (await Cli(_s.Http, Secret, "pending")).Output);
            Assert.Equal(1, (await Cli(_s.Http, Secret, "requests", "maybe")).Code);
            Assert.Equal(0, (await Cli(_s.Http, Secret, "requests", "open")).Code);
        }
        finally { await Cli(_s.Http, Secret, "requests", "open"); await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task ClientLibrary_asksForAccess_andConnectsOnceTheAdministratorAccepts()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var me = NewIdentity("req-lib");
            await using var pool = new FreeSpeech.Client.Relay.RelayPool(me.DiscoveryId, new FreeSpeech.Client.Relay.RegisterAuth(me.EncryptionPublicKey, me.SigningPublicKey, me.SigningPrivateKey));

            await pool.AddOwnAsync(_s.Http);
            Assert.Equal("private_server", pool.RefusalOf(_s.Http));
            Assert.Equal(0, await Waiting());                       // adding a server never sends a name by itself

            Assert.Equal("access_pending", await pool.RequestAccessAsync(_s.Http, "Lib User", "hello"));
            Assert.Equal("Lib User", (await Requests()).GetProperty("requests")[0].GetProperty("name").GetString());

            await pool.MaintainAsync();                             // the periodic retry: still waiting, still one request
            Assert.Equal("access_pending", pool.RefusalOf(_s.Http));
            Assert.Equal(1, await Waiting());

            await Decide("accept", new { ids = new[] { me.DiscoveryId[..8] } });
            await pool.MaintainAsync();
            Assert.True(pool.IsConnected(_s.Http));
            Assert.Null(pool.RefusalOf(_s.Http));
            Assert.Equal("allowlist", pool.AccessOf(_s.Http));   // connected: the server says it is private
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task ClientLibrary_asksOnAServerNeverReachedBefore()
    {
        await SetMode("allowlist");
        try
        {
            await ClearRequests();
            var me = NewIdentity("req-lib-first");
            await using var pool = new FreeSpeech.Client.Relay.RelayPool(me.DiscoveryId, new FreeSpeech.Client.Relay.RegisterAuth(me.EncryptionPublicKey, me.SigningPublicKey, me.SigningPrivateKey));
            Assert.Equal("access_pending", await pool.RequestAccessAsync(_s.Http, "First Contact", null));
            Assert.Equal(1, await Waiting());
        }
        finally { await ClearRequests(); await SetMode("open"); }
    }

    [Fact]
    public async Task Requests_surviveARestart_andOldOnesExpire()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-requests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var kept = NewIdentity("req-restart");
        var stale = NewIdentity("req-stale");
        try
        {
            var (process, http, ws) = await StartServer(dir);
            try
            {
                await Admin(http, HttpMethod.Post, "/api/admin/access/mode", new { mode = "allowlist" });
                Assert.Equal("access_pending", Message(await RegisterAsync(kept, ask: true, name: "Kept", ws: ws)));
            }
            finally { Stop(process); }

            // One more request, written as if it had been made a month ago, to see it expire.
            var file = Path.Combine(dir, "access.json");
            var state = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var requests = state.GetProperty("requests").EnumerateArray().Select(r => r.Clone()).ToList();
            Assert.Single(requests);
            var old = new Dictionary<string, object?> { ["id"] = stale.DiscoveryId, ["name"] = "Stale", ["requested_at"] = DateTime.UtcNow.AddDays(-30) };
            var rewritten = new Dictionary<string, object?> { ["mode"] = "allowlist", ["requests"] = new object[] { requests[0], old } };
            File.WriteAllText(file, JsonSerializer.Serialize(rewritten));

            (process, http, ws) = await StartServer(dir);
            try
            {
                var status = await (await Admin(http, HttpMethod.Get, "/api/admin/status")).Content.ReadAsStringAsync();
                Assert.Contains("\"pending_request_count\":1", status);
                Assert.Equal("access_pending", Message(await RegisterAsync(kept, ws: ws)));
                Assert.Equal("private_server", Message(await RegisterAsync(stale, ws: ws)));   // expired: unknown again
            }
            finally { Stop(process); }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
