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
/// Local, per-profile alias -> discovery_id map so the console smoke-test
/// client doesn't require typing a full discovery_id every time. Not part
/// of the protocol (no ContactCard/OTC yet, §3) — a convenience layer only.
/// Persisted in the profile database.
/// </summary>
public class ContactAliasStore
{
    private sealed record Alias(string Name, string Id);

    private readonly DocCollection<Alias> _docs;
    private readonly Dictionary<string, string> _aliases;

    public ContactAliasStore(ClientDatabase db)
    {
        _docs = new DocCollection<Alias>(db, "aliases", a => a.Name);
        _aliases = _docs.Load().ToDictionary(a => a.Name, a => a.Id);
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal ContactAliasStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public void Set(string name, string discoveryId)
    {
        _aliases[name] = discoveryId;
        Save();
    }

    public IReadOnlyDictionary<string, string> All => _aliases;

    /// <summary>Resolves an alias to its discovery_id, or returns the input unchanged if it isn't a known alias.</summary>
    public string Resolve(string nameOrId) => _aliases.TryGetValue(nameOrId, out var id) ? id : nameOrId;

    private void Save() => _docs.Save(_aliases.Select(kv => new Alias(kv.Key, kv.Value)));
}
