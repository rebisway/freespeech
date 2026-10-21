/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Groups;

/// <summary>
/// Local storage of groups (profile database): states, keys, message log. Everything the
/// protocol refuses to put in a GroupState (history, old keys, the serving log) lives here
/// and only here. Encrypted at rest with the rest of the database.
/// </summary>
public class GroupStore
{
    private readonly DocCollection<GroupRecord> _docs;
    private readonly List<GroupRecord> _groups;
    private readonly DocCollection<PendingGroupInvite> _inviteDocs;
    private readonly List<PendingGroupInvite> _invites;
    private readonly DocCollection<GroupSuggestion> _suggestionDocs;
    private readonly List<GroupSuggestion> _suggestions;
    private readonly object _lock = new();

    public GroupStore(ClientDatabase db)
    {
        _docs = new DocCollection<GroupRecord>(db, "groups", g => g.GroupId);
        _groups = _docs.Load();
        _inviteDocs = new DocCollection<PendingGroupInvite>(db, "group_invites", i => i.GroupId);
        _invites = _inviteDocs.Load();
        _suggestionDocs = new DocCollection<GroupSuggestion>(db, "group_suggestions", s => s.Key);
        _suggestions = _suggestionDocs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal GroupStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<GroupRecord> All { get { lock (_lock) return _groups.ToList(); } }

    public GroupRecord? Find(string groupId)
    {
        lock (_lock) return _groups.Find(g => g.GroupId == groupId);
    }

    /// <summary>Exact group id, else a unique name match (case-insensitive), else a unique id prefix.</summary>
    public GroupRecord? Resolve(string idOrName)
    {
        lock (_lock)
        {
            var byId = _groups.Find(g => g.GroupId == idOrName);
            if (byId != null) return byId;
            var byName = _groups.Where(g => string.Equals(g.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            var byPrefix = _groups.Where(g => g.GroupId.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            return byPrefix.Count == 1 ? byPrefix[0] : null;
        }
    }

    public void Add(GroupRecord record)
    {
        lock (_lock)
        {
            _groups.Add(record);
            Save();
        }
    }

    public void Remove(string groupId)
    {
        lock (_lock)
        {
            _groups.RemoveAll(g => g.GroupId == groupId);
            Save();
        }
    }

    public void Save()
    {
        lock (_lock) _docs.Save(_groups);
    }

    // ---- invitations waiting for an answer ----

    /// <summary>Invitations not answered yet (declined ones are only kept as a mark).</summary>
    public IReadOnlyList<PendingGroupInvite> Invites { get { lock (_lock) return _invites.Where(i => !i.Declined).ToList(); } }

    public PendingGroupInvite? FindInvite(string groupId)
    {
        lock (_lock) return _invites.Find(i => i.GroupId == groupId);
    }

    public void AddInvite(PendingGroupInvite invite)
    {
        lock (_lock)
        {
            _invites.RemoveAll(i => i.GroupId == invite.GroupId);
            _invites.Add(invite);
            _inviteDocs.Save(_invites);
        }
    }

    public void RemoveInvite(string groupId)
    {
        lock (_lock)
        {
            _invites.RemoveAll(i => i.GroupId == groupId);
            _inviteDocs.Save(_invites);
        }
    }

    // ---- members proposed to the moderators ----

    public IReadOnlyList<GroupSuggestion> Suggestions { get { lock (_lock) return _suggestions.ToList(); } }

    public GroupSuggestion? FindSuggestion(string groupId, string candidateId)
    {
        lock (_lock) return _suggestions.Find(s => s.GroupId == groupId && s.Candidate.DiscoveryId == candidateId);
    }

    public void AddSuggestion(GroupSuggestion s)
    {
        lock (_lock)
        {
            _suggestions.RemoveAll(x => x.Key == s.Key);
            _suggestions.Add(s);
            _suggestionDocs.Save(_suggestions);
        }
    }

    public void RemoveSuggestion(string groupId, string candidateId)
    {
        lock (_lock)
        {
            _suggestions.RemoveAll(s => s.GroupId == groupId && s.Candidate.DiscoveryId == candidateId);
            _suggestionDocs.Save(_suggestions);
        }
    }

    public void SaveInvites()
    {
        lock (_lock) _inviteDocs.Save(_invites);
    }
}
