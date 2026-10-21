/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;
using FreeSpeech.Client.Contacts;

namespace FreeSpeech.Client.Groups;

// ---------------------------------------------------------------------
// Wire formats (protocol §7)
// ---------------------------------------------------------------------

/// <summary>One member as embedded in a GroupState. Members need not be contacts of each other, so the state carries everything needed to verify and reach them; the id is self-certifying (SHA-256 of both public keys).</summary>
public class GroupMember
{
    [JsonPropertyName("discovery_id")] public required string DiscoveryId { get; set; }
    [JsonPropertyName("nickname")] public required string Nickname { get; set; }
    [JsonPropertyName("encryption_public_key")] public required string EncryptionPublicKey { get; set; }
    [JsonPropertyName("signing_public_key")] public required string SigningPublicKey { get; set; }
    [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = [];
}

/// <summary>The signed unit: composition of the group for one epoch + a commitment to that epoch's key. Serialized ONCE — the signature covers those exact bytes, so nothing needs canonicalizing.</summary>
public class GroupStateData
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("prev_hash")] public string? PrevHash { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("created_by")] public required string CreatedBy { get; set; }
    [JsonPropertyName("moderators")] public List<string> Moderators { get; set; } = [];
    [JsonPropertyName("members")] public List<GroupMember> Members { get; set; } = [];

    /// <summary>HKDF(group_key, "grp-check"), base64: lets anyone holding a key verify it is the moderator's key, without the state containing it.</summary>
    [JsonPropertyName("key_check")] public required string KeyCheck { get; set; }
}

/// <summary>Payload of a "group_state" Letter. <c>KeyEnvelope</c> (the group key wrapped for THIS recipient only) is outside the signature; <c>key_check</c> inside the state authenticates it.</summary>
public class GroupStateLetter
{
    [JsonPropertyName("state")] public required string State { get; set; }
    [JsonPropertyName("signer")] public required string Signer { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
    [JsonPropertyName("key_envelope")] public string? KeyEnvelope { get; set; }

    /// <summary>Only on an invitation sent to someone who is not the moderator's contact: the member who proposed them vouches (§7).</summary>
    [JsonPropertyName("sponsor")] public SponsorProof? Sponsor { get; set; }
}

/// <summary>"I, a member, propose this person for this group": the member's signature over (group, candidate, ts). It travels with the invitation, so the invitee can check it against a contact of theirs.</summary>
public class SponsorProof
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

/// <summary>Payload of a "group_suggest" Letter: a member proposes one of their contacts to the moderators. The sender is the sponsor.</summary>
public class GroupSuggestLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("candidate")] public required GroupMember Candidate { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

/// <summary>A proposal waiting for a moderator: invite the candidate (with the sponsor's proof), or dismiss it.</summary>
public class GroupSuggestion
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
    [JsonPropertyName("candidate")] public required GroupMember Candidate { get; set; }
    [JsonPropertyName("sponsor_id")] public string SponsorId { get; set; } = "";
    [JsonPropertyName("sponsor_name")] public string SponsorName { get; set; } = "";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public string Sig { get; set; } = "";
    [JsonPropertyName("at")] public DateTime At { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public string Key => GroupId + "/" + Candidate.DiscoveryId;
}

/// <summary>Payload of a "group_sync" Letter: what I have. <c>Have[sender]</c> = highest seq such that I hold 1..seq of that sender contiguously.</summary>
public class GroupSyncLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("have")] public Dictionary<string, long> Have { get; set; } = [];
}

public class GroupKeyItem
{
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("state_hash")] public required string StateHash { get; set; }
    [JsonPropertyName("envelope")] public required string Envelope { get; set; }
}

/// <summary>A group message exactly as it travelled (still sealed with the group key) — a peer can hand it over without being able to forge it: the author's signature is inside.</summary>
public class GroupRelayMessage
{
    [JsonPropertyName("from")] public required string From { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("msg")] public required string Msg { get; set; }

    /// <summary>Only on a gap marker (<see cref="Msg"/> empty): the seq of a message that was deleted for everyone, so a late joiner's contiguous "have" is not stuck on it.</summary>
    [JsonPropertyName("seq")] public long Seq { get; set; }
}

public class GroupRelayLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("states")] public List<GroupStateLetter> States { get; set; } = [];
    [JsonPropertyName("keys")] public List<GroupKeyItem> Keys { get; set; } = [];
    [JsonPropertyName("messages")] public List<GroupRelayMessage> Messages { get; set; } = [];

    /// <summary>The whole blacklist (small): a member who was offline learns it from any peer.</summary>
    [JsonPropertyName("bans")] public List<BanOp> Bans { get; set; } = [];

    /// <summary>The pinned messages (the latest operation of each message).</summary>
    [JsonPropertyName("pins")] public List<PinOp> Pins { get; set; } = [];

    /// <summary>Current group name/picture and members' own profiles, as signed by their authors.</summary>
    [JsonPropertyName("profile")] public GroupProfileOp? Profile { get; set; }
    [JsonPropertyName("member_profiles")] public List<MemberProfile> MemberProfiles { get; set; } = [];

    /// <summary>More messages are available: ask again.</summary>
    [JsonPropertyName("more")] public bool More { get; set; }
}

/// <summary>
/// One entry of the group blacklist (§7): signed by a moderator ("add") or by the creator ("remove"). Not tied to an
/// epoch and never triggers a key rotation. The effective state of an id is its latest operation by <c>Ts</c>.
/// </summary>
public class BanOp
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("kind")] public required string Kind { get; set; }   // add | remove
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("signer")] public required string Signer { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

/// <summary>
/// A message pinned in the group (§7), or released: signed by a moderator, hung on the message id (<c>author:ts</c>), outside the GroupState
/// (no key rotation). For one message the latest operation by <c>Ts</c> wins; at most <see cref="GroupService.MaxPinned"/> messages are pinned, the newest ones.
/// </summary>
public class PinOp
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("kind")] public required string Kind { get; set; }   // pin | unpin
    [JsonPropertyName("target")] public required string Target { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("signer")] public required string Signer { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

/// <summary>Payload of a "group_pin" Letter.</summary>
public class PinUpdateLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("ops")] public List<PinOp> Ops { get; set; } = [];
}

/// <summary>
/// The group's display name and picture (§7), changed by the creator. Like a blacklist entry it is a signed, timestamped
/// operation — latest wins — kept OUT of the GroupState, so renaming a group or changing its picture never rotates the key.
/// Payload of a "group_profile" Letter.
/// </summary>
public class GroupProfileOp
{
    public const int MaxPhotoBytes = 100 * 1024;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 300;

    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }

    /// <summary>Base64 JPEG/PNG/WebP, at most 100 KiB once decoded; null = no picture.</summary>
    [JsonPropertyName("photo")] public string? Photo { get; set; }

    /// <summary>What the group is about (at most 300 characters); null = none. Covered by the signature.</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("signer")] public required string Signer { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

/// <summary>A member's own name and picture as shown inside one group (payload of a "member_profile" Letter). Signed by that member, so any peer can relay it.</summary>
public class MemberProfile
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("nickname")] public required string Nickname { get; set; }
    [JsonPropertyName("photo")] public string? Photo { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }

    /// <summary>
    /// The member's revocation public key (base64), pinned by whoever sees it first (§12, identity migration). It has its own signature
    /// (<see cref="RevocationSig"/>) and stays out of <see cref="Sig"/>, so versions that do not know it still verify the profile.
    /// </summary>
    [JsonPropertyName("revocation_public_key")] public string? RevocationPublicKey { get; set; }

    /// <summary>The member's signing key over <c>revpin|id|key</c>: the key was announced by that member, not slipped in by a relaying peer.</summary>
    [JsonPropertyName("revocation_sig")] public string? RevocationSig { get; set; }
}

/// <summary>Payload of a "blacklist_update" Letter.</summary>
public class BanUpdateLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("ops")] public List<BanOp> Ops { get; set; } = [];
}

public class GroupLeaveLetter
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
}

/// <summary>Decrypted content of a group message (sign-then-encrypt: <c>sig</c> is by the sender's Ed25519 key over type|payload|ts|group_id|epoch|seq).</summary>
public class GroupMessageLetter
{
    [JsonPropertyName("type")] public required string Type { get; set; }
    [JsonPropertyName("payload")] public required string Payload { get; set; }
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("seq")] public long Seq { get; set; }

    /// <summary>message_id ("author:ts") this message answers, or null. Covered by the signature.</summary>
    [JsonPropertyName("reply_to")] public string? ReplyTo { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

// ---------------------------------------------------------------------
// Local storage (never on the wire, never in a GroupState: history stays local)
// ---------------------------------------------------------------------

public class StoredState
{
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("hash")] public required string Hash { get; set; }
    [JsonPropertyName("json")] public required string Json { get; set; }
    [JsonPropertyName("signer")] public required string Signer { get; set; }
    [JsonPropertyName("sig")] public required string Sig { get; set; }
}

public class StoredKey
{
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("state_hash")] public required string StateHash { get; set; }
    [JsonPropertyName("key")] public required string Key { get; set; }
    [JsonPropertyName("tag")] public required string Tag { get; set; }
}

public class LogEntry
{
    [JsonPropertyName("from")] public required string From { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("msg")] public required string Msg { get; set; }
}

/// <summary>A membership change I issued as moderator, remembered until its epoch is settled, so it can be replayed on top of a concurrent winner.</summary>
public class PendingOp
{
    [JsonPropertyName("kind")] public required string Kind { get; set; }   // add | remove | promote
    [JsonPropertyName("epoch")] public int Epoch { get; set; }
    [JsonPropertyName("member_id")] public required string MemberId { get; set; }
    [JsonPropertyName("member")] public GroupMember? Member { get; set; }
}

/// <summary>
/// An invitation to a group, waiting for my answer. The signed state and my key envelope are kept exactly as received, so
/// accepting adopts them as if they had just arrived; declining keeps only a mark, so the same group does not ask again.
/// </summary>
public class PendingGroupInvite
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("inviter")] public string Inviter { get; set; } = "";
    [JsonPropertyName("inviter_name")] public string InviterName { get; set; } = "";
    /// <summary>Set when the inviter is not my contact but a member I know proposed me.</summary>
    [JsonPropertyName("sponsor_name")] public string? SponsorName { get; set; }
    /// <summary>Set when the inviter is an administrator tool of a private server I belong to (named by that server), not my contact.</summary>
    [JsonPropertyName("from_administrator")] public bool FromAdministrator { get; set; }
    [JsonPropertyName("members")] public int Members { get; set; }
    [JsonPropertyName("letter")] public string Letter { get; set; } = "";
    [JsonPropertyName("at")] public DateTime At { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("declined")] public bool Declined { get; set; }
}

public class GroupRecord
{
    [JsonPropertyName("group_id")] public required string GroupId { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }

    /// <summary>False once we left or were excluded: we keep what we have, send and accept nothing new.</summary>
    [JsonPropertyName("active")] public bool Active { get; set; } = true;

    [JsonPropertyName("current_hash")] public required string CurrentHash { get; set; }
    [JsonPropertyName("states")] public List<StoredState> States { get; set; } = [];
    [JsonPropertyName("pending")] public List<GroupStateLetter> Pending { get; set; } = [];
    [JsonPropertyName("keys")] public List<StoredKey> Keys { get; set; } = [];
    [JsonPropertyName("log")] public List<LogEntry> Log { get; set; } = [];
    [JsonPropertyName("next_seq")] public long NextSeq { get; set; } = 1;

    /// <summary>Per sender: seqs of messages deleted for everyone that we only know as a gap marker from a peer (no content, no ts). They count as "had" so catch-up moves past them; they never block a real message.</summary>
    [JsonPropertyName("covered")] public Dictionary<string, List<long>> Covered { get; set; } = [];
    [JsonPropertyName("ops")] public List<PendingOp> Ops { get; set; } = [];

    /// <summary>The latest valid pin operation of each message (the pinned ones are derived from it).</summary>
    [JsonPropertyName("pins")] public List<PinOp> Pins { get; set; } = [];

    /// <summary>Every valid blacklist operation seen (the effective list is derived from it).</summary>
    [JsonPropertyName("bans")] public List<BanOp> Bans { get; set; } = [];

    /// <summary>Members I have proof of life from (a message, a catch-up request): the others in the state are still invited, not yet joined. Null on groups created before this existed: nobody is shown as pending there.</summary>
    [JsonPropertyName("heard")] public List<string>? Heard { get; set; }

    /// <summary>Latest valid group profile (name + picture) signed by the creator, if the name was ever changed.</summary>
    [JsonPropertyName("profile")] public GroupProfileOp? Profile { get; set; }

    /// <summary>The name I use in this group, when it is not my global one (null = the global name). Local; it travels in my <see cref="MemberProfile"/>.</summary>
    [JsonPropertyName("my_name")] public string? MyName { get; set; }

    /// <summary>The picture I use in this group (base64), when it is not my global one (null = the global picture). Local; it travels in my <see cref="MemberProfile"/>.</summary>
    [JsonPropertyName("my_photo")] public string? MyPhoto { get; set; }

    /// <summary>Latest valid profile of each member who customised theirs.</summary>
    [JsonPropertyName("member_profiles")] public List<MemberProfile> MemberProfiles { get; set; } = [];
}
