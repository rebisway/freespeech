/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

// Groups (§7): the group engine is exposed as <see cref="Groups"/>; this part adds name resolution and the transport.
public sealed partial class FreeSpeechClient
{
    /// <summary>The group engine: create, add/remove members, roles, bans, profile, sync. Group messages, files and history go through <see cref="SendAsync"/>, <see cref="UploadAsync"/>, <see cref="GetHistory"/>.</summary>
    public GroupService Groups => _groups;

    public GroupRecord ResolveGroup(string nameOrId) =>
        _groups.Store.Resolve(nameOrId) ?? throw new ClientException($"No group '{nameOrId}' (see 'group list').");

    /// <summary>A member by the name they currently display, the name they joined under, or an id (prefix).</summary>
    public GroupMember ResolveGroupMember(GroupRecord rec, string arg)
    {
        var members = _groups.Members(rec);
        var originalNames = _groups.CurrentState(rec).Members;   // a member who renamed themselves can still be found by the name they joined under
        return members.FirstOrDefault(x => string.Equals(x.Nickname, arg, StringComparison.OrdinalIgnoreCase))
            ?? members.FirstOrDefault(x => originalNames.Any(o => o.DiscoveryId == x.DiscoveryId && string.Equals(o.Nickname, arg, StringComparison.OrdinalIgnoreCase)))
            ?? members.FirstOrDefault(x => x.DiscoveryId == arg)
            ?? members.FirstOrDefault(x => x.DiscoveryId.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
            ?? throw new ClientException($"'{arg}' is not a member of '{rec.Name}'.");
    }

    public static GroupMember ToGroupMember(Contact c) => new()
    {
        DiscoveryId = c.DiscoveryId, Nickname = c.Nickname,
        EncryptionPublicKey = c.EncryptionPublicKey, SigningPublicKey = c.SigningPublicKey, Servers = c.Servers,
    };

    /// <summary>Creates a group with the given confirmed contacts (by name, alias or id); each gets an invitation, queued if offline.</summary>
    public async Task<GroupRecord> CreateGroupAsync(string name, IEnumerable<string> contacts) =>
        await _groups.CreateAsync(name, contacts.Select(c => ToGroupMember(RequireContact(c))).ToList());

    public Task AddGroupMemberAsync(GroupRecord rec, string contact) =>
        _groups.AddMemberAsync(rec, ToGroupMember(RequireContact(contact)));

    /// <summary>The pinned messages of a group changed (a moderator pinned or released one).</summary>
    public event Action<Conversation>? PinsChanged;

    /// <summary>Whether I may pin and release messages in this conversation: a moderator of the group, the creator included.</summary>
    public bool CanPin(Conversation c) => c.Group != null && c.Group.Active && _groups.IsModerator(c.Group);

    /// <summary>Pins or releases the message at <paramref name="index"/> (1-based, as the other message calls), whoever wrote it. Moderators only.</summary>
    public async Task PinMessageAsync(Conversation c, int index, bool pin)
    {
        if (c.Group == null) throw new ClientException("Only a group has pinned messages.", "pin_not_group");
        var entry = _history.At(c.HistoryKey, index) ?? throw new ClientException($"No message #{index}.");
        await _groups.PinAsync(c.Group, entry.Id, pin);
    }

    /// <summary>The pinned messages that this device holds and that still stand (a deleted one is left out), the most recently pinned first.</summary>
    public IReadOnlyList<FreeSpeech.Client.History.HistoryEntry> GetPinned(Conversation c)
    {
        if (c.Group == null) return [];
        var found = new List<FreeSpeech.Client.History.HistoryEntry>();
        foreach (var id in _groups.PinnedIds(c.Group))
            if (_history.Find(c.HistoryKey, id) is { } e && e.State is not (FreeSpeech.Client.History.MessageState.Retracted or FreeSpeech.Client.History.MessageState.DeletedForMe)) found.Add(e);
        return found;
    }

    /// <summary>Whether this message is pinned now.</summary>
    public bool IsPinned(Conversation c, string messageId) => c.Group != null && _groups.PinnedIds(c.Group).Contains(messageId);

    /// <summary>Changes the group picture from a file; null clears it.</summary>
    public Task SetGroupPhotoAsync(GroupRecord rec, string? path) =>
        path == null ? _groups.SetProfileAsync(rec, name: null, photo: null, clearPhoto: true)
                     : _groups.SetProfileAsync(rec, name: null, ReadPicture(path));

    // ------------------------------------------------------------------
    // IGroupTransport
    // ------------------------------------------------------------------

    async Task<bool> IGroupTransport.SendLetterAsync(GroupMember to, string type, string payload, bool queue)
    {
        var ts = NextTs();
        var cipher = MessageCrypto.Encrypt(_identity.DiscoveryId, to.DiscoveryId, type, payload, ts, Convert.FromBase64String(to.EncryptionPublicKey), _identity.SigningPrivateKey);
        var envelope = new Envelope { From = _identity.DiscoveryId, To = to.DiscoveryId, Crypto = 1, Msg = cipher, Ts = ts };
        var result = await _relay.SendAsync(_relay.Candidates(to.Servers, to.DiscoveryId), envelope);

        if (!result.Delivered && queue)
        {
            // Invitations must survive an offline invitee; sync/relay letters are cheap and simply retried on the next tick.
            _pendingMessages.Add(new PendingMessage
            {
                Id = $"{_identity.DiscoveryId}:{ts}", To = to.DiscoveryId, Type = type, Text = payload, Ts = ts, CreatedAt = DateTime.UtcNow,
                RecipientEncKey = to.EncryptionPublicKey, RecipientServers = to.Servers,
            });
        }
        return result.Delivered;
    }

    async Task<bool> IGroupTransport.SendGroupMessageAsync(GroupMember to, string sealedMsg, long ts)
    {
        var envelope = new Envelope { From = _identity.DiscoveryId, To = to.DiscoveryId, Crypto = 2, Msg = sealedMsg, Ts = ts };
        return (await _relay.SendAsync(_relay.Candidates(to.Servers, to.DiscoveryId), envelope)).Delivered;
    }
}
