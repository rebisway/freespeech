/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.History;

namespace FreeSpeech.Client;

/// <summary>One row of the conversation list: who or what it is, and its last message.</summary>
/// <param name="Title">The contact's nickname, or the group's name.</param>
/// <param name="Photo">Base64 picture (contact's or group's), or null.</param>
/// <param name="Last">The newest message, or null when the conversation is empty.</param>
/// <param name="Count">Number of messages kept here.</param>
public sealed record ConversationSummary(Conversation Conversation, string Title, string? Photo, HistoryEntry? Last, int Count, bool ArchivedContact = false)
{
    public bool IsGroup => Conversation.Group != null;

    /// <summary>A group I left or was excluded from: kept for reading, no longer part of the live list. <see cref="FreeSpeechClient.DeleteGroupPermanently"/> removes it.</summary>
    public bool IsArchived => ArchivedContact || Conversation.Group is { Active: false };

    /// <summary>What to show under the title: the last message, or a short label for a deleted one or a file.</summary>
    public string Preview => Last == null ? "" : Last.State switch
    {
        MessageState.Retracted => "Message deleted",
        MessageState.FileRetracted => "File removed",
        _ => Last.Text,
    };
}

public sealed partial class FreeSpeechClient
{
    /// <summary>
    /// Every conversation — each confirmed contact and each group still kept here — newest activity first, then by name.
    /// What a front-end shows as its home screen.
    /// </summary>
    public IReadOnlyList<ConversationSummary> GetConversations()
    {
        var list = new List<ConversationSummary>();

        foreach (var c in _confirmedContacts.All)
            list.Add(Summarize(new Conversation(c.DiscoveryId, c.DiscoveryId, null), c.DisplayName, c.Photo));

        foreach (var a in _archivedContacts.All.Where(a => _confirmedContacts.FindById(a.DiscoveryId) == null))
            list.Add(Summarize(new Conversation(a.DiscoveryId, a.DiscoveryId, null), a.Nickname, a.Photo) with { ArchivedContact = true });

        foreach (var g in _groups.Store.All)
            list.Add(Summarize(GroupConversation(g), _groups.Store.Find(g.GroupId)?.Name ?? g.Name, _groups.GroupPhoto(g)));

        return list
            .OrderByDescending(s => s.Last?.Ts ?? 0)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>One message found by <see cref="Search"/>: where it is (the conversation and its title), who wrote it, and the message itself.</summary>
    public sealed record SearchHit(Conversation Conversation, string Title, string AuthorName, HistoryEntry Entry);

    /// <summary>Shortest text <see cref="Search"/> looks for (one letter would match nearly everything).</summary>
    public const int MinSearchLength = 2;

    /// <summary>
    /// Messages containing <paramref name="text"/> (case and accents ignored), newest first, across every conversation — or only <paramref name="within"/> —
    /// at most <paramref name="limit"/>. Names of files count (a file's line carries its name). Deleted messages never match.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string text, int limit = 100, Conversation? within = null)
    {
        text = text.Trim();
        if (text.Length < MinSearchLength) return [];

        var titles = GetConversations().ToDictionary(s => s.Conversation.HistoryKey);
        var hits = new List<SearchHit>();
        foreach (var (key, entry) in _history.Search(text, limit, within?.HistoryKey))
        {
            if (!titles.TryGetValue(key, out var summary)) continue;   // a conversation that no longer exists here
            var author = entry.Direction == MessageDirection.Sent ? _identity.Nickname ?? "" : DisplayNameOf(summary.Conversation, entry.From ?? summary.Conversation.ContactId ?? "");
            hits.Add(new SearchHit(summary.Conversation, summary.Title, author, entry));
        }
        return hits;
    }

    private ConversationSummary Summarize(Conversation conversation, string title, string? photo) =>
        new(conversation, title, photo, _history.Latest(conversation.HistoryKey, 1).Entries.FirstOrDefault(), _history.Count(conversation.HistoryKey));

    /// <summary>
    /// Deletes a group I am no longer in, for good: its states, keys and log, its messages and the files that came with
    /// them. Refused while I am still a member (leave first). Not undoable.
    /// </summary>
    public void DeleteGroupPermanently(Groups.GroupRecord rec)
    {
        if (rec.Active) throw new ClientException("You are still in this group — leave it first.", "group_still_in");

        var key = GroupConversation(rec).HistoryKey;
        foreach (var e in _history.Get(key))
            if (IsFileMessage(e)) _attachments.RemoveContent(e.Id);
        _history.DeleteConversation(key);
        _groups.Forget(rec);
    }

    /// <summary>The display name of a message author in a conversation: the contact's nickname, or the member's name in a group.</summary>
    public string MemberNameIn(Groups.GroupRecord group, string memberId)
    {
        if (memberId == _identity.DiscoveryId) return _groups.MyNameIn(group);
        var contact = _confirmedContacts.FindById(memberId);
        if (!string.IsNullOrWhiteSpace(contact?.LocalName)) return contact.LocalName;   // my own name for them wins
        return _groups.MemberChosenName(group, memberId)                               // then the name they chose for this group
            ?? contact?.Nickname
            ?? _groups.Members(group).FirstOrDefault(m => m.DiscoveryId == memberId)?.Nickname ?? memberId[..Math.Min(8, memberId.Length)];
    }

    /// <summary>The picture of a member as shown in a group: the one they chose for it, else the one of my contact; null when there is none.</summary>
    public string? MemberPhotoIn(Groups.GroupRecord group, string memberId) =>
        memberId == _identity.DiscoveryId ? _groups.MyPhotoIn(group) : _groups.MemberPhoto(group, memberId) ?? _confirmedContacts.FindById(memberId)?.Photo;

    /// <summary>
    /// Every downloaded picture of a conversation, oldest first (a viewer swipes through them). Found from the attachment records, so the
    /// messages of a long history are not read; a picture whose message I deleted for myself is left out.
    /// </summary>
    public IReadOnlyList<(string Path, string MessageId)> GetPictures(Conversation conversation)
    {
        var found = new List<(HistoryEntry Entry, string Path)>();
        foreach (var rec in Attachments.Store.All)
        {
            if (rec.State != FreeSpeech.Client.Attachments.AttachmentState.Complete || rec.Path == null || !rec.Mime.StartsWith("image/", StringComparison.Ordinal)) continue;
            var mine = conversation.Group != null ? rec.GroupId == conversation.Group.GroupId : rec.GroupId == null && rec.ContactId == conversation.ContactId;
            if (!mine || !File.Exists(rec.Path)) continue;
            if (_history.Find(conversation.HistoryKey, rec.Ref) is { } entry) found.Add((entry, rec.Path));
        }
        return found.OrderBy(f => f.Entry.Ts).ThenBy(f => f.Entry.Id, StringComparer.Ordinal).Select(f => (f.Path, f.Entry.Id)).ToList();
    }

    /// <summary>
    /// How far I can vouch for an id seen in a group: 0 = not one of my contacts, 1 = a confirmed contact, 2 = a contact whose safety number I compared.
    /// Computed here from my own contact list; nothing about it is ever sent.
    /// </summary>
    public int TrustOf(string memberId) => _confirmedContacts.FindById(memberId) is { } c ? (c.VerifiedAt != null ? 2 : 1) : 0;

    /// <summary>My picture in one group, from a file; null = my global picture again.</summary>
    public Task SetMyGroupPhotoAsync(Groups.GroupRecord group, string? path) => _groups.SetMyPhotoAsync(group, path == null ? null : ReadPicture(path));

    public string DisplayNameOf(Conversation conversation, string authorId)
    {
        if (conversation.Group != null) return MemberNameIn(conversation.Group, authorId);
        if (authorId == _identity.DiscoveryId) return _identity.Nickname ?? authorId[..8];
        return _confirmedContacts.FindById(authorId)?.DisplayName ?? authorId[..Math.Min(8, authorId.Length)];
    }

    /// <summary>1-based position of a message in its conversation (the "#N" of <see cref="DownloadAsync"/>, <see cref="ReplyAsync"/>...), or 0 if it is not shown.</summary>
    public int IndexOfMessage(Conversation conversation, string messageId) => _history.IndexOf(conversation.HistoryKey, messageId);

    /// <summary>The conversation for a history key (a contact id, or <c>group:{id}</c>), or null.</summary>
    public Conversation? FindConversation(string historyKey)
    {
        if (historyKey.StartsWith("group:", StringComparison.Ordinal))
        {
            var g = _groups.Store.Find(historyKey[6..]);
            return g == null ? null : GroupConversation(g);
        }
        // A contact I removed is still a conversation to read (archived).
        return _confirmedContacts.FindById(historyKey) == null && _archivedContacts.Find(historyKey) == null ? null : new Conversation(historyKey, historyKey, null);
    }
}
