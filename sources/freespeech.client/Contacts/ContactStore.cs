/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Contacts;

/// <summary>Local store of confirmed contacts (both sides exchanged and accepted a ContactCard).</summary>
public class ContactStore
{
    private readonly DocCollection<Contact> _docs;
    private readonly List<Contact> _contacts;

    public ContactStore(ClientDatabase db)
    {
        _docs = new DocCollection<Contact>(db, "contacts", c => c.DiscoveryId);
        _contacts = _docs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal ContactStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<Contact> All => _contacts;

    public Contact? FindById(string discoveryId) => _contacts.Find(c => c.DiscoveryId == discoveryId);

    /// <summary>
    /// Case-insensitive name lookup. The name I gave a contact is tried first (it is what I see and type), then their own
    /// nickname; the first match wins if names collide.
    /// </summary>
    public Contact? FindByNickname(string nickname) =>
        _contacts.Find(c => string.Equals(c.DisplayName, nickname, StringComparison.OrdinalIgnoreCase))
        ?? _contacts.Find(c => string.Equals(c.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

    public void Upsert(Contact contact)
    {
        _contacts.RemoveAll(c => c.DiscoveryId == contact.DiscoveryId);
        _contacts.Add(contact);
        Save();
    }

    public bool Remove(string discoveryId)
    {
        if (_contacts.RemoveAll(c => c.DiscoveryId == discoveryId) == 0) return false;
        Save();
        return true;
    }

    private void Save() => _docs.Save(_contacts);
}
