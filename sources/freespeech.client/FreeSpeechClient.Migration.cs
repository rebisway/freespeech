/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client;

// Identity migration (PROTOCOL §12): after a theft, the owner moves to a NEW identity (new phrases) and tells contacts and group moderators
// with an `identity_moved` letter signed by the OLD identity's revocation key (derived from the old phrases, never stored). A receiver trusts
// it only against the revocation key it pinned earlier; the decision to follow is the user's (one tap), and a moderator excludes the old
// identifier from the groups at once (with a key rotation) and invites the new one once the user accepted.
public sealed partial class FreeSpeechClient
{
    private const string TypeMoved = "identity_moved";   // listed in PROTOCOL §5 "Types de Letter"

    private RevocationPinStore _pins = null!;
    private DocCollection<MigrationNotice>? _noticeDocs;
    private List<MigrationNotice>? _notices;
    private DocCollection<MigrationOut>? _outDocs;

    /// <summary>A contact, or a member of a group I moderate, announced a new identity and the notice checks out: the user decides (<see cref="AcceptMigrationAsync"/> / <see cref="DeclineMigration"/>).</summary>
    public event Action<MigrationNotice>? MigrationRequested;

    /// <summary>Accept a verified migration of a contact without asking. Never applied to a contact I verified with the safety number. Off by default.</summary>
    public bool AutoAcceptMigration { get; set; }

    /// <summary>A moderator excludes the old identifier from the groups as soon as a verified migration arrives (the stolen phone stops reading). On by default.</summary>
    public bool AutoExcludeMigrated { get; set; } = true;

    /// <summary>The revocation key (base64) pinned for an identity, or null — what lets a notice from them be checked.</summary>
    public string? PinnedRevocationKey(string discoveryId) => _pins.Find(discoveryId);

    /// <summary>Tests only: sends a raw 1-to-1 letter (a forged migration notice, for instance).</summary>
    internal Task<bool> SendLetterForTestsAsync(string to, string type, string payload) => SendLetterOnceAsync(to, type, payload);

    /// <summary>What a revocation signature covers for a migration: the old and the new identifier and the moment.</summary>
    internal static byte[] MovedSigned(string oldId, string newId, long ts) => Encoding.UTF8.GetBytes($"FreeSpeech-moved-v1|{oldId}|{newId}|{ts}");

    /// <summary>The payload of an <c>identity_moved</c> letter.</summary>
    public sealed class MovedOp
    {
        [JsonPropertyName("old_id")] public string OldId { get; set; } = "";
        [JsonPropertyName("new_card")] public ContactCard? NewCard { get; set; }
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("sig")] public string Sig { get; set; } = "";
    }

    /// <summary>A verified migration notice I received and have not answered yet (or answered): who moved to which identity.</summary>
    public sealed class MigrationNotice
    {
        [JsonPropertyName("old_id")] public string OldId { get; set; } = "";
        [JsonPropertyName("old_name")] public string OldName { get; set; } = "";
        [JsonPropertyName("new_card")] public ContactCard NewCard { get; set; } = null!;
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("received_at")] public DateTime ReceivedAt { get; set; }

        /// <summary>False for a member of a group I moderate who is not one of my contacts.</summary>
        [JsonPropertyName("from_contact")] public bool FromContact { get; set; }

        /// <summary>"pending", "accepted" or "declined".</summary>
        [JsonPropertyName("state")] public string State { get; set; } = "pending";

        /// <summary>Groups I excluded the old identifier from, to invite the new one when the user accepts.</summary>
        [JsonPropertyName("excluded_groups")] public List<string> ExcludedGroups { get; set; } = [];
    }

    private sealed class MigrationOut
    {
        [JsonPropertyName("new_id")] public string NewId { get; set; } = "";
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("sent_at")] public DateTime SentAt { get; set; }
    }

    /// <summary>What <see cref="SendMovedAsync"/> reached: contacts and group moderators told, and how many got it at once (the others are queued).</summary>
    public sealed record MigrationSendResult(string NewId, int Recipients, int Delivered, int Queued);

    /// <summary>A group where I hold a role, and who could take it over.</summary>
    public sealed record RoleHandover(GroupRecord Group, bool IsCreator, IReadOnlyList<GroupMember> Candidates);

    private List<MigrationNotice> Notices
    {
        get
        {
            if (_notices == null)
            {
                _noticeDocs = new DocCollection<MigrationNotice>(_db, "migration_notices", n => n.OldId);
                _notices = _noticeDocs.Load();
            }
            return _notices;
        }
    }

    private void SaveNotices() => _noticeDocs!.Save(_notices!);

    private DocCollection<MigrationOut> OutDocs => _outDocs ??= new DocCollection<MigrationOut>(_db, "migration_out", _ => "out");

    /// <summary>Notices waiting for my answer.</summary>
    public IReadOnlyList<MigrationNotice> PendingMigrations => Notices.Where(n => n.State == "pending").ToList();

    /// <summary>The identifier I announced as my new identity, if a migration was sent and I have not switched yet.</summary>
    public string? OutgoingMigrationId => OutDocs.Load().FirstOrDefault()?.NewId;

    // ------------------------------------------------------------------
    // Announcing my revocation key to group peers
    // ------------------------------------------------------------------

    /// <summary>
    /// Announces my revocation key (a signed member profile) to the groups that have not seen it yet, so group peers who are not my contacts can
    /// pin it too. Once per group; call it after connecting.
    /// </summary>
    public async Task AnnounceRevocationKeyAsync()
    {
        if (_identity.RevocationPublicKey == null) return;
        var todo = _groups.Store.All.Where(r => r.Active && !_identity.RevocationAnnouncedGroups.Contains(r.GroupId)).Select(r => r.GroupId).ToHashSet();
        if (todo.Count == 0) return;
        await _groups.PublishMyProfileAsync(_identity.Nickname ?? _identity.DiscoveryId[..8], _identity.Photo, _identity.Description, todo);
        _identity.RevocationAnnouncedGroups.AddRange(todo);
        _identityStorage.Save(_identity);
    }

    // ------------------------------------------------------------------
    // Sending a migration
    // ------------------------------------------------------------------

    /// <summary>
    /// Step 1 on the sender side: the groups where I am creator or moderator and who could take the role over (moderators first). A creator
    /// cannot be excluded, so the role has to be handed over before the old identifier is cut off.
    /// </summary>
    public IReadOnlyList<RoleHandover> RolesToHandOver()
    {
        var list = new List<RoleHandover>();
        foreach (var rec in _groups.Store.All.Where(r => r.Active))
        {
            var state = _groups.CurrentState(rec);
            if (!state.Moderators.Contains(_identity.DiscoveryId)) continue;
            var others = _groups.Members(rec).Where(m => m.DiscoveryId != _identity.DiscoveryId).ToList();
            var candidates = others.OrderBy(m => state.Moderators.Contains(m.DiscoveryId) ? 0 : 1).ToList();
            list.Add(new RoleHandover(rec, state.CreatedBy == _identity.DiscoveryId, candidates));
        }
        return list;
    }

    /// <summary>
    /// Hands my roles over, group by group: as creator, the creator role goes to the chosen member (who becomes a moderator) and I step down;
    /// as a mere moderator I step down. <paramref name="successors"/> maps a group id to the member id; a group of which I am creator with no
    /// entry is skipped (returned in the list), since its role cannot be dropped without someone to take it. Returns the groups left aside.
    /// </summary>
    public async Task<IReadOnlyList<string>> HandOverRolesAsync(IReadOnlyDictionary<string, string> successors)
    {
        var skipped = new List<string>();
        foreach (var h in RolesToHandOver())
        {
            if (h.IsCreator)
            {
                if (!successors.TryGetValue(h.Group.GroupId, out var successor)) { skipped.Add(h.Group.GroupId); continue; }
                await _groups.TransferCreatorAsync(h.Group, successor);
            }
            await _groups.DemoteAsync(h.Group, _identity.DiscoveryId);
        }
        return skipped;
    }

    /// <summary>
    /// Step 2 on the sender side, run on the device that holds the OLD identity (restored with the old phrases): signs the migration with the
    /// revocation key derived from them, then sends it to every contact and to the moderators of my groups (queued when offline). The new
    /// identity is derived from <paramref name="newPhrases"/>; the app switches to it later (<see cref="SwitchToNewIdentity"/>), once the
    /// notice has been delivered — the queued letters are signed by the old identity and cannot be sent after the switch.
    /// </summary>
    public async Task<MigrationSendResult> SendMovedAsync(string[] oldPhrases, string[] newPhrases, string? nickname = null)
    {
        if (oldPhrases.Length != 3 || newPhrases.Length != 3) throw new ArgumentException("Three phrases each are expected.");
        CheckPhrasesAreMine(oldPhrases[0], oldPhrases[1], oldPhrases[2]);
        UserIdentity fresh;
        try { fresh = IdentityService.Derive(newPhrases[0], newPhrases[1], newPhrases[2], nickname ?? _identity.Nickname); }
        catch (ArgumentException ex) { throw new ClientException(ex.Message, "migrate_bad_phrases"); }
        if (fresh.DiscoveryId == _identity.DiscoveryId) throw new ClientException("The new phrases are the old ones: a new identity needs new phrases.", "migrate_same_phrases");

        var (seed, pub) = IdentityService.DeriveRevocationKey(oldPhrases[0], oldPhrases[1], oldPhrases[2]);
        if (_identity.RevocationPublicKey == null || !_identity.RevocationPublicKey.AsSpan().SequenceEqual(pub))
        {
            _identity.RevocationPublicKey = pub;
            _identityStorage.Save(_identity);
        }

        var card = new ContactCard
        {
            Nickname = nickname ?? _identity.Nickname ?? fresh.DiscoveryId[..8],
            Photo = _identity.Photo, Description = _identity.Description,
            DiscoveryId = fresh.DiscoveryId,
            EncryptionPublicKey = Convert.ToBase64String(fresh.EncryptionPublicKey),
            SigningPublicKey = Convert.ToBase64String(fresh.SigningPublicKey),
            RevocationPublicKey = Convert.ToBase64String(fresh.RevocationPublicKey!),
            Servers = Relay.RoutePolicy.CardServers(_identity.Servers),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var op = new MovedOp { OldId = _identity.DiscoveryId, NewCard = card, Ts = ts, Sig = Convert.ToBase64String(Primitives.Ed25519Sign(seed, MovedSigned(_identity.DiscoveryId, fresh.DiscoveryId, ts))) };
        var payload = JsonSerializer.Serialize(op);

        // Everyone who has to know: my contacts, and the moderators of my groups (they decide who is in the group).
        var recipients = new Dictionary<string, GroupMember>();
        foreach (var c in _confirmedContacts.All.Where(c => c.MovedTo == null)) recipients[c.DiscoveryId] = ToGroupMember(c);
        foreach (var rec in _groups.Store.All.Where(r => r.Active))
        {
            var state = _groups.CurrentState(rec);
            foreach (var m in _groups.Members(rec).Where(m => state.Moderators.Contains(m.DiscoveryId) && m.DiscoveryId != _identity.DiscoveryId))
                recipients.TryAdd(m.DiscoveryId, m);
        }

        int delivered = 0, queued = 0;
        foreach (var m in recipients.Values)
        {
            if (await ((IGroupTransport)this).SendLetterAsync(m, TypeMoved, payload, queue: true)) delivered++; else queued++;
        }

        OutDocs.Save([new MigrationOut { NewId = fresh.DiscoveryId, Ts = ts, SentAt = DateTime.UtcNow }]);
        return new MigrationSendResult(fresh.DiscoveryId, recipients.Count, delivered, queued);
    }

    /// <summary>Migration letters still waiting for an offline recipient: the old identity must stay until they are gone (or the user gives up).</summary>
    public int PendingMigrationLetters => _pendingMessages.Unsent.Count(p => p.Type == TypeMoved);

    /// <summary>
    /// Last step on the sender side: becomes the new identity inside this profile. The new phrases must give the identifier that was announced
    /// (it proves they were saved right). Contacts, history and groups stay; the groups become archived (the new identifier is not a member until
    /// a moderator invites it); messages not yet delivered are dropped (they were signed by the old identity). The local database is re-keyed with
    /// the new phrases' key. This object is stale afterwards: dispose it and open the profile again.
    /// </summary>
    public void SwitchToNewIdentity(string[] newPhrases)
    {
        var announced = OutDocs.Load().FirstOrDefault() ?? throw new ClientException("No migration was sent: nothing to switch to.", "migrate_not_sent");
        UserIdentity fresh;
        try { fresh = IdentityService.Derive(newPhrases[0], newPhrases[1], newPhrases[2], _identity.Nickname); }
        catch (ArgumentException ex) { throw new ClientException(ex.Message, "migrate_bad_phrases"); }
        if (fresh.DiscoveryId != announced.NewId) throw new ClientException("These phrases are not the new ones that were announced.", "migrate_phrases_mismatch");

        var key = IdentityService.DeriveDatabaseKey(newPhrases[0], newPhrases[1], newPhrases[2]);
        fresh.Photo = _identity.Photo;
        fresh.Description = _identity.Description;
        fresh.PublicServer = _identity.PublicServer;
        fresh.Servers = _identity.Servers.Select(s => new ServerEntry { Url = s.Url, Priority = s.Priority, Restricted = s.Restricted }).ToList();
        fresh.RetiredServers = _identity.RetiredServers.ToList();

        foreach (var rec in _groups.Store.All.Where(r => r.Active)) rec.Active = false;
        _groups.Store.Save();
        foreach (var p in _pendingMessages.All.ToList()) _pendingMessages.Remove(p.Id);
        OutDocs.Save([]);

        _identityStorage.Save(fresh);
        _db.Rekey(key);
        _keyCache.Store(key);
    }

    // ------------------------------------------------------------------
    // Receiving a migration
    // ------------------------------------------------------------------

    private async Task HandleMovedSafeAsync(string from, string payload, bool fromContact)
    {
        try { await HandleMovedAsync(from, payload, fromContact); }
        catch (Exception ex) when (ex is ClientException or InvalidOperationException or JsonException or FormatException)
        {
            RaiseNotice($"[!] Migration notice from {from} could not be processed: {ex.Message}");
        }
    }

    private static bool CardIdMatchesKeys(ContactCard c)
    {
        try
        {
            var combined = Convert.FromBase64String(c.EncryptionPublicKey).Concat(Convert.FromBase64String(c.SigningPublicKey)).ToArray();
            return combined.Length == 64 && Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant() == c.DiscoveryId;
        }
        catch (FormatException) { return false; }
    }

    private async Task HandleMovedAsync(string from, string payload, bool fromContact)
    {
        var op = JsonSerializer.Deserialize<MovedOp>(payload);
        var card = op?.NewCard;
        if (op == null || card == null || op.OldId != from || card.DiscoveryId == from || card.Servers.Count == 0 || !CardIdMatchesKeys(card))
        {
            RaiseNotice($"[!] Malformed identity_moved from {from} — dropped.");
            return;
        }

        // The notice counts only against the revocation key I pinned earlier: nothing is taken on faith.
        var pinned = _pins.Find(from);
        if (pinned == null)
        {
            RaiseNotice($"[!] {from} announces a new identity, but I never saw their revocation key: it cannot be checked — ignored. Exchange contact details again.");
            return;
        }
        byte[] sig;
        try { sig = Convert.FromBase64String(op.Sig); }
        catch (FormatException) { return; }
        if (!Primitives.Ed25519Verify(Convert.FromBase64String(pinned), MovedSigned(from, card.DiscoveryId, op.Ts), sig))
        {
            RaiseNotice($"[!] identity_moved from {from} has an invalid signature — ignored.");
            return;
        }

        // One migration per old identifier: the same one again is a repeat, another one is a conflict to look at.
        var known = Notices.Find(n => n.OldId == from);
        if (known != null)
        {
            if (known.NewCard.DiscoveryId != card.DiscoveryId) RaiseNotice($"[!] {from} announced two different new identities — the first one stays, check with them.");
            return;
        }

        // A banned identifier cannot change identity to get round the ban.
        foreach (var rec in _groups.Store.All.Where(r => r.Active && _groups.IsModerator(r)))
            if (_groups.Bans(rec).Contains(from))
            {
                RaiseNotice($"[!] {from} is banned from '{rec.Name}' and asked to move to a new identity — refused.");
                return;
            }

        var notice = new MigrationNotice
        {
            OldId = from, OldName = _confirmedContacts.FindById(from)?.DisplayName ?? _groups.FindMember(from)?.Nickname ?? from[..8],
            NewCard = card, Ts = op.Ts, ReceivedAt = DateTime.UtcNow, FromContact = fromContact && _confirmedContacts.FindById(from) != null,
        };

        // Moderator: the old identifier stops reading now (exclusion with a key rotation, one state).
        if (AutoExcludeMigrated)
        {
            foreach (var rec in _groups.Store.All.Where(r => r.Active && _groups.IsModerator(r) && _groups.IsMember(r.GroupId, from)).ToList())
            {
                if (_groups.CurrentState(rec).CreatedBy == from) { RaiseNotice($"[!] {from} is the creator of '{rec.Name}': the creator role must be handed over first, the old identifier stays."); continue; }
                try { await _groups.RemoveMemberAsync(rec, from); notice.ExcludedGroups.Add(rec.GroupId); }
                catch (Exception ex) when (ex is InvalidOperationException or ClientException) { RaiseNotice($"[!] Could not exclude {from} from '{rec.Name}': {ex.Message}"); }
            }
        }

        // Nothing to decide for a stranger: a peer of a group I do not moderate, and who is not my contact.
        if (!notice.FromContact && notice.ExcludedGroups.Count == 0) return;

        Notices.Add(notice);
        SaveNotices();
        MigrationRequested?.Invoke(notice);

        var contact = _confirmedContacts.FindById(from);
        if (AutoAcceptMigration && contact is { VerifiedAt: null }) await AcceptMigrationAsync(from);
    }

    /// <summary>
    /// The user follows the migration: the new identity becomes a contact (my private name kept, not the verification mark), the old one is marked
    /// as moved (nothing is sent to it any more), and in each group I excluded the old identifier from, the new one is invited.
    /// </summary>
    public async Task AcceptMigrationAsync(string oldId)
    {
        var notice = Notices.Find(n => n.OldId == oldId && n.State == "pending") ?? throw new ClientException("No migration waiting for this contact.", "migrate_none");
        var card = notice.NewCard;
        _pins.TryPin(card.DiscoveryId, card.RevocationPublicKey);

        var old = _confirmedContacts.FindById(oldId);
        if (old != null)
        {
            _confirmedContacts.Upsert(new Contact
            {
                DiscoveryId = card.DiscoveryId, Nickname = card.Nickname, Photo = card.Photo, Description = card.Description,
                EncryptionPublicKey = card.EncryptionPublicKey, SigningPublicKey = card.SigningPublicKey, Servers = card.Servers,
                ConfirmedAt = DateTime.UtcNow, LocalName = old.LocalName,
            });
            old.MovedTo = card.DiscoveryId;
            old.MovedAt = DateTime.UtcNow;
            _confirmedContacts.Upsert(old);
            ContactUpdated?.Invoke(old);
            ContactConfirmed?.Invoke(_confirmedContacts.FindById(card.DiscoveryId)!);
        }

        var member = new GroupMember
        {
            DiscoveryId = card.DiscoveryId, Nickname = card.Nickname, EncryptionPublicKey = card.EncryptionPublicKey,
            SigningPublicKey = card.SigningPublicKey, Servers = card.Servers,
        };
        foreach (var groupId in notice.ExcludedGroups)
        {
            var rec = _groups.Store.Find(groupId);
            if (rec is not { Active: true } || !_groups.IsModerator(rec) || _groups.IsMember(groupId, card.DiscoveryId)) continue;
            try { await _groups.AddMemberAsync(rec, member); }
            catch (Exception ex) when (ex is InvalidOperationException or ClientException) { RaiseNotice($"[!] Could not invite the new identity of {notice.OldName} to '{rec.Name}': {ex.Message}"); }
        }

        notice.State = "accepted";
        SaveNotices();
    }

    /// <summary>The user does not follow: the old identifier stays excluded where it was, and nothing else changes.</summary>
    public void DeclineMigration(string oldId)
    {
        var notice = Notices.Find(n => n.OldId == oldId && n.State == "pending") ?? throw new ClientException("No migration waiting for this contact.", "migrate_none");
        notice.State = "declined";
        SaveNotices();
    }
}
