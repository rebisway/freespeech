/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Outbox;

/// <summary>Local outbox of messages waiting for their recipient to come back online.</summary>
public class PendingMessageStore
{
    private readonly DocCollection<PendingMessage> _docs;
    private readonly List<PendingMessage> _pending;

    public PendingMessageStore(ClientDatabase db)
    {
        _docs = new DocCollection<PendingMessage>(db, "outbox", m => m.Id);
        _pending = _docs.Load();
    }

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal PendingMessageStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    public IReadOnlyList<PendingMessage> All => _pending;

    public void Add(PendingMessage message)
    {
        _pending.Add(message);
        Save();
    }

    /// <summary>What still waits for its contact to be reachable (not delivered): what the user sees as "not sent yet".</summary>
    public IEnumerable<PendingMessage> Unsent => _pending.Where(p => p.SentAt == null);

    /// <summary>The relay delivered this letter: it stays, waiting for the contact's acknowledgement.</summary>
    public void MarkSent(string id)
    {
        var m = _pending.Find(p => p.Id == id);
        if (m == null) return;
        m.SentAt = DateTime.UtcNow;
        Save();
    }

    /// <summary>The delivered letter was sent again.</summary>
    public void MarkResent(string id)
    {
        var m = _pending.Find(p => p.Id == id);
        if (m == null) return;
        m.SentAt = DateTime.UtcNow;
        m.Resends++;
        Save();
    }

    /// <summary>The contact is back online: every letter of mine delivered to them and not confirmed is sent again at once (it may have gone into a dead connection).</summary>
    public void MarkAllUnsentFor(string contactId)
    {
        var changed = false;
        foreach (var m in _pending.Where(p => p.To == contactId && p.SentAt != null)) { m.SentAt = null; changed = true; }
        if (changed) Save();
    }

    /// <summary>The contact has not answered for long enough: the letter waits for them again.</summary>
    public void MarkUnsent(string id)
    {
        var m = _pending.Find(p => p.Id == id);
        if (m == null) return;
        m.SentAt = null;
        Save();
    }

    /// <summary>The contact acknowledged these letters: they are done.</summary>
    public void Acknowledge(string contactId, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        if (_pending.RemoveAll(m => m.To == contactId && set.Contains(m.Id)) > 0) Save();
    }

    /// <summary>The contact read up to this time: the messages and files delivered to them before it are done.</summary>
    public void AcknowledgeReadUpTo(string contactId, long ts)
    {
        if (_pending.RemoveAll(m => m.To == contactId && m.SentAt != null && m.Ts <= ts && m.Type is "message" or "attachment") > 0) Save();
    }

    /// <summary>A message not delivered yet can still be edited: what is sent later is the new text.</summary>
    public bool SetText(string id, string text)
    {
        var m = _pending.Find(p => p.Id == id && p.SentAt == null);
        if (m == null) return false;
        m.Text = text;
        Save();
        return true;
    }

    public void Remove(string id)
    {
        _pending.RemoveAll(m => m.Id == id);
        Save();
    }

    /// <summary>Drops everything still waiting for one recipient (a contact I removed).</summary>
    public void RemoveFor(string recipientId)
    {
        if (_pending.RemoveAll(m => m.To == recipientId) > 0) Save();
    }

    /// <summary>Stops (or resumes) the waiting for a letter that has not been delivered: the letter itself stays queued.</summary>
    public bool SetParked(string id, bool parked)
    {
        var m = _pending.Find(p => p.Id == id && p.SentAt == null);
        if (m == null) return false;
        if (m.Parked == parked) return true;
        m.Parked = parked;
        Save();
        return true;
    }

    public void TouchLastAttempt(string id)
    {
        var m = _pending.Find(p => p.Id == id);
        if (m == null) return;
        m.LastAttemptAt = DateTime.UtcNow;
        Save();
    }

    private void Save() => _docs.Save(_pending);
}
