/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Contacts;

namespace FreeSpeech.Client;

// Removing a contact archives the conversation; adding the contact again brings it back; deleting it is for good.
public sealed partial class FreeSpeechClient
{
    private ArchivedContactStore _archivedContacts = null!;

    /// <summary>Contacts I removed, whose conversation is kept for reading.</summary>
    public IReadOnlyList<ArchivedContact> ArchivedContacts => _archivedContacts.All;

    /// <summary>
    /// Removes a contact. The conversation is archived, not erased: its messages stay readable, and if the contact is added
    /// again (a new exchange of cards) the same conversation is back in the list. Nothing is sent to the contact; what they
    /// send afterwards is refused as from a stranger.
    /// </summary>
    public void RemoveContact(string nameOrId)
    {
        var c = RequireContact(nameOrId);
        _archivedContacts.Add(new ArchivedContact { DiscoveryId = c.DiscoveryId, Nickname = c.DisplayName, Photo = c.Photo });
        _pendingMessages.RemoveFor(c.DiscoveryId);   // what never left is not sent to someone I removed
        _confirmedContacts.Remove(c.DiscoveryId);
    }

    /// <summary>Deletes an archived conversation for good: its messages, the files that came with them, and the record of the contact. Not undoable; talking again means a new exchange of cards and a new, empty conversation.</summary>
    public void DeleteArchivedContactPermanently(string discoveryId)
    {
        if (_confirmedContacts.FindById(discoveryId) != null) throw new ClientException("This contact is not archived.");
        foreach (var e in _history.Get(discoveryId))
            if (IsFileMessage(e)) _attachments.RemoveContent(e.Id);
        _history.DeleteConversation(discoveryId);
        _archivedContacts.Remove(discoveryId);
    }
}