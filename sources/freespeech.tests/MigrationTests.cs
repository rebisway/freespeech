/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json;
using FreeSpeech.Client;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Storage;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>Identity migration (PROTOCOL §12) between real clients on a real server: contacts, group moderators and members, forged notices, bans.</summary>
public class MigrationTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public MigrationTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static string[] Phrases(string tag) => [Pad(tag + " a1"), Pad(tag + " a2"), Pad(tag + " a3")];

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-migration-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        return dir;
    }

    private async Task<FreeSpeechClient> ClientAsync(string name, string[] phrases)
    {
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = Path.Combine(NewDir(), name), ServerUrl = _server.Http });
        c.CreateIdentity(phrases[0], phrases[1], phrases[2], name);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    /// <summary>b scans a's QR card: a contact of each other, no step by hand.</summary>
    private static async Task BefriendAsync(FreeSpeechClient a, FreeSpeechClient b)
    {
        var (an, bn) = (a.Contacts.Count, b.Contacts.Count);
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == an + 1 && b.Contacts.Count == bn + 1), "the contact exchange did not finish");
    }

    private static async Task<GroupRecord> JoinAsync(FreeSpeechClient member, string groupId)
    {
        Assert.True(await UntilAsync(() => member.Groups.PendingGroupInvites.Any(i => i.GroupId == groupId)), $"{member.Identity.Nickname} got no invitation");
        return await member.Groups.AcceptInvitationAsync(groupId);
    }

    /// <summary>A group created by <paramref name="creator"/> with the others, who all accepted.</summary>
    private static async Task<GroupRecord> GroupAsync(string name, FreeSpeechClient creator, params FreeSpeechClient[] others)
    {
        var rec = await creator.CreateGroupAsync(name, others.Select(o => o.Identity.Nickname!));
        foreach (var o in others) await JoinAsync(o, rec.GroupId);
        Assert.True(await UntilAsync(() => others.All(o => o.Groups.Store.Find(rec.GroupId) is { Active: true })));
        return rec;
    }

    private static bool IsMember(FreeSpeechClient c, string groupId, string id) => c.Groups.Store.Find(groupId) is { } r && c.Groups.IsMember(groupId, id);

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ the pin

    [Fact]
    public void ARevocationKeyIsPinnedOnFirstSightAndNeverReplaced()
    {
        var dir = NewDir();
        using var db = ClientDatabase.Open(dir, null);
        var pins = new RevocationPinStore(db);
        Assert.True(pins.TryPin("id", "key-one"));
        Assert.True(pins.TryPin("id", "key-one"));    // the same again is fine
        Assert.False(pins.TryPin("id", "key-two"));   // another one is refused
        Assert.Equal("key-one", pins.Find("id"));
        Assert.False(pins.TryPin("other", null));
        Assert.Null(pins.Find("other"));
    }

    // ------------------------------------------------------------------ contacts

    [Fact]
    public async Task AContactSeesAVerifiedMigrationAndFollowsIt()
    {
        var alice = await ClientAsync("alice", Phrases("mig one old"));
        var bob = await ClientAsync("bob", Phrases("mig one bob"));
        await BefriendAsync(alice, bob);
        var seen = new TaskCompletionSource<FreeSpeechClient.MigrationNotice>();
        bob.MigrationRequested += n => seen.TrySetResult(n);
        var newPhrases = Phrases("mig one new");
        var oldId = alice.Identity.DiscoveryId;

        var sent = await alice.SendMovedAsync(Phrases("mig one old"), newPhrases, "alice");

        Assert.Equal(1, sent.Recipients);
        Assert.Same(seen.Task, await Task.WhenAny(seen.Task, Task.Delay(15000)));
        var notice = await seen.Task;
        Assert.Equal(oldId, notice.OldId);
        Assert.Equal(sent.NewId, notice.NewCard.DiscoveryId);
        Assert.Contains(notice.OldId, bob.PendingMigrations.Select(n => n.OldId));

        await bob.AcceptMigrationAsync(oldId);

        var moved = bob.Contacts.First(c => c.DiscoveryId == oldId);
        Assert.Equal(sent.NewId, moved.MovedTo);
        Assert.Contains(bob.Contacts, c => c.DiscoveryId == sent.NewId);
        Assert.Empty(bob.PendingMigrations);
        var toOld = new Conversation(oldId, oldId, null);
        var ex = await Assert.ThrowsAsync<ClientException>(() => bob.SendAsync(toOld, "hello?"));
        Assert.Equal("contact_moved", ex.Code);   // nothing more is sent to the abandoned identifier
    }

    [Fact]
    public async Task ANoticeSignedByTheIdentityKeyAloneIsIgnored()
    {
        // A thief holds the identity keys of the stolen phone, never the phrases: the revocation key is out of his reach.
        var alice = await ClientAsync("alice", Phrases("mig forged old"));
        var bob = await ClientAsync("bob", Phrases("mig forged bob"));
        await BefriendAsync(alice, bob);
        var raised = 0;
        bob.MigrationRequested += _ => Interlocked.Increment(ref raised);

        var thiefIdentity = IdentityService.Derive(Phrases("mig forged thief")[0], Phrases("mig forged thief")[1], Phrases("mig forged thief")[2], "thief");
        var card = new FreeSpeech.Client.Contacts.ContactCard
        {
            Nickname = "alice", DiscoveryId = thiefIdentity.DiscoveryId,
            EncryptionPublicKey = Convert.ToBase64String(thiefIdentity.EncryptionPublicKey), SigningPublicKey = Convert.ToBase64String(thiefIdentity.SigningPublicKey),
            Servers = [new() { Url = _server.Http, Priority = 0 }],
        };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var forged = new FreeSpeechClient.MovedOp
        {
            OldId = alice.Identity.DiscoveryId, NewCard = card, Ts = ts,
            Sig = Convert.ToBase64String(Primitives.Ed25519Sign(alice.Identity.SigningPrivateKey, FreeSpeechClient.MovedSigned(alice.Identity.DiscoveryId, card.DiscoveryId, ts))),
        };

        await alice.SendLetterForTestsAsync(bob.Identity.DiscoveryId, "identity_moved", JsonSerializer.Serialize(forged));

        await Task.Delay(2500);
        Assert.Equal(0, Volatile.Read(ref raised));
        Assert.Empty(bob.PendingMigrations);
        Assert.Null(bob.Contacts.First(c => c.DiscoveryId == alice.Identity.DiscoveryId).MovedTo);
    }

    [Fact]
    public async Task ANoticeIsIgnoredWhenNoRevocationKeyWasPinned()
    {
        var alice = await ClientAsync("alice", Phrases("mig nopin old"));
        var bob = await ClientAsync("bob", Phrases("mig nopin bob"));
        alice.Identity.RevocationPublicKey = null;   // a card from before the feature: it carries no key to pin
        await BefriendAsync(alice, bob);
        var raised = 0;
        bob.MigrationRequested += _ => Interlocked.Increment(ref raised);

        await alice.SendMovedAsync(Phrases("mig nopin old"), Phrases("mig nopin new"), "alice");

        await Task.Delay(2500);
        Assert.Equal(0, Volatile.Read(ref raised));   // never accepted on faith
    }

    [Fact]
    public async Task AutoAcceptFollowsAnUnverifiedContactAndNeverAVerifiedOne()
    {
        var alice = await ClientAsync("alice", Phrases("mig auto old"));
        var bob = await ClientAsync("bob", Phrases("mig auto bob"));
        var carol = await ClientAsync("carol", Phrases("mig auto carol"));
        await BefriendAsync(alice, bob);
        await BefriendAsync(alice, carol);
        bob.AutoAcceptMigration = carol.AutoAcceptMigration = true;
        bob.Contacts.First(c => c.DiscoveryId == alice.Identity.DiscoveryId).VerifiedAt = DateTime.UtcNow;   // bob compared the safety number

        await alice.SendMovedAsync(Phrases("mig auto old"), Phrases("mig auto new"), "alice");

        Assert.True(await UntilAsync(() => carol.Contacts.First(c => c.DiscoveryId == alice.Identity.DiscoveryId).MovedTo != null), "carol did not follow");
        Assert.True(await UntilAsync(() => bob.PendingMigrations.Count == 1));
        await Task.Delay(500);
        Assert.Null(bob.Contacts.First(c => c.DiscoveryId == alice.Identity.DiscoveryId).MovedTo);   // a verified contact decides by hand
    }

    [Fact]
    public async Task APeerOfTheOldPhrasesCannotReuseTheSameOldPhrasesAsNew()
    {
        var alice = await ClientAsync("alice", Phrases("mig same old"));
        var ex = await Assert.ThrowsAsync<ClientException>(() => alice.SendMovedAsync(Phrases("mig same old"), Phrases("mig same old"), "alice"));
        Assert.Equal("migrate_same_phrases", ex.Code);
        var wrong = await Assert.ThrowsAsync<ClientException>(() => alice.SendMovedAsync(Phrases("mig someone else"), Phrases("mig same new"), "alice"));
        Assert.Equal("wipe_wrong_phrases", wrong.Code);   // the old phrases must be the ones of this identity
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task AModeratorExcludesAMigratedMemberWithAKeyRotationThenInvitesTheNewIdentity()
    {
        // alice is an ordinary member of bob's group; carol is another member.
        var alice = await ClientAsync("alice", Phrases("mig grp member old"));
        var bob = await ClientAsync("bob", Phrases("mig grp member bob"));
        var carol = await ClientAsync("carol", Phrases("mig grp member carol"));
        await BefriendAsync(bob, alice);
        await BefriendAsync(bob, carol);
        await BefriendAsync(alice, carol);
        var rec = await GroupAsync("friends", bob, alice, carol);
        var oldId = alice.Identity.DiscoveryId;
        var epoch = bob.Groups.CurrentEpoch(rec);

        var sent = await alice.SendMovedAsync(Phrases("mig grp member old"), Phrases("mig grp member new"), "alice");

        // The moderator cuts the old identifier off at once: a new epoch, a new key, the old identifier is gone from the state.
        Assert.True(await UntilAsync(() => !IsMember(bob, rec.GroupId, oldId)), "bob did not exclude the old identity");
        Assert.True(bob.Groups.CurrentEpoch(rec) > epoch);
        Assert.True(await UntilAsync(() => !IsMember(carol, rec.GroupId, oldId)), "carol still sees the old identity");
        Assert.False(IsMember(bob, rec.GroupId, sent.NewId));   // the new one waits for the user's decision

        await bob.AcceptMigrationAsync(oldId);

        Assert.True(IsMember(bob, rec.GroupId, sent.NewId));    // invited into a new epoch
        Assert.True(await UntilAsync(() => IsMember(carol, rec.GroupId, sent.NewId)));
    }

    [Fact]
    public async Task ACreatorHandsTheGroupOverThenMigrates()
    {
        var alice = await ClientAsync("alice", Phrases("mig grp creator old"));
        var bob = await ClientAsync("bob", Phrases("mig grp creator bob"));
        var carol = await ClientAsync("carol", Phrases("mig grp creator carol"));
        await BefriendAsync(alice, bob);
        await BefriendAsync(alice, carol);
        await BefriendAsync(bob, carol);
        var rec = await GroupAsync("alice's group", alice, bob, carol);
        var oldId = alice.Identity.DiscoveryId;

        var roles = alice.RolesToHandOver();
        var role = Assert.Single(roles);
        Assert.True(role.IsCreator);
        Assert.Equal(2, role.Candidates.Count);
        var skipped = await alice.HandOverRolesAsync(new Dictionary<string, string> { [rec.GroupId] = bob.Identity.DiscoveryId });
        Assert.Empty(skipped);

        Assert.True(await UntilAsync(() => bob.Groups.CurrentState(rec).CreatedBy == bob.Identity.DiscoveryId), "bob is not the creator");
        Assert.DoesNotContain(oldId, bob.Groups.CurrentState(rec).Moderators);   // alice stepped down

        var sent = await alice.SendMovedAsync(Phrases("mig grp creator old"), Phrases("mig grp creator new"), "alice");

        Assert.True(await UntilAsync(() => !IsMember(bob, rec.GroupId, oldId)), "bob did not exclude the old identity");
        Assert.True(await UntilAsync(() => !IsMember(carol, rec.GroupId, oldId)));
        Assert.Equal(bob.Identity.DiscoveryId, bob.Groups.CurrentState(rec).CreatedBy);
        Assert.Contains(sent.NewId, bob.PendingMigrations.Select(n => n.NewCard.DiscoveryId));
    }

    [Fact]
    public async Task ACreatorWhoDidNotHandOverStaysInTheGroup()
    {
        var alice = await ClientAsync("alice", Phrases("mig grp stay old"));
        var bob = await ClientAsync("bob", Phrases("mig grp stay bob"));
        await BefriendAsync(alice, bob);
        var rec = await GroupAsync("alice's group", alice, bob);
        await alice.Groups.PromoteAsync(rec, bob.Identity.DiscoveryId);
        Assert.True(await UntilAsync(() => bob.Groups.IsModerator(bob.Groups.Store.Find(rec.GroupId)!)));
        var oldId = alice.Identity.DiscoveryId;

        await alice.SendMovedAsync(Phrases("mig grp stay old"), Phrases("mig grp stay new"), "alice");

        Assert.True(await UntilAsync(() => bob.PendingMigrations.Count == 1));
        await Task.Delay(500);
        Assert.True(IsMember(bob, rec.GroupId, oldId));   // the creator cannot be excluded: the role has to be handed over first
    }

    [Fact]
    public async Task ABannedIdentifierCannotMigrate()
    {
        var alice = await ClientAsync("alice", Phrases("mig ban old"));
        var bob = await ClientAsync("bob", Phrases("mig ban bob"));
        await BefriendAsync(bob, alice);
        var rec = await GroupAsync("rules", bob, alice);
        await bob.Groups.BanAsync(rec, alice.Identity.DiscoveryId);
        var raised = 0;
        bob.MigrationRequested += _ => Interlocked.Increment(ref raised);

        await alice.SendMovedAsync(Phrases("mig ban old"), Phrases("mig ban new"), "alice");

        await Task.Delay(2500);
        Assert.Equal(0, Volatile.Read(ref raised));   // refused: a ban is not a way to a fresh start
        Assert.Empty(bob.PendingMigrations);
    }

    [Fact]
    public async Task ARevocationKeyAnnouncedToAGroupIsPinnedByPeersWhoAreNotContacts()
    {
        // carol is not alice's contact: she learns alice's revocation key from the signed member profile, so she can check a notice from alice.
        var alice = await ClientAsync("alice", Phrases("mig announce old"));
        var bob = await ClientAsync("bob", Phrases("mig announce bob"));
        var carol = await ClientAsync("carol", Phrases("mig announce carol"));
        await BefriendAsync(bob, alice);
        await BefriendAsync(bob, carol);
        var rec = await GroupAsync("strangers", bob, alice, carol);

        await alice.AnnounceRevocationKeyAsync();

        Assert.True(await UntilAsync(() => carol.PinnedRevocationKey(alice.Identity.DiscoveryId) != null), "carol did not pin alice's key");
        Assert.Equal(Convert.ToBase64String(alice.Identity.RevocationPublicKey!), carol.PinnedRevocationKey(alice.Identity.DiscoveryId));
        _ = rec;
    }

    // ------------------------------------------------------------------ the whole way

    [Fact]
    public async Task TheOwnerSwitchesToTheNewIdentityAndIsInvitedBackIntoTheGroup()
    {
        var oldPhrases = Phrases("mig full old");
        var newPhrases = Phrases("mig full new");
        var alice = await ClientAsync("alice", oldPhrases);
        var bob = await ClientAsync("bob", Phrases("mig full bob"));
        await BefriendAsync(bob, alice);
        var rec = await GroupAsync("friends", bob, alice);
        var aliceDir = alice.ProfileDir;
        var oldId = alice.Identity.DiscoveryId;

        var sent = await alice.SendMovedAsync(oldPhrases, newPhrases, "alice");
        Assert.True(await UntilAsync(() => bob.PendingMigrations.Count == 1));
        Assert.Equal(sent.NewId, alice.OutgoingMigrationId);

        // The wrong phrases do not switch: only the ones announced (they prove the owner saved them right).
        var wrong = Assert.Throws<ClientException>(() => alice.SwitchToNewIdentity(Phrases("mig full other")));
        Assert.Equal("migrate_phrases_mismatch", wrong.Code);
        alice.SwitchToNewIdentity(newPhrases);
        await alice.DisposeAsync();
        _clients.Remove(alice);

        // The profile opens again as the new identity: contacts and history stay, the group is archived.
        var again = new FreeSpeechClient(new ClientOptions { ProfileDir = aliceDir, ServerUrl = _server.Http });
        Assert.True(again.TryLoadIdentity());
        Assert.Equal(sent.NewId, again.Identity.DiscoveryId);
        Assert.NotEqual(oldId, again.Identity.DiscoveryId);
        again.Initialize();
        await again.ConnectAsync();
        _clients.Add(again);
        Assert.Contains(again.Contacts, c => c.DiscoveryId == bob.Identity.DiscoveryId);
        Assert.False(again.Groups.Store.Find(rec.GroupId)!.Active);

        // Bob follows, the new identity is invited, accepts, and the group is back — with its history.
        await bob.AcceptMigrationAsync(oldId);
        var back = await JoinAsync(again, rec.GroupId);
        Assert.True(back.Active);
        Assert.True(IsMember(bob, rec.GroupId, sent.NewId));
        Assert.False(IsMember(bob, rec.GroupId, oldId));
        Assert.Equal(sent.NewId, bob.Contacts.First(c => c.DiscoveryId == oldId).MovedTo);

        // And they talk again, under the new identity.
        var received = new TaskCompletionSource<string>();
        bob.MessageReceived += m => received.TrySetResult(m.Text);
        await again.SendAsync(new Conversation(bob.Identity.DiscoveryId, bob.Identity.DiscoveryId, null), "I am back");
        Assert.Same(received.Task, await Task.WhenAny(received.Task, Task.Delay(15000)));
        Assert.Equal("I am back", await received.Task);
    }
}
