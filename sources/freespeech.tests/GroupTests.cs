/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>
/// Groups (§7) over an in-memory network whose peers can go offline and whose letters can be held
/// back, so every rule — invitation, rotation, exclusion, late relays, p2p catch-up, concurrent
/// moderators, forged states — is exercised deterministically without sockets.
/// </summary>
public class GroupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-grp-" + Guid.NewGuid().ToString("N"));

    // ---- in-memory network ---------------------------------------------

    private sealed class Peer : IGroupTransport
    {
        public required string Name { get; init; }
        public required UserIdentity Id { get; init; }
        public GroupService Svc { get; set; } = null!;
        public GroupStore Store { get; set; } = null!;
        public Cluster Net { get; init; } = null!;
        public bool Online = true;
        public List<(string Gid, string From, long Ts, string Text)> Got { get; } = [];
        public List<string> Notes { get; } = [];
        public Dictionary<(string From, long Ts), string?> Replies { get; } = [];
        public List<(string From, string Type, string Payload)> Queued { get; } = [];
        public string Did => Id.DiscoveryId;
        public HashSet<string>? Contacts;   // null = everybody is a confirmed contact

        public Task<bool> SendLetterAsync(GroupMember to, string type, string payload, bool queue) => Net.LetterAsync(this, to.DiscoveryId, type, payload, queue);
        public Task<bool> SendGroupMessageAsync(GroupMember to, string msg, long ts) => Net.EnvelopeAsync(this, to.DiscoveryId, msg, ts);

        public async Task GoOnlineAsync(bool sync = true)
        {
            Online = true;
            var q = Queued.ToList();
            Queued.Clear();
            foreach (var l in q) await Svc.OnLetterAsync(l.From, l.Type, l.Payload);
            if (sync) await Svc.SyncAllAsync();
        }

        public GroupRecord Rec => Store.All.Single();
        public IEnumerable<string> Texts => Got.Select(g => g.Text);
    }

    private sealed class Cluster
    {
        public Dictionary<string, Peer> ById { get; } = [];
        public bool Hold;
        public List<(Peer From, string To, string Type, string Payload)> Held { get; } = [];
        public int Letters;

        public async Task<bool> LetterAsync(Peer from, string toId, string type, string payload, bool queue)
        {
            Letters++;
            if (Hold) { Held.Add((from, toId, type, payload)); return true; }
            var target = ById[toId];
            if (target.Online) { await target.Svc.OnLetterAsync(from.Did, type, payload); return true; }
            if (queue) target.Queued.Add((from.Did, type, payload));
            return false;
        }

        public async Task<bool> EnvelopeAsync(Peer from, string toId, string msg, long ts)
        {
            var target = ById[toId];
            if (!target.Online) return false;
            await target.Svc.OnGroupEnvelopeAsync(from.Did, msg, ts);
            return true;
        }

        public async Task ReleaseAsync()
        {
            Hold = false;
            var held = Held.ToList();
            Held.Clear();
            foreach (var (from, to, type, payload) in held)
                if (ById[to].Online) await ById[to].Svc.OnLetterAsync(from.Did, type, payload);
        }
    }

    private Peer NewPeer(Cluster net, string name)
    {
        string Phrase(int k) => (name + "-phrase-" + k + "-").PadRight(80, (char)('a' + k));
        var id = IdentityService.Derive(Phrase(1), Phrase(2), Phrase(3), name);
        var peer = new Peer { Name = name, Id = id, Net = net };
        peer.Store = new GroupStore(Path.Combine(_root, name));
        peer.Svc = new GroupService(id, peer.Store, peer, other => peer.Contacts?.Contains(other) ?? net.ById.ContainsKey(other), peer.Notes.Add);
        peer.Svc.OnMessage += (g, f, t, type, x, reply) => { if (type == "message") { peer.Got.Add((g, f, t, x)); peer.Replies[(f, t)] = reply; } };
        net.ById[id.DiscoveryId] = peer;
        return peer;
    }

    private (Cluster Net, List<Peer> Peers) Cluster_(params string[] names)
    {
        var net = new Cluster();
        return (net, names.Select(n => NewPeer(net, n)).ToList());
    }

    private static string Hash(Peer p) => p.Rec.CurrentHash;

    // ------------------------------------------------------------------
    // Crypto primitives
    // ------------------------------------------------------------------

    [Fact]
    public async Task EveryGroupLetterType_isRoutedByTheService_andListedForClients()
    {
        // Two console bugs so far were "a letter type the service handles but the client never forwarded".
        var (_, p) = Cluster_("a", "b");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        foreach (var type in new[] { "group_state", "group_sync", "group_relay", "group_leave", "group_profile", "member_profile", "blacklist_update" })
        {
            Assert.True(GroupService.IsGroupLetter(type), type);
            await p[1].Svc.OnLetterAsync(p[0].Did, type, "{}");          // malformed on purpose: must be ignored, never throw
        }
        Assert.False(GroupService.IsGroupLetter("message"));
        Assert.False(GroupService.IsGroupLetter("attachment_request"));
    }

    [Fact]
    public void SealOpen_roundTrip_andRejectsWrongKeyTamperingAndTruncation()
    {
        var key = GroupCrypto.NewKey();
        var sealedMsg = GroupCrypto.Seal(key, "hello");
        Assert.Equal("hello", GroupCrypto.Open(key, sealedMsg));
        Assert.Null(GroupCrypto.Open(GroupCrypto.NewKey(), sealedMsg));

        var raw = Convert.FromBase64String(sealedMsg);
        var flipped = (byte[])raw.Clone(); flipped[^1] ^= 1;
        Assert.Null(GroupCrypto.Open(key, Convert.ToBase64String(flipped)));
        var tagFlipped = (byte[])raw.Clone(); tagFlipped[0] ^= 1;               // the tag is authenticated data too
        Assert.Null(GroupCrypto.Open(key, Convert.ToBase64String(tagFlipped)));
        Assert.Null(GroupCrypto.Open(key, Convert.ToBase64String(raw[..10])));
        Assert.Null(GroupCrypto.Open(key, "!!!"));
    }

    [Fact]
    public void Tag_isStablePerKey_distinctAcrossKeys_andDoesNotLeakTheKey()
    {
        var k1 = GroupCrypto.NewKey();
        var k2 = GroupCrypto.NewKey();
        Assert.Equal(GroupCrypto.TagHex(k1), GroupCrypto.TagOf(GroupCrypto.Seal(k1, "a")));
        Assert.Equal(GroupCrypto.TagHex(k1), GroupCrypto.TagOf(GroupCrypto.Seal(k1, "b")));
        Assert.NotEqual(GroupCrypto.TagHex(k1), GroupCrypto.TagHex(k2));
        Assert.True(GroupCrypto.KeyMatches(k1, GroupCrypto.KeyCheck(k1)));
        Assert.False(GroupCrypto.KeyMatches(k2, GroupCrypto.KeyCheck(k1)));
        Assert.NotEqual(GroupCrypto.KeyCheck(k1), GroupCrypto.TagHex(k1)); // independent derivations
    }

    // ------------------------------------------------------------------
    // Creation, invitation, messaging
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_invitesMembers_whoAllShareTheKey_andCanExchangeMessages()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);

        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me, c.Svc.Me]);

        Assert.Equal(1, b.Svc.CurrentEpoch(b.Rec));
        Assert.Equal(3, c.Svc.Members(c.Rec).Count);
        Assert.True(b.Svc.HasCurrentKey(b.Rec) && c.Svc.HasCurrentKey(c.Rec));
        Assert.Equal(rec.GroupId, b.Rec.GroupId);

        var (delivered, total) = await a.Svc.SendAsync(a.Rec, "hi all");
        Assert.Equal((2, 2), (delivered, total));
        Assert.Equal(["hi all"], b.Texts);
        Assert.Equal(["hi all"], c.Texts);

        await c.Svc.SendAsync(c.Rec, "hey");          // members write to everyone, moderators or not
        Assert.Contains("hey", a.Texts);
        Assert.Contains("hey", b.Texts);
    }

    [Fact]
    public async Task Invitation_waitsForAnswer_whenApprovalIsRequired_andAcceptingJoinsTheGroup()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Svc.RequireInvitationApproval = true;
        PendingGroupInvite? announced = null;
        b.Svc.InvitationReceived += i => announced = i;

        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me]);

        Assert.Empty(b.Store.All);                                   // nothing joined, no key kept
        var invite = Assert.Single(b.Svc.PendingGroupInvites);
        Assert.Equal(("team", rec.GroupId), (invite.Name, invite.GroupId));
        Assert.Equal(a.Did, invite.Inviter);
        Assert.Equal(invite.GroupId, announced?.GroupId);

        var joined = await b.Svc.AcceptInvitationAsync(rec.GroupId);

        Assert.Equal(rec.GroupId, joined.GroupId);
        Assert.Empty(b.Svc.PendingGroupInvites);
        Assert.True(b.Svc.HasCurrentKey(b.Rec));
        await a.Svc.SendAsync(a.Rec, "welcome");
        Assert.Equal(["welcome"], b.Texts);
    }

    [Fact]
    public async Task AnInvitedMember_isPending_untilTheyShowAnySignOfLife()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Svc.RequireInvitationApproval = true;
        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me]);

        Assert.Equal([b.Did], a.Svc.PendingMembers(a.Rec));          // invited, has not answered
        await b.Svc.AcceptInvitationAsync(rec.GroupId);              // joining sends a catch-up request
        Assert.Empty(a.Svc.PendingMembers(a.Rec));
    }

    [Fact]
    public async Task AddedBackAfterLeaving_waitsForAnswer_andAcceptingMakesTheArchivedGroupLiveAgain()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me]);
        await a.Svc.SendAsync(a.Rec, "before");

        await b.Svc.LeaveAsync(b.Rec);
        Assert.False(b.Rec.Active);                                   // archived on b's side: the group record and its history stay

        b.Svc.RequireInvitationApproval = true;
        await a.Svc.AddMemberAsync(a.Rec, b.Svc.Me);

        Assert.False(b.Rec.Active);                                   // not back until b says so
        Assert.Equal(rec.GroupId, Assert.Single(b.Svc.PendingGroupInvites).GroupId);

        await b.Svc.AcceptInvitationAsync(rec.GroupId);

        Assert.True(b.Rec.Active);
        Assert.Empty(b.Svc.PendingGroupInvites);
        await a.Svc.SendAsync(a.Rec, "welcome back");
        Assert.Contains("welcome back", b.Texts);
    }

    [Fact]
    public async Task DecliningAnInvitation_dropsIt_andTheSameGroupDoesNotAskAgain()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Svc.RequireInvitationApproval = true;

        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me]);
        b.Svc.DeclineInvitation(rec.GroupId);
        Assert.Empty(b.Svc.PendingGroupInvites);

        await a.Svc.SendAsync(a.Rec, "anyone there?");
        await a.Svc.SyncAllAsync();
        Assert.Empty(b.Store.All);
        Assert.Empty(b.Svc.PendingGroupInvites);                     // the mark of the refusal stays
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.AcceptInvitationAsync(rec.GroupId));
    }

    // ---- a member proposes one of their contacts; a moderator invites with the member's signature ----

    private async Task<(Peer A, Peer B, Peer C, string GroupId)> SponsorSetupAsync()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        a.Contacts = [b.Did];             // the moderator knows b only
        b.Contacts = [a.Did, c.Did];      // b knows both
        c.Contacts = [b.Did];             // c knows b only: the moderator is a stranger to c
        c.Svc.RequireInvitationApproval = true;
        var rec = await a.Svc.CreateAsync("team", [b.Svc.Me]);
        return (a, b, c, rec.GroupId);
    }

    [Fact]
    public async Task AMemberProposesAContact_aModeratorInvites_andTheStrangerAcceptsBecauseTheyKnowTheSponsor()
    {
        var (a, b, c, gid) = await SponsorSetupAsync();
        PendingGroupInvite? seen = null;
        GroupSuggestion? proposed = null;
        c.Svc.InvitationReceived += i => seen = i;
        a.Svc.SuggestionReceived += s => proposed = s;

        Assert.Equal(1, await b.Svc.SuggestAsync(b.Rec, c.Svc.Me));       // one moderator told

        var s = Assert.Single(a.Svc.Suggestions);
        Assert.Equal((b.Did, c.Did, gid), (s.SponsorId, s.Candidate.DiscoveryId, s.GroupId));
        Assert.Equal(s.Key, proposed?.Key);
        Assert.Empty(c.Svc.PendingGroupInvites);                          // nothing reaches c before a moderator decides

        await a.Svc.InviteSuggestionAsync(gid, c.Did);

        Assert.Empty(a.Svc.Suggestions);
        var invite = Assert.Single(c.Svc.PendingGroupInvites);
        Assert.Equal((a.Did, b.Svc.Me.Nickname), (invite.Inviter, invite.SponsorName));
        Assert.Equal(invite.GroupId, seen?.GroupId);

        await c.Svc.AcceptInvitationAsync(gid);
        await a.Svc.SendAsync(a.Rec, "welcome");
        Assert.Equal(["welcome"], c.Texts);
    }

    [Fact]
    public async Task AnInvitationFromAStranger_isIgnored_withoutASponsor_orWhenTheSponsorIsNotMyContact()
    {
        var (a, b, c, gid) = await SponsorSetupAsync();

        await a.Svc.AddMemberAsync(a.Rec, c.Svc.Me);                       // the moderator invites directly: c does not know them
        Assert.Empty(c.Svc.PendingGroupInvites);
        Assert.Empty(c.Store.All);

        var (_, q) = Cluster_("a2", "b2", "c2");                           // a valid sponsor proof, but the invitee does not know the sponsor
        var (a2, b2, c2) = (q[0], q[1], q[2]);
        a2.Contacts = [b2.Did]; b2.Contacts = [a2.Did, c2.Did]; c2.Contacts = [];
        var rec = await a2.Svc.CreateAsync("team", [b2.Svc.Me]);
        await b2.Svc.SuggestAsync(b2.Rec, c2.Svc.Me);
        await a2.Svc.InviteSuggestionAsync(rec.GroupId, c2.Did);
        Assert.Empty(c2.Store.All);
    }

    [Fact]
    public async Task AProposal_isHonouredOnlyFromAMember_withAValidSignature_forAnUnknownPerson()
    {
        var (a, b, c, gid) = await SponsorSetupAsync();

        // c is not in the group: its proposal is ignored.
        var letterFromStranger = System.Text.Json.JsonSerializer.Serialize(new GroupSuggestLetter { GroupId = gid, Candidate = c.Svc.Me, Ts = 1, Sig = "AAAA" });
        await a.Svc.OnLetterAsync(c.Did, "group_suggest", letterFromStranger);
        Assert.Empty(a.Svc.Suggestions);

        // b is a member but the signature is not b's.
        await a.Svc.OnLetterAsync(b.Did, "group_suggest", letterFromStranger);
        Assert.Empty(a.Svc.Suggestions);

        // A moderator has no use for the proposal route.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SuggestAsync(a.Rec, c.Svc.Me));

        // A real proposal can be dismissed, and then cannot be used.
        await b.Svc.SuggestAsync(b.Rec, c.Svc.Me);
        a.Svc.DismissSuggestion(gid, c.Did);
        Assert.Empty(a.Svc.Suggestions);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.InviteSuggestionAsync(gid, c.Did));
    }

    [Fact]
    public async Task InvitationFromANonContact_isIgnored()
    {
        var (_, p) = Cluster_("a", "b");
        p[1].Contacts = [];                                   // b knows nobody

        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        Assert.Empty(p[1].Store.All);
    }

    [Fact]
    public async Task InvitationForSomeoneElse_orWithAWrongKey_isIgnored()
    {
        var (_, p) = Cluster_("a", "b", "c");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        var letter = JsonSerializer.Deserialize<GroupStateLetter>(JsonSerializer.Serialize(new GroupStateLetter { State = p[0].Rec.States[0].Json, Signer = p[0].Did, Sig = p[0].Rec.States[0].Sig, KeyEnvelope = "AAAA" }))!;

        // c is not in the member list; b's copy with a garbage envelope must not create a group either.
        await p[2].Svc.OnLetterAsync(p[0].Did, "group_state", JsonSerializer.Serialize(letter));
        Assert.Empty(p[2].Store.All);
    }

    [Fact]
    public async Task Create_rejectsAMemberWhoseIdDoesNotMatchItsKeys()
    {
        var (_, p) = Cluster_("a", "b");
        var forged = new GroupMember
        {
            DiscoveryId = new string('0', 64), Nickname = "mallory",
            EncryptionPublicKey = p[1].Svc.Me.EncryptionPublicKey, SigningPublicKey = p[1].Svc.Me.SigningPublicKey,
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => p[0].Svc.CreateAsync("g", [forged]));
    }

    [Fact]
    public async Task Message_withForgedAuthor_isRejected()
    {
        var (_, p) = Cluster_("a", "b", "c");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me, p[2].Svc.Me]);

        // b's genuine message, replayed as if it came from c: the signature inside says b.
        await p[1].Svc.SendAsync(p[1].Rec, "from b");
        var entry = p[1].Rec.Log.Single();
        p[0].Got.Clear();
        await p[0].Svc.OnGroupEnvelopeAsync(p[2].Did, entry.Msg, entry.Ts + 1);   // wrong ts and wrong author

        Assert.Empty(p[0].Got.Where(g => g.From == p[2].Did));
    }

    [Fact]
    public async Task DuplicateDelivery_isSurfacedOnce()
    {
        var (_, p) = Cluster_("a", "b");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        await p[0].Svc.SendAsync(p[0].Rec, "once");
        var e = p[0].Rec.Log.Single();

        await p[1].Svc.OnGroupEnvelopeAsync(p[0].Did, e.Msg, e.Ts);   // same envelope again
        Assert.Single(p[1].Got);
    }

    // ------------------------------------------------------------------
    // Rotation
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddingAMember_rotatesTheKey_andTheNewcomerCannotReadThePast()
    {
        var (_, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.SendAsync(a.Rec, "secret from epoch 1");

        await a.Svc.AddMemberAsync(a.Rec, d.Svc.Me);

        Assert.All(new[] { a, b, c, d }, x => Assert.Equal(2, x.Svc.CurrentEpoch(x.Rec)));
        Assert.Equal(Hash(a), Hash(d));
        Assert.NotEqual(a.Rec.Keys[0].Tag, a.Rec.Keys[1].Tag);             // a fresh key
        Assert.Single(d.Rec.Keys);                                          // d only ever got epoch 2's key
        Assert.Empty(d.Texts);                                              // and none of the old messages

        await a.Svc.SendAsync(a.Rec, "welcome d");
        Assert.Equal(["welcome d"], d.Texts);
    }

    [Fact]
    public async Task Exclusion_rotatesTheKey_soTheExcludedCannotDecryptWhatFollows_evenIfIntercepted()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);
        Assert.False(c.Rec.Active);
        Assert.Contains(c.Notes, n => n.Contains("no longer a member"));
        Assert.Equal(2, b.Svc.CurrentEpoch(b.Rec));

        await a.Svc.SendAsync(a.Rec, "c must not read this");
        Assert.Contains("c must not read this", b.Texts);
        Assert.DoesNotContain("c must not read this", c.Texts);

        // A network attacker / the relay hands the ciphertext to c anyway: c only holds the epoch-1 key.
        var intercepted = a.Rec.Log.Last();
        await c.Svc.OnGroupEnvelopeAsync(a.Did, intercepted.Msg, intercepted.Ts);
        Assert.DoesNotContain("c must not read this", c.Texts);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => c.Svc.SendAsync(c.Rec, "still here?"));
    }

    [Fact]
    public async Task ExcludedMemberWhoDidNotLearnOfIt_cannotInjectUnderTheOldKey()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        c.Online = false;                                        // c misses its own exclusion
        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);
        c.Online = true;

        await c.Svc.SendAsync(c.Rec, "injected");                // c still believes it is a member
        Assert.DoesNotContain("injected", a.Texts);
        Assert.DoesNotContain("injected", b.Texts);
    }

    [Fact]
    public async Task ExcludedMembersOlderMessage_arrivesRelayedByACurrentMember_andIsAccepted()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        b.Online = false;                                        // b misses everything that follows
        await c.Svc.SendAsync(c.Rec, "written by c before the exclusion");   // only a gets it
        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);

        await b.GoOnlineAsync();                                 // b syncs: state chain, key, and c's message via a
        Assert.Equal(2, b.Svc.CurrentEpoch(b.Rec));
        Assert.Contains("written by c before the exclusion", b.Texts);
        Assert.True(b.Svc.HasCurrentKey(b.Rec));
    }

    [Fact]
    public async Task Leave_byAMember_makesAModeratorRotate()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await c.Svc.LeaveAsync(c.Rec);

        Assert.False(c.Rec.Active);
        Assert.Equal(2, a.Svc.CurrentEpoch(a.Rec));
        Assert.DoesNotContain(a.Svc.Members(a.Rec), m => m.DiscoveryId == c.Did);
        await a.Svc.SendAsync(a.Rec, "after c left");
        Assert.DoesNotContain("after c left", c.Texts);
        Assert.Contains("after c left", b.Texts);
    }

    [Fact]
    public async Task Leave_byTheOnlyModerator_promotesSomeoneElse()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await a.Svc.LeaveAsync(a.Rec);

        Assert.False(a.Rec.Active);
        Assert.True(b.Svc.IsModerator(b.Rec));                   // first remaining member
        await b.Svc.AddMemberAsync(b.Rec, a.Svc.Me);             // the group is still administrable
        Assert.Equal(3, b.Svc.CurrentEpoch(b.Rec));
    }

    [Fact]
    public async Task OnlyModerators_canChangeTheGroup_andTheCreatorCannotBeExcluded()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.RemoveMemberAsync(b.Rec, c.Did));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.AddMemberAsync(b.Rec, NewPeer(new Cluster(), "x").Svc.Me));

        await a.Svc.PromoteAsync(a.Rec, b.Did);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.RemoveMemberAsync(b.Rec, a.Did)); // creator
        await b.Svc.RemoveMemberAsync(b.Rec, c.Did);                                                        // b is a moderator now
        Assert.Equal(3, a.Svc.CurrentEpoch(a.Rec));
    }

    // ------------------------------------------------------------------
    // Forged / invalid states
    // ------------------------------------------------------------------

    private static (GroupStateLetter Letter, byte[] Key) Forge(Peer signer, GroupRecord like, Action<GroupStateData>? edit = null, Peer? recipient = null, bool wrongKeyInEnvelope = false)
    {
        var cur = JsonSerializer.Deserialize<GroupStateData>(like.States.First(s => s.Hash == like.CurrentHash).Json)!;
        var key = GroupCrypto.NewKey();
        var next = new GroupStateData
        {
            GroupId = cur.GroupId, Epoch = cur.Epoch + 1, PrevHash = like.CurrentHash, CreatedAt = 1, Name = cur.Name, CreatedBy = cur.CreatedBy,
            Moderators = cur.Moderators.ToList(), Members = cur.Members.ToList(), KeyCheck = GroupCrypto.KeyCheck(key),
        };
        edit?.Invoke(next);
        var json = JsonSerializer.Serialize(next);
        var target = recipient ?? signer;
        var envKey = wrongKeyInEnvelope ? GroupCrypto.NewKey() : key;
        return (new GroupStateLetter { State = json, Signer = signer.Did, Sig = GroupCrypto.SignState(json, signer.Id.SigningPrivateKey), KeyEnvelope = GroupCrypto.WrapKey(envKey, target.Svc.Me.EncryptionPublicKey) }, key);
    }

    [Fact]
    public async Task StateSignedByANonModerator_isRejected()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        // b (plain member) crafts epoch 2 making itself moderator and dropping a.
        var (letter, _) = Forge(b, b.Rec, d => { d.Moderators = [b.Did]; d.Members.RemoveAll(m => m.DiscoveryId == a.Did); }, recipient: c);
        await c.Svc.OnLetterAsync(b.Did, "group_state", JsonSerializer.Serialize(letter));

        Assert.Equal(1, c.Svc.CurrentEpoch(c.Rec));
        Assert.Contains(c.Svc.Members(c.Rec), m => m.DiscoveryId == a.Did);
    }

    [Fact]
    public async Task StateWithATamperedBody_orForgedMemberIds_isRejected()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);

        var (good, _) = Forge(a, a.Rec, recipient: b);
        var tampered = new GroupStateLetter { State = good.State.Replace("\"epoch\":2", "\"epoch\":2 "), Signer = good.Signer, Sig = good.Sig, KeyEnvelope = good.KeyEnvelope };
        await b.Svc.OnLetterAsync(a.Did, "group_state", JsonSerializer.Serialize(tampered));
        Assert.Equal(1, b.Svc.CurrentEpoch(b.Rec));                     // signature no longer matches the bytes

        var (withFake, _) = Forge(a, a.Rec, d => d.Members.Add(new GroupMember { DiscoveryId = new string('1', 64), Nickname = "ghost", EncryptionPublicKey = a.Svc.Me.EncryptionPublicKey, SigningPublicKey = a.Svc.Me.SigningPublicKey }), recipient: b);
        await b.Svc.OnLetterAsync(a.Did, "group_state", JsonSerializer.Serialize(withFake));
        Assert.Equal(1, b.Svc.CurrentEpoch(b.Rec));                     // member id does not match its keys
    }

    [Fact]
    public async Task ModeratorSignedState_withAKeyThatFailsKeyCheck_isAdoptedButNeverUsedToSend()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);

        var (letter, _) = Forge(a, a.Rec, recipient: b, wrongKeyInEnvelope: true);   // a peer/attacker swapped the key
        await b.Svc.OnLetterAsync(a.Did, "group_state", JsonSerializer.Serialize(letter));

        Assert.Equal(2, b.Svc.CurrentEpoch(b.Rec));                      // the composition is authentic...
        Assert.False(b.Svc.HasCurrentKey(b.Rec));                        // ...the fake key was refused
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.SendAsync(b.Rec, "x"));
    }

    [Fact]
    public async Task StaleAndReplayedStates_areHarmless()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        var first = a.Rec.States[0];
        await a.Svc.PromoteAsync(a.Rec, b.Did);                          // epoch 2

        // The epoch-1 state again (replay): ignored, no downgrade.
        await b.Svc.OnLetterAsync(a.Did, "group_state", JsonSerializer.Serialize(new GroupStateLetter { State = first.Json, Signer = first.Signer, Sig = first.Sig }));
        Assert.Equal(2, b.Svc.CurrentEpoch(b.Rec));
        Assert.Equal(2, b.Rec.States.Count);
    }

    // ------------------------------------------------------------------
    // Peer catch-up
    // ------------------------------------------------------------------

    [Fact]
    public async Task CatchUp_followsTheSpecExample_andEveryoneConverges()
    {
        // A writes 6 messages. D is offline for all of them, B misses #3, C has all.
        var (_, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me, d.Svc.Me]);      // d's invitation is queued

        await a.Svc.SendAsync(a.Rec, "m1");
        await a.Svc.SendAsync(a.Rec, "m2");
        b.Online = false;
        await a.Svc.SendAsync(a.Rec, "m3");                                // only c gets it
        b.Online = true;
        await a.Svc.SendAsync(a.Rec, "m4");
        await a.Svc.SendAsync(a.Rec, "m5");
        await a.Svc.SendAsync(a.Rec, "m6");
        Assert.Equal(["m1", "m2", "m4", "m5", "m6"], b.Texts);
        Assert.Equal(["m1", "m2", "m3", "m4", "m5", "m6"], c.Texts);

        a.Online = false;                                                  // the author is gone
        await d.GoOnlineAsync();                                           // d joins: invitation, then catch-up from b and c

        Assert.Equal(["m1", "m2", "m3", "m4", "m5", "m6"], d.Texts.OrderBy(t => t).ToList());
        Assert.Equal(6, d.Texts.Count());                                  // duplicates from b and c were dropped
        // Convergence: d's summary showed b it was missing m3, so b asked in turn.
        Assert.Contains("m3", b.Texts);
        Assert.Equal(6, b.Rec.Log.Count);
    }

    [Fact]
    public async Task ADeletedMessage_leavesNoContentInTheLog_andALateJoinerIsNotStuckOnItsGap()
    {
        var (_, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me, d.Svc.Me]);      // d's invitation is queued
        var gid = a.Rec.GroupId;

        await a.Svc.SendAsync(a.Rec, "m1");
        var m2 = await a.Svc.SendAsync(a.Rec, "m2");
        await a.Svc.SendAsync(a.Rec, "m3");
        var seq = a.Svc.SeqOf(gid, a.Did, m2.Ts);
        Assert.Equal(2, seq);

        // a deletes m2 for everyone: every holder empties its sealed copy (the client does this on the change Letter).
        foreach (var peer in new[] { a, b, c }) Assert.True(peer.Svc.Redact(gid, a.Did, m2.Ts, seq));
        Assert.All(new[] { a, b, c }, peer => Assert.Equal("", peer.Rec.Log.Single(e => e.Ts == m2.Ts).Msg));
        Assert.Equal(3, b.Rec.Log.Count);                                   // the entry stays: seq contiguity is kept

        a.Online = false;
        await d.GoOnlineAsync();                                            // d joins and catches up from b and c

        Assert.Equal(["m1", "m3"], d.Texts.OrderBy(t => t).ToList());        // the deleted content never reached d
        Assert.Equal(3, d.Svc.HaveMap(d.Rec)[a.Did]);                       // gap marker: d is not stuck below the hole
        Assert.Contains(2, d.Rec.Covered[a.Did]);
    }

    [Fact]
    public async Task ADeletedMessage_takesItsEditsOutOfTheLogToo_andNothingElse()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        var m1 = await a.Svc.SendAsync(a.Rec, "first");
        var m2 = await a.Svc.SendAsync(a.Rec, "second");
        string Edit(long ts, string text) => System.Text.Json.JsonSerializer.Serialize(new { target = $"{a.Did}:{ts}", text });
        var e1 = await a.Svc.SendAsync(a.Rec, Edit(m1.Ts, "first (edited)"), "message_edit");
        var e2 = await a.Svc.SendAsync(a.Rec, Edit(m2.Ts, "second (edited)"), "message_edit");

        foreach (var peer in new[] { a, b }) Assert.True(peer.Svc.Redact(a.Rec.GroupId, a.Did, m1.Ts, 1));
        Assert.All(new[] { a, b }, peer =>
        {
            Assert.Equal("", peer.Rec.Log.Single(e => e.Ts == m1.Ts).Msg);
            Assert.Equal("", peer.Rec.Log.Single(e => e.Ts == e1.Ts).Msg);          // the edit of the deleted message
            Assert.NotEqual("", peer.Rec.Log.Single(e => e.Ts == m2.Ts).Msg);
            Assert.NotEqual("", peer.Rec.Log.Single(e => e.Ts == e2.Ts).Msg);       // the edit of another message stays
            Assert.Equal(4, peer.Rec.Log.Count);                                     // seqs stay contiguous
        });
    }

    [Fact]
    public async Task ADeletedMessage_isNotProcessedAgain_whenItIsDeliveredOnceMore()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        var m1 = await a.Svc.SendAsync(a.Rec, "m1");
        Assert.Equal(["m1"], b.Texts);

        var sealedCopy = a.Rec.Log.Single(e => e.Ts == m1.Ts).Msg;
        b.Svc.Redact(b.Rec.GroupId, a.Did, m1.Ts, 1);
        Assert.False(await b.Svc.OnGroupEnvelopeAsync(a.Did, sealedCopy, m1.Ts));   // a duplicate of what b deleted is dropped
        Assert.Equal(["m1"], b.Texts);                                               // nothing new announced
        Assert.Equal("", b.Rec.Log.Single().Msg);
    }

    [Fact]
    public async Task RedactingAMessageWeNeverHad_justAccountsForItsSeq()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        Assert.False(b.Svc.Redact(b.Rec.GroupId, a.Did, ts: 12345, seq: 4));
        Assert.Contains(4, b.Rec.Covered[a.Did]);
        Assert.False(b.Svc.Redact(b.Rec.GroupId, a.Did, ts: 12345, seq: 4));           // idempotent
        Assert.Single(b.Rec.Covered[a.Did]);
    }
    [Fact]
    public async Task CatchUp_deliversMissedRotations_andTheKeysRewrappedByAPeer()
    {
        var (_, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me, d.Svc.Me]);
        await a.Svc.SendAsync(a.Rec, "epoch1");

        d.Online = false;
        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);                       // epoch 2
        await a.Svc.PromoteAsync(a.Rec, b.Did);                            // epoch 3
        await a.Svc.SendAsync(a.Rec, "epoch3");
        a.Online = false;                                                  // the moderator is not needed any more

        await d.GoOnlineAsync();                                           // only b is online: b re-wraps the keys for d

        Assert.Equal(3, d.Svc.CurrentEpoch(d.Rec));
        Assert.True(d.Svc.HasCurrentKey(d.Rec));
        Assert.Contains("epoch3", d.Texts);
        Assert.Contains("epoch1", d.Texts);                                // d was a member of epoch 1: its key was re-issued too
        Assert.Equal(b.Rec.CurrentHash, d.Rec.CurrentHash);
    }

    [Fact]
    public async Task CatchUp_isBatched_andContinuesUntilComplete()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        for (var i = 1; i <= GroupService.RelayBatch * 2 + 7; i++) await a.Svc.SendAsync(a.Rec, "m" + i);

        await b.GoOnlineAsync();

        Assert.Equal(GroupService.RelayBatch * 2 + 7, b.Texts.Count());
    }

    [Fact]
    public async Task Sync_fromANonMember_getsNothing()
    {
        var (_, p) = Cluster_("a", "b", "x");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        await p[0].Svc.SendAsync(p[0].Rec, "private");

        await p[0].Svc.OnLetterAsync(p[2].Did, "group_sync", JsonSerializer.Serialize(new GroupSyncLetter { GroupId = p[0].Rec.GroupId }));
        Assert.Empty(p[2].Store.All);
        Assert.Empty(p[2].Got);
    }

    // ------------------------------------------------------------------
    // Concurrent moderators
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentExclusions_byTwoModerators_convergeAndTheLoserReplaysItsChange()
    {
        var (net, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me, d.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);                            // epoch 2: a and b are moderators

        net.Hold = true;                                                   // neither sees the other's move
        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);                       // a: epoch 3, without c
        await b.Svc.RemoveMemberAsync(b.Rec, d.Did);                       // b: epoch 3, without d (concurrent)
        await net.ReleaseAsync();

        // Same winner everywhere that matters, and both exclusions ended up applied (the loser replayed on top).
        Assert.Equal(Hash(a), Hash(b));
        var members = a.Svc.Members(a.Rec).Select(m => m.DiscoveryId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { a.Did, b.Did }.OrderBy(x => x).ToList(), members);
        Assert.Equal(4, a.Svc.CurrentEpoch(a.Rec));
        Assert.True(a.Svc.HasCurrentKey(a.Rec) && b.Svc.HasCurrentKey(b.Rec));

        await a.Svc.SendAsync(a.Rec, "still talking");
        Assert.Contains("still talking", b.Texts);
    }

    [Fact]
    public async Task MessagesSealedWithTheLosingKey_remainReadable()
    {
        var (net, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        net.Hold = true;
        await a.Svc.AddMemberAsync(a.Rec, NewPeer(net, "e").Svc.Me);       // two competing epoch 3 states
        await b.Svc.PromoteAsync(b.Rec, c.Did);
        // Each says something under its own (soon possibly losing) epoch-3 key before hearing of the other.
        await a.Svc.SendAsync(a.Rec, "from a's epoch 3");
        await b.Svc.SendAsync(b.Rec, "from b's epoch 3");
        await net.ReleaseAsync();
        foreach (var x in new[] { a, b, c }) await x.Svc.SyncAllAsync();

        Assert.Equal(Hash(a), Hash(b));
        Assert.Contains("from b's epoch 3", a.Texts);
        Assert.Contains("from a's epoch 3", b.Texts);
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task AfterARestart_groupsKeysAndLogSurvive_andMessagesStillDecrypt()
    {
        var (net, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        await a.Svc.SendAsync(a.Rec, "before restart");

        // b restarts: fresh store + service over the same directory.
        var reloaded = new GroupStore(Path.Combine(_root, "b"));
        var got = new List<string>();
        var svc = new GroupService(b.Id, reloaded, b, _ => true, _ => { });
        svc.OnMessage += (_, _, _, _, t, _) => got.Add(t);
        b.Svc = svc;

        await a.Svc.SendAsync(a.Rec, "after restart");
        Assert.Equal(["after restart"], got);
        Assert.Equal(2, reloaded.All.Single().Log.Count);
        Assert.True(svc.HasCurrentKey(reloaded.All.Single()));
        _ = net;
    }

    [Fact]
    public async Task Resolve_findsByExactId_nameOrUniquePrefix()
    {
        var (_, p) = Cluster_("a", "b");
        var rec = await p[0].Svc.CreateAsync("Team Rocket", [p[1].Svc.Me]);
        Assert.Same(rec, p[0].Store.Resolve(rec.GroupId));
        Assert.Same(rec, p[0].Store.Resolve("team rocket"));
        Assert.Same(rec, p[0].Store.Resolve(rec.GroupId[..6]));
        Assert.Null(p[0].Store.Resolve("nope"));
    }

    // ------------------------------------------------------------------
    // Group blacklist (signed, no rotation)
    // ------------------------------------------------------------------

    private static BanOp SignedBan(Peer signer, GroupRecord rec, string kind, string id, long ts = 5, Peer? actualSigner = null) => new()
    {
        GroupId = rec.GroupId, Kind = kind, Id = id, Ts = ts, Signer = signer.Did,
        Sig = MessageCrypto.Sign(GroupCrypto.BanSigData(rec.GroupId, kind, id, ts), (actualSigner ?? signer).Id.SigningPrivateKey),
    };

    private static string Letter(GroupRecord rec, params BanOp[] ops) =>
        JsonSerializer.Serialize(new BanUpdateLetter { GroupId = rec.GroupId, Ops = ops.ToList() });

    [Fact]
    public async Task Ban_hidesAuthorFromEveryone_withoutRotatingTheKey_andIsNotRelayedToLateJoiners()
    {
        var (_, p) = Cluster_("a", "b", "c", "d");
        var (a, b, c, d) = (p[0], p[1], p[2], p[3]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me, d.Svc.Me]);
        await c.Svc.SendAsync(c.Rec, "old-spam");
        Assert.Contains("old-spam", b.Texts);

        var epoch = a.Svc.CurrentEpoch(a.Rec);
        await a.Svc.BanAsync(a.Rec, c.Did);
        Assert.Equal(epoch, a.Svc.CurrentEpoch(a.Rec));                    // a social measure: no key rotation
        Assert.Equal([c.Did], b.Svc.Bans(b.Rec));

        await c.Svc.SendAsync(c.Rec, "new-spam");                          // c is still a member and still holds the key
        Assert.DoesNotContain("new-spam", a.Texts);
        Assert.DoesNotContain("new-spam", b.Texts);

        await d.GoOnlineAsync();                                           // d joins late: gets the blacklist, and none of c's messages
        Assert.Equal([c.Did], d.Svc.Bans(d.Rec));
        Assert.DoesNotContain("old-spam", d.Texts);
        Assert.DoesNotContain(a.Rec.Log, e => e.From == c.Did);            // not stored, hence never relayed
        await c.Svc.SendAsync(c.Rec, "spam-to-d");
        Assert.DoesNotContain("spam-to-d", d.Texts);
    }

    [Fact]
    public async Task Ban_byANonModerator_orWithAForgedSignature_isIgnored()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await a.Svc.OnLetterAsync(b.Did, "blacklist_update", Letter(a.Rec, SignedBan(b, a.Rec, "add", c.Did)));                       // b is not a moderator
        await a.Svc.OnLetterAsync(b.Did, "blacklist_update", Letter(a.Rec, SignedBan(a, a.Rec, "add", c.Did, actualSigner: b)));       // claims to be a, signed by b
        Assert.Empty(a.Svc.Bans(a.Rec));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.BanAsync(b.Rec, c.Did));
    }

    [Fact]
    public async Task Ban_fromANonMemberSender_isIgnored_evenIfValidlySigned()
    {
        var (_, p) = Cluster_("a", "b", "x");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        var op = SignedBan(p[0], p[0].Rec, "add", p[1].Did);

        await p[0].Svc.OnLetterAsync(p[2].Did, "blacklist_update", Letter(p[0].Rec, op));   // handed over by someone outside the group
        Assert.Empty(p[0].Svc.Bans(p[0].Rec));
    }

    [Fact]
    public async Task OnlyTheCreator_canLiftABan()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);                            // b is a moderator, not the creator
        await a.Svc.BanAsync(a.Rec, c.Did);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.UnbanAsync(b.Rec, c.Did));
        Assert.Equal("group_only_creator_unban", Assert.IsAssignableFrom<FreeSpeech.Client.ICodedMessage>(refused).Code);   // an app translates by this code
        await a.Svc.OnLetterAsync(b.Did, "blacklist_update", Letter(a.Rec, SignedBan(b, a.Rec, "remove", c.Did, ts: long.MaxValue / 2)));   // a moderator's forged lift
        Assert.Equal([c.Did], a.Svc.Bans(a.Rec));

        await a.Svc.UnbanAsync(a.Rec, c.Did);                              // the creator can
        Assert.Empty(a.Svc.Bans(a.Rec));
        Assert.Empty(b.Svc.Bans(b.Rec));
        await c.Svc.SendAsync(c.Rec, "back");
        Assert.Contains("back", a.Texts);
    }

    [Fact]
    public async Task ModeratorsAndTheCreator_cannotBeBlacklisted()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.BanAsync(a.Rec, b.Did));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.BanAsync(b.Rec, a.Did));
        // Even a correctly signed op by a moderator against another moderator is dropped by receivers.
        await a.Svc.OnLetterAsync(b.Did, "blacklist_update", Letter(a.Rec, SignedBan(b, a.Rec, "add", a.Did)));
        Assert.Empty(a.Svc.Bans(a.Rec));
    }

    [Fact]
    public async Task ABlacklistedPerson_cannotBeAddedBack()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.BanAsync(a.Rec, c.Did);
        await a.Svc.RemoveMemberAsync(a.Rec, c.Did);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.AddMemberAsync(a.Rec, c.Svc.Me));
        Assert.Contains("blacklist", ex.Message);

        await a.Svc.UnbanAsync(a.Rec, c.Did);
        await a.Svc.AddMemberAsync(a.Rec, c.Svc.Me);                       // allowed again once the creator lifts it
    }

    [Fact]
    public async Task ReplayedAndDuplicateBanOps_areHarmless_andLatestOperationWins()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        var add = SignedBan(a, a.Rec, "add", c.Did, ts: 100);
        var lift = SignedBan(a, a.Rec, "remove", c.Did, ts: 200);
        await b.Svc.OnLetterAsync(a.Did, "blacklist_update", Letter(b.Rec, add, add, lift));
        await b.Svc.OnLetterAsync(a.Did, "blacklist_update", Letter(b.Rec, add));           // an old "add" replayed after the lift
        Assert.Empty(b.Svc.Bans(b.Rec));                                                     // the later lift still wins
        Assert.Equal(2, b.Rec.Bans.Count);
    }

    // ------------------------------------------------------------------
    // Group name/picture (creator) and member names/pictures
    // ------------------------------------------------------------------

    private static string Pic(int bytes = 1000) => Convert.ToBase64String(new byte[bytes]);

    private static GroupProfileOp SignedProfile(Peer signer, GroupRecord rec, string name, string? photo, long ts, Peer? actualSigner = null) => new()
    {
        GroupId = rec.GroupId, Name = name, Photo = photo, Ts = ts, Signer = signer.Did,
        Sig = MessageCrypto.Sign(GroupCrypto.GroupProfileSigData(rec.GroupId, name, photo, ts), (actualSigner ?? signer).Id.SigningPrivateKey),
    };

    [Fact]
    public async Task Creator_canRenameTheGroup_andChangeItsPicture_withoutRotatingTheKey()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me, c.Svc.Me]);
        var epoch = a.Svc.CurrentEpoch(a.Rec);

        await a.Svc.SetProfileAsync(a.Rec, "Crew", Pic());

        foreach (var x in new[] { a, b, c })
        {
            Assert.Equal("Crew", x.Rec.Name);
            Assert.Equal(Pic(), x.Svc.GroupPhoto(x.Rec));
            Assert.Equal(epoch, x.Svc.CurrentEpoch(x.Rec));            // cosmetic: no new epoch, no new key
        }
        Assert.Single(a.Rec.Keys);

        await a.Svc.SetProfileAsync(a.Rec, name: "Crew 2", photo: null);      // rename only: the picture is kept
        Assert.Equal("Crew 2", b.Rec.Name);
        Assert.Equal(Pic(), b.Svc.GroupPhoto(b.Rec));

        await a.Svc.SetProfileAsync(a.Rec, name: null, photo: null, clearPhoto: true);
        Assert.Null(c.Svc.GroupPhoto(c.Rec));
        Assert.Equal("Crew 2", c.Rec.Name);
        await a.Svc.SendAsync(a.Rec, "still works");
        Assert.Contains("still works", b.Texts);
    }

    [Fact]
    public async Task OnlyTheCreator_canChangeTheGroupProfile()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);                              // a moderator, still not the creator

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.SetProfileAsync(b.Rec, "Mine", null));

        // Forged: a real member signs a rename claiming to be the creator, or signs as itself.
        await c.Svc.OnLetterAsync(b.Did, "group_profile", JsonSerializer.Serialize(SignedProfile(a, a.Rec, "Hijacked", null, 50, actualSigner: b)));
        await c.Svc.OnLetterAsync(b.Did, "group_profile", JsonSerializer.Serialize(SignedProfile(b, a.Rec, "Hijacked", null, 51)));
        Assert.Equal("Team", c.Rec.Name);
    }

    [Fact]
    public async Task GroupProfile_followsTheCreatorRole_whenItIsTransferred()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me]);
        await a.Svc.SetProfileAsync(a.Rec, "By A", null);

        await a.Svc.TransferCreatorAsync(a.Rec, b.Did);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, "Still A?", null));
        await b.Svc.SetProfileAsync(b.Rec, "By B", Pic());

        Assert.Equal("By B", a.Rec.Name);
        Assert.Equal(Pic(), a.Svc.GroupPhoto(a.Rec));
        // An older operation by the former creator, replayed, cannot win (older ts, and no longer the creator).
        await a.Svc.OnLetterAsync(b.Did, "group_profile", JsonSerializer.Serialize(SignedProfile(a, a.Rec, "By A again", null, long.MaxValue / 2)));
        Assert.Equal("By B", a.Rec.Name);
    }

    [Fact]
    public async Task ACreatorSetName_survivesLaterKeyRotations()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me]);
        await a.Svc.SetProfileAsync(a.Rec, "Crew", null);

        await a.Svc.AddMemberAsync(a.Rec, c.Svc.Me);                         // epoch 2 states still say "Team"
        await a.Svc.RemoveMemberAsync(a.Rec, b.Did);

        Assert.Equal("Crew", a.Rec.Name);
        Assert.Equal("Crew", b.Rec.Name);
    }

    [Fact]
    public async Task ALateJoiner_learnsTheGroupProfile_fromAPeer()
    {
        var (_, p) = Cluster_("a", "b", "d");
        var (a, b, d) = (p[0], p[1], p[2]);
        d.Online = false;
        await a.Svc.CreateAsync("Team", [b.Svc.Me, d.Svc.Me]);
        await a.Svc.SetProfileAsync(a.Rec, "Crew", Pic());

        a.Online = false;
        await d.GoOnlineAsync();                                              // gets the invitation, then catches up from b alone

        Assert.Equal("Crew", d.Rec.Name);
        Assert.Equal(Pic(), d.Svc.GroupPhoto(d.Rec));
    }

    [Fact]
    public async Task GroupProfile_limits_areEnforced()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me]);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, "  ", null));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, new string('x', 101), null));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, "ok", Pic(GroupProfileOp.MaxPhotoBytes + 1)));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, "ok", "not base64!!"));
        await a.Svc.SetProfileAsync(a.Rec, "ok", Pic(GroupProfileOp.MaxPhotoBytes));   // exactly the limit is fine

        // A peer sending an oversized picture anyway is ignored by receivers.
        var big = SignedProfile(a, a.Rec, "Big", Pic(GroupProfileOp.MaxPhotoBytes + 10), ts: long.MaxValue / 2);
        await b.Svc.OnLetterAsync(a.Did, "group_profile", JsonSerializer.Serialize(big));
        Assert.Equal("ok", b.Rec.Name);
    }

    [Fact]
    public async Task OlderGroupProfileOperation_isIgnored()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me]);
        await a.Svc.SetProfileAsync(a.Rec, "New", null);

        await b.Svc.OnLetterAsync(a.Did, "group_profile", JsonSerializer.Serialize(SignedProfile(a, a.Rec, "Old", null, ts: 1)));
        Assert.Equal("New", b.Rec.Name);
    }

    [Fact]
    public async Task Member_canPublishTheirOwnName_andPicture_toEveryGroup()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await b.Svc.PublishMyProfileAsync("Bobby", Pic());

        foreach (var x in new[] { a, b, c })
        {
            Assert.Equal("Bobby", x.Svc.Members(x.Rec).Single(m => m.DiscoveryId == b.Did).Nickname);
            Assert.Equal(Pic(), x.Svc.MemberPhoto(x.Rec, b.Did));
            Assert.Equal("b", x.Svc.CurrentState(x.Rec).Members.Single(m => m.DiscoveryId == b.Did).Nickname);   // the signed state is untouched
        }

        await b.Svc.PublishMyProfileAsync("Robert", null);                    // a later change replaces it
        Assert.Equal("Robert", c.Svc.Members(c.Rec).Single(m => m.DiscoveryId == b.Did).Nickname);
        Assert.Null(c.Svc.MemberPhoto(c.Rec, b.Did));
    }

    [Fact]
    public async Task Member_canUseAnotherNameInOneGroup()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        b.Svc.ProfileDelay = TimeSpan.Zero;   // sent at once here; the delay has its own test
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        var (aG, bG, cG) = (a.Rec, b.Rec, c.Rec);
        var other = await a.Svc.CreateAsync("h", [b.Svc.Me]);
        var aH = a.Store.Find(other.GroupId)!;

        await b.Svc.SetMyNameAsync(bG, "Anon");
        foreach (var (x, rec) in new[] { (a, aG), (b, bG), (c, cG) }) Assert.Equal("Anon", x.Svc.Members(rec).Single(m => m.DiscoveryId == b.Did).Nickname);
        Assert.Equal("Anon", b.Svc.MyNameIn(bG));
        Assert.Equal("b", a.Svc.Members(aH).Single(m => m.DiscoveryId == b.Did).Nickname);   // the other group still sees the global name

        // A change of the global name leaves the name chosen for the group alone, and still reaches the groups without one.
        await b.Svc.PublishMyProfileAsync("Bobby", null);
        Assert.Equal("Anon", c.Svc.Members(cG).Single(m => m.DiscoveryId == b.Did).Nickname);
        Assert.Equal("Bobby", a.Svc.Members(aH).Single(m => m.DiscoveryId == b.Did).Nickname);

        await b.Svc.SetMyNameAsync(bG, "");   // empty = back to the global name (still "b" for the identity)
        Assert.Equal("b", c.Svc.Members(cG).Single(m => m.DiscoveryId == b.Did).Nickname);
        Assert.Null(bG.MyName);
    }

    [Fact]
    public async Task Member_canUseAnotherPictureInOneGroup_andGoBackToTheGlobalOne()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Svc.ProfileDelay = TimeSpan.Zero;   // sent at once here; the delay has its own test
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        var (aG, bG) = (a.Rec, b.Rec);

        await b.Svc.SetMyPhotoAsync(bG, Pic());
        Assert.Equal(Pic(), a.Svc.MemberPhoto(aG, b.Did));
        Assert.Equal(Pic(), b.Svc.MyPhotoIn(bG));

        await b.Svc.SetMyPhotoAsync(bG, null);   // back to the global picture (none here)
        Assert.Null(bG.MyPhoto);
        Assert.Null(a.Svc.MemberPhoto(aG, b.Did));
    }

    [Fact]
    public async Task ChangesToMyNameInAGroup_areSentOnce_afterTheLastEdit()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        b.Svc.ProfileDelay = TimeSpan.FromMilliseconds(400);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        var (aG, bG) = (a.Rec, b.Rec);

        await b.Svc.SetMyNameAsync(bG, "First");
        await b.Svc.SetMyNameAsync(bG, "Second");
        Assert.Equal("Second", b.Svc.MyNameIn(bG));                                              // on my screen at once
        Assert.Equal("b", a.Svc.Members(aG).Single(m => m.DiscoveryId == b.Did).Nickname);       // not sent yet

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && a.Svc.Members(aG).Single(m => m.DiscoveryId == b.Did).Nickname == "b") await Task.Delay(50);
        Assert.Equal("Second", a.Svc.Members(aG).Single(m => m.DiscoveryId == b.Did).Nickname);   // only the last edit went out
    }

    [Fact]
    public async Task AModerator_pinsAnyMessage_andEveryoneSeesIt_untilItIsReleased()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        await a.Svc.PinAsync(a.Rec, $"{c.Did}:7", pin: true);   // a message written by c, pinned by the creator
        foreach (var x in new[] { a, b, c }) Assert.Equal([$"{c.Did}:7"], x.Svc.PinnedIds(x.Rec));

        await a.Svc.PinAsync(a.Rec, $"{c.Did}:7", pin: false);
        foreach (var x in new[] { a, b, c }) Assert.Empty(x.Svc.PinnedIds(x.Rec));
    }

    [Fact]
    public async Task OnlyAModerator_canPin_aForgedPinIsIgnored()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        var ex = await Assert.ThrowsAsync<FreeSpeech.Client.CodedInvalidOperationException>(() => b.Svc.PinAsync(b.Rec, "x:1", pin: true));
        Assert.Equal("group_only_moderator_pin", ex.Code);

        // b signs a pin by hand and hands it to a and c: not a moderator, so nobody takes it.
        var op = new PinOp { GroupId = a.Rec.GroupId, Kind = "pin", Target = "x:1", Ts = 50, Signer = b.Did, Sig = MessageCrypto.Sign(GroupCrypto.PinSigData(a.Rec.GroupId, "pin", "x:1", 50), b.Id.SigningPrivateKey) };
        var letter = JsonSerializer.Serialize(new PinUpdateLetter { GroupId = a.Rec.GroupId, Ops = [op] });
        await a.Svc.OnLetterAsync(b.Did, "group_pin", letter);
        await c.Svc.OnLetterAsync(b.Did, "group_pin", letter);
        Assert.Empty(a.Svc.PinnedIds(a.Rec));
        Assert.Empty(c.Svc.PinnedIds(c.Rec));

        // a moderator's pin with a bad signature is ignored too.
        var bad = new PinOp { GroupId = a.Rec.GroupId, Kind = "pin", Target = "x:2", Ts = 60, Signer = a.Did, Sig = op.Sig };
        await b.Svc.OnLetterAsync(a.Did, "group_pin", JsonSerializer.Serialize(new PinUpdateLetter { GroupId = a.Rec.GroupId, Ops = [bad] }));
        Assert.Empty(b.Svc.PinnedIds(b.Rec));
    }

    [Fact]
    public async Task AtMostThreeMessagesArePinned_theNewestOnes_andTheLatestOperationWins()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);

        foreach (var n in new[] { 1, 2, 3, 4 }) await a.Svc.PinAsync(a.Rec, $"{b.Did}:{n}", pin: true);
        Assert.Equal([$"{b.Did}:4", $"{b.Did}:3", $"{b.Did}:2"], b.Svc.PinnedIds(b.Rec));   // the oldest was let go, on every device

        await a.Svc.PinAsync(a.Rec, $"{b.Did}:3", pin: false);
        await a.Svc.PinAsync(a.Rec, $"{b.Did}:3", pin: true);   // pinned again: newest
        Assert.Equal([$"{b.Did}:3", $"{b.Did}:4", $"{b.Did}:2"], b.Svc.PinnedIds(b.Rec));
    }

    [Fact]
    public async Task APinReachesALateJoiner_throughAPeer()
    {
        var (_, p) = Cluster_("a", "b", "d");
        var (a, b, d) = (p[0], p[1], p[2]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, d.Svc.Me]);
        await a.Svc.PinAsync(a.Rec, $"{b.Did}:9", pin: true);

        await d.GoOnlineAsync();   // d asks the others for what it missed
        Assert.Equal([$"{b.Did}:9"], d.Svc.PinnedIds(d.Rec));
    }

    [Fact]
    public async Task MemberProfile_cannotBeForged_orPushedByAStranger()
    {
        var (_, p) = Cluster_("a", "b", "c", "x");
        var (a, b, c, x) = (p[0], p[1], p[2], p[3]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        MemberProfile Mp(Peer claimed, Peer signer, string nick, long ts = 10) => new()
        {
            GroupId = a.Rec.GroupId, Id = claimed.Did, Nickname = nick, Ts = ts,
            Sig = MessageCrypto.Sign(GroupCrypto.MemberProfileSigData(a.Rec.GroupId, claimed.Did, nick, null, ts), signer.Id.SigningPrivateKey),
        };

        // c claims to speak for b (signed by c): rejected. Even handed over by b itself.
        await a.Svc.OnLetterAsync(c.Did, "member_profile", JsonSerializer.Serialize(Mp(b, c, "Fake B")));
        await a.Svc.OnLetterAsync(b.Did, "member_profile", JsonSerializer.Serialize(Mp(b, c, "Fake B")));
        // A direct letter must come from its author.
        await a.Svc.OnLetterAsync(c.Did, "member_profile", JsonSerializer.Serialize(Mp(b, b, "Real B but relayed by c")));
        // A non-member's profile and a non-member sender are ignored.
        await a.Svc.OnLetterAsync(x.Did, "member_profile", JsonSerializer.Serialize(Mp(x, x, "Intruder")));

        Assert.Equal("b", a.Svc.Members(a.Rec).Single(m => m.DiscoveryId == b.Did).Nickname);
        Assert.DoesNotContain(a.Svc.Members(a.Rec), m => m.Nickname == "Intruder");

        Assert.Empty(a.Rec.MemberProfiles);
    }

    [Fact]
    public async Task MemberProfile_reachesALateJoiner_throughAPeer()
    {
        var (_, p) = Cluster_("a", "b", "d");
        var (a, b, d) = (p[0], p[1], p[2]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, d.Svc.Me]);
        await b.Svc.PublishMyProfileAsync("Bobby", Pic());

        b.Online = false;
        await d.GoOnlineAsync();                                              // the only peer left is a, who relays b's signed profile

        Assert.Equal("Bobby", d.Svc.Members(d.Rec).Single(m => m.DiscoveryId == b.Did).Nickname);
    }

    // ------------------------------------------------------------------
    // Descriptions (group and member)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Creator_canDescribeTheGroup_keepItAcrossRenames_andClearIt()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me, c.Svc.Me]);
        var epoch = a.Svc.CurrentEpoch(a.Rec);

        await a.Svc.SetProfileAsync(a.Rec, name: null, photo: null, description: "Weekly ops sync");

        foreach (var x in new[] { a, b, c })
        {
            Assert.Equal("Weekly ops sync", x.Svc.GroupDescription(x.Rec));
            Assert.Equal("Team", x.Rec.Name);                                   // name untouched
            Assert.Equal(epoch, x.Svc.CurrentEpoch(x.Rec));                    // no rotation
        }

        await a.Svc.SetProfileAsync(a.Rec, "Crew", Pic());                      // rename + picture: the description is kept
        Assert.Equal("Weekly ops sync", b.Svc.GroupDescription(b.Rec));

        await a.Svc.SetProfileAsync(a.Rec, name: null, photo: null, clearDescription: true);
        Assert.Null(c.Svc.GroupDescription(c.Rec));
        Assert.Equal("Crew", c.Rec.Name);
        Assert.Equal(Pic(), c.Svc.GroupPhoto(c.Rec));                           // picture untouched by the clear
    }

    [Fact]
    public async Task GroupDescription_isCreatorOnly_limited_andCoveredByTheSignature()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("Team", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.SetProfileAsync(b.Rec, null, null, description: "mine"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, null, null, description: new string('x', GroupProfileOp.MaxDescriptionLength + 1)));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.SetProfileAsync(a.Rec, null, null, description: "   "));
        await a.Svc.SetProfileAsync(a.Rec, null, null, description: new string('x', GroupProfileOp.MaxDescriptionLength));   // exactly the limit

        // A captured operation re-sent with a different description no longer verifies.
        var genuine = a.Rec.Profile!;
        var tampered = new GroupProfileOp { GroupId = genuine.GroupId, Name = genuine.Name, Photo = genuine.Photo, Description = "buy my coins", Ts = genuine.Ts + 5, Signer = genuine.Signer, Sig = genuine.Sig };
        await c.Svc.OnLetterAsync(b.Did, "group_profile", JsonSerializer.Serialize(tampered));
        Assert.Equal(new string('x', GroupProfileOp.MaxDescriptionLength), c.Svc.GroupDescription(c.Rec));

        // Oversized description from a correctly signed op (the signer ignoring the limit) is refused by receivers.
        var huge = new GroupProfileOp
        {
            GroupId = a.Rec.GroupId, Name = "Team", Description = new string('y', GroupProfileOp.MaxDescriptionLength + 1), Ts = genuine.Ts + 10, Signer = a.Did,
            Sig = MessageCrypto.Sign(GroupCrypto.GroupProfileSigData(a.Rec.GroupId, "Team", null, genuine.Ts + 10, new string('y', GroupProfileOp.MaxDescriptionLength + 1)), a.Id.SigningPrivateKey),
        };
        await c.Svc.OnLetterAsync(a.Did, "group_profile", JsonSerializer.Serialize(huge));
        Assert.Equal(new string('x', GroupProfileOp.MaxDescriptionLength), c.Svc.GroupDescription(c.Rec));
    }

    [Fact]
    public async Task GroupDescription_reachesALateJoiner_throughAPeer()
    {
        var (_, p) = Cluster_("a", "b", "d");
        var (a, b, d) = (p[0], p[1], p[2]);
        d.Online = false;
        await a.Svc.CreateAsync("Team", [b.Svc.Me, d.Svc.Me]);
        await a.Svc.SetProfileAsync(a.Rec, null, null, description: "All about the team");

        a.Online = false;
        await d.GoOnlineAsync();

        Assert.Equal("All about the team", d.Svc.GroupDescription(d.Rec));
    }

    [Fact]
    public async Task MemberDescription_isPublishedSignedLimited_andRelayed()
    {
        var (_, p) = Cluster_("a", "b", "d");
        var (a, b, d) = (p[0], p[1], p[2]);
        d.Online = false;
        await a.Svc.CreateAsync("g", [b.Svc.Me, d.Svc.Me]);

        await b.Svc.PublishMyProfileAsync("Bobby", null, "Backend dev, loves rust");
        Assert.Equal("Backend dev, loves rust", a.Svc.MemberDescription(a.Rec, b.Did));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.PublishMyProfileAsync("Bobby", null, new string('z', GroupProfileOp.MaxDescriptionLength + 1)));

        // Tampering with a captured profile's description breaks its signature.
        var mp = b.Rec.MemberProfiles.Single();
        var forged = new MemberProfile { GroupId = mp.GroupId, Id = mp.Id, Nickname = mp.Nickname, Photo = mp.Photo, Description = "send me money", Ts = mp.Ts + 1, Sig = mp.Sig };
        await a.Svc.OnLetterAsync(b.Did, "member_profile", JsonSerializer.Serialize(forged));
        Assert.Equal("Backend dev, loves rust", a.Svc.MemberDescription(a.Rec, b.Did));

        b.Online = false;
        await d.GoOnlineAsync();                                                    // d learns it from a, who relays b's signed profile
        Assert.Equal("Backend dev, loves rust", d.Svc.MemberDescription(d.Rec, b.Did));
    }

    // ------------------------------------------------------------------
    // Demotion, forgetting a group, replies
    // ------------------------------------------------------------------

    [Fact]
    public async Task Creator_canDemoteAModerator_whoStaysAMemberWithTheKey()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        await a.Svc.DemoteAsync(a.Rec, b.Did);

        foreach (var x in new[] { a, b, c }) Assert.DoesNotContain(b.Did, x.Svc.CurrentState(x.Rec).Moderators);
        Assert.False(b.Svc.IsModerator(b.Rec));
        Assert.Contains(b.Svc.Members(b.Rec), m => m.DiscoveryId == b.Did);       // still a member
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.PromoteAsync(b.Rec, c.Did));   // and no longer able to administer
        await b.Svc.SendAsync(b.Rec, "still here");
        Assert.Contains("still here", a.Texts);
    }

    [Fact]
    public async Task OnlyTheCreator_canDemoteSomeoneElse_andTheCreatorCannotBeDemoted()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);
        await a.Svc.PromoteAsync(a.Rec, c.Did);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.DemoteAsync(b.Rec, c.Did));   // a moderator cannot strip another
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.DemoteAsync(b.Rec, a.Did));   // nor the creator
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.DemoteAsync(a.Rec, a.Did));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.DemoteAsync(a.Rec, new string('7', 64)));
    }

    [Fact]
    public async Task AModerator_canStepDownThemselves()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        await b.Svc.DemoteAsync(b.Rec, b.Did);

        Assert.False(b.Svc.IsModerator(b.Rec));
        Assert.DoesNotContain(b.Did, a.Svc.CurrentState(a.Rec).Moderators);
    }

    [Fact]
    public async Task ForgedDemotion_byAModerator_isRejected_butExclusionStillDropsTheRole()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);
        await a.Svc.PromoteAsync(a.Rec, c.Did);

        // b (moderator, not creator) crafts a state stripping c's moderator role while c stays a member.
        var (forged, _) = Forge(b, b.Rec, d => d.Moderators.Remove(c.Did), recipient: a);
        await a.Svc.OnLetterAsync(b.Did, "group_state", JsonSerializer.Serialize(forged));
        Assert.Contains(c.Did, a.Svc.CurrentState(a.Rec).Moderators);
        Assert.Equal(3, a.Svc.CurrentEpoch(a.Rec));

        // Excluding a moderator (a legitimate moderator action) does remove both membership and role.
        await b.Svc.RemoveMemberAsync(b.Rec, c.Did);
        Assert.DoesNotContain(c.Did, a.Svc.CurrentState(a.Rec).Moderators);
    }

    [Fact]
    public async Task Forget_deletesALeftGroup_butRefusesAnActiveOne()
    {
        var (_, p) = Cluster_("a", "b");
        var (a, b) = (p[0], p[1]);
        await a.Svc.CreateAsync("g", [b.Svc.Me]);
        await a.Svc.SendAsync(a.Rec, "before");

        Assert.ThrowsAny<InvalidOperationException>(() => b.Svc.Forget(b.Rec));
        await b.Svc.LeaveAsync(b.Rec);
        var id = b.Rec.GroupId;
        b.Svc.Forget(b.Rec);

        Assert.Empty(b.Store.All);
        Assert.Null(new GroupStore(Path.Combine(_root, "b")).Find(id));            // gone from disk too
        await a.Svc.SendAsync(a.Rec, "after");                                    // a sealed message for a group we forgot is just unknown
        Assert.DoesNotContain("after", b.Texts);
    }

    [Fact]
    public async Task Reply_carriesTheTargetMessageId_signedAndDeliveredToEveryone()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);

        var first = await a.Svc.SendAsync(a.Rec, "question?");
        var target = $"{a.Did}:{first.Ts}";
        var answer = await b.Svc.SendAsync(b.Rec, "answer!", replyTo: target);

        Assert.Equal(target, a.Replies[(b.Did, answer.Ts)]);
        Assert.Equal(target, c.Replies[(b.Did, answer.Ts)]);
        Assert.Null(c.Replies[(a.Did, first.Ts)]);                                 // a plain message has no reply_to

        // The reply target is covered by the signature: altering it in a captured message invalidates it.
        var entry = b.Rec.Log.Last();
        var plain = GroupCrypto.Open(Convert.FromBase64String(b.Rec.Keys[0].Key), entry.Msg)!;
        var letter = JsonSerializer.Deserialize<GroupMessageLetter>(plain)!;
        letter.ReplyTo = $"{c.Did}:1";
        var tampered = GroupCrypto.Seal(Convert.FromBase64String(b.Rec.Keys[0].Key), JsonSerializer.Serialize(letter));   // re-sealed by someone holding the key
        await a.Svc.OnGroupEnvelopeAsync(b.Did, tampered, entry.Ts + 7);
        Assert.DoesNotContain(a.Replies, kv => kv.Key.Ts == entry.Ts + 7);
    }

    // ------------------------------------------------------------------
    // Creator role
    // ------------------------------------------------------------------

    [Fact]
    public async Task TransferCreator_movesTheCreatorPowers_toAnotherMember()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.BanAsync(a.Rec, c.Did);

        await a.Svc.TransferCreatorAsync(a.Rec, b.Did);                    // b was a plain member: becomes moderator too

        foreach (var x in new[] { a, b, c })
        {
            Assert.Equal(b.Did, x.Svc.CurrentState(x.Rec).CreatedBy);
            Assert.Equal(2, x.Svc.CurrentEpoch(x.Rec));
        }
        Assert.True(b.Svc.IsModerator(b.Rec));
        Assert.True(a.Svc.IsModerator(a.Rec));                             // a stays a moderator

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.UnbanAsync(a.Rec, c.Did));   // no longer the creator
        await b.Svc.UnbanAsync(b.Rec, c.Did);                              // the new creator lifts what a had set
        Assert.Empty(a.Svc.Bans(a.Rec));

        await b.Svc.RemoveMemberAsync(b.Rec, a.Did);                       // the old creator is now an ordinary moderator: it can be excluded
        Assert.False(a.Rec.Active);
    }

    [Fact]
    public async Task TransferCreator_isReservedToTheCreator_andToAMember()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => b.Svc.TransferCreatorAsync(b.Rec, c.Did));   // a moderator, not the creator
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.TransferCreatorAsync(a.Rec, a.Did));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => a.Svc.TransferCreatorAsync(a.Rec, new string('9', 64)));
    }

    [Fact]
    public async Task ForgedCreatorChange_byAModerator_isRejected()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, b.Did);                            // b is a moderator

        var (seize, _) = Forge(b, b.Rec, d => d.CreatedBy = b.Did, recipient: c);        // b tries to crown itself
        await c.Svc.OnLetterAsync(b.Did, "group_state", JsonSerializer.Serialize(seize));
        Assert.Equal(a.Did, c.Svc.CurrentState(c.Rec).CreatedBy);
        Assert.Equal(2, c.Svc.CurrentEpoch(c.Rec));

        var (evict, _) = Forge(b, b.Rec, d => { d.Members.RemoveAll(m => m.DiscoveryId == a.Did); d.Moderators.Remove(a.Did); }, recipient: c);   // or to drop the creator
        await c.Svc.OnLetterAsync(b.Did, "group_state", JsonSerializer.Serialize(evict));
        Assert.Contains(c.Svc.Members(c.Rec), m => m.DiscoveryId == a.Did);
    }

    [Fact]
    public async Task CreatorWhoLeaves_handsTheRoleToTheFirstRemainingModerator()
    {
        var (_, p) = Cluster_("a", "b", "c");
        var (a, b, c) = (p[0], p[1], p[2]);
        await a.Svc.CreateAsync("g", [b.Svc.Me, c.Svc.Me]);
        await a.Svc.PromoteAsync(a.Rec, c.Did);                            // moderators: a, c

        await a.Svc.LeaveAsync(a.Rec);

        Assert.Equal(c.Did, b.Svc.CurrentState(b.Rec).CreatedBy);          // never left pointing at someone who is gone
        Assert.DoesNotContain(b.Svc.Members(b.Rec), m => m.DiscoveryId == a.Did);
        await c.Svc.BanAsync(c.Rec, b.Did);
        await c.Svc.UnbanAsync(c.Rec, b.Did);                              // the new creator can lift bans
    }

    [Fact]
    public async Task CreatorWhoLeavesWithNoOtherModerator_promotesAndCrownsTheFirstMember()
    {
        var (_, p) = Cluster_("a", "b");
        await p[0].Svc.CreateAsync("g", [p[1].Svc.Me]);
        await p[0].Svc.LeaveAsync(p[0].Rec);

        var s = p[1].Svc.CurrentState(p[1].Rec);
        Assert.Equal(p[1].Did, s.CreatedBy);
        Assert.Contains(p[1].Did, s.Moderators);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir, best effort */ }
    }
}
