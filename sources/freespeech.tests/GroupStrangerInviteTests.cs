/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>A moderator the invited person has never met invites them because a member they know proposed them: the invitation must arrive (real clients, real server).</summary>
public class GroupStrangerInviteTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public GroupStrangerInviteTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<FreeSpeechClient> ClientAsync(string nickname)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-stranger-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = _server.Http });
        c.CreateIdentity(Pad(nickname + " p1"), Pad(nickname + " p2"), Pad(nickname + " p3"), nickname);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    private static async Task BefriendAsync(FreeSpeechClient a, FreeSpeechClient b)
    {
        var (an, bn) = (a.Contacts.Count, b.Contacts.Count);
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == an + 1 && b.Contacts.Count == bn + 1), "the contact exchange did not finish");
    }

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AModeratorWhoIsNotMyContact_invitesMe_whenAMemberIKnowProposedMe()
    {
        var creator = await ClientAsync("creator");
        var member = await ClientAsync("member");
        var stranger = await ClientAsync("stranger");
        await BefriendAsync(creator, member);
        await BefriendAsync(member, stranger);   // the creator and the stranger have never met

        var rec = await creator.CreateGroupAsync("friends", [member.Identity.Nickname!]);
        Assert.True(await UntilAsync(() => member.Groups.PendingGroupInvites.Any(i => i.GroupId == rec.GroupId)));
        await member.Groups.AcceptInvitationAsync(rec.GroupId);

        var card = member.Contacts.Single(c => c.DiscoveryId == stranger.Identity.DiscoveryId);
        var memberRec = member.Groups.Store.Find(rec.GroupId)!;
        await member.Groups.SuggestAsync(memberRec, FreeSpeechClient.ToGroupMember(card));
        Assert.True(await UntilAsync(() => creator.Groups.Suggestions.Any(s => s.Candidate.DiscoveryId == stranger.Identity.DiscoveryId)), "the moderator got no proposal");
        await creator.Groups.InviteSuggestionAsync(rec.GroupId, stranger.Identity.DiscoveryId);

        Assert.True(await UntilAsync(() => stranger.Groups.PendingGroupInvites.Any(i => i.GroupId == rec.GroupId)), "the invitation of a moderator I do not know did not arrive");
    }
}
