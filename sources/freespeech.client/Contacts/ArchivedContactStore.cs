/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json.Serialization;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Contacts;

/// <summary>A contact I removed: only what the conversation list needs to keep showing the archived conversation (the history itself stays under the same id).</summary>
public class ArchivedContact
{
    [JsonPropertyName("id")] public required string DiscoveryId { get; set; }
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("photo")] public string? Photo { get; set; }
    [JsonPropertyName("at")] public DateTime ArchivedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Removed contacts, kept so their conversation can be read, restored (the contact is added again) or deleted for good.</summary>
public class ArchivedContactStore
{
    private readonly DocCollection<ArchivedContact> _docs;
    private readonly List<ArchivedContact> _items;

    public ArchivedContactStore(ClientDatabase db)
    {
        _docs = new DocCollection<ArchivedContact>(db, "archived_contacts", c => c.DiscoveryId);
        _items = _docs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal ArchivedContactStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<ArchivedContact> All => _items;

    public ArchivedContact? Find(string discoveryId) => _items.Find(c => c.DiscoveryId == discoveryId);

    public void Add(ArchivedContact c)
    {
        _items.RemoveAll(x => x.DiscoveryId == c.DiscoveryId);
        _items.Add(c);
        _docs.Save(_items);
    }

    public bool Remove(string discoveryId)
    {
        if (_items.RemoveAll(c => c.DiscoveryId == discoveryId) == 0) return false;
        _docs.Save(_items);
        return true;
    }
}