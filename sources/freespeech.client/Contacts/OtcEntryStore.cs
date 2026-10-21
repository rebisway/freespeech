/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Contacts;

/// <summary>Local store of OTCs we generated, waiting to be consumed by a reply card.</summary>
public class OtcEntryStore
{
    private readonly DocCollection<OtcEntry> _docs;
    private readonly List<OtcEntry> _entries;

    public OtcEntryStore(ClientDatabase db)
    {
        _docs = new DocCollection<OtcEntry>(db, "otc_entries", e => e.Otc);
        _entries = _docs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal OtcEntryStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<OtcEntry> All => _entries;

    public void Add(OtcEntry entry)
    {
        _entries.Add(entry);
        Save();
    }

    public void Remove(string otc)
    {
        _entries.RemoveAll(e => e.Otc == otc);
        Save();
    }

    /// <summary>Drops the entries <paramref name="expired"/> accepts; returns how many went.</summary>
    public int RemoveWhere(Func<OtcEntry, bool> expired)
    {
        var removed = _entries.RemoveAll(e => expired(e));
        if (removed > 0) Save();
        return removed;
    }

    private void Save() => _docs.Save(_entries);
}
