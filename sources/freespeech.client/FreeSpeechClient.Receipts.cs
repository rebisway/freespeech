/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Client.History;

namespace FreeSpeech.Client;

// Unread counters and read receipts. A read mark is the position (origin timestamp, message_id) up to which a conversation
// was read: mine for what I received, the contact's for what I sent. One receipt covers every earlier message.
public sealed partial class FreeSpeechClient
{
    private const string TypeReadReceipt = "read_ack";   // listed in PROTOCOL §5 "Types de Letter"

    /// <summary>Raised when a contact tells me they read my messages (1-to-1 only: there are no receipts in groups).</summary>
    public event Action<Conversation>? ReadReceived;

    /// <summary>False = never tell contacts that I read their messages (and, as everywhere, I then see nobody's receipts less than before: they still send theirs).</summary>
    public bool SendReadReceipts { get; set; } = true;

    /// <summary>How many messages I received in this conversation since I last looked at it.</summary>
    public int UnreadCount(Conversation c) => _history.UnreadCount(c.HistoryKey);

    /// <summary>
    /// I am looking at the conversation: everything received so far counts as read. In a 1-to-1 conversation the contact is
    /// told with one best-effort receipt (not queued, not shown in the history): if it is lost, the next one covers it.
    /// </summary>
    public async Task MarkReadAsync(Conversation c)
    {
        var last = _history.LastReceived(c.HistoryKey);
        if (last == null || !_history.AdvanceRead(c.HistoryKey, HistoryStore.ReadByMe, new HistoryCursor(last.Ts, last.Id))) return;
        if (c.Group != null || !SendReadReceipts) return;
        await SendLetterOnceAsync(c.ContactId!, TypeReadReceipt, JsonSerializer.Serialize(new MessageOp { Target = last.Id }));
    }

    /// <summary>Whether the contact read my message <paramref name="e"/> (always false in a group).</summary>
    public bool IsReadByPeer(Conversation c, HistoryEntry e)
    {
        if (c.Group != null || e.Direction != MessageDirection.Sent) return false;
        return _history.ReadCursor(c.HistoryKey, HistoryStore.ReadByPeer) is { } m && (e.Ts < m.Ts || (e.Ts == m.Ts && string.CompareOrdinal(e.Id, m.Id) <= 0));
    }

    private void HandleReadReceipt(string from, string payload)
    {
        MessageOp? op;
        try { op = JsonSerializer.Deserialize<MessageOp>(payload); }
        catch (JsonException) { op = null; }

        // Only a receipt for a message I wrote means anything; the mark points at it.
        if (op == null || AuthorOf(op.Target) != _identity.DiscoveryId) return;
        var mine = _history.Find(from, op.Target);
        if (mine == null) return;
        _pendingMessages.AcknowledgeReadUpTo(from, mine.Ts);   // read means received: no need to wait for their acknowledgement or send it again
        FlagDownIfNothingWaits(from);
        if (_history.AdvanceRead(from, HistoryStore.ReadByPeer, new HistoryCursor(mine.Ts, mine.Id)))
            ReadReceived?.Invoke(new Conversation(from, from, null));
    }
}
