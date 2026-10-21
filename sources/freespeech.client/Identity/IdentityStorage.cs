/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// Loads and saves the <see cref="UserIdentity"/> (private keys included) inside the encrypted profile
/// database: nothing sensitive is left in a plain file.
/// </summary>
public class IdentityStorage
{
    private readonly DocCollection<UserIdentity> _docs;

    public IdentityStorage(ClientDatabase db) => _docs = new DocCollection<UserIdentity>(db, "identity", _ => "me");

    /// <summary>Returns the stored identity, or null if none has been created yet.</summary>
    public UserIdentity? Load() => _docs.Load().FirstOrDefault();

    /// <summary>Persists the identity, overwriting any previous one.</summary>
    public void Save(UserIdentity identity) => _docs.Save([identity]);
}
