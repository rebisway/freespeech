/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using FreeSpeech.Client.Archive;
using FreeSpeech.Client.History;

namespace FreeSpeech.Console;

/// <summary>
/// Console commands for the features the mobile app has beyond plain messaging: conversation list, search, unread/read receipts,
/// pinned messages, removed contacts and left groups, file archive, device sync, migration to a new identity, remote wipe.
/// Same rule as the rest of the console: no logic here, only calls to <see cref="FreeSpeechClient"/>.
/// </summary>
public partial class ConsoleApp
{
    private void HookExtraEvents()
    {
        _client.PinsChanged += c => PrintNotification($"[pins changed in {TitleOf(c)}]");
        _client.ReadReceived += c => PrintNotification($"[{TitleOf(c)} read your messages]");
        _client.WipeRequested += () => PrintNotification("[wipe order received from another device of this identity — the data of this profile must be erased]");
        _client.MigrationRequested += n => PrintNotification($"[migration] {n.OldName} [{n.OldId}] moved to a new identity — 'migrate accept {n.OldId}' or 'migrate decline {n.OldId}'");
        _client.DeviceSyncRequested += r => PrintNotification($"[device sync] another device of this identity asks for your data (request {r.Id}) — 'sync approve {r.Id} <code shown on that device>' or 'sync reject {r.Id}'");
        _client.DeviceSyncChanged += s => PrintNotification($"[device sync] {s}");
        _client.WireFrame += ShowFrame;
    }

    // ------------------------------------------------------------------
    // Raw protocol access (debugging a server or another implementation)
    // ------------------------------------------------------------------

    private bool _showFrames;
    private bool _framesFull;

    private void ShowFrame(string url, string dir, string text)
    {
        if (!_showFrames) return;
        if (!_framesFull && text.Length > 500) text = text[..500] + $"… (+{text.Length - 500} chars; 'raw on full' shows everything)";
        PrintNotification($"[wire {new Uri(url).Authority} {dir}] {text}");
    }

    /// <summary>The text after the first <paramref name="skip"/> words of the line, untouched (JSON keeps its spaces).</summary>
    private static string Rest(string line, int skip)
    {
        var rest = line.TrimStart();
        for (var i = 0; i < skip; i++)
        {
            var sp = rest.IndexOf(' ');
            if (sp < 0) return "";
            rest = rest[(sp + 1)..].TrimStart();
        }
        return rest;
    }

    /// <summary>
    /// <c>raw on|off|send|http</c>: shows every frame and HTTP call of the client, and sends frames the client would never produce
    /// (unknown type, missing field, wrong signature…) to check how a server or another implementation reacts.
    /// </summary>
    private async Task HandleRawAsync(string line, string[] parts)
    {
        const string usage = """
            Usage: raw on [full] | raw off          show every WebSocket frame and HTTP call (default off)
                   raw send [url] <json>            send one WebSocket frame as is (invalid ones too); the answer shows with 'raw on'
                   raw http [url] <GET|POST|DELETE|...> <path> [json]   one HTTP call with this identity's token (flags, sessions)
                   raw example [type]               the frame types of PROTOCOL_API, with an example to adapt
            """;
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "on":
                _showFrames = true;
                _framesFull = parts.Length >= 3 && parts[2].Equals("full", StringComparison.OrdinalIgnoreCase);
                Print(_framesFull ? "Frames shown in full." : "Frames shown (long ones cut at 500 characters).");
                return;
            case "off":
                _showFrames = false;
                Print("Frames hidden.");
                return;
            case "send":
            {
                var rest = Rest(line, 2);
                var url = _client.CliServerUrl;
                if (rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { var sp = rest.IndexOf(' '); if (sp < 0) { PrintError(usage); return; } url = rest[..sp]; rest = rest[(sp + 1)..].TrimStart(); }
                if (rest.Length == 0) { PrintError(usage); return; }
                var wasShown = _showFrames; _showFrames = true;
                try { await _client.SendRawFrameAsync(url, rest); await Task.Delay(700); }
                finally { _showFrames = wasShown; }
                return;
            }
            case "http":
            {
                var rest = Rest(line, 2);
                var url = _client.CliServerUrl;
                if (rest.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { var sp = rest.IndexOf(' '); if (sp < 0) { PrintError(usage); return; } url = rest[..sp]; rest = rest[(sp + 1)..].TrimStart(); }
                var words = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) { PrintError(usage); return; }
                var (status, body) = await _client.SendRawHttpAsync(url, words[0], words[1], words.Length >= 3 ? words[2] : null);
                Print($"  {status} {body}");
                return;
            }
            case "example":
                PrintFrameExamples(parts.Length >= 3 ? parts[2] : null);
                return;
            default:
                PrintError(usage);
                return;
        }
    }

    private void PrintFrameExamples(string? type)
    {
        var id = _client.Identity.DiscoveryId;
        var examples = new (string Type, string Json)[]
        {
            ("is_online", $$"""{"type":"is_online","request_id":"r1","discovery_id":"{{id}}"}"""),
            ("send", $$$"""{"type":"send","request_id":"r2","envelope":{"from":"{{{id}}}","to":"<discovery_id>","crypto":1,"msg":"<base64>","ts":0}}"""),
            ("register", $$"""{"type":"register","request_id":"r3","discovery_id":"{{id}}"}"""),
            ("push_unregister", """{"type":"push_unregister","request_id":"r4"}"""),
            ("(unknown type)", """{"type":"does_not_exist","request_id":"r5"}"""),
            ("(not JSON)", "this is not json"),
        };
        foreach (var e in examples.Where(e => type == null || e.Type.Contains(type, StringComparison.OrdinalIgnoreCase)))
            Print($"  {e.Type,-16} raw send {e.Json}");
        Print("  The complete list of frames, with their fields: PROTOCOL_API.en.md.");
    }

    private string TitleOf(Conversation c) => c.Group?.Name ?? _client.FindContact(c.ContactId ?? "")?.DisplayName ?? c.HistoryKey;

    private static bool IsOn(string? s) => s?.ToLowerInvariant() is "on" or "yes" or "true" or "1";

    // ------------------------------------------------------------------
    // Conversations, search, read state
    // ------------------------------------------------------------------

    /// <summary><c>chats</c>: the home screen of the mobile app — every conversation, newest first, with unread count.</summary>
    private void HandleChats()
    {
        var list = _client.GetConversations().Where(s => !s.IsArchived).ToList();
        if (list.Count == 0) { Print("No conversation."); return; }
        foreach (var s in list)
        {
            var unread = _client.UnreadCount(s.Conversation);
            Print($"  {(s.IsGroup ? "[group]" : "[1-1]  ")} {s.Title}{(unread > 0 ? $"  ({unread} unread)" : "")}  — {s.Count} message(s){(s.Last == null ? "" : $", last: {Shown(s.Last)}")}");
        }
    }

    /// <summary><c>search &lt;text&gt; [in &lt;contact|group&gt;]</c>: the message search of the mobile app.</summary>
    private void HandleSearch(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: search <text> [in <contact|group>]"); return; }
        var inIdx = Array.FindLastIndex(parts, p => p == "in");
        Conversation? within = null;
        var words = parts[1..];
        if (inIdx > 1 && inIdx == parts.Length - 2) { within = _client.ResolveConversation(parts[^1]); words = parts[1..inIdx]; }
        var text = string.Join(' ', words);
        if (text.Length < FreeSpeechClient.MinSearchLength) { PrintError($"Type at least {FreeSpeechClient.MinSearchLength} characters."); return; }
        var hits = _client.Search(text, 100, within);
        if (hits.Count == 0) { Print("No match."); return; }
        foreach (var h in hits)
            Print($"  {h.Title} #{_client.IndexOfMessage(h.Conversation, h.Entry.Id)} [{h.Entry.At:u}] {h.AuthorName}: {Shown(h.Entry)}");
    }

    private async Task HandleReadAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: read <contact|group>   (marks everything received as read; a contact is told unless 'settings receipts off')"); return; }
        var c = _client.ResolveConversation(parts[1]);
        await _client.MarkReadAsync(c);
        Print($"{TitleOf(c)}: marked as read.");
    }

    private async Task HandleTypingAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: typing <contact|group>   (tells the conversation that you are typing)"); return; }
        await _client.SendTypingAsync(_client.ResolveConversation(parts[1]));
        Print("Typing indicator sent.");
    }

    /// <summary><c>settings</c>: the privacy and migration switches of the mobile app's settings page.</summary>
    private void HandleSettings(string[] parts)
    {
        if (parts.Length >= 3)
        {
            var on = IsOn(parts[2]);
            switch (parts[1].ToLowerInvariant())
            {
                case "receipts": _client.SendReadReceipts = on; break;
                case "typing": _client.SendTypingIndicator = on; break;
                case "auto-accept-migration": _client.AutoAcceptMigration = on; break;
                case "auto-exclude-migrated": _client.AutoExcludeMigrated = on; break;
                default: PrintError("Usage: settings [receipts|typing|auto-accept-migration|auto-exclude-migrated on|off]"); return;
            }
        }
        else if (parts.Length != 1) { PrintError("Usage: settings [receipts|typing|auto-accept-migration|auto-exclude-migrated on|off]"); return; }

        string Flag(bool b) => b ? "on" : "off";
        Print($"  receipts (send read receipts)          : {Flag(_client.SendReadReceipts)}");
        Print($"  typing (send typing indicator)         : {Flag(_client.SendTypingIndicator)}");
        Print($"  auto-accept-migration                  : {Flag(_client.AutoAcceptMigration)}");
        Print($"  auto-exclude-migrated                  : {Flag(_client.AutoExcludeMigrated)}");
        Print($"  remote wipe ready on this device       : {Flag(_client.RemoteWipeReady)}");
    }

    // ------------------------------------------------------------------
    // Pinned messages (groups)
    // ------------------------------------------------------------------

    private async Task HandlePinAsync(string verb, string[] parts)
    {
        if (verb == "pins")
        {
            if (parts.Length < 2) { PrintError("Usage: pins <group>"); return; }
            var c = _client.ResolveConversation(parts[1]);
            var pinned = _client.GetPinned(c);
            if (pinned.Count == 0) { Print("No pinned message."); return; }
            foreach (var e in pinned) Print($"  #{_client.IndexOfMessage(c, e.Id)} [{e.At:u}] {Shown(e)}");
            return;
        }
        if (parts.Length < 3 || !int.TryParse(parts[2].TrimStart('#'), out var index)) { PrintError($"Usage: {verb} <group> <#>   (moderators only)"); return; }
        var conv = _client.ResolveConversation(parts[1]);
        await _client.PinMessageAsync(conv, index, verb == "pin");
        Print(verb == "pin" ? $"Message #{index} pinned." : $"Message #{index} released.");
    }

    // ------------------------------------------------------------------
    // Removed contacts, left groups
    // ------------------------------------------------------------------

    /// <summary><c>contact remove|rename|safety</c>: removing a contact archives the conversation (nothing is sent to them).</summary>
    private void HandleContactCommand(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "remove" when parts.Length >= 3:
                _client.RemoveContact(parts[2]);
                Print($"{parts[2]} removed; the conversation is archived ('archive list').");
                return;
            case "rename" when parts.Length >= 3:
            {
                var name = parts.Length >= 4 ? string.Join(' ', parts.Skip(3)) : null;
                _client.RenameContact(_client.RequireContact(parts[2]), name == "clear" ? null : name);
                Print(name == null || name == "clear" ? "Private name cleared." : $"Private name set to '{name}' (only you see it).");
                return;
            }
            default:
                PrintError("Usage: contact remove <name|id> | contact rename <name|id> <private name>|clear");
                return;
        }
    }

    /// <summary><c>archive list|open|delete</c>: the contacts I removed and the groups I left, readable until deleted for good.</summary>
    private void HandleArchive(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                var items = _client.GetConversations().Where(s => s.IsArchived).ToList();
                if (items.Count == 0) { Print("Archive empty."); return; }
                foreach (var s in items)
                    Print($"  {(s.IsGroup ? "[group]" : "[1-1]  ")} {s.Title}  [{s.Conversation.Group?.GroupId ?? s.Conversation.HistoryKey}]  — {s.Count} message(s)");
                return;
            }
            case "delete" when parts.Length >= 3:
            {
                var target = _client.GetConversations().Where(s => s.IsArchived)
                    .FirstOrDefault(s => string.Equals(s.Title, parts[2], StringComparison.OrdinalIgnoreCase)
                        || (s.Conversation.Group?.GroupId ?? s.Conversation.HistoryKey) == parts[2])
                    ?? throw new ClientException($"No archived conversation '{parts[2]}'.");
                if (target.IsGroup) _client.DeleteGroupPermanently(target.Conversation.Group!);
                else _client.DeleteArchivedContactPermanently(target.Conversation.HistoryKey);
                Print($"{target.Title}: deleted for good, with its files.");
                return;
            }
            default:
                PrintError("Usage: archive list | archive delete <name|id>   (deleting is permanent)");
                return;
        }
    }

    // ------------------------------------------------------------------
    // File archive
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>filearchive preview|create|restore</c>: moves attachment files out of the profile into an encrypted archive file (frees space);
    /// the messages stay, the files come back with <c>restore</c>.
    /// </summary>
    private void HandleFileArchive(string[] parts)
    {
        const string usage = "Usage: filearchive preview|create <path> [months <n>] [with <contact|group>] [kind image|video|audio|other] [min <MB>] | filearchive restore <path>";
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        if (sub == "restore" && parts.Length >= 3)
        {
            var r = _client.Archive.Restore(parts[2]) ?? throw new ClientException($"File not found: {parts[2]}");
            Print($"Restored {r.Files} file(s), {r.Records} record(s); {r.Skipped} skipped.");
            return;
        }
        if (sub is not ("preview" or "create") || (sub == "create" && parts.Length < 3)) { PrintError(usage); return; }

        var sel = new ArchiveSelection();
        for (var i = sub == "create" ? 3 : 2; i + 1 < parts.Length; i += 2)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "months": sel = ArchiveSelection.OlderThanMonths(int.Parse(parts[i + 1])); break;
                case "with": sel.Conversation = _client.ResolveConversation(parts[i + 1]).HistoryKey; break;
                case "kind":
                    switch (parts[i + 1].ToLowerInvariant())
                    {
                        case "image": sel.MimePrefix = "image/"; break;
                        case "video": sel.MimePrefix = "video/"; break;
                        case "audio": sel.MimePrefix = "audio/"; break;
                        case "other": sel.OtherKindsOnly = true; break;
                        default: PrintError(usage); return;
                    }
                    break;
                case "min": sel.MinSize = (long)(double.Parse(parts[i + 1], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024); break;
                default: PrintError(usage); return;
            }
        }
        if (sub == "preview")
        {
            var p = _client.Archive.Preview(sel);
            Print($"{p.Files} file(s), {p.Records} record(s), {p.Bytes / 1024.0 / 1024.0:F1} MB would move to the archive.");
            return;
        }
        var s = _client.Archive.Create(parts[2], sel);
        Print($"Archive written to {parts[2]}: {s.Files} file(s), {s.Records} record(s), {s.FreedBytes / 1024.0 / 1024.0:F1} MB freed, archive {s.ArchiveBytes / 1024.0 / 1024.0:F1} MB.");
    }

    // ------------------------------------------------------------------
    // Device sync
    // ------------------------------------------------------------------

    /// <summary><c>sync request|list|approve|reject</c>: mirror another device of the same identity (the other device is the reference). Group sync is <c>group sync</c>.</summary>
    private async Task HandleDeviceSyncAsync(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "request":
            {
                var reached = await _client.RequestDeviceSyncAsync();
                Print(reached ? $"Request sent. Give this code on the other device: {_client.DeviceSyncCode}"
                    : $"No other device is connected now; the request is repeated for a few minutes. Code: {_client.DeviceSyncCode}");
                return;
            }
            case "list":
            {
                var pending = _client.PendingDeviceSyncs;
                if (pending.Count == 0) { Print("No device sync request waiting."); return; }
                foreach (var r in pending) Print($"  {r.Id}  asked at {r.At:u}");
                return;
            }
            case "approve" when parts.Length >= 4:
            {
                var n = await _client.ApproveDeviceSyncAsync(parts[2], string.Join("", parts.Skip(3)));
                Print($"Approved: {n} item(s) sent to the other device.");
                return;
            }
            case "reject" when parts.Length >= 3:
                await _client.RejectDeviceSyncAsync(parts[2]);
                Print("Request rejected.");
                return;
            default:
                PrintError("Usage: sync request | sync list | sync approve <id> <code> | sync reject <id>   (group sync: 'group sync [g]')");
                return;
        }
    }

    // ------------------------------------------------------------------
    // Migration to a new identity, remote wipe
    // ------------------------------------------------------------------

    private static string[] ReadPhrases(string label)
    {
        System.Console.WriteLine(label);
        return [ReadPhrase("  Phrase 1: "), ReadPhrase("  Phrase 2: "), ReadPhrase("  Phrase 3: ")];
    }

    /// <summary><c>migrate send|switch|list|accept|decline</c>: move to a new identity (new phrases) and follow a contact who moved.</summary>
    private async Task HandleMigrateAsync(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "list":
            {
                var pending = _client.PendingMigrations;
                if (pending.Count == 0) Print("No migration waiting.");
                foreach (var n in pending) Print($"  {n.OldName} [{n.OldId}] -> {n.NewCard.Nickname} [{n.NewCard.DiscoveryId}]  received {n.ReceivedAt:u}{(n.FromContact ? "" : " (group member, not a contact)")}");
                if (_client.OutgoingMigrationId is { } outId)
                    Print($"  Outgoing: this identity announced {outId}; {_client.PendingMigrationLetters} letter(s) still queued. 'migrate switch' when ready.");
                return;
            }
            case "accept" when parts.Length >= 3:
                await _client.AcceptMigrationAsync(parts[2]);
                Print("Migration accepted: the new identity is now a contact.");
                return;
            case "decline" when parts.Length >= 3:
                _client.DeclineMigration(parts[2]);
                Print("Migration declined.");
                return;
            case "send":
            {
                var roles = _client.RolesToHandOver();
                foreach (var r in roles)
                    Print($"  [!] You hold a role in '{r.Group.Name}' ({(r.IsCreator ? "creator" : "moderator")}); hand it over first if you can: group transfer / promote.");
                var oldP = ReadPhrases("Phrases of the CURRENT identity:");
                var newP = ReadPhrases("Phrases of the NEW identity (new phrases, saved elsewhere):");
                var res = await _client.SendMovedAsync(oldP, newP);
                Print($"Announced {res.NewId} to {res.Recipients} contact(s)/moderator(s): {res.Delivered} now, {res.Queued} queued. Keep this identity until the queue is empty, then 'migrate switch'.");
                return;
            }
            case "switch":
            {
                if (_client.PendingMigrationLetters > 0)
                    Print($"  [!] {_client.PendingMigrationLetters} migration letter(s) are still queued; they are lost if you switch now.");
                _client.SwitchToNewIdentity(ReadPhrases("Phrases of the NEW identity:"));
                Print("Switched. Restart the console with the same profile name to use the new identity.");
                return;
            }
            default:
                PrintError("Usage: migrate send | migrate switch | migrate list | migrate accept <id> | migrate decline <id>");
                return;
        }
    }

    /// <summary><c>wipe check|enable|send</c>: erase every other device of this identity (needs the three phrases).</summary>
    private async Task HandleWipeAsync(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "check":
            {
                var p = ReadPhrases("Phrases of this identity:");
                _client.CheckMyPhrases(p[0], p[1], p[2]);
                Print("These are the phrases of this identity.");
                return;
            }
            case "enable":
            {
                var p = ReadPhrases("Phrases of this identity:");
                _client.EnableRemoteWipe(p[0], p[1], p[2]);
                Print("This device now obeys a remote wipe.");
                return;
            }
            case "send":
            {
                System.Console.Write("This erases every OTHER device of this identity. Type 'wipe' to confirm: ");
                if (ConsoleLine.Read()?.Trim() != "wipe") { Print("Cancelled."); return; }
                var p = ReadPhrases("Phrases of this identity:");
                var now = await _client.SendWipeAsync(p[0], p[1], p[2]);
                Print(now ? "Order delivered to a device now; offered again to the others for 30 days." : "No other device connected now; the order is offered again for 30 days.");
                return;
            }
            default:
                PrintError("Usage: wipe check | wipe enable | wipe send   (phrases are asked)");
                return;
        }
    }
}
