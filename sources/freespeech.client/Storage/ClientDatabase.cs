/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using Microsoft.Data.Sqlite;

namespace FreeSpeech.Client.Storage;

/// <summary>
/// The profile's single local database (<c>freespeech.db</c>): SQLite (a plain file), whose content is encrypted field by
/// field by a <see cref="Vault"/> when a key is given. Holds everything except attachment file contents: identity and small collections
/// as JSON documents (<see cref="DocCollection{T}"/>), and the message history as real rows so it can be
/// paged (<see cref="History.HistoryStore"/>). WAL mode + transactions make every write atomic: a process
/// killed mid-write (routine on mobile) loses nothing.
///
/// One connection, serialized by a lock — the workload is small and the stores are called from several threads.
/// </summary>
public sealed class ClientDatabase : IDisposable
{
    private const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private Vault? _vault;

    static ClientDatabase() => SQLitePCL.Batteries_V2.Init();

    private ClientDatabase(SqliteConnection conn, Vault? vault) { _conn = conn; _vault = vault; }

    public static string PathFor(string profileDir) => Path.Combine(profileDir, "freespeech.db");

    public static bool Exists(string profileDir) => File.Exists(PathFor(profileDir));

    /// <summary>
    /// Opens (creating if needed) the profile database. <paramref name="key"/> is the 32-byte key of the <see cref="Vault"/>
    /// (null = nothing is encrypted, tests and tools only). A wrong key is reported as <see cref="ClientException"/>.
    /// A file that is not a SQLite database (one written by an earlier encrypted format) is set aside as <c>freespeech.db.legacy</c>
    /// and a new database is started.
    /// </summary>
    public static ClientDatabase Open(string profileDir, byte[]? key)
    {
        Directory.CreateDirectory(profileDir);
        SetAsideForeignFile(PathFor(profileDir));
        var conn = new SqliteConnection($"Data Source={PathFor(profileDir)};Pooling=False");
        conn.Open();
        try
        {
            Exec(conn, "PRAGMA busy_timeout = 5000;");
            Exec(conn, "PRAGMA journal_mode = WAL;");   // the first real read: fails here if the file is not a database
            Exec(conn, "PRAGMA synchronous = NORMAL;");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS meta(k TEXT NOT NULL PRIMARY KEY, v BLOB NOT NULL) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS docs(
                    collection TEXT NOT NULL, id TEXT NOT NULL, pos INTEGER NOT NULL, json NOT NULL,
                    PRIMARY KEY(collection, id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS messages(
                    conv TEXT NOT NULL, id TEXT NOT NULL, ts INTEGER NOT NULL, direction INTEGER NOT NULL,
                    text NOT NULL, reply_to, from_id,
                    state INTEGER NOT NULL DEFAULT 0, edited_ts INTEGER,
                    PRIMARY KEY(conv, id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_messages_conv_ts ON messages(conv, ts, id);
                CREATE TABLE IF NOT EXISTS message_ops(
                    conv TEXT NOT NULL, target TEXT NOT NULL, kind TEXT NOT NULL, ts INTEGER NOT NULL, text,
                    PRIMARY KEY(conv, target, kind)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS reactions(
                    conv TEXT NOT NULL, target TEXT NOT NULL, who TEXT NOT NULL, emoji NOT NULL, ts INTEGER NOT NULL,
                    PRIMARY KEY(conv, target, who)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS read_marks(
                    conv TEXT NOT NULL, side INTEGER NOT NULL, ts INTEGER NOT NULL, id TEXT NOT NULL,
                    PRIMARY KEY(conv, side)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS thumbnails(
                    root TEXT NOT NULL PRIMARY KEY, width INTEGER NOT NULL, height INTEGER NOT NULL, data BLOB NOT NULL) WITHOUT ROWID;
                """);
            var vault = key == null ? null : new Vault(key);
            CheckKey(conn, vault);
            Migrate(conn);
            return new ClientDatabase(conn, vault);
        }
        catch (SqliteException)
        {
            conn.Dispose();
            throw new ClientException("Cannot open the local database — wrong key, or the file is corrupted.", "db_open");
        }
        catch (ClientException)
        {
            conn.Dispose();
            throw;
        }
    }

    private const string ModeSealed = "sealed", ModePlain = "plain", CheckContext = "meta|check", CheckText = "freespeech-database";

    /// <summary>
    /// A database is either sealed (every content field is encrypted under the key) or plain, from its first open. The key is checked
    /// against a sealed marker, so a wrong key fails at once instead of on the first row read.
    /// </summary>
    private static void CheckKey(SqliteConnection conn, Vault? vault)
    {
        var mode = Meta(conn, "mode");
        if (mode == null)
        {
            Exec(conn, $"INSERT INTO meta(k, v) VALUES('mode', x'{Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(vault == null ? ModePlain : ModeSealed))}');");
            if (vault != null) SetMeta(conn, "check", vault.SealText(CheckText, CheckContext));
            return;
        }
        var sealedMode = System.Text.Encoding.ASCII.GetString(mode) == ModeSealed;
        if (sealedMode != (vault != null)) throw new ClientException("The local database was opened with the other mode (key / no key).", "db_open");
        if (vault != null && (Meta(conn, "check") is not { } marker || vault.OpenText(marker, CheckContext) != CheckText))
            throw new ClientException("Cannot open the local database — wrong key.", "db_open");
    }

    private static byte[]? Meta(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = $k";
        cmd.Parameters.AddWithValue("$k", name);
        return cmd.ExecuteScalar() as byte[];
    }

    private static void SetMeta(SqliteConnection conn, string name, byte[] value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(k, v) VALUES($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v";
        cmd.Parameters.AddWithValue("$k", name);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A file that does not start with the SQLite header was written by an encrypted format this build cannot read: it is kept aside, not deleted.</summary>
    private static void SetAsideForeignFile(string path)
    {
        if (!File.Exists(path)) return;
        var header = new byte[16];
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (f.Length == 0) return;
            if (f.Read(header, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(header) == "SQLite format 3\0") return;
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, path + suffix + ".legacy", overwrite: true);
        ClientLog.Warn("storage", "The local database was written by an earlier encrypted format and cannot be read: it is kept as freespeech.db.legacy and a new one is started.");
    }
    /// <summary>Brings a database made by an older build up to <see cref="SchemaVersion"/> (message state and edit time).</summary>
    private static void Migrate(SqliteConnection conn)
    {
        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar());
        }

        if (version is > 0 and < 2)
        {
            Exec(conn, "ALTER TABLE messages ADD COLUMN state INTEGER NOT NULL DEFAULT 0;");
            Exec(conn, "ALTER TABLE messages ADD COLUMN edited_ts INTEGER;");
        }
        Exec(conn, $"PRAGMA user_version = {SchemaVersion};");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, (string Name, object? Value)[] ps)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public int Execute(string sql, params (string Name, object? Value)[] ps)
    {
        lock (_lock)
        {
            using var cmd = Command(_conn, null, sql, ps);
            return cmd.ExecuteNonQuery();
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] ps)
    {
        lock (_lock)
        {
            using var cmd = Command(_conn, null, sql, ps);
            using var reader = cmd.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read()) rows.Add(map(reader));
            return rows;
        }
    }

    public long Scalar(string sql, params (string Name, object? Value)[] ps)
    {
        lock (_lock)
        {
            using var cmd = Command(_conn, null, sql, ps);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Changes the key of the <see cref="Vault"/>: every sealed field is opened with the old key and sealed again under the new one, in one
    /// transaction. Used when the identity changes (§12, migration): the key derives from the phrases, so a new identity needs a new key.
    /// </summary>
    public void Rekey(byte[] newKey)
    {
        lock (_lock)
        {
            var old = _vault ?? throw new InvalidOperationException("The database is not encrypted.");
            var next = new Vault(newKey);
            using var tx = _conn.BeginTransaction();
            Reseal("docs", ["collection", "id"], "json", row => $"doc|{row[0]}|{row[1]}", old, next, tx);
            Reseal("messages", ["id"], "text", row => $"m.text|{row[0]}", old, next, tx, rowKeyColumns: ["conv", "id"]);
            Reseal("messages", ["id"], "reply_to", row => $"m.reply|{row[0]}", old, next, tx, rowKeyColumns: ["conv", "id"]);
            Reseal("messages", ["id"], "from_id", row => $"m.from|{row[0]}", old, next, tx, rowKeyColumns: ["conv", "id"]);
            Reseal("message_ops", ["target", "kind"], "text", row => $"op|{row[0]}|{row[1]}", old, next, tx, rowKeyColumns: ["conv", "target", "kind"]);
            Reseal("reactions", ["target", "who"], "emoji", row => $"r.emoji|{row[0]}|{row[1]}", old, next, tx, rowKeyColumns: ["conv", "target", "who"]);
            Reseal("thumbnails", ["root"], "data", row => $"thumb|{row[0]}", old, next, tx);
            SetMeta(_conn, "check", next.SealText(CheckText, CheckContext));
            tx.Commit();
            _vault = next;
        }
    }

    /// <summary>Opens every sealed value of one column under <paramref name="old"/> and seals it again under <paramref name="next"/>.</summary>
    private void Reseal(string table, string[] contextColumns, string column, Func<object?[], string> context, Vault old, Vault next, SqliteTransaction tx, string[]? rowKeyColumns = null)
    {
        var keyColumns = rowKeyColumns ?? contextColumns;
        var rows = new List<(object?[] Key, object? Value)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {string.Join(", ", keyColumns)}, {column} FROM {table}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = new object?[keyColumns.Length];
                for (var i = 0; i < key.Length; i++) key[i] = r.GetValue(i);
                rows.Add((key, r.IsDBNull(key.Length) ? null : r.GetValue(key.Length)));
            }
        }

        foreach (var (key, value) in rows)
        {
            if (value is not byte[] sealedValue) continue;   // NULL, or an empty value kept as it is
            var ctxValues = contextColumns.Select(c => key[Array.IndexOf(keyColumns, c)]).ToArray();
            var ctx = context(ctxValues);
            var plain = old.Open(sealedValue, ctx) ?? throw new ClientException("Cannot read the local database while changing its key.", "db_open");
            using var upd = _conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE {table} SET {column} = $v WHERE " + string.Join(" AND ", keyColumns.Select((c, i) => $"{c} = $k{i}"));
            upd.Parameters.AddWithValue("$v", next.Seal(plain, ctx));
            for (var i = 0; i < keyColumns.Length; i++) upd.Parameters.AddWithValue($"$k{i}", key[i] ?? DBNull.Value);
            upd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Sealed fields. Without a key (tests and tools) values stay as they are.
    // ------------------------------------------------------------------

    /// <summary>The value to store for a text field: sealed bytes, or the text itself when there is no key. An empty or null text is stored as it is (so "is empty" stays a plain test).</summary>
    public object? SealText(string? text, string context) =>
        text == null ? DBNull.Value : _vault == null || text.Length == 0 ? text : _vault.SealText(text, context);

    /// <summary>The text of a stored field (see <see cref="SealText"/>), or null for NULL.</summary>
    public string? OpenText(SqliteDataReader r, int ordinal, string context)
    {
        if (r.IsDBNull(ordinal)) return null;
        var value = r.GetValue(ordinal);
        if (value is string s) return s;
        var bytes = (byte[])value;
        return _vault?.OpenText(bytes, context) ?? throw new ClientException("Cannot read the local database.", "db_open");
    }

    /// <summary>The value to store for a binary field (a picture, a JSON document): sealed bytes, or the bytes themselves when there is no key.</summary>
    public byte[] SealBytes(byte[] data, string context) => _vault == null ? data : _vault.Seal(data, context);

    public byte[] OpenBytes(SqliteDataReader r, int ordinal, string context)
    {
        var bytes = (byte[])r.GetValue(ordinal);
        if (_vault == null) return bytes;
        return _vault.Open(bytes, context) ?? throw new ClientException("Cannot read the local database.", "db_open");
    }
    /// <summary>Runs several statements atomically.</summary>
    public void Transaction(Action<Func<string, (string Name, object? Value)[], int>> work)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            work((sql, ps) =>
            {
                using var cmd = Command(_conn, tx, sql, ps);
                return cmd.ExecuteNonQuery();
            });
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
    }
}
