/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Relay;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Tests;

/// <summary>
/// The protocol API reference (PROTOCOL_API.*.md) and the database diagram must not drift from the code. Every JSON example tagged
/// <c>&lt;!-- schema: Type --&gt;</c> is read with the real class: no unknown field, every required field present, identifiers that match
/// their keys. The two languages carry the same examples, and the diagram lists exactly the tables and columns the library creates.
/// </summary>
public class ApiDocTests
{
    private static readonly Regex ExampleRx = new(@"<!-- schema: (\w+) -->\s*```json\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string DocsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "freespeech.mobile"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("folder holding freespeech.mobile");
    }

    private static List<(string Schema, string Json)> Examples(string file) =>
        ExampleRx.Matches(File.ReadAllText(Path.Combine(DocsDir(), file))).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    private static Type Resolve(string name)
    {
        var found = typeof(FreeSpeechClient).Assembly.GetTypes().Where(t => t.Name == name).ToList();
        Assert.True(found.Count == 1, $"schema '{name}' must name exactly one class of the client library (found {found.Count})");
        return found[0];
    }

    private static string JsonNameOf(PropertyInfo p) => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

    private static bool IsHex64(string s) => Regex.IsMatch(s, "^[0-9a-f]{64}$");

    /// <summary>A discovery id is the SHA-256 of the two public keys: an example whose id does not match its keys would mislead an implementer.</summary>
    private static void CheckIdentity(string id, string encKey, string signKey, string where)
    {
        var enc = Convert.FromBase64String(encKey);
        var sign = Convert.FromBase64String(signKey);
        Assert.True(enc.Length == 32 && sign.Length == 32, $"{where}: keys must be 32 bytes");
        var expected = Convert.ToHexString(SHA256.HashData([.. enc, .. sign])).ToLowerInvariant();
        Assert.True(expected == id, $"{where}: discovery_id {id} is not the hash of its keys ({expected})");
    }

    private static void CheckCard(ContactCard card, string where) =>
        CheckIdentity(card.DiscoveryId, card.EncryptionPublicKey, card.SigningPublicKey, where);

    /// <summary>The key of the example group: the documentation gives its key_check and its tag, which are recomputed below.</summary>
    private static readonly byte[] DocGroupKey = SHA256.HashData("freespeech-doc-group-key"u8);

    private static void CheckGroupState(GroupStateData state, string where)
    {
        Assert.True(Regex.IsMatch(state.GroupId, "^[0-9a-f]{32}$"), $"{where}: group_id is 32 lowercase hex characters");
        foreach (var m in state.Members) Assert.True(GroupCrypto.MemberIdMatchesKeys(m), $"{where}: member {m.Nickname} does not match its keys");
        Assert.Contains(state.CreatedBy, state.Members.Select(m => m.DiscoveryId));
        foreach (var moderator in state.Moderators) Assert.Contains(moderator, state.Members.Select(m => m.DiscoveryId));
        Assert.True(GroupCrypto.KeyMatches(DocGroupKey, state.KeyCheck), $"{where}: key_check is not the one of the example group key");
    }

    /// <summary>How many sibling hashes the proof of a chunk holds: one per level where its node has a sibling.</summary>
    private static int ProofLength(int leaves, int index)
    {
        var length = 0;
        for (var count = leaves; count > 1; count = (count + 1) / 2, index /= 2)
            if ((index ^ 1) < count) length++;
        return length;
    }

    private static void CheckExample(string file, int index, string schema, string json)
    {
        var where = $"{file} example #{index + 1} ({schema})";
        var type = Resolve(schema);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, $"{where}: an object is expected");

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null).ToList();
        var known = props.Select(JsonNameOf).ToHashSet();
        foreach (var field in doc.RootElement.EnumerateObject())
            Assert.True(known.Contains(field.Name), $"{where}: '{field.Name}' is not a field of {schema} (known: {string.Join(", ", known.Order())})");
        foreach (var p in props.Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() != null))
            Assert.True(doc.RootElement.TryGetProperty(JsonNameOf(p), out _), $"{where}: required field '{JsonNameOf(p)}' is missing");

        var value = JsonSerializer.Deserialize(json, type);
        Assert.NotNull(value);

        switch (value)
        {
            case ContactCard card:
                CheckCard(card, where);
                break;
            case Envelope env:
                Assert.True(IsHex64(env.From) && IsHex64(env.To), $"{where}: from and to are 64 lowercase hex characters");
                break;
            case WsMessage ws:
                if (ws.DiscoveryId != null) Assert.True(IsHex64(ws.DiscoveryId), $"{where}: discovery_id is 64 lowercase hex characters");
                if (ws.Type == "register" && ws.EncKey != null && ws.SignKey != null) CheckIdentity(ws.DiscoveryId!, ws.EncKey, ws.SignKey, where);
                if (ws.Envelope != null) Assert.True(IsHex64(ws.Envelope.From) && IsHex64(ws.Envelope.To), $"{where}: envelope from and to are 64 lowercase hex characters");
                break;
            case GroupStateData state:
                CheckGroupState(state, where);
                break;
            case GroupStateLetter letter:
                var embedded = JsonSerializer.Deserialize<GroupStateData>(letter.State)!;
                CheckGroupState(embedded, where + " state");
                Assert.Contains(letter.Signer, embedded.Moderators);
                break;
            case GroupSuggestLetter suggest:
                Assert.True(GroupCrypto.MemberIdMatchesKeys(suggest.Candidate), $"{where}: candidate does not match its keys");
                Assert.Matches("^[0-9a-f]{32}$", suggest.GroupId);
                break;
            case GroupMessageLetter gm:
                Assert.Matches("^[0-9a-f]{32}$", gm.GroupId);
                break;
            case GroupSyncLetter or GroupRelayLetter or GroupLeaveLetter or BanUpdateLetter or PinUpdateLetter or GroupProfileOp or MemberProfile:
                Assert.Matches("^[0-9a-f]{32}$", doc.RootElement.GetProperty("group_id").GetString()!);
                break;
            case AttachmentOffer offer:
                Assert.Matches("^[0-9a-f]{64}$", offer.Root);
                break;
            case AttachmentChunk chunk:
                Assert.Equal(ProofLength(chunk.TotalChunks, chunk.ChunkId), chunk.Proof.Count);
                break;
            case AttachmentHave have:
                Assert.NotNull(Convert.FromBase64String(have.Have));
                break;
            case OtcEnvelope otc when otc.Type is "contact_request" or "contact_confirm":
                Assert.NotNull(otc.Payload);
                CheckCard(JsonSerializer.Deserialize<ContactCard>(otc.Payload)!, where + " payload");
                break;
        }

        // A Letter of type identity_update carries a card, as a string.
        if (schema == "LetterPayload")
        {
            var letterType = doc.RootElement.GetProperty("type").GetString();
            if (letterType == "identity_update")
                CheckCard(JsonSerializer.Deserialize<ContactCard>(doc.RootElement.GetProperty("payload").GetString()!)!, where + " payload");
        }
        if (schema == "MovedOp") CheckCard(JsonSerializer.Deserialize<ContactCard>(doc.RootElement.GetProperty("new_card").GetRawText())!, where + " new_card");
    }

    [Theory]
    [InlineData("PROTOCOL_API.en.md")]
    [InlineData("PROTOCOL_API.fr.md")]
    public void Every_json_example_is_read_by_the_real_class(string file)
    {
        var examples = Examples(file);
        Assert.True(examples.Count >= 25, $"{file}: expected at least 25 tagged examples, found {examples.Count}");
        for (var i = 0; i < examples.Count; i++) CheckExample(file, i, examples[i].Schema, examples[i].Json);
    }

    [Fact]
    public void The_two_languages_carry_the_same_examples_and_diagrams()
    {
        static string Normal(string json) { using var d = JsonDocument.Parse(json); return JsonSerializer.Serialize(d.RootElement); }
        var en = Examples("PROTOCOL_API.en.md").Select(e => (e.Schema, Json: Normal(e.Json))).ToList();
        var fr = Examples("PROTOCOL_API.fr.md").Select(e => (e.Schema, Json: Normal(e.Json))).ToList();
        Assert.Equal(en, fr);

        static int Diagrams(string text) => Regex.Matches(text, "```mermaid").Count;
        var textEn = File.ReadAllText(Path.Combine(DocsDir(), "PROTOCOL_API.en.md"));
        var textFr = File.ReadAllText(Path.Combine(DocsDir(), "PROTOCOL_API.fr.md"));
        Assert.Equal(Diagrams(textEn), Diagrams(textFr));
    }

    [Fact]
    public void Database_diagram_lists_exactly_the_tables_and_columns_the_library_creates()
    {
        var diagram = File.ReadAllText(Path.Combine(DocsDir(), "diagrams", "db", "client-database.md"));
        var block = Regex.Match(diagram, @"```mermaid\s*erDiagram(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        var documented = new Dictionary<string, (HashSet<string> Columns, HashSet<string> Keys)>();
        foreach (Match entity in Regex.Matches(block, @"(\w+)\s*\{([^}]*)\}"))
        {
            var columns = new HashSet<string>();
            var keys = new HashSet<string>();
            foreach (var line in entity.Groups[2].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(line.Trim(), @"^(\w+)\s+(\w+)(\s+PK)?");
                if (!m.Success) continue;
                columns.Add(m.Groups[2].Value);
                if (m.Groups[3].Success) keys.Add(m.Groups[2].Value);
            }
            documented[entity.Groups[1].Value] = (columns, keys);
        }

        var dir = Path.Combine(Path.GetTempPath(), "fs-apidoc-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = ClientDatabase.Open(dir, null);
            var tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", r => r.GetString(0));
            Assert.Equal(tables.Order().ToList(), documented.Keys.Order().ToList());
            foreach (var table in tables)
            {
                var info = db.Query($"PRAGMA table_info({table})", r => (Name: r.GetString(1), Pk: r.GetInt32(5) > 0));
                Assert.Equal(info.Select(c => c.Name).Order().ToList(), documented[table].Columns.Order().ToList());
                Assert.Equal(info.Where(c => c.Pk).Select(c => c.Name).Order().ToList(), documented[table].Keys.Order().ToList());
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_example_group_key_gives_the_documented_check_and_tag()
    {
        Assert.Equal("bksratIeJiWrP4qaRWqckLYRbRofygvzjnNMU25UpbA=", GroupCrypto.KeyCheck(DocGroupKey));
        Assert.Equal("40127c9c5755b5d6", GroupCrypto.TagHex(DocGroupKey));
        foreach (var file in new[] { "PROTOCOL_API.en.md", "PROTOCOL_API.fr.md" })
        {
            var text = File.ReadAllText(Path.Combine(DocsDir(), file));
            Assert.Contains("bksratIeJiWrP4qaRWqckLYRbRofygvzjnNMU25UpbA=", text);
            Assert.Contains("40127c9c5755b5d6", text);
        }
    }
}