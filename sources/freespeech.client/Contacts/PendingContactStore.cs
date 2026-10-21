/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// Local store of contact requests we could not yet deliver. The relay never
/// stores anything for us (§9/§10 — presence flags only), so this is what
/// survives a restart and feeds the retry loop.
/// </summary>
public class PendingContactStore
{
    private readonly DocCollection<PendingContact> _docs;
    private readonly List<PendingContact> _pending;

    public PendingContactStore(ClientDatabase db)
    {
        _docs = new DocCollection<PendingContact>(db, "pending_contacts", p => p.Id);
        _pending = _docs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal PendingContactStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<PendingContact> All => _pending;

    public PendingContact? FindByTargetId(string discoveryId) =>
        _pending.Find(p => p.TheirCard.DiscoveryId == discoveryId);

    public void Add(PendingContact pc)
    {
        _pending.Add(pc);
        Save();
    }

    public void Remove(string id)
    {
        _pending.RemoveAll(p => p.Id == id);
        Save();
    }

    public void TouchLastAttempt(string id)
    {
        var pc = _pending.Find(p => p.Id == id);
        if (pc == null) return;
        pc.LastAttemptAt = DateTime.UtcNow;
        Save();
    }

    private void Save() => _docs.Save(_pending);
}
