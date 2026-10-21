/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;
using Microsoft.Data.Sqlite;

namespace FreeSpeech.Client.History;

/// <summary>Position of a message in a conversation: sender-local origin timestamp, then message_id as tie-break — the same total order on every device.</summary>
public readonly record struct HistoryCursor(long Ts, string Id);

/// <summary>A window of a conversation, oldest first, with whether more messages exist on each side.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, bool HasMoreBefore, bool HasMoreAfter)
{
    public HistoryCursor? Oldest => Entries.Count == 0 ? null : new(Entries[0].Ts, Entries[0].Id);
    public HistoryCursor? Newest => Entries.Count == 0 ? null : new(Entries[^1].Ts, Entries[^1].Id);
}

/// <summary>
/// Local conversation history, one row per message in the <c>messages</c> table, keyed by conversation
/// (a contact's discovery_id, or <c>group:{id}</c>). Encrypted with the rest of the database. Conversations
/// are read in windows (<see cref="Latest"/>, <see cref="Before"/>, <see cref="After"/>, <see cref="Around"/>)
/// so a front-end only loads what it can display; cursors, not offsets, so messages arriving while the user
/// scrolls never shift the page.
///
/// A message can later be edited, deleted for everyone (<see cref="MessageState.Retracted"/>), or have its
/// file removed. Those changes are operations that may arrive BEFORE the message they target (catch-up,
/// several servers): they are parked in <c>message_ops</c> and applied the moment the message is stored.
/// A message deleted on this device stays as a hidden row, so a re-delivered copy is not stored again.
/// </summary>
public class HistoryStore
{
    private const string Columns = "id, direction, text, reply_to, from_id, ts, state, edited_ts";
    private const string Visible = "state <> 1";

    private readonly ClientDatabase _db;

    public HistoryStore(ClientDatabase db) => _db = db;

    /// <summary>Tests and tools: an unencrypted database in <paramref name="profileDir"/>.</summary>
    internal HistoryStore(string profileDir) : this(ClientDatabase.Open(profileDir, null)) { }

    private HistoryEntry Map(SqliteDataReader r, int o = 0)
    {
        var ts = r.GetInt64(o + 5);
        var id = r.GetString(o);
        return new HistoryEntry
        {
            Id = id, Direction = (MessageDirection)r.GetInt32(o + 1), Text = _db.OpenText(r, o + 2, $"m.text|{id}") ?? "",
            ReplyToId = _db.OpenText(r, o + 3, $"m.reply|{id}"), From = _db.OpenText(r, o + 4, $"m.from|{id}"),
            Ts = ts, At = new DateTime(ts, DateTimeKind.Utc),
            State = (MessageState)r.GetInt32(o + 6), EditedTs = r.IsDBNull(o + 7) ? null : r.GetInt64(o + 7),
        };
    }

    /// <summary>
    /// Records one entry in a conversation, keyed by <paramref name="messageId"/> (protocol §5). A duplicate
    /// id is ignored — the same message can be delivered to more than one of our connections, and a message
    /// deleted on this device must not come back. Order is by <paramref name="originTs"/> (sender clock), not
    /// arrival: messages cross in flight, especially in groups. Operations that arrived earlier for this message
    /// (edit, delete, file removal) are applied now. Returns false when the message was already there. <paramref name="state"/>/<paramref name="editedTs"/> are for restoring a backup.
    /// </summary>
    public bool Append(string conversation, string messageId, long originTs, MessageDirection direction, string text, string? replyToId = null, string? from = null,
        MessageState state = MessageState.Normal, long? editedTs = null)
    {
        var inserted = _db.Execute("INSERT OR IGNORE INTO messages(conv, id, ts, direction, text, reply_to, from_id, state, edited_ts) VALUES($c, $i, $t, $d, $x, $r, $f, $s, $e)",
            ("$c", conversation), ("$i", messageId), ("$t", originTs), ("$d", (int)direction), ("$x", _db.SealText(text, $"m.text|{messageId}")), ("$r", _db.SealText(replyToId, $"m.reply|{messageId}")), ("$f", _db.SealText(from, $"m.from|{messageId}")), ("$s", (int)state), ("$e", editedTs));
        if (inserted == 0) return false;   // already stored (delivered twice), or deleted here: nothing new to announce
        ApplyParkedOps(conversation, messageId);
        return true;
    }

    // ------------------------------------------------------------------
    // Edit / delete / file removal
    // ------------------------------------------------------------------

    private const string KindEdit = "edit";
    private const string KindRetract = "retract";
    private const string KindFileRetract = "file_retract";

    private bool Exists(string conversation, string id) =>
        _db.Scalar("SELECT COUNT(*) FROM messages WHERE conv = $c AND id = $i", ("$c", conversation), ("$i", id)) > 0;

    private void Park(string conversation, string target, string kind, long ts, string? text) =>
        _db.Execute("INSERT INTO message_ops(conv, target, kind, ts, text) VALUES($c, $g, $k, $t, $x) ON CONFLICT(conv, target, kind) DO UPDATE SET ts = excluded.ts, text = excluded.text WHERE excluded.ts > message_ops.ts",
            ("$c", conversation), ("$g", target), ("$k", kind), ("$t", ts), ("$x", _db.SealText(text, $"op|{target}|{kind}")));

    private void ApplyParkedOps(string conversation, string id)
    {
        var ops = _db.Query("SELECT kind, ts, text FROM message_ops WHERE conv = $c AND target = $g", r => (Kind: r.GetString(0), Ts: r.GetInt64(1), Text: _db.OpenText(r, 2, $"op|{id}|{r.GetString(0)}")),
            ("$c", conversation), ("$g", id));
        if (ops.Count == 0) return;

        foreach (var op in ops.Where(o => o.Kind == KindEdit)) Edit(conversation, id, op.Text ?? "", op.Ts);
        foreach (var op in ops.Where(o => o.Kind == KindFileRetract)) FileRetract(conversation, id);
        foreach (var op in ops.Where(o => o.Kind == KindRetract)) Retract(conversation, id);   // last: a deletion beats any edit
        _db.Execute("DELETE FROM message_ops WHERE conv = $c AND target = $g", ("$c", conversation), ("$g", id));
    }

    /// <summary>
    /// The author replaced the text. Applied only to a normal message and only if newer than the last edit; if the
    /// message is not here yet the edit is parked. Returns whether a visible change was made.
    /// </summary>
    public bool Edit(string conversation, string id, string text, long editTs)
    {
        if (!Exists(conversation, id)) { Park(conversation, id, KindEdit, editTs, text); return false; }
        return _db.Execute("UPDATE messages SET text = $x, edited_ts = $e WHERE conv = $c AND id = $i AND state = 0 AND (edited_ts IS NULL OR edited_ts < $e)",
            ("$x", _db.SealText(text, $"m.text|{id}")), ("$e", editTs), ("$c", conversation), ("$i", id)) > 0;
    }

    /// <summary>The author deleted the message for everyone: the content is erased, the line stays ("message deleted"). Beats any edit.</summary>
    public bool Retract(string conversation, string id)
    {
        if (!Exists(conversation, id)) { Park(conversation, id, KindRetract, 0, null); return false; }
        return _db.Execute("UPDATE messages SET text = '', state = $s, edited_ts = NULL WHERE conv = $c AND id = $i AND state IN (0, 3, 4)",
            ("$s", (int)MessageState.Retracted), ("$c", conversation), ("$i", id)) > 0;
    }

    /// <summary>I deleted the message on this device only: content erased, the row stays hidden so it is not stored again.</summary>
    public bool DeleteForMe(string conversation, string id) =>
        _db.Execute("UPDATE messages SET text = '', state = $s, edited_ts = NULL WHERE conv = $c AND id = $i AND state <> 1",
            ("$s", (int)MessageState.DeletedForMe), ("$c", conversation), ("$i", id)) > 0;

    /// <summary>I deleted the attached file from this device: the line and its description stay.</summary>
    public bool FileDelete(string conversation, string id) =>
        _db.Execute("UPDATE messages SET state = $s WHERE conv = $c AND id = $i AND state = 0",
            ("$s", (int)MessageState.FileDeleted), ("$c", conversation), ("$i", id)) > 0;

    /// <summary>The author removed the attached file for everyone: the line stays, its description is erased.</summary>
    public bool FileRetract(string conversation, string id)
    {
        if (!Exists(conversation, id)) { Park(conversation, id, KindFileRetract, 0, null); return false; }
        return _db.Execute("UPDATE messages SET text = '', state = $s WHERE conv = $c AND id = $i AND state IN (0, 3)",
            ("$s", (int)MessageState.FileRetracted), ("$c", conversation), ("$i", id)) > 0;
    }

    // ------------------------------------------------------------------
    // Reactions (one emoji per person per message, the latest wins)
    // ------------------------------------------------------------------

    /// <summary>Records <paramref name="who"/>'s reaction to a message; an empty emoji takes it back. Applied only if newer than what is stored (messages and reactions can arrive in any order). Returns whether it changed.</summary>
    public bool SetReaction(string conversation, string target, string who, string emoji, long ts) =>
        _db.Execute("INSERT INTO reactions(conv, target, who, emoji, ts) VALUES($c, $g, $w, $e, $t) ON CONFLICT(conv, target, who) DO UPDATE SET emoji = excluded.emoji, ts = excluded.ts WHERE excluded.ts > reactions.ts",
            ("$c", conversation), ("$g", target), ("$w", who), ("$e", _db.SealText(emoji, $"r.emoji|{target}|{who}")), ("$t", ts)) > 0;

    /// <summary>Every reaction of a conversation: message id → (who, emoji), oldest first.</summary>
    public Dictionary<string, List<(string Who, string Emoji)>> Reactions(string conversation)
    {
        var map = new Dictionary<string, List<(string, string)>>();
        foreach (var (target, who, emoji) in _db.Query("SELECT target, who, emoji FROM reactions WHERE conv = $c AND emoji <> '' ORDER BY ts", r => (r.GetString(0), r.GetString(1), _db.OpenText(r, 2, $"r.emoji|{r.GetString(0)}|{r.GetString(1)}") ?? ""), ("$c", conversation)))
        {
            if (!map.TryGetValue(target, out var list)) map[target] = list = [];
            list.Add((who, emoji));
        }
        return map;
    }

    /// <summary>The reactions of some messages only (the lines a screen shows): message id → (who, emoji), oldest first. Read in blocks, so the size of the conversation does not matter.</summary>
    public Dictionary<string, List<(string Who, string Emoji)>> Reactions(string conversation, IReadOnlyCollection<string> messageIds)
    {
        var map = new Dictionary<string, List<(string, string)>>();
        const int Block = 200;
        var ids = messageIds.ToList();
        for (var from = 0; from < ids.Count; from += Block)
        {
            var block = ids.Skip(from).Take(Block).ToList();
            var names = string.Join(",", block.Select((_, i) => $"$g{i}"));
            var args = new List<(string, object?)> { ("$c", conversation) };
            for (var i = 0; i < block.Count; i++) args.Add(($"$g{i}", block[i]));
            foreach (var (target, who, emoji) in _db.Query($"SELECT target, who, emoji FROM reactions WHERE conv = $c AND emoji <> '' AND target IN ({names}) ORDER BY ts",
                         r => (r.GetString(0), r.GetString(1), _db.OpenText(r, 2, $"r.emoji|{r.GetString(0)}|{r.GetString(1)}") ?? ""), args.ToArray()))
            {
                if (!map.TryGetValue(target, out var list)) map[target] = list = [];
                list.Add((who, emoji));
            }
        }
        return map;
    }

    /// <summary>Erases a whole conversation: its messages (hidden ones included), the changes still parked for them and the read marks. Not undoable.</summary>
    public void DeleteConversation(string conversation)
    {
        _db.Execute("DELETE FROM messages WHERE conv = $c", ("$c", conversation));
        _db.Execute("DELETE FROM message_ops WHERE conv = $c", ("$c", conversation));
        _db.Execute("DELETE FROM read_marks WHERE conv = $c", ("$c", conversation));
        _db.Execute("DELETE FROM reactions WHERE conv = $c", ("$c", conversation));
    }

    // ------------------------------------------------------------------
    // Read marks
    // ------------------------------------------------------------------

    /// <summary>Whose reading a mark records: I read what they sent, or they read what I sent.</summary>
    public const int ReadByMe = 0, ReadByPeer = 1;

    /// <summary>Moves a read mark forward (never back). Returns whether it moved.</summary>
    public bool AdvanceRead(string conversation, int side, HistoryCursor cursor) =>
        _db.Execute("INSERT INTO read_marks(conv, side, ts, id) VALUES($c, $s, $t, $i) ON CONFLICT(conv, side) DO UPDATE SET ts = excluded.ts, id = excluded.id " +
                    "WHERE excluded.ts > read_marks.ts OR (excluded.ts = read_marks.ts AND excluded.id > read_marks.id)",
            ("$c", conversation), ("$s", side), ("$t", cursor.Ts), ("$i", cursor.Id)) > 0;

    /// <summary>Everything up to this message (included) counts as read, or null when nothing was read yet.</summary>
    public HistoryCursor? ReadCursor(string conversation, int side)
    {
        var rows = _db.Query("SELECT ts, id FROM read_marks WHERE conv = $c AND side = $s", r => new HistoryCursor(r.GetInt64(0), r.GetString(1)), ("$c", conversation), ("$s", side));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The newest message I received (the one a read mark points at), or null.</summary>
    public HistoryEntry? LastReceived(string conversation) =>
        _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND direction = {(int)MessageDirection.Received} AND {Visible} ORDER BY ts DESC, id DESC LIMIT 1", r => Map(r), ("$c", conversation)).FirstOrDefault();

    /// <summary>How many messages I received after my read mark (all of them when I never read the conversation).</summary>
    public int UnreadCount(string conversation)
    {
        var mark = ReadCursor(conversation, ReadByMe);
        var where = $"conv = $c AND direction = {(int)MessageDirection.Received} AND state = 0";
        return mark is { } m
            ? (int)_db.Scalar($"SELECT COUNT(*) FROM messages WHERE {where} AND (ts > $t OR (ts = $t AND id > $i))", ("$c", conversation), ("$t", m.Ts), ("$i", m.Id))
            : (int)_db.Scalar($"SELECT COUNT(*) FROM messages WHERE {where}", ("$c", conversation));
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>The whole conversation, oldest first. Fine for backup and small tools; front-ends page instead.</summary>
    public IReadOnlyList<HistoryEntry> Get(string conversation) =>
        _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND {Visible} ORDER BY ts, id", r => Map(r), ("$c", conversation));

    public int Count(string conversation) => (int)_db.Scalar($"SELECT COUNT(*) FROM messages WHERE conv = $c AND {Visible}", ("$c", conversation));

    /// <summary>Every conversation — used to build a backup (protocol §2). Messages deleted for me stay out.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HistoryEntry>> AllEntries
    {
        get
        {
            var rows = _db.Query($"SELECT conv, {Columns} FROM messages WHERE {Visible} ORDER BY conv, ts, id",
                r => (Conv: r.GetString(0), Entry: Map(r, 1)));
            return rows.GroupBy(x => x.Conv).ToDictionary(g => g.Key, g => (IReadOnlyList<HistoryEntry>)g.Select(x => x.Entry).ToList());
        }
    }

    /// <summary>
    /// For every conversation holding a message, a short digest of its content (ids, states, edit times, in order). Two devices of one identity hold the same
    /// conversation exactly when the digests are equal: what a restored device sends to be mirrored on another (<see cref="Conversations"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Digests()
    {
        var result = new Dictionary<string, string>();
        foreach (var conv in Conversations())
        {
            // One conversation at a time, in blocks: a long history is never held in memory whole.
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var e in Stream(conv))
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{e.Id}|{(int)e.State}|{e.EditedTs ?? 0};"));
            result[conv] = Convert.ToHexString(hash.GetCurrentHash())[..16];
        }
        return result;
    }

    /// <summary>
    /// The whole conversation, oldest first, read in blocks of <paramref name="block"/> lines as the caller goes: a backup or a mirror walks a
    /// long history without ever holding it whole.
    /// </summary>
    public IEnumerable<HistoryEntry> Stream(string conversation, int block = 500)
    {
        var cursor = new HistoryCursor(long.MinValue, "");
        while (true)
        {
            var page = After(conversation, cursor, block);
            foreach (var e in page.Entries) yield return e;
            if (!page.HasMoreAfter || page.Newest is not { } newest) yield break;
            cursor = newest;
        }
    }

    /// <summary>Every conversation that holds a message, deleted-for-me ones left out.</summary>
    public IReadOnlyList<string> Conversations() => _db.Query($"SELECT DISTINCT conv FROM messages WHERE {Visible}", r => r.GetString(0));
    /// <summary>One message by id, or null (also null for one deleted on this device).</summary>
    public HistoryEntry? Find(string conversation, string messageId) =>
        _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND id = $i AND {Visible}", r => Map(r), ("$c", conversation), ("$i", messageId)).FirstOrDefault();

    /// <summary>1-based position of a message in its conversation (what front-ends show as "#N"), or 0 when it is not there.</summary>
    public int IndexOf(string conversation, string messageId)
    {
        var e = Find(conversation, messageId);
        if (e == null) return 0;
        return (int)_db.Scalar($"SELECT COUNT(*) FROM messages WHERE conv = $c AND {Visible} AND (ts < $t OR (ts = $t AND id <= $i))",
            ("$c", conversation), ("$t", e.Ts), ("$i", e.Id));
    }

    /// <summary>The message at 1-based position <paramref name="index"/>, or null.</summary>
    public HistoryEntry? At(string conversation, int index) =>
        index < 1 ? null : _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND {Visible} ORDER BY ts, id LIMIT 1 OFFSET $o", r => Map(r), ("$c", conversation), ("$o", index - 1)).FirstOrDefault();

    // ------------------------------------------------------------------
    // Paging
    // ------------------------------------------------------------------

    /// <summary>The newest <paramref name="limit"/> messages — what a conversation screen opens on.</summary>
    public HistoryPage Latest(string conversation, int limit)
    {
        var rows = _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND {Visible} ORDER BY ts DESC, id DESC LIMIT $n", r => Map(r), ("$c", conversation), ("$n", limit + 1));
        return Descending(rows, limit, hasMoreAfter: false);
    }

    /// <summary>Up to <paramref name="limit"/> messages older than <paramref name="cursor"/> (scrolling up).</summary>
    public HistoryPage Before(string conversation, HistoryCursor cursor, int limit)
    {
        var rows = _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND {Visible} AND (ts < $t OR (ts = $t AND id < $i)) ORDER BY ts DESC, id DESC LIMIT $n",
            r => Map(r), ("$c", conversation), ("$t", cursor.Ts), ("$i", cursor.Id), ("$n", limit + 1));
        return Descending(rows, limit, hasMoreAfter: true);
    }

    /// <summary>Up to <paramref name="limit"/> messages newer than <paramref name="cursor"/> (scrolling back down).</summary>
    public HistoryPage After(string conversation, HistoryCursor cursor, int limit)
    {
        var rows = _db.Query($"SELECT {Columns} FROM messages WHERE conv = $c AND {Visible} AND (ts > $t OR (ts = $t AND id > $i)) ORDER BY ts, id LIMIT $n",
            r => Map(r), ("$c", conversation), ("$t", cursor.Ts), ("$i", cursor.Id), ("$n", limit + 1));
        var more = rows.Count > limit;
        return new HistoryPage(rows.Take(limit).ToList(), HasMoreBefore: true, HasMoreAfter: more);
    }

    /// <summary>
    /// Messages whose text contains <paramref name="query"/> — ignoring case and accents ("ete" finds "Été") — newest first, at most <paramref name="limit"/>, in every
    /// conversation or only in <paramref name="conversation"/>. Messages deleted for me or for everyone have no text left and never match. Read in blocks, so a long
    /// history is never held in memory whole.
    /// </summary>
    public IReadOnlyList<(string Conversation, HistoryEntry Entry)> Search(string query, int limit, string? conversation = null)
    {
        var hits = new List<(string, HistoryEntry)>();
        query = query.Trim();
        if (query.Length == 0 || limit <= 0) return hits;

        const int Block = 1000;
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions Loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var where = conversation == null ? "" : "AND conv = $c";
        HistoryCursor? cursor = null;
        while (hits.Count < limit)
        {
            var paging = cursor == null ? "" : "AND (ts < $t OR (ts = $t AND id < $i))";
            var args = new List<(string, object?)> { ("$n", Block) };
            if (conversation != null) args.Add(("$c", conversation));
            if (cursor != null) { args.Add(("$t", cursor.Value.Ts)); args.Add(("$i", cursor.Value.Id)); }
            var rows = _db.Query($"SELECT conv, {Columns} FROM messages WHERE {Visible} AND text <> '' {where} {paging} ORDER BY ts DESC, id DESC LIMIT $n",
                r => (Conv: r.GetString(0), Entry: Map(r, 1)), args.ToArray());
            foreach (var (conv, entry) in rows)
            {
                if (compare.IndexOf(entry.Text, query, Loose) >= 0) { hits.Add((conv, entry)); if (hits.Count >= limit) break; }
            }
            if (rows.Count < Block) break;
            cursor = new HistoryCursor(rows[^1].Entry.Ts, rows[^1].Entry.Id);
        }
        return hits;
    }

    /// <summary>A window centred on one message (jump to a quoted message or a search hit); empty when it is unknown.</summary>
    public HistoryPage Around(string conversation, string messageId, int limit)
    {
        var e = Find(conversation, messageId);
        if (e == null) return new HistoryPage([], false, false);

        var cursor = new HistoryCursor(e.Ts, e.Id);
        var older = Before(conversation, cursor, limit / 2);
        var newer = After(conversation, cursor, Math.Max(1, limit - limit / 2 - 1));
        return new HistoryPage(older.Entries.Append(e).Concat(newer.Entries).ToList(), older.HasMoreBefore, newer.HasMoreAfter);
    }

    private static HistoryPage Descending(List<HistoryEntry> rowsNewestFirst, int limit, bool hasMoreAfter)
    {
        var more = rowsNewestFirst.Count > limit;
        var page = rowsNewestFirst.Take(limit).Reverse().ToList();
        return new HistoryPage(page, HasMoreBefore: more, HasMoreAfter: hasMoreAfter);
    }
}
