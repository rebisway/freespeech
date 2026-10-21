/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;

namespace FreeSpeech.Client.Storage;

/// <summary>
/// A small collection stored as one JSON document per item in the <c>docs</c> table. The owning store keeps
/// its objects in memory, mutates them and calls <see cref="Save"/>; only the rows whose JSON actually
/// changed are written, all in one transaction (so a crash never leaves a half-saved collection).
/// For collections that stay small (contacts, groups, outbox, attachment records) — the message history,
/// which grows, has its own table.
/// </summary>
public sealed class DocCollection<T>
{
    private readonly ClientDatabase _db;
    private readonly string _name;
    private readonly Func<T, string> _key;
    private Dictionary<string, (string Json, int Pos)> _written = [];

    public DocCollection(ClientDatabase db, string name, Func<T, string> key)
    {
        _db = db;
        _name = name;
        _key = key;
    }

    public List<T> Load()
    {
        var rows = _db.Query("SELECT id, pos, json FROM docs WHERE collection = $c ORDER BY pos", r => (Id: r.GetString(0), Pos: r.GetInt32(1), Json: _db.OpenText(r, 2, $"doc|{_name}|{r.GetString(0)}")!), ("$c", _name));
        _written = rows.ToDictionary(r => r.Id, r => (r.Json, r.Pos));
        return rows.Select(r => JsonSerializer.Deserialize<T>(r.Json)!).ToList();
    }

    /// <summary>Makes the stored collection equal to <paramref name="items"/>, in the same order.</summary>
    public void Save(IEnumerable<T> items)
    {
        var next = new Dictionary<string, (string Json, int Pos)>();
        var pos = 0;
        foreach (var item in items) next[_key(item)] = (JsonSerializer.Serialize(item), pos++);

        var upserts = next.Where(kv => !_written.TryGetValue(kv.Key, out var old) || old != kv.Value).ToList();
        var deletes = _written.Keys.Where(k => !next.ContainsKey(k)).ToList();
        if (upserts.Count == 0 && deletes.Count == 0) return;

        _db.Transaction(run =>
        {
            foreach (var id in deletes)
                run("DELETE FROM docs WHERE collection = $c AND id = $i", [("$c", _name), ("$i", id)]);
            foreach (var (id, (json, p)) in upserts)
                run("INSERT INTO docs(collection, id, pos, json) VALUES($c, $i, $p, $j) ON CONFLICT(collection, id) DO UPDATE SET pos = excluded.pos, json = excluded.json",
                    [("$c", _name), ("$i", id), ("$p", p), ("$j", _db.SealText(json, $"doc|{_name}|{id}"))]);
        });
        _written = next;
    }
}