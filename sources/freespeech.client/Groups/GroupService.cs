/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text.Json;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Client.Groups;

/// <summary>Outcome of sending a group message: delivered live to <c>Delivered</c> of <c>Recipients</c> (the others catch up from peers); <c>Ts</c> is its origin timestamp (message_id = "me:Ts").</summary>
public record GroupSendResult(int Delivered, int Recipients, long Ts)
{
    public void Deconstruct(out int delivered, out int recipients) { delivered = Delivered; recipients = Recipients; }
}

/// <summary>What the group layer needs from the network: 1-to-1 Letters (authenticated by the caller) and raw group envelopes.</summary>
public interface IGroupTransport
{
    /// <summary>Sends a 1-to-1 Letter of <paramref name="type"/> to a member (encrypted for the key in <paramref name="to"/>, so non-contacts work). <paramref name="queue"/> = keep and retry if unreachable (used for invitations). Returns true if delivered now.</summary>
    Task<bool> SendLetterAsync(GroupMember to, string type, string payload, bool queue);

    /// <summary>Sends an already-sealed group message (Envelope crypto=2) to a member. Returns true if delivered now.</summary>
    Task<bool> SendGroupMessageAsync(GroupMember to, string sealedMsg, long ts);
}

/// <summary>
/// Groups (protocol §7): shared key per epoch with rotation on every membership change,
/// signed GroupState, p2p catch-up. Transport-agnostic (<see cref="IGroupTransport"/>) and free of
/// console I/O so mobile/desktop clients can reuse it as is. All state changes happen under one lock;
/// network calls never do.
/// </summary>
public class GroupService
{
    public const int RelayBatch = 50;
    public const int SyncPeers = 3;
    public const int MaxSyncAttempts = 10;
    public const int MaxKeyEpochsPerRelay = 8;

    private readonly UserIdentity _me;
    private readonly GroupStore _store;
    private readonly IGroupTransport _net;
    private readonly Func<string, bool> _isConfirmedContact;
    private readonly Action<string> _notify;

    private readonly object _lock = new();
    private readonly Dictionary<string, GroupStateData> _parsed = new();
    private readonly Dictionary<string, (GroupRecord Rec, StoredKey Key)> _tags = new();

    /// <summary>A new group message was accepted: (groupId, author, ts, type, payload, replyTo). Type is "message" (payload = text) or "attachment" (payload = the offer, §6).</summary>
    public event Action<string, string, long, string, string, string?>? OnMessage;

    /// <summary>An administrator tool of a private server I am connected to (named by that server): its group invitations are heard like a contact's, and still wait for my answer.</summary>
    private readonly Func<string, bool> _isServerAdministrator;

    public GroupService(UserIdentity me, GroupStore store, IGroupTransport net, Func<string, bool> isConfirmedContact, Action<string> notify, Func<string, bool>? isServerAdministrator = null)
    {
        _me = me;
        _store = store;
        _net = net;
        _isConfirmedContact = isConfirmedContact;
        _isServerAdministrator = isServerAdministrator ?? (_ => false);
        _notify = notify;
        foreach (var rec in _store.All)
            foreach (var k in rec.Keys) _tags[k.Tag] = (rec, k);
    }

    public GroupStore Store => _store;

    /// <summary>Every 1-to-1 Letter type the group layer handles — one list, so a client cannot route some and forget others.</summary>
    public static readonly IReadOnlySet<string> GroupLetterTypes = new HashSet<string>
    {
        "group_state", "group_sync", "group_relay", "group_leave", "group_profile", "member_profile", "blacklist_update", "group_suggest", "group_pin",
    };

    public static bool IsGroupLetter(string type) => GroupLetterTypes.Contains(type);

    public GroupMember Me => new()
    {
        DiscoveryId = _me.DiscoveryId,
        Nickname = _me.Nickname ?? _me.DiscoveryId[..8],
        EncryptionPublicKey = Convert.ToBase64String(_me.EncryptionPublicKey),
        SigningPublicKey = Convert.ToBase64String(_me.SigningPublicKey),
        Servers = Relay.RoutePolicy.CardServers(_me.Servers, _me.DiscoveryId),
    };

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------

    public GroupStateData CurrentState(GroupRecord rec) { lock (_lock) return Current(rec); }

    /// <summary>Members as displayed: the signed state's entries, with each member's own chosen nickname where they published one.</summary>
    public IReadOnlyList<GroupMember> Members(GroupRecord rec)
    {
        lock (_lock)
        {
            return Current(rec).Members.Select(m =>
            {
                var mp = rec.MemberProfiles.FirstOrDefault(p => p.Id == m.DiscoveryId);
                return mp == null ? m : new GroupMember
                {
                    DiscoveryId = m.DiscoveryId, Nickname = mp.Nickname, EncryptionPublicKey = m.EncryptionPublicKey,
                    SigningPublicKey = m.SigningPublicKey, Servers = m.Servers,
                };
            }).ToList();
        }
    }

    public string? GroupPhoto(GroupRecord rec) { lock (_lock) return rec.Profile?.Photo; }

    public string? GroupDescription(GroupRecord rec) { lock (_lock) return rec.Profile?.Description; }

    public string? MemberDescription(GroupRecord rec, string memberId) { lock (_lock) return rec.MemberProfiles.FirstOrDefault(p => p.Id == memberId)?.Description; }

    public string? MemberPhoto(GroupRecord rec, string memberId) { lock (_lock) return rec.MemberProfiles.FirstOrDefault(p => p.Id == memberId)?.Photo; }

    public bool IsModerator(GroupRecord rec) { lock (_lock) return Current(rec).Moderators.Contains(_me.DiscoveryId); }

    public int CurrentEpoch(GroupRecord rec) { lock (_lock) return Current(rec).Epoch; }

    /// <summary>Is this id a current member of that group? (Serving policy for group attachments.)</summary>
    public bool IsMember(string groupId, string discoveryId)
    {
        lock (_lock)
        {
            var rec = _store.Find(groupId);
            return rec is { Active: true } && Current(rec).Members.Any(m => m.DiscoveryId == discoveryId);
        }
    }

    public bool HasCurrentKey(GroupRecord rec) { lock (_lock) return KeyFor(rec, rec.CurrentHash) != null; }

    /// <summary>A member of one of our active groups, by id — lets the caller verify a 1-to-1 Letter from someone who is not one of our contacts.</summary>
    public GroupMember? FindMember(string discoveryId)
    {
        lock (_lock)
        {
            foreach (var rec in _store.All.Where(r => r.Active))
            {
                var m = Current(rec).Members.FirstOrDefault(x => x.DiscoveryId == discoveryId);
                if (m != null) return m;
            }
            return null;
        }
    }

    /// <summary>Highest contiguous seq per author that we hold (what we announce in a sync).</summary>
    public Dictionary<string, long> HaveMap(GroupRecord rec)
    {
        lock (_lock) return Have(rec);
    }

    private Dictionary<string, long> Have(GroupRecord rec)
    {
        var have = new Dictionary<string, long>();
        foreach (var g in rec.Log.GroupBy(e => e.From))
        {
            var seqs = g.Select(e => e.Seq).ToHashSet();
            if (rec.Covered.TryGetValue(g.Key, out var covered)) seqs.UnionWith(covered);
            long n = 0;
            while (seqs.Contains(n + 1)) n++;
            have[g.Key] = n;
        }
        return have;
    }

    // ------------------------------------------------------------------
    // Composition (moderators)
    // ------------------------------------------------------------------

    public async Task<GroupRecord> CreateAsync(string name, IEnumerable<GroupMember> others)
    {
        var members = new List<GroupMember> { Me };
        foreach (var m in others)
        {
            if (!GroupCrypto.MemberIdMatchesKeys(m)) throw new InvalidOperationException($"Member {m.DiscoveryId} has keys that do not match its id.");
            if (members.All(x => x.DiscoveryId != m.DiscoveryId)) members.Add(m);
        }

        GroupRecord rec;
        List<(GroupMember M, GroupStateLetter L)> invites;
        lock (_lock)
        {
            var key = GroupCrypto.NewKey();
            var data = new GroupStateData
            {
                GroupId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Epoch = 1, PrevHash = null, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Name = name, CreatedBy = _me.DiscoveryId, Moderators = [_me.DiscoveryId], Members = members,
                KeyCheck = GroupCrypto.KeyCheck(key),
            };
            var json = JsonSerializer.Serialize(data);
            var hash = GroupCrypto.HashState(json);
            var sig = GroupCrypto.SignState(json, _me.SigningPrivateKey);

            rec = new GroupRecord { GroupId = data.GroupId, Name = name, CurrentHash = hash, Heard = [_me.DiscoveryId] };
            rec.States.Add(new StoredState { Epoch = 1, Hash = hash, Json = json, Signer = _me.DiscoveryId, Sig = sig });
            AddKey(rec, 1, hash, key);
            _store.Add(rec);
            invites = members.Where(m => m.DiscoveryId != _me.DiscoveryId)
                .Select(m => (m, new GroupStateLetter { State = json, Signer = _me.DiscoveryId, Sig = sig, KeyEnvelope = GroupCrypto.WrapKey(key, m.EncryptionPublicKey) }))
                .ToList();
        }

        foreach (var (m, l) in invites) await _net.SendLetterAsync(m, "group_state", JsonSerializer.Serialize(l), queue: true);
        return rec;
    }

    public Task AddMemberAsync(GroupRecord rec, GroupMember member)
    {
        if (!GroupCrypto.MemberIdMatchesKeys(member)) throw new InvalidOperationException("That member's keys do not match its id.");
        lock (_lock)
        {
            if (Current(rec).Members.Any(m => m.DiscoveryId == member.DiscoveryId)) throw new CodedInvalidOperationException("Already a member.", "group_already_member");
            if (BannedNow(rec, member.DiscoveryId)) throw new CodedInvalidOperationException("That person is on the group's blacklist — the creator must lift it first.", "group_banned_cant_add");
        }
        return IssueAsync(rec, new PendingOp { Kind = "add", MemberId = member.DiscoveryId, Member = member },
            d => d.Members.Add(member));
    }

    /// <summary>
    /// A moderator sends the invitation again to a member who has not answered yet: the current signed state with a fresh key envelope, queued like the first one
    /// (the member answers the same way). Nothing changes in the group: no new epoch.
    /// </summary>
    public async Task ResendInvitationAsync(GroupRecord rec, string memberId)
    {
        GroupMember member;
        GroupStateLetter letter;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (!cur.Moderators.Contains(_me.DiscoveryId)) throw new CodedInvalidOperationException("Only a moderator can change the group.", "group_only_moderator_change");
            member = cur.Members.FirstOrDefault(m => m.DiscoveryId == memberId) ?? throw new CodedInvalidOperationException("Not a member.", "group_not_member");
            if (!PendingMembers(rec).Contains(memberId)) throw new CodedInvalidOperationException("That member has already answered.", "group_already_joined");
            var stored = rec.States.First(s => s.Hash == rec.CurrentHash);
            var key = KeyFor(rec, rec.CurrentHash) ?? throw new CodedInvalidOperationException("Waiting for the group key of the current epoch — try again after the next sync.", "group_key_pending");
            letter = new GroupStateLetter { State = stored.Json, Signer = stored.Signer, Sig = stored.Sig, KeyEnvelope = GroupCrypto.WrapKey(key, member.EncryptionPublicKey) };
        }
        await _net.SendLetterAsync(member, "group_state", JsonSerializer.Serialize(letter), queue: true);
    }

    public Task RemoveMemberAsync(GroupRecord rec, string memberId)
    {
        lock (_lock)
        {
            var cur = Current(rec);
            if (memberId == _me.DiscoveryId) throw new CodedInvalidOperationException("Use leave to remove yourself.", "group_use_leave");
            if (cur.Members.All(m => m.DiscoveryId != memberId)) throw new CodedInvalidOperationException("Not a member.", "group_not_member");
            if (memberId == cur.CreatedBy) throw new CodedInvalidOperationException("The creator cannot be excluded.", "group_creator_not_excluded");
        }
        return IssueAsync(rec, new PendingOp { Kind = "remove", MemberId = memberId },
            d => { d.Members.RemoveAll(m => m.DiscoveryId == memberId); d.Moderators.Remove(memberId); });
    }

    public Task PromoteAsync(GroupRecord rec, string memberId)
    {
        lock (_lock)
        {
            var cur = Current(rec);
            if (cur.Members.All(m => m.DiscoveryId != memberId)) throw new CodedInvalidOperationException("Not a member.", "group_not_member");
            if (cur.Moderators.Contains(memberId)) throw new CodedInvalidOperationException("Already a moderator.", "group_already_moderator");
        }
        return IssueAsync(rec, new PendingOp { Kind = "promote", MemberId = memberId },
            d => d.Moderators.Add(memberId));
    }

    /// <summary>
    /// Creator only: a moderator becomes an ordinary member again (they stay in the group and keep the key — this is not
    /// an exclusion). A moderator may step down from their own role too. The spec's "vote to remove a moderator" stays
    /// deferred (§12): until then only the creator can take the role away from someone else.
    /// </summary>
    public Task DemoteAsync(GroupRecord rec, string memberId)
    {
        lock (_lock)
        {
            var cur = Current(rec);
            if (!cur.Moderators.Contains(memberId)) throw new CodedInvalidOperationException("Not a moderator.", "group_not_moderator");
            if (memberId == cur.CreatedBy) throw new CodedInvalidOperationException("The creator stays a moderator — hand the creator role over first.", "group_creator_stays_moderator");
            if (memberId != _me.DiscoveryId && cur.CreatedBy != _me.DiscoveryId) throw new CodedInvalidOperationException("Only the creator can remove someone else's moderator role.", "group_only_creator_demote");
        }
        return IssueAsync(rec, new PendingOp { Kind = "demote", MemberId = memberId },
            d => d.Moderators.Remove(memberId));
    }

    /// <summary>Deletes the local copy of a group we left or were excluded from (states, keys, log). History already shown stays in the conversation history.</summary>
    public void Forget(GroupRecord rec)
    {
        lock (_lock)
        {
            if (rec.Active) throw new CodedInvalidOperationException("You are still in this group — leave it first.", "group_still_in");
            foreach (var k in rec.Keys) _tags.Remove(k.Tag);
            _store.Remove(rec.GroupId);
        }
    }

    /// <summary>
    /// The creator hands the creator role to another member (who becomes a moderator if they were not). The
    /// former creator stays a moderator but loses the creator-only powers (lifting a ban) and the protection
    /// against exclusion; the new creator gets them.
    /// </summary>
    public Task TransferCreatorAsync(GroupRecord rec, string memberId)
    {
        lock (_lock)
        {
            var cur = Current(rec);
            if (cur.CreatedBy != _me.DiscoveryId) throw new CodedInvalidOperationException("Only the creator can hand over the creator role.", "group_only_creator_transfer");
            if (memberId == _me.DiscoveryId) throw new CodedInvalidOperationException("You already are the creator.", "group_already_creator");
            if (cur.Members.All(m => m.DiscoveryId != memberId)) throw new CodedInvalidOperationException("Not a member.", "group_not_member");
        }
        return IssueAsync(rec, new PendingOp { Kind = "transfer", MemberId = memberId },
            d =>
            {
                d.CreatedBy = memberId;
                if (!d.Moderators.Contains(memberId)) d.Moderators.Add(memberId);
            });
    }

    /// <summary>
    /// Leaves the group. A moderator issues the state without themselves (rotating the key so they can no
    /// longer read what follows; if no moderator would remain, the first remaining member is promoted).
    /// A plain member asks the moderators to do it.
    /// </summary>
    public async Task LeaveAsync(GroupRecord rec)
    {
        bool moderator;
        List<GroupMember> moderators;
        lock (_lock)
        {
            var cur = Current(rec);
            moderator = cur.Moderators.Contains(_me.DiscoveryId);
            moderators = cur.Members.Where(m => cur.Moderators.Contains(m.DiscoveryId) && m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        if (moderator)
        {
            await IssueAsync(rec, op: null, d =>
            {
                d.Members.RemoveAll(m => m.DiscoveryId == _me.DiscoveryId);
                d.Moderators.Remove(_me.DiscoveryId);
                if (d.Moderators.Count == 0 && d.Members.Count > 0) d.Moderators.Add(d.Members[0].DiscoveryId);
                // A creator who leaves hands the role over (first remaining moderator): the group must always have a present creator.
                if (d.CreatedBy == _me.DiscoveryId && d.Moderators.Count > 0) d.CreatedBy = d.Moderators[0];
            });
        }
        else
        {
            var payload = JsonSerializer.Serialize(new GroupLeaveLetter { GroupId = rec.GroupId });
            foreach (var m in moderators) await _net.SendLetterAsync(m, "group_leave", payload, queue: true);
        }

        lock (_lock)
        {
            rec.Active = false;
            _store.Save();
        }
    }

    private async Task IssueAsync(GroupRecord rec, PendingOp? op, Action<GroupStateData> mutate)
    {
        List<(GroupMember M, GroupStateLetter L, bool Queue)> outgoing;
        List<(GroupMember M, GroupStateLetter L)> farewells;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (!cur.Moderators.Contains(_me.DiscoveryId)) throw new CodedInvalidOperationException("Only a moderator can change the group.", "group_only_moderator_change");

            var next = new GroupStateData
            {
                GroupId = cur.GroupId, Epoch = cur.Epoch + 1, PrevHash = rec.CurrentHash,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Name = cur.Name, CreatedBy = cur.CreatedBy,
                Moderators = cur.Moderators.ToList(), Members = cur.Members.ToList(), KeyCheck = "",
            };
            mutate(next);

            var key = GroupCrypto.NewKey();
            next.KeyCheck = GroupCrypto.KeyCheck(key);
            var json = JsonSerializer.Serialize(next);
            var hash = GroupCrypto.HashState(json);
            var sig = GroupCrypto.SignState(json, _me.SigningPrivateKey);

            rec.States.Add(new StoredState { Epoch = next.Epoch, Hash = hash, Json = json, Signer = _me.DiscoveryId, Sig = sig });
            rec.CurrentHash = hash;
            rec.Name = rec.Profile?.Name ?? next.Name;
            AddKey(rec, next.Epoch, hash, key);
            rec.Ops.RemoveAll(o => o.Epoch <= cur.Epoch); // settled: we built on top of it
            if (op != null) { op.Epoch = next.Epoch; rec.Ops.Add(op); }

            var previous = cur.Members.Select(m => m.DiscoveryId).ToHashSet();
            outgoing = next.Members.Where(m => m.DiscoveryId != _me.DiscoveryId)
                .Select(m => (m, new GroupStateLetter { State = json, Signer = _me.DiscoveryId, Sig = sig, KeyEnvelope = GroupCrypto.WrapKey(key, m.EncryptionPublicKey),
                    Sponsor = !previous.Contains(m.DiscoveryId) && _sponsors.TryGetValue(m.DiscoveryId, out var proof) ? proof : null }, !previous.Contains(m.DiscoveryId)))
                .ToList();

            // Whoever was removed gets the signed state WITHOUT a key envelope: they learn they are out,
            // and still cannot read anything that follows.
            var remaining = next.Members.Select(m => m.DiscoveryId).ToHashSet();
            farewells = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId && !remaining.Contains(m.DiscoveryId))
                .Select(m => (m, new GroupStateLetter { State = json, Signer = _me.DiscoveryId, Sig = sig }))
                .ToList();
            _store.Save();
        }

        foreach (var (m, l, queue) in outgoing)
            await _net.SendLetterAsync(m, "group_state", JsonSerializer.Serialize(l), queue);
        foreach (var (m, l) in farewells)
            await _net.SendLetterAsync(m, "group_state", JsonSerializer.Serialize(l), queue: false);
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    private long NextTs() => MessageClock.Next();

    /// <summary>Seals one message with the current group key and sends it to every other member. Returns (delivered now, recipients); the rest catch up from peers (§7).</summary>
    public async Task<GroupSendResult> SendAsync(GroupRecord rec, string text, string type = "message", long? forcedTs = null, string? replyTo = null)
    {
        List<GroupMember> recipients;
        string sealedMsg;
        long ts;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            var key = KeyFor(rec, rec.CurrentHash) ?? throw new CodedInvalidOperationException("Waiting for the group key of the current epoch — try again after the next sync.", "group_key_pending");

            ts = forcedTs ?? NextTs(); // an attachment's ref is "author:ts", so its offer must carry the ts the caller already used
            var seq = rec.NextSeq++;
            var sig = MessageCrypto_Sign(GroupCrypto.MessageSigData(type, text, ts, rec.GroupId, cur.Epoch, seq, replyTo));
            var letter = new GroupMessageLetter { Type = type, Payload = text, GroupId = rec.GroupId, Epoch = cur.Epoch, Seq = seq, ReplyTo = replyTo, Sig = sig };
            sealedMsg = GroupCrypto.Seal(key, JsonSerializer.Serialize(letter));

            rec.Log.Add(new LogEntry { From = _me.DiscoveryId, Ts = ts, Seq = seq, Msg = sealedMsg });
            _store.Save();
            recipients = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        var delivered = 0;
        foreach (var m in recipients)
            if (await _net.SendGroupMessageAsync(m, sealedMsg, ts)) delivered++;
        return new GroupSendResult(delivered, recipients.Count, ts);
    }

    private void MarkCovered(GroupRecord rec, string from, long seq)
    {
        lock (_lock)
        {
            if (!rec.Covered.TryGetValue(from, out var list)) rec.Covered[from] = list = [];
            if (list.Contains(seq)) return;
            list.Add(seq);
            _store.Save();
        }
    }

    /// <summary>
    /// A message was deleted for everyone by its author: its sealed copy leaves the serving log, so it is never
    /// handed to a late joiner. The entry itself stays — its seq keeps the log contiguous (peers serve it as a gap
    /// marker) and a re-delivery is not processed again — only the content is emptied.
    /// </summary>
    public bool Redact(string groupId, string from, long ts, long seq = 0)
    {
        lock (_lock)
        {
            var rec = _store.Find(groupId);
            if (rec == null) return false;
            var entry = rec.Log.FirstOrDefault(e => e.From == from && e.Ts == ts);
            if (entry == null)
            {
                if (seq > 0) MarkCovered(rec, from, seq);   // never had it (offline, joined later): just account for the seq
                return false;
            }
            if (entry.Msg.Length == 0) return false;
            entry.Msg = "";
            // The edits the author made to it carry its text too: they leave the serving log as well (their seqs stay, as gap markers).
            var target = $"{from}:{ts}";
            foreach (var e in rec.Log.Where(e => e.From == from && e.Msg.Length > 0 && e != entry))
                if (IsEditOf(e.Msg, target)) e.Msg = "";
            _store.Save();
            return true;
        }
    }

    /// <summary>Is this sealed log entry a <c>message_edit</c> aimed at <paramref name="target"/>? (call under _lock)</summary>
    private bool IsEditOf(string sealedMsg, string target)
    {
        var tag = GroupCrypto.TagOf(sealedMsg);
        if (tag == null || !_tags.TryGetValue(tag, out var found)) return false;
        var plain = GroupCrypto.Open(Convert.FromBase64String(found.Item2.Key), sealedMsg);
        if (plain == null) return false;
        try
        {
            var l = JsonSerializer.Deserialize<GroupMessageLetter>(plain);
            if (l == null || l.Type != "message_edit") return false;
            using var doc = JsonDocument.Parse(l.Payload);
            return doc.RootElement.TryGetProperty("target", out var t) && t.GetString() == target;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The seq this sender gave to the message sent at <paramref name="ts"/>, or 0 if unknown — it travels in a delete-for-everyone so peers can account for the gap.</summary>
    public long SeqOf(string groupId, string from, long ts)
    {
        lock (_lock) return _store.Find(groupId)?.Log.FirstOrDefault(e => e.From == from && e.Ts == ts)?.Seq ?? 0;
    }

    private string MessageCrypto_Sign(string data) => Contacts.MessageCrypto.Sign(data, _me.SigningPrivateKey);

    /// <summary>A group envelope (crypto=2) arrived from <paramref name="from"/>.</summary>
    public Task<bool> OnGroupEnvelopeAsync(string from, string sealedMsg, long ts) =>
        Task.FromResult(ProcessMessage(from, sealedMsg, ts, relayed: false));

    private bool ProcessMessage(string from, string sealedMsg, long ts, bool relayed)
    {
        string groupId;
        string type, payload;
        string? replyTo;
        lock (_lock)
        {
            var tag = GroupCrypto.TagOf(sealedMsg);
            if (tag == null || !_tags.TryGetValue(tag, out var entry)) return false;
            var (rec, sk) = entry;

            var plain = GroupCrypto.Open(Convert.FromBase64String(sk.Key), sealedMsg);
            if (plain == null) return false;
            GroupMessageLetter? l;
            try { l = JsonSerializer.Deserialize<GroupMessageLetter>(plain); }
            catch (JsonException) { return false; }
            if (l == null || l.GroupId != rec.GroupId || l.Epoch != sk.Epoch) return false;

            var keyState = ParseStored(rec, sk.StateHash);
            var author = keyState?.Members.FirstOrDefault(m => m.DiscoveryId == from);
            if (author == null) return false; // not a member at that epoch
            if (!Contacts.MessageCrypto.Verify(GroupCrypto.MessageSigData(l.Type, l.Payload, ts, l.GroupId, l.Epoch, l.Seq, l.ReplyTo), l.Sig, Convert.FromBase64String(author.SigningPublicKey)))
                return false;

            // A removed member cannot inject under the old key: only a current member may send directly.
            // Their genuine older messages still reach us relayed by a current member.
            var isCurrentMember = rec.Active && Current(rec).Members.Any(m => m.DiscoveryId == from);
            if (!isCurrentMember && !relayed) return false;
            if (BannedNow(rec, from)) return false; // blacklisted author: not shown, not stored, therefore never relayed
            if (rec.Log.Any(e => e.From == from && e.Ts == ts)) return false;

            rec.Log.Add(new LogEntry { From = from, Ts = ts, Seq = l.Seq, Msg = sealedMsg });
            Hear(rec, from);
            _store.Save();
            groupId = rec.GroupId;
            (type, payload, replyTo) = (l.Type, l.Payload, l.ReplyTo);
        }

        OnMessage?.Invoke(groupId, from, ts, type, payload, replyTo);
        return true;
    }

    // ------------------------------------------------------------------
    // Letters
    // ------------------------------------------------------------------

    public async Task OnLetterAsync(string from, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "group_state": await OnStateAsync(from, payload); break;
                case "group_sync": await OnSyncAsync(from, payload); break;
                case "group_relay": await OnRelayAsync(from, payload); break;
                case "group_leave": await OnLeaveAsync(from, payload); break;
                case "blacklist_update": OnBanUpdate(from, payload); break;
                case "group_pin": await OnPinUpdateAsync(from, payload); break;
                case "group_suggest": OnSuggest(from, payload); break;
                case "group_profile": OnProfileLetter(from, payload, member: false); break;
                case "member_profile": OnProfileLetter(from, payload, member: true); break;
            }
        }
        catch (JsonException) { /* malformed peer input: ignore */ }
    }

    private async Task OnStateAsync(string from, string payload)
    {
        var letter = JsonSerializer.Deserialize<GroupStateLetter>(payload);
        if (letter == null) return;

        var r = ApplyState(letter, from);
        await AfterApplyAsync(r);
    }

    private async Task AfterApplyAsync(ApplyResult r)
    {
        if (r.Rec == null) return;
        if (r.RebaseEpoch is { } lost) await RebaseAsync(r.Rec, lost);
        if (r.NeedSync) await SyncAsync(r.Rec);
    }

    private async Task OnLeaveAsync(string from, string payload)
    {
        var l = JsonSerializer.Deserialize<GroupLeaveLetter>(payload);
        var rec = l == null ? null : _store.Find(l.GroupId);
        if (rec == null) return;

        lock (_lock)
        {
            if (!rec.Active) return;
            var cur = Current(rec);
            if (!cur.Moderators.Contains(_me.DiscoveryId) || cur.Members.All(m => m.DiscoveryId != from) || from == cur.CreatedBy) return;
        }
        await IssueAsync(rec, new PendingOp { Kind = "remove", MemberId = from },
            d => { d.Members.RemoveAll(m => m.DiscoveryId == from); d.Moderators.Remove(from); });
    }

    // ---- profiles: group name/picture (creator) and member names/pictures (each member) ---------

    /// <summary>
    /// Creator only: rename the group and/or change its picture (<paramref name="clearPhoto"/> removes it). A signed,
    /// timestamped operation kept outside the GroupState — the key is NOT rotated for a cosmetic change.
    /// </summary>
    public async Task SetProfileAsync(GroupRecord rec, string? name, string? photo, bool clearPhoto = false, string? description = null, bool clearDescription = false)
    {
        GroupProfileOp op;
        List<GroupMember> recipients;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (cur.CreatedBy != _me.DiscoveryId) throw new CodedInvalidOperationException("Only the creator can change the group's name or picture.", "group_only_creator_profile");

            var newName = (name ?? rec.Profile?.Name ?? rec.Name).Trim();
            if (newName.Length == 0 || newName.Length > GroupProfileOp.MaxNameLength) throw new CodedInvalidOperationException($"The name must be 1 to {GroupProfileOp.MaxNameLength} characters.", "group_name_length", GroupProfileOp.MaxNameLength);
            var newDescription = clearDescription ? null : description?.Trim() ?? rec.Profile?.Description;
            if (newDescription != null && (newDescription.Length == 0 || newDescription.Length > GroupProfileOp.MaxDescriptionLength))
                throw new CodedInvalidOperationException($"The description must be 1 to {GroupProfileOp.MaxDescriptionLength} characters (use clear to remove it).", "group_description_length", GroupProfileOp.MaxDescriptionLength);
            var newPhoto = clearPhoto ? null : photo ?? rec.Profile?.Photo;
            if (!GroupCrypto.PhotoAcceptable(newPhoto)) throw new CodedInvalidOperationException("Picture too large (max 100 KiB) or not valid base64.", "group_picture_big");

            var ts = NextTs();
            op = new GroupProfileOp
            {
                GroupId = rec.GroupId, Name = newName, Photo = newPhoto, Description = newDescription, Ts = ts, Signer = _me.DiscoveryId,
                Sig = MessageCrypto_Sign(GroupCrypto.GroupProfileSigData(rec.GroupId, newName, newPhoto, ts, newDescription)),
            };
            ApplyProfileLocked(rec, op);
            recipients = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        var payload = JsonSerializer.Serialize(op);
        foreach (var m in recipients) await _net.SendLetterAsync(m, "group_profile", payload, queue: false);
    }

    /// <summary>Publishes my own nickname/picture to every group I am in (signed by me, so peers can relay it to latecomers).</summary>
    public async Task PublishMyProfileAsync(string nickname, string? photo, string? description = null, IReadOnlySet<string>? onlyGroups = null)
    {
        if (description != null && description.Length > GroupProfileOp.MaxDescriptionLength) throw new CodedInvalidOperationException("Description too long.", "group_description_long");
        if (!GroupCrypto.PhotoAcceptable(photo)) throw new CodedInvalidOperationException("Picture too large (max 100 KiB) or not valid base64.", "group_picture_big");
        var outgoing = new List<(GroupMember M, string Payload)>();
        lock (_lock)
        {
            foreach (var rec in _store.All.Where(r => r.Active && (onlyGroups == null || onlyGroups.Contains(r.GroupId))))
            {
                var ts = NextTs();
                var shown = rec.MyName ?? nickname;   // the name and picture chosen for this group win over the global ones
                var shownPhoto = rec.MyPhoto ?? photo;
                var mp = new MemberProfile
                {
                    GroupId = rec.GroupId, Id = _me.DiscoveryId, Nickname = shown, Photo = shownPhoto, Description = description, Ts = ts,
                    Sig = MessageCrypto_Sign(GroupCrypto.MemberProfileSigData(rec.GroupId, _me.DiscoveryId, shown, shownPhoto, ts, description)),
                };
                if (_me.RevocationPublicKey != null)
                {
                    // My revocation key rides along (its own signature), so group peers who are not my contacts can pin it too.
                    mp.RevocationPublicKey = Convert.ToBase64String(_me.RevocationPublicKey);
                    mp.RevocationSig = MessageCrypto_Sign(GroupCrypto.RevocationPinSigData(_me.DiscoveryId, mp.RevocationPublicKey));
                }
                ApplyMemberProfileLocked(rec, mp);
                var payload = JsonSerializer.Serialize(mp);
                foreach (var m in Current(rec).Members.Where(x => x.DiscoveryId != _me.DiscoveryId)) outgoing.Add((m, payload));
            }
        }
        foreach (var (m, payload) in outgoing) await _net.SendLetterAsync(m, "member_profile", payload, queue: false);
    }

    /// <summary>Longest name a member can show in a group.</summary>
    public const int MaxMemberNameLength = 64;

    /// <summary>
    /// How long a change of my name or picture in a group waits before it is sent: edits made in the meantime replace it, so only
    /// the last one reaches the members. Zero sends at once.
    /// </summary>
    public TimeSpan ProfileDelay { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, CancellationTokenSource> _profileTimers = [];

    /// <summary>Sends my profile for one group: at once, or once <see cref="ProfileDelay"/> has passed without a newer change.</summary>
    private async Task PublishMyGroupProfileAsync(GroupRecord rec, bool immediate)
    {
        CancellationTokenSource? previous;
        lock (_lock) _profileTimers.Remove(rec.GroupId, out previous);
        previous?.Cancel();

        var only = new HashSet<string> { rec.GroupId };
        if (immediate || ProfileDelay <= TimeSpan.Zero)
        {
            await PublishMyProfileAsync(_me.Nickname ?? _me.DiscoveryId[..8], _me.Photo, _me.Description, only);
            return;
        }

        var timer = new CancellationTokenSource();
        lock (_lock) _profileTimers[rec.GroupId] = timer;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ProfileDelay, timer.Token); }
            catch (OperationCanceledException) { return; }   // a newer change took over
            lock (_lock)
            {
                if (!_profileTimers.TryGetValue(rec.GroupId, out var current) || current != timer) return;
                _profileTimers.Remove(rec.GroupId);
            }
            try { await PublishMyProfileAsync(_me.Nickname ?? _me.DiscoveryId[..8], _me.Photo, _me.Description, only); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the members get it with the next profile letter */ }
        });
    }

    /// <summary>The name I show in this group: the one chosen for it, else my global name.</summary>
    public string MyNameIn(GroupRecord rec) { lock (_lock) return rec.MyName ?? _me.Nickname ?? _me.DiscoveryId[..8]; }

    /// <summary>The name a member chose for this group, or null when they never published one (their global name is then used).</summary>
    public string? MemberChosenName(GroupRecord rec, string memberId) { lock (_lock) return rec.MemberProfiles.FirstOrDefault(p => p.Id == memberId)?.Nickname; }

    /// <summary>
    /// Sets the name I show in one group (null or empty = my global name again). It shows on my screen at once; the members get it
    /// after <see cref="ProfileDelay"/> (or at once with <paramref name="immediate"/>).
    /// </summary>
    public async Task SetMyNameAsync(GroupRecord rec, string? name, bool immediate = false)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name != null && name.Length > MaxMemberNameLength) throw new CodedInvalidOperationException($"The name must be 1 to {MaxMemberNameLength} characters.", "group_member_name_length", MaxMemberNameLength);
        if (name == _me.Nickname) name = null;   // the global name is the default, not a choice

        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            if (name == rec.MyName) return;
            rec.MyName = name;
            _store.Save();
        }
        await PublishMyGroupProfileAsync(rec, immediate);
    }

    /// <summary>Whether I use a name or a picture of my own in this group (not my usual ones): worth reminding on the group's screen.</summary>
    public bool HasOwnIdentity(GroupRecord rec) { lock (_lock) return rec.MyName != null || rec.MyPhoto != null; }

    /// <summary>The picture I show in this group (base64): the one chosen for it, else my global picture; null when I have none.</summary>
    public string? MyPhotoIn(GroupRecord rec) { lock (_lock) return rec.MyPhoto ?? _me.Photo; }

    /// <summary>
    /// Sets the picture I show in one group (a base64 JPEG/PNG/WebP of at most 100 KiB; null = my global picture again). It shows on my
    /// screen at once; the members get it after <see cref="ProfileDelay"/> (or at once with <paramref name="immediate"/>).
    /// </summary>
    public async Task SetMyPhotoAsync(GroupRecord rec, string? photo, bool immediate = false)
    {
        if (!GroupCrypto.PhotoAcceptable(photo)) throw new CodedInvalidOperationException("Picture too large (max 100 KiB) or not valid base64.", "group_picture_big");
        if (photo == _me.Photo) photo = null;   // the global picture is the default, not a choice

        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            if (photo == rec.MyPhoto) return;
            rec.MyPhoto = photo;
            _store.Save();
        }
        await PublishMyGroupProfileAsync(rec, immediate);
    }

    private void OnProfileLetter(string from, string payload, bool member)
    {
        var rec = _store.Find(member
            ? JsonSerializer.Deserialize<MemberProfile>(payload)?.GroupId ?? ""
            : JsonSerializer.Deserialize<GroupProfileOp>(payload)?.GroupId ?? "");
        if (rec == null) return;

        lock (_lock)
        {
            if (!rec.Active || Current(rec).Members.All(m => m.DiscoveryId != from)) return; // only a member may hand us anything
            if (member)
            {
                var mp = JsonSerializer.Deserialize<MemberProfile>(payload)!;
                if (mp.Id == from) ApplyMemberProfileLocked(rec, mp); // a direct letter must come from its author
            }
            else ApplyProfileLocked(rec, JsonSerializer.Deserialize<GroupProfileOp>(payload)!);
        }
    }

    /// <summary>Valid iff signed by the CURRENT creator, newer than what we hold, within size limits.</summary>
    private bool ApplyProfileLocked(GroupRecord rec, GroupProfileOp op)
    {
        var cur = Current(rec);
        if (op.GroupId != rec.GroupId || op.Signer != cur.CreatedBy || op.Ts <= (rec.Profile?.Ts ?? 0)) return false;
        if (op.Name.Trim().Length == 0 || op.Name.Length > GroupProfileOp.MaxNameLength || !GroupCrypto.PhotoAcceptable(op.Photo)) return false;
        if (op.Description != null && (op.Description.Length == 0 || op.Description.Length > GroupProfileOp.MaxDescriptionLength)) return false;
        var signer = cur.Members.FirstOrDefault(m => m.DiscoveryId == op.Signer);
        if (signer == null || !Contacts.MessageCrypto.Verify(GroupCrypto.GroupProfileSigData(op.GroupId, op.Name, op.Photo, op.Ts, op.Description), op.Sig, Convert.FromBase64String(signer.SigningPublicKey))) return false;

        rec.Profile = op;
        rec.Name = op.Name;
        _store.Save();
        return true;
    }

    /// <summary>Valid iff the author is a current member, signed it, it is newer than what we hold, within limits.</summary>
    private bool ApplyMemberProfileLocked(GroupRecord rec, MemberProfile mp)
    {
        var cur = Current(rec);
        var author = cur.Members.FirstOrDefault(m => m.DiscoveryId == mp.Id);
        if (author == null || mp.GroupId != rec.GroupId || mp.Nickname.Trim().Length == 0 || mp.Nickname.Length > 64 || !GroupCrypto.PhotoAcceptable(mp.Photo)
            || (mp.Description != null && mp.Description.Length > GroupProfileOp.MaxDescriptionLength)) return false;
        var existing = rec.MemberProfiles.FirstOrDefault(p => p.Id == mp.Id);
        if (existing != null && mp.Ts <= existing.Ts) return false;
        if (!Contacts.MessageCrypto.Verify(GroupCrypto.MemberProfileSigData(mp.GroupId, mp.Id, mp.Nickname, mp.Photo, mp.Ts, mp.Description), mp.Sig, Convert.FromBase64String(author.SigningPublicKey))) return false;

        // The revocation key counts only with its own signature by that member (a relaying peer cannot choose it for them).
        if (mp.RevocationPublicKey != null && mp.RevocationSig != null && mp.Id != _me.DiscoveryId
            && Contacts.MessageCrypto.Verify(GroupCrypto.RevocationPinSigData(mp.Id, mp.RevocationPublicKey), mp.RevocationSig, Convert.FromBase64String(author.SigningPublicKey)))
            PinRevocation?.Invoke(mp.Id, mp.RevocationPublicKey);

        if (existing != null) rec.MemberProfiles.Remove(existing);
        rec.MemberProfiles.Add(mp);
        _store.Save();
        return true;
    }

    /// <summary>Called with (member id, revocation key) for a key a member announced: the client pins it on first sight (§12, identity migration).</summary>
    public Action<string, string>? PinRevocation { get; set; }

    // ---- invited, not yet joined ----

    /// <summary>Records proof of life from a member; true when it was news.</summary>
    private static bool Hear(GroupRecord rec, string id)
    {
        if (rec.Heard == null || rec.Heard.Contains(id)) return false;
        rec.Heard.Add(id);
        return true;
    }

    /// <summary>Members of the current state who were invited but have never shown a sign of life (no message, no catch-up request): they have not answered yet.</summary>
    public IReadOnlySet<string> PendingMembers(GroupRecord rec)
    {
        lock (_lock)
        {
            if (rec.Heard == null) return new HashSet<string>();
            var st = Current(rec);
            return st.Members.Select(m => m.DiscoveryId).Where(id => id != _me.DiscoveryId && id != st.CreatedBy && !st.Moderators.Contains(id) && !rec.Heard.Contains(id)).ToHashSet();
        }
    }

    // ---- group blacklist (§7) ---------------------------------------------

    public const int MaxBanOps = 2000;

    /// <summary>Currently blacklisted ids: an id is banned when its latest operation (by ts) is an "add".</summary>
    public IReadOnlyList<string> Bans(GroupRecord rec)
    {
        lock (_lock) return rec.Bans.Select(o => o.Id).Distinct().Where(id => BannedNow(rec, id)).ToList();
    }

    private bool BannedNow(GroupRecord rec, string id)
    {
        BanOp? last = null;
        foreach (var o in rec.Bans)
            if (o.Id == id && (last == null || o.Ts > last.Ts || (o.Ts == last.Ts && o.Kind == "add"))) last = o;
        return last?.Kind == "add";
    }

    /// <summary>Moderators only. A blacklisted author is neither shown nor relayed by anyone who applies it — a social guarantee, unlike exclusion (which rotates the key). Moderators and the creator cannot be blacklisted.</summary>
    public async Task BanAsync(GroupRecord rec, string memberId) => await IssueBanAsync(rec, "add", memberId);

    /// <summary>Only the creator may lift a ban.</summary>
    public async Task UnbanAsync(GroupRecord rec, string memberId) => await IssueBanAsync(rec, "remove", memberId);

    private async Task IssueBanAsync(GroupRecord rec, string kind, string id)
    {
        BanOp op;
        List<GroupMember> recipients;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (kind == "add")
            {
                if (!cur.Moderators.Contains(_me.DiscoveryId)) throw new CodedInvalidOperationException("Only a moderator can blacklist.", "group_only_moderator_ban");
                if (cur.Moderators.Contains(id) || id == cur.CreatedBy) throw new CodedInvalidOperationException("A moderator cannot be blacklisted.", "group_moderator_not_banned");
            }
            else if (cur.CreatedBy != _me.DiscoveryId) throw new CodedInvalidOperationException("Only the creator can lift a blacklist entry.", "group_only_creator_unban");

            var ts = NextTs();
            op = new BanOp { GroupId = rec.GroupId, Kind = kind, Id = id, Ts = ts, Signer = _me.DiscoveryId, Sig = MessageCrypto_Sign(GroupCrypto.BanSigData(rec.GroupId, kind, id, ts)) };
            ApplyBanOpsLocked(rec, [op]);
            recipients = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        var payload = JsonSerializer.Serialize(new BanUpdateLetter { GroupId = rec.GroupId, Ops = [op] });
        foreach (var m in recipients) await _net.SendLetterAsync(m, "blacklist_update", payload, queue: false);
    }

    private void OnBanUpdate(string from, string payload)
    {
        var l = JsonSerializer.Deserialize<BanUpdateLetter>(payload);
        var rec = l == null ? null : _store.Find(l.GroupId);
        if (l == null || rec == null) return;
        lock (_lock)
        {
            if (!rec.Active || Current(rec).Members.All(m => m.DiscoveryId != from)) return; // only a member may hand us anything
            ApplyBanOpsLocked(rec, l.Ops);
        }
    }

    private void ApplyBanOps(GroupRecord rec, List<BanOp> ops)
    {
        lock (_lock) ApplyBanOpsLocked(rec, ops);
    }

    /// <summary>Verifies and merges operations: "add" signed by a current moderator, "remove" by the creator; anything else is ignored.</summary>
    private void ApplyBanOpsLocked(GroupRecord rec, List<BanOp> ops)
    {
        var cur = Current(rec);
        var changed = false;
        foreach (var op in ops)
        {
            if (op.GroupId != rec.GroupId || (op.Kind != "add" && op.Kind != "remove") || rec.Bans.Count >= MaxBanOps) continue;
            if (rec.Bans.Any(o => o.Signer == op.Signer && o.Kind == op.Kind && o.Id == op.Id && o.Ts == op.Ts)) continue;

            var authorized = op.Kind == "add" ? cur.Moderators.Contains(op.Signer) : op.Signer == cur.CreatedBy;
            var signer = cur.Members.FirstOrDefault(m => m.DiscoveryId == op.Signer);
            if (!authorized || signer == null) continue;
            if (op.Kind == "add" && (cur.Moderators.Contains(op.Id) || op.Id == cur.CreatedBy)) continue;
            if (!Contacts.MessageCrypto.Verify(GroupCrypto.BanSigData(op.GroupId, op.Kind, op.Id, op.Ts), op.Sig, Convert.FromBase64String(signer.SigningPublicKey))) continue;

            rec.Bans.Add(op);
            changed = true;
        }
        if (!changed) return;

        // Whoever is banned now disappears from what we hold and would relay.
        foreach (var id in rec.Bans.Select(o => o.Id).Distinct().Where(id => BannedNow(rec, id)))
            rec.Log.RemoveAll(e => e.From == id);
        _store.Save();
    }

    // ---- pinned messages (§7) -------------------------------------------

    /// <summary>At most this many messages are pinned at once: when more are, the oldest pins are released.</summary>
    public const int MaxPinned = 3;

    private const int MaxPinOps = 500;

    /// <summary>Raised when the pinned messages of a group changed (a pin or an unpin, mine or a moderator's).</summary>
    public event Action<GroupRecord>? PinsChanged;

    /// <summary>The ids of the pinned messages, the most recently pinned first: the latest operation of a message decides, at most <see cref="MaxPinned"/> are kept.</summary>
    public IReadOnlyList<string> PinnedIds(GroupRecord rec)
    {
        lock (_lock) return rec.Pins.Where(o => o.Kind == "pin").OrderByDescending(o => o.Ts).Take(MaxPinned).Select(o => o.Target).ToList();
    }

    /// <summary>Moderators and the creator pin or release any message of the group, whoever wrote it.</summary>
    public async Task PinAsync(GroupRecord rec, string messageId, bool pin)
    {
        PinOp op;
        List<GroupMember> recipients;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (!cur.Moderators.Contains(_me.DiscoveryId)) throw new CodedInvalidOperationException("Only a moderator can pin a message.", "group_only_moderator_pin");

            var kind = pin ? "pin" : "unpin";
            var ts = NextTs();
            op = new PinOp { GroupId = rec.GroupId, Kind = kind, Target = messageId, Ts = ts, Signer = _me.DiscoveryId, Sig = MessageCrypto_Sign(GroupCrypto.PinSigData(rec.GroupId, kind, messageId, ts)) };
            ApplyPinOpsLocked(rec, [op]);
            recipients = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        var payload = JsonSerializer.Serialize(new PinUpdateLetter { GroupId = rec.GroupId, Ops = [op] });
        foreach (var m in recipients) await _net.SendLetterAsync(m, "group_pin", payload, queue: false);
    }

    private async Task OnPinUpdateAsync(string from, string payload)
    {
        var l = JsonSerializer.Deserialize<PinUpdateLetter>(payload);
        var rec = l == null ? null : _store.Find(l.GroupId);
        if (l == null || rec == null) return;

        bool missing;
        lock (_lock)
        {
            if (!rec.Active || Current(rec).Members.All(m => m.DiscoveryId != from)) return;   // only a member may hand us anything
            ApplyPinOpsLocked(rec, l.Ops);
            // A pinned message this device does not hold: ask a member for what it lacks, as after any absence (the answer carries it).
            missing = PinnedIds(rec).Any(id => !HoldsMessage(rec, id));
        }
        if (missing) await SyncAsync(rec, onlyPeer: from);
    }

    /// <summary>Whether the sealed message with this id (<c>author:ts</c>) is in the log of the group (a gap marker counts: it was deleted for everyone).</summary>
    private static bool HoldsMessage(GroupRecord rec, string messageId) => rec.Log.Any(e => $"{e.From}:{e.Ts}" == messageId);

    /// <summary>Verifies and merges operations: signed by a moderator of the current state, newer than the one held for that message; anything else is ignored.</summary>
    private void ApplyPinOpsLocked(GroupRecord rec, List<PinOp> ops)
    {
        var cur = Current(rec);
        var changed = false;
        foreach (var op in ops)
        {
            if (op.GroupId != rec.GroupId || (op.Kind != "pin" && op.Kind != "unpin") || op.Target.Length == 0 || op.Target.Length > 200) continue;
            if (rec.Pins.Any(o => o.Target == op.Target && o.Ts >= op.Ts)) continue;   // not newer than what we hold (a replay included)

            var signer = cur.Members.FirstOrDefault(m => m.DiscoveryId == op.Signer);
            if (signer == null || !cur.Moderators.Contains(op.Signer)) continue;
            if (!Contacts.MessageCrypto.Verify(GroupCrypto.PinSigData(op.GroupId, op.Kind, op.Target, op.Ts), op.Sig, Convert.FromBase64String(signer.SigningPublicKey))) continue;

            rec.Pins.RemoveAll(o => o.Target == op.Target);
            rec.Pins.Add(op);
            changed = true;
        }
        if (!changed) return;

        // The pins that no longer count (beyond the newest ones) and the oldest releases are let go, so the list stays small.
        var keep = rec.Pins.Where(o => o.Kind == "pin").OrderByDescending(o => o.Ts).Take(MaxPinned).ToHashSet();
        rec.Pins.RemoveAll(o => o.Kind == "pin" && !keep.Contains(o));
        foreach (var old in rec.Pins.Where(o => o.Kind == "unpin").OrderBy(o => o.Ts).Take(Math.Max(0, rec.Pins.Count - MaxPinOps)).ToList()) rec.Pins.Remove(old);
        _store.Save();
        PinsChanged?.Invoke(rec);
    }

    // ---- state adoption (§7 rules) --------------------------------------

    private record ApplyResult(GroupRecord? Rec, bool NeedSync = false, int? RebaseEpoch = null);

    private ApplyResult ApplyState(GroupStateLetter sl, string sender)
    {
        lock (_lock) return ApplyStateLocked(sl, sender);
    }

    // ---- invitations: the user answers before a group is joined ----

    /// <summary>
    /// True (what the client sets): an invitation from a contact waits in <see cref="PendingGroupInvites"/> until
    /// <see cref="AcceptInvitationAsync"/>; nothing is joined, no key is kept, nobody learns anything before the answer.
    /// False: it is adopted at once (library tests, tools).
    /// </summary>
    public bool RequireInvitationApproval { get; set; }

    /// <summary>Raised when an invitation arrives and waits for an answer.</summary>
    public event Action<PendingGroupInvite>? InvitationReceived;

    public IReadOnlyList<PendingGroupInvite> PendingGroupInvites => _store.Invites;

    /// <summary>I accept: the invitation is adopted as if it had just arrived, and I catch up with the group.</summary>
    public async Task<GroupRecord> AcceptInvitationAsync(string groupId, string? myName = null, string? myPhoto = null)
    {
        var invite = _store.FindInvite(groupId);
        if (invite == null || invite.Declined) throw new CodedInvalidOperationException("No invitation waiting for this group.", "group_invite_none");
        var letter = JsonSerializer.Deserialize<GroupStateLetter>(invite.Letter) ?? throw new CodedInvalidOperationException("The invitation is damaged.", "group_invite_damaged");

        ApplyResult r;
        lock (_lock) r = ApplyStateLocked(letter, invite.Inviter, approved: true);
        if (r.Rec == null) { _store.RemoveInvite(groupId); throw new CodedInvalidOperationException("The invitation is no longer valid.", "group_invite_invalid"); }

        _store.RemoveInvite(groupId);
        await AfterApplyAsync(r);
        if (!string.IsNullOrWhiteSpace(myName) && myName.Trim() != _me.Nickname && myName.Trim().Length <= MaxMemberNameLength)
            await SetMyNameAsync(r.Rec, myName, immediate: true);   // my name for this group, announced as soon as I am in
        if (myPhoto != null) await SetMyPhotoAsync(r.Rec, myPhoto, immediate: true);   // and my picture
        return r.Rec;
    }

    // ---- proposing a contact of mine to the moderators (§7) ----

    private readonly Dictionary<string, SponsorProof> _sponsors = [];   // candidate id -> proof, only while a proposed person's invitation is being issued

    /// <summary>Raised on a moderator's device when a member proposes someone.</summary>
    public event Action<GroupSuggestion>? SuggestionReceived;

    /// <summary>Proposals waiting for me, a moderator.</summary>
    public IReadOnlyList<GroupSuggestion> Suggestions => _store.Suggestions;

    /// <summary>
    /// A member (not a moderator: moderators invite directly) proposes one of their contacts. Every moderator gets a signed
    /// proposal; one of them may then invite the person, and the signature goes with the invitation so the invitee, who
    /// need not know that moderator, can check it against the member they do know. Returns how many moderators were told.
    /// </summary>
    public async Task<int> SuggestAsync(GroupRecord rec, GroupMember candidate)
    {
        if (!GroupCrypto.MemberIdMatchesKeys(candidate)) throw new InvalidOperationException("That person's keys do not match their id.");

        List<GroupMember> moderators;
        string payload;
        lock (_lock)
        {
            if (!rec.Active) throw new CodedInvalidOperationException("You are no longer in this group.", "group_not_in");
            var cur = Current(rec);
            if (cur.Members.All(m => m.DiscoveryId != _me.DiscoveryId)) throw new CodedInvalidOperationException("You are not in this group.", "group_not_in_this");
            if (cur.Moderators.Contains(_me.DiscoveryId)) throw new CodedInvalidOperationException("You are a moderator: invite directly.", "group_moderator_invite_direct");
            if (cur.Members.Any(m => m.DiscoveryId == candidate.DiscoveryId)) throw new CodedInvalidOperationException("Already a member.", "group_already_member");
            if (BannedNow(rec, candidate.DiscoveryId)) throw new CodedInvalidOperationException("That person is on the group's blacklist.", "group_banned_cant_suggest");

            var ts = NextTs();
            payload = JsonSerializer.Serialize(new GroupSuggestLetter
            {
                GroupId = rec.GroupId, Candidate = candidate, Ts = ts,
                Sig = MessageCrypto_Sign(GroupCrypto.SponsorSigData(rec.GroupId, candidate.DiscoveryId, ts)),
            });
            moderators = cur.Members.Where(m => cur.Moderators.Contains(m.DiscoveryId) && m.DiscoveryId != _me.DiscoveryId).ToList();
        }

        foreach (var m in moderators) await _net.SendLetterAsync(m, "group_suggest", payload, queue: true);
        return moderators.Count;
    }

    private void OnSuggest(string from, string payload)
    {
        var l = JsonSerializer.Deserialize<GroupSuggestLetter>(payload);
        var rec = l == null ? null : _store.Find(l.GroupId);
        if (l == null || rec == null) return;

        GroupSuggestion suggestion;
        lock (_lock)
        {
            if (!rec.Active) return;
            var cur = Current(rec);
            var sponsor = cur.Members.FirstOrDefault(m => m.DiscoveryId == from);   // only a member of the group can propose
            if (!cur.Moderators.Contains(_me.DiscoveryId) || sponsor == null) return;
            if (!GroupCrypto.MemberIdMatchesKeys(l.Candidate) || cur.Members.Any(m => m.DiscoveryId == l.Candidate.DiscoveryId) || BannedNow(rec, l.Candidate.DiscoveryId)) return;
            if (!Contacts.MessageCrypto.Verify(GroupCrypto.SponsorSigData(l.GroupId, l.Candidate.DiscoveryId, l.Ts), l.Sig, Convert.FromBase64String(sponsor.SigningPublicKey))) return;
            if (_store.FindSuggestion(l.GroupId, l.Candidate.DiscoveryId) != null) return;

            suggestion = new GroupSuggestion
            {
                GroupId = l.GroupId, GroupName = rec.Name, Candidate = l.Candidate, SponsorId = from, SponsorName = sponsor.Nickname, Ts = l.Ts, Sig = l.Sig,
            };
            _store.AddSuggestion(suggestion);
        }
        _notify($"[group] {suggestion.SponsorName} proposes {suggestion.Candidate.Nickname} for '{suggestion.GroupName}'.");
        SuggestionReceived?.Invoke(suggestion);
    }

    /// <summary>Moderator: invite a proposed person. The sponsor's signature rides with the invitation, so it is accepted even if the person does not know me.</summary>
    public async Task InviteSuggestionAsync(string groupId, string candidateId)
    {
        var s = _store.FindSuggestion(groupId, candidateId) ?? throw new CodedInvalidOperationException("No such proposal.", "group_proposal_none");
        var rec = _store.Find(groupId) ?? throw new InvalidOperationException("Unknown group.");
        lock (_lock) _sponsors[candidateId] = new SponsorProof { Id = s.SponsorId, Ts = s.Ts, Sig = s.Sig };
        try { await AddMemberAsync(rec, s.Candidate); }
        finally { lock (_lock) _sponsors.Remove(candidateId); }
        _store.RemoveSuggestion(groupId, candidateId);
    }

    /// <summary>Moderator: drop a proposal (nobody is told).</summary>
    public void DismissSuggestion(string groupId, string candidateId) => _store.RemoveSuggestion(groupId, candidateId);

    /// <summary>The sender is not my contact: does a member I know vouch for me? Their signature must cover this group and me, and they must be a member of the state.</summary>
    private bool SponsorVouches(GroupStateLetter sl, GroupStateData data)
    {
        var proof = sl.Sponsor;
        if (proof == null || !_isConfirmedContact(proof.Id)) return false;
        var sponsor = data.Members.FirstOrDefault(m => m.DiscoveryId == proof.Id);
        return sponsor != null && Contacts.MessageCrypto.Verify(GroupCrypto.SponsorSigData(data.GroupId, _me.DiscoveryId, proof.Ts), proof.Sig, Convert.FromBase64String(sponsor.SigningPublicKey));
    }

    /// <summary>I decline: the invitation goes, and the same group will not ask again.</summary>
    public void DeclineInvitation(string groupId)
    {
        var invite = _store.FindInvite(groupId);
        if (invite == null) return;
        invite.Declined = true;
        invite.Letter = "";
        _store.SaveInvites();
    }

    private ApplyResult ApplyStateLocked(GroupStateLetter sl, string sender, bool approved = false)
    {
        var data = ParseState(sl.State);
        if (data == null || data.Members.Count == 0 || !data.Members.All(GroupCrypto.MemberIdMatchesKeys)) return new(null);
        var hash = GroupCrypto.HashState(sl.State);
        var rec = _store.Find(data.GroupId);

        // ---- first contact with this group: an invitation ----
        if (rec == null)
        {
            var fromAdministrator = !_isConfirmedContact(sender) && _isServerAdministrator(sender);
            if (!_isConfirmedContact(sender) && !fromAdministrator && !SponsorVouches(sl, data)) return new(null);
            var signer = data.Members.FirstOrDefault(m => m.DiscoveryId == sl.Signer);
            if (signer == null || !data.Moderators.Contains(sl.Signer) || !GroupCrypto.VerifyState(sl.State, sl.Sig, signer.SigningPublicKey)) return new(null);

            var mine = data.Members.FirstOrDefault(m => m.DiscoveryId == _me.DiscoveryId);
            if (mine == null || mine.EncryptionPublicKey != Convert.ToBase64String(_me.EncryptionPublicKey)) return new(null);
            var key = sl.KeyEnvelope == null ? null : GroupCrypto.UnwrapKey(sl.KeyEnvelope, _me.EncryptionPrivateKey);
            if (key == null || !GroupCrypto.KeyMatches(key, data.KeyCheck)) return new(null);

            if (RequireInvitationApproval && !approved)
            {
                if (_store.FindInvite(data.GroupId) != null) return new(null);   // already waiting, or declined
                var invite = new PendingGroupInvite
                {
                    GroupId = data.GroupId, Name = data.Name, Inviter = sender, InviterName = signer.Nickname, Members = data.Members.Count,
                    SponsorName = !_isConfirmedContact(sender) && !fromAdministrator ? data.Members.FirstOrDefault(m => m.DiscoveryId == sl.Sponsor?.Id)?.Nickname : null,
                    FromAdministrator = fromAdministrator,
                    Letter = JsonSerializer.Serialize(sl),
                };
                _store.AddInvite(invite);
                _notify($"[group] {signer.Nickname} invites you to '{data.Name}' — accept or decline.");
                InvitationReceived?.Invoke(invite);
                return new(null);
            }

            rec = new GroupRecord { GroupId = data.GroupId, Name = data.Name, CurrentHash = hash, Heard = data.Members.Select(x => x.DiscoveryId).ToList() };   // those already in it when I join are taken as joined
            rec.States.Add(new StoredState { Epoch = data.Epoch, Hash = hash, Json = sl.State, Signer = sl.Signer, Sig = sl.Sig });
            AddKey(rec, data.Epoch, hash, key);
            _store.Add(rec);
            _notify($"[group] You were added to '{data.Name}' ({data.Members.Count} members) by {signer.Nickname}.");
            return new(rec, NeedSync: true);
        }

        // ---- known group ----
        if (rec.States.Any(s => s.Hash == hash))
        {
            if (sl.KeyEnvelope != null) LearnKey(rec, hash, sl.KeyEnvelope);
            return new(rec);
        }

        var cur = Current(rec);

        // Normal successor: signed by a moderator of the state it replaces.
        if (data.Epoch == cur.Epoch + 1 && data.PrevHash == rec.CurrentHash)
        {
            if (!SignedByModeratorOf(cur, sl, data)) return new(rec);

            // I had left (or was excluded) and this state adds me back: like any invitation, it waits for my answer.
            if (!rec.Active && RequireInvitationApproval && !approved && data.Members.Any(m => m.DiscoveryId == _me.DiscoveryId))
            {
                if (_store.FindInvite(data.GroupId) != null) return new(rec);
                var again = new PendingGroupInvite
                {
                    GroupId = data.GroupId, Name = rec.Profile?.Name ?? data.Name, Inviter = sender,
                    InviterName = data.Members.FirstOrDefault(m => m.DiscoveryId == sl.Signer)?.Nickname ?? sender[..8],
                    Members = data.Members.Count, Letter = JsonSerializer.Serialize(sl),
                };
                _store.AddInvite(again);
                _notify($"[group] {again.InviterName} invites you back to '{again.Name}' — accept or decline.");
                InvitationReceived?.Invoke(again);
                return new(rec);
            }
            AdoptSuccessor(rec, data, sl, hash);
            DrainPending(rec);
            return new(rec, NeedSync: KeyFor(rec, rec.CurrentHash) == null && rec.Active);
        }

        // Concurrent sibling: same parent, same epoch, different content — smallest hash wins.
        if (data.Epoch == cur.Epoch && data.PrevHash != null && data.PrevHash == cur.PrevHash)
        {
            var prev = ParseStored(rec, data.PrevHash);
            if (prev == null || !SignedByModeratorOf(prev, sl, data)) return new(rec);

            rec.States.Add(new StoredState { Epoch = data.Epoch, Hash = hash, Json = sl.State, Signer = sl.Signer, Sig = sl.Sig });
            if (sl.KeyEnvelope != null) LearnKey(rec, hash, sl.KeyEnvelope);

            if (string.CompareOrdinal(hash, rec.CurrentHash) < 0)
            {
                rec.CurrentHash = hash;
                rec.Name = rec.Profile?.Name ?? data.Name; // a creator-set name outlives the state's original one
                ApplyMembershipEffects(rec, data);
                _store.Save();
                var replay = rec.Ops.Any(o => o.Epoch == data.Epoch) ? (int?)data.Epoch : null;
                return new(rec, NeedSync: KeyFor(rec, hash) == null, RebaseEpoch: replay);
            }
            _store.Save();
            return new(rec);
        }

        // An authentic alternative of an EARLIER epoch (the losing side of a past tie-break, arriving late):
        // never adopted, but kept with its key — messages sealed under it are still legitimate and must stay readable.
        if (data.PrevHash != null && ParseStored(rec, data.PrevHash) is { } parent && data.Epoch == parent.Epoch + 1 && SignedByModeratorOf(parent, sl, data))
        {
            rec.States.Add(new StoredState { Epoch = data.Epoch, Hash = hash, Json = sl.State, Signer = sl.Signer, Sig = sl.Sig });
            if (sl.KeyEnvelope != null) LearnKey(rec, hash, sl.KeyEnvelope);
            _store.Save();
            return new(rec);
        }

        // Ahead of us (missing intermediate states) or on an unknown branch: keep it, ask peers for the chain.
        if (data.Epoch > cur.Epoch + 1 && rec.Pending.All(p => GroupCrypto.HashState(p.State) != hash))
        {
            rec.Pending.Add(new GroupStateLetter { State = sl.State, Signer = sl.Signer, Sig = sl.Sig, KeyEnvelope = sl.KeyEnvelope });
            _store.Save();
            return new(rec, NeedSync: true);
        }
        return new(rec);
    }

    private bool SignedByModeratorOf(GroupStateData basis, GroupStateLetter sl, GroupStateData incoming)
    {
        if (!basis.Moderators.Contains(sl.Signer)) return false;

        // A moderator role can be taken away only by the creator — unless the holder left/was excluded, or steps down themselves.
        foreach (var dropped in basis.Moderators.Where(id => !incoming.Moderators.Contains(id)))
        {
            var stillMember = incoming.Members.Any(m => m.DiscoveryId == dropped);
            if (stillMember && dropped != sl.Signer && sl.Signer != basis.CreatedBy) return false;
        }

        // The creator must always still be a member of the state it heads...
        if (incoming.Members.All(m => m.DiscoveryId != incoming.CreatedBy)) return false;
        // ...and only the current creator may change who it is, handing over to a moderator of the new state.
        if (incoming.CreatedBy != basis.CreatedBy && (sl.Signer != basis.CreatedBy || !incoming.Moderators.Contains(incoming.CreatedBy))) return false;

        var signer = basis.Members.FirstOrDefault(m => m.DiscoveryId == sl.Signer);
        return signer != null && GroupCrypto.VerifyState(sl.State, sl.Sig, signer.SigningPublicKey);
    }

    private void AdoptSuccessor(GroupRecord rec, GroupStateData data, GroupStateLetter sl, string hash)
    {
        rec.States.Add(new StoredState { Epoch = data.Epoch, Hash = hash, Json = sl.State, Signer = sl.Signer, Sig = sl.Sig });
        rec.CurrentHash = hash;
        rec.Name = rec.Profile?.Name ?? data.Name; // a creator-set name outlives the state's original one
        rec.Ops.RemoveAll(o => o.Epoch < data.Epoch);
        if (sl.KeyEnvelope != null) LearnKey(rec, hash, sl.KeyEnvelope);
        ApplyMembershipEffects(rec, data);
        _store.Save();
    }

    private void ApplyMembershipEffects(GroupRecord rec, GroupStateData data)
    {
        var stillMember = data.Members.Any(m => m.DiscoveryId == _me.DiscoveryId);
        if (!stillMember && rec.Active)
        {
            rec.Active = false;
            _notify($"[group] You are no longer a member of '{data.Name}'.");
        }
        else if (stillMember && !rec.Active)
        {
            rec.Active = true;   // added back: the archived group, with its history, is live again
            _notify($"[group] You are a member of '{data.Name}' again.");
        }
    }

    private void DrainPending(GroupRecord rec)
    {
        while (true)
        {
            var cur = Current(rec);
            var next = rec.Pending
                .Select(p => (Letter: p, Data: ParseState(p.State)))
                .FirstOrDefault(x => x.Data != null && x.Data.Epoch == cur.Epoch + 1 && x.Data.PrevHash == rec.CurrentHash);
            if (next.Data == null) return;

            rec.Pending.Remove(next.Letter);
            if (!SignedByModeratorOf(cur, next.Letter, next.Data)) continue;
            AdoptSuccessor(rec, next.Data, next.Letter, GroupCrypto.HashState(next.Letter.State));
        }
    }

    /// <summary>Stores the group key for a state we hold, if the envelope opens for us and matches the state's signed <c>key_check</c>.</summary>
    private bool LearnKey(GroupRecord rec, string stateHash, string envelope)
    {
        if (KeyFor(rec, stateHash) != null) return true;
        var state = ParseStored(rec, stateHash);
        if (state == null || state.Members.All(m => m.DiscoveryId != _me.DiscoveryId)) return false;

        var key = GroupCrypto.UnwrapKey(envelope, _me.EncryptionPrivateKey);
        if (key == null || !GroupCrypto.KeyMatches(key, state.KeyCheck)) return false;
        AddKey(rec, state.Epoch, stateHash, key);
        _store.Save();
        return true;
    }

    /// <summary>Replays my own membership changes that lost a concurrent-epoch tie-break, on top of the winner.</summary>
    private async Task RebaseAsync(GroupRecord rec, int lostEpoch)
    {
        List<PendingOp> ops;
        lock (_lock)
        {
            ops = rec.Ops.Where(o => o.Epoch == lostEpoch).ToList();
            rec.Ops.RemoveAll(o => o.Epoch == lostEpoch);
            _store.Save();
        }

        foreach (var op in ops)
        {
            try
            {
                GroupStateData cur;
                lock (_lock) cur = Current(rec);
                switch (op.Kind)
                {
                    case "add" when cur.Members.All(m => m.DiscoveryId != op.MemberId) && op.Member != null:
                        await AddMemberAsync(rec, op.Member); break;
                    case "remove" when cur.Members.Any(m => m.DiscoveryId == op.MemberId):
                        await RemoveMemberAsync(rec, op.MemberId); break;
                    case "promote" when cur.Members.Any(m => m.DiscoveryId == op.MemberId) && !cur.Moderators.Contains(op.MemberId):
                        await PromoteAsync(rec, op.MemberId); break;
                    case "demote" when cur.Moderators.Contains(op.MemberId) && op.MemberId != cur.CreatedBy:
                        await DemoteAsync(rec, op.MemberId); break;
                    case "transfer" when cur.CreatedBy == _me.DiscoveryId && cur.Members.Any(m => m.DiscoveryId == op.MemberId):
                        await TransferCreatorAsync(rec, op.MemberId); break;
                }
            }
            catch (InvalidOperationException ex)
            {
                _notify($"[group] Could not re-apply '{op.Kind}' after a concurrent change: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Peer catch-up (§7)
    // ------------------------------------------------------------------

    /// <summary>Background tick: ask a few online members what we are missing.</summary>
    public async Task SyncAllAsync()
    {
        foreach (var rec in _store.All.Where(r => r.Active).ToList()) await SyncAsync(rec);
    }

    public async Task SyncAsync(GroupRecord rec, string? onlyPeer = null)
    {
        string payload;
        List<GroupMember> targets;
        lock (_lock)
        {
            var cur = Current(rec);
            payload = JsonSerializer.Serialize(new GroupSyncLetter { GroupId = rec.GroupId, Epoch = cur.Epoch, Have = Have(rec) });
            targets = cur.Members.Where(m => m.DiscoveryId != _me.DiscoveryId && (onlyPeer == null || m.DiscoveryId == onlyPeer)).ToList();
        }

        var delivered = 0;
        foreach (var m in targets.Take(MaxSyncAttempts))
        {
            if (await _net.SendLetterAsync(m, "group_sync", payload, queue: false) && ++delivered >= SyncPeers) break;
        }
    }

    private async Task OnSyncAsync(string from, string payload)
    {
        var req = JsonSerializer.Deserialize<GroupSyncLetter>(payload);
        var rec = req == null ? null : _store.Find(req.GroupId);
        if (req == null || rec == null) return;
        lock (_lock) if (Hear(rec, from)) _store.Save();

        GroupMember requester;
        string relayJson;
        bool theyKnowMore;
        lock (_lock)
        {
            if (!rec.Active) return;
            var cur = Current(rec);
            var m = cur.Members.FirstOrDefault(x => x.DiscoveryId == from);
            if (m == null) return; // not (or no longer) a member: nothing for them
            requester = m;

            var relay = new GroupRelayLetter { GroupId = rec.GroupId };

            // 1. states they lack, oldest first (the adopted chain).
            var chain = new List<StoredState>();
            var s = rec.States.FirstOrDefault(x => x.Hash == rec.CurrentHash);
            while (s != null && s.Epoch > req.Epoch)
            {
                chain.Add(s);
                var prev = ParseState(s.Json)?.PrevHash;
                s = prev == null ? null : rec.States.FirstOrDefault(x => x.Hash == prev);
            }
            chain.Reverse();
            relay.States = chain.Select(x => new GroupStateLetter { State = x.Json, Signer = x.Signer, Sig = x.Sig }).ToList();

            // 2. keys for epochs where they were a member: re-wrapped by ME for THEM (no stored envelopes).
            foreach (var st in rec.States.OrderByDescending(x => x.Epoch).Take(MaxKeyEpochsPerRelay))
            {
                var data = ParseState(st.Json);
                var key = KeyFor(rec, st.Hash);
                var member = data?.Members.FirstOrDefault(x => x.DiscoveryId == from);
                if (key == null || member == null) continue;
                relay.Keys.Add(new GroupKeyItem { Epoch = st.Epoch, StateHash = st.Hash, Envelope = GroupCrypto.WrapKey(key, member.EncryptionPublicKey) });

                // A key is only checkable against its state: ship alternative branches too (chain states are already included).
                if (st.Epoch <= req.Epoch || relay.States.All(x => GroupCrypto.HashState(x.State) != st.Hash))
                    relay.States.Add(new GroupStateLetter { State = st.Json, Signer = st.Signer, Sig = st.Sig });
            }

            // 3. messages they lack.
            relay.Bans = rec.Bans.ToList();
            relay.Pins = rec.Pins.ToList();
            relay.Profile = rec.Profile;
            relay.MemberProfiles = rec.MemberProfiles.ToList();
            var missing = rec.Log.Where(e => e.Seq > req.Have.GetValueOrDefault(e.From) && !BannedNow(rec, e.From)).OrderBy(e => e.Ts).ToList();
            relay.Messages = missing.Take(RelayBatch).Select(e => new GroupRelayMessage { From = e.From, Ts = e.Ts, Msg = e.Msg, Seq = e.Seq }).ToList();
            relay.More = missing.Count > RelayBatch;

            var mine = Have(rec);
            theyKnowMore = req.Epoch > cur.Epoch || req.Have.Any(kv => kv.Value > mine.GetValueOrDefault(kv.Key));
            relayJson = JsonSerializer.Serialize(relay);
        }

        await _net.SendLetterAsync(requester, "group_relay", relayJson, queue: false);

        // Convergence: they hold something we do not — ask them in turn.
        if (theyKnowMore) await SyncAsync(rec, onlyPeer: from);
    }

    private async Task OnRelayAsync(string from, string payload)
    {
        var relay = JsonSerializer.Deserialize<GroupRelayLetter>(payload);
        var rec = relay == null ? null : _store.Find(relay.GroupId);
        if (relay == null || rec == null) return;

        bool needRebaseOrSync = false;
        int? rebase = null;
        lock (_lock)
        {
            // Only a current member may hand us anything.
            if (!rec.Active || Current(rec).Members.All(m => m.DiscoveryId != from)) return;

            foreach (var sl in relay.States.OrderBy(x => ParseState(x.State)?.Epoch ?? int.MaxValue))
            {
                var r = ApplyStateLocked(sl, from);
                needRebaseOrSync |= r.NeedSync;
                rebase ??= r.RebaseEpoch;
            }
            foreach (var k in relay.Keys) LearnKey(rec, k.StateHash, k.Envelope);
            ApplyBanOps(rec, relay.Bans);
            ApplyPinOpsLocked(rec, relay.Pins);
            if (relay.Profile != null) ApplyProfileLocked(rec, relay.Profile);
            foreach (var mp in relay.MemberProfiles) ApplyMemberProfileLocked(rec, mp);
        }

        if (rebase is { } lost) await RebaseAsync(rec, lost);

        foreach (var m in relay.Messages)
        {
            if (m.Msg.Length > 0) ProcessMessage(m.From, m.Msg, m.Ts, relayed: true);
            else if (m.Seq > 0) MarkCovered(rec, m.From, m.Seq);   // deleted for everyone: nothing to show, but the seq is accounted for
        }

        if (relay.More) await SyncAsync(rec, onlyPeer: from);
    }

    // ------------------------------------------------------------------
    // Helpers (call under _lock)
    // ------------------------------------------------------------------

    private GroupStateData? ParseState(string json)
    {
        if (_parsed.TryGetValue(json, out var cached)) return cached;
        try
        {
            var d = JsonSerializer.Deserialize<GroupStateData>(json);
            if (d != null) _parsed[json] = d;
            return d;
        }
        catch (JsonException) { return null; }
    }

    private GroupStateData? ParseStored(GroupRecord rec, string hash)
    {
        var s = rec.States.FirstOrDefault(x => x.Hash == hash);
        return s == null ? null : ParseState(s.Json);
    }

    private GroupStateData Current(GroupRecord rec) =>
        ParseStored(rec, rec.CurrentHash) ?? throw new InvalidOperationException("Group has no current state.");

    private byte[]? KeyFor(GroupRecord rec, string stateHash)
    {
        var k = rec.Keys.FirstOrDefault(x => x.StateHash == stateHash);
        return k == null ? null : Convert.FromBase64String(k.Key);
    }

    private void AddKey(GroupRecord rec, int epoch, string stateHash, byte[] key)
    {
        var sk = new StoredKey { Epoch = epoch, StateHash = stateHash, Key = Convert.ToBase64String(key), Tag = GroupCrypto.TagHex(key) };
        rec.Keys.Add(sk);
        _tags[sk.Tag] = (rec, sk);
    }
}
