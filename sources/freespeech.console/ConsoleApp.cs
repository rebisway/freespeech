/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Backup;
using FreeSpeech.Client.Discovery;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Console;

/// <summary>
/// Interactive console front-end: reads a command line, calls <see cref="FreeSpeechClient"/> (the whole
/// client API lives there), prints the result and the client's events. It holds no protocol logic, so a
/// mobile or desktop app uses the same calls. It exists to drive the server and the client API from a real
/// multi-instance test ("dotnet run alice" / "dotnet run bob").
///
/// USAGE: dotnet run &lt;profileName&gt; [serverUrl] [--log level] [--log-file path] — type 'help' for the commands.
/// </summary>
public partial class ConsoleApp
{
    private readonly string _profileName;
    private readonly FreeSpeechClient _client;

    public ConsoleApp(string profileName, string profileDir, string serverUrl)
    {
        _profileName = profileName;
        // Test aid: FREESPEECH_CHUNK_DELAY_MS slows the serving side so a transfer can be interrupted mid-file on localhost.
        var chunkDelay = int.TryParse(Environment.GetEnvironmentVariable("FREESPEECH_CHUNK_DELAY_MS"), out var delayMs) && delayMs > 0
            ? TimeSpan.FromMilliseconds(delayMs) : default;
        _client = new FreeSpeechClient(new ClientOptions { ProfileDir = profileDir, ServerUrl = serverUrl, Directories = DirectoryPool.Parse(Environment.GetEnvironmentVariable("FS_DIRECTORIES")), ChunkDelay = chunkDelay, KeyCache = OperatingSystem.IsWindows() ? new DpapiKeyCache(profileDir) : null });
    }

    public async Task RunAsync()
    {
        PrintBanner();

        if (!_client.TryLoadIdentity()) CreateIdentity();
        _client.Initialize();

        var identity = _client.Identity;
        Print($"Identity : {identity.Nickname} [{identity.DiscoveryId[..16]}...]");
        Print($"Servers  : {string.Join(", ", identity.Servers.OrderBy(s => s.Priority).Select(s => s.Url))}");
        if (!identity.Servers.Any(s => RelayPool.Normalize(s.Url) == _client.CliServerUrl))
            Print($"           (+ {_client.CliServerUrl} connected for this run only, not advertised)");

        _client.MessageReceived += OnMessageReceived;
        _client.MessageChanged += OnMessageChanged;
        _client.ContactRequested += card =>
            PrintNotification($"[contact request] {card.Nickname} [{card.DiscoveryId}] wants to connect — type 'confirm {card.DiscoveryId}' to accept.");
        _client.ContactConfirmed += contact => PrintNotification($"[contact confirmed] {contact.Nickname} [{contact.DiscoveryId}]");
        _client.ContactUpdated += contact => PrintNotification($"[contact updated] {contact.Nickname} [{contact.DiscoveryId}]");
        _client.ServerConnectionChanged += (url, up) =>
            PrintNotification(up ? $"[reconnected to {url}]" : $"[disconnected from {url} — will retry automatically]");
        _client.Notice += PrintNotification;
        HookExtraEvents();

        foreach (var s in await _client.ConnectAsync())
            Print(s.Connected ? $"Connected and registered on {s.Server.Url}." : $"[!] {s.Server.Url} unreachable — will retry automatically.");

        Print("\nType 'help' for commands.\n");
        while (true)
        {
            System.Console.Write($"[{_profileName}]> ");
            var rawLine = ConsoleLine.Read();
            if (rawLine == null)
            {
                // Stdin closed (EOF) — happens when running unattended/piped. Exit instead of spinning on a null read.
                System.Console.WriteLine();
                await _client.DisposeAsync();
                return;
            }

            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "show": PrintIdentity(); break;
                    case "send": await HandleSendAsync(parts); break;
                    case "reply": await HandleReplyAsync(parts); break;
                    case "upload": await HandleUploadAsync(line); break;
                    case "download": await HandleDownloadAsync(parts); break;
                    case "forward": await HandleForwardAsync(parts); break;
                    case "delete" or "retract" or "edit" or "dropfile" or "retractfile" or "react":
                        if (parts.Length < 2) { PrintError($"Usage: {ChangeUsage(command, "<name|id>")}"); break; }
                        await HandleChangeAsync(_client.ContactConversation(parts[1]), command, parts[2..], ChangeUsage(command, "<name|id>"));
                        break;
                    case "group": await HandleGroupAsync(parts); break;
                    case "profile": await HandleProfileAsync(line); break;
                    case "files": HandleFiles(); break;
                    case "online": await HandleOnlineAsync(parts); break;
                    case "wait": await HandleWaitAsync(parts); break;
                    case "ready": await HandleReadyAsync(parts); break;
                    case "flags": await HandleFlagsAsync(); break;
                    case "clear": await HandleClearAsync(parts); break;
                    case "history": HandleHistory(parts); break;
                    case "alias": HandleAlias(parts); break;
                    case "contacts": HandleContacts(); break;
                    case "safety": HandleSafety(parts); break;
                    case "card": HandleCard(parts); break;
                    case "export": HandleExport(parts); break;
                    case "import": await HandleImportAsync(parts); break;
                    case "requests": HandleRequests(); break;
                    case "confirm": await HandleConfirmAsync(parts); break;
                    case "outbox": HandleOutbox(); break;
                    case "backup": HandleBackup(parts); break;
                    case "restore": HandleRestore(parts); break;
                    case "server": await HandleServerAsync(parts); break;
                    case "log": Print(ConsoleLog.Command(parts)); break;
                    case "chats": HandleChats(); break;
                    case "raw": await HandleRawAsync(line, parts); break;
                    case "search": HandleSearch(parts); break;
                    case "read": await HandleReadAsync(parts); break;
                    case "typing": await HandleTypingAsync(parts); break;
                    case "settings": HandleSettings(parts); break;
                    case "pin" or "unpin" or "pins": await HandlePinAsync(command, parts); break;
                    case "contact": HandleContactCommand(parts); break;
                    case "archive": HandleArchive(parts); break;
                    case "filearchive": HandleFileArchive(parts); break;
                    case "sync": await HandleDeviceSyncAsync(parts); break;
                    case "migrate": await HandleMigrateAsync(parts); break;
                    case "wipe": await HandleWipeAsync(parts); break;
                    case "help": PrintHelp(); break;
                    case "quit":
                    case "exit":
                        await _client.DisposeAsync();
                        return;
                    default:
                        PrintError($"Unknown command '{command}'. Type 'help'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    // Incoming messages
    // ------------------------------------------------------------------

    /// <summary>" (re: #7)" — the local, per-conversation history index instead of the raw message_id.</summary>
    private static string ReplySuffix(int? replyIndex) => replyIndex switch
    {
        null => "",
        0 => " (re: an earlier message not in local history)",
        var n => $" (re: #{n})",
    };

    private void OnMessageReceived(ChatMessage m)
    {
        var suffix = ReplySuffix(m.ReplyIndex);
        if (m.Conversation.Group is { } g)
        {
            PrintNotification($"[{g.Name}] {m.FromName}: {m.Text}{suffix}"
                + (m.IsFile && !m.Mine ? $" — 'group download {g.Name} #{m.HistoryIndex}' to fetch" : ""));
        }
        else if (m.FromOtherDevice)
        {
            PrintNotification($"[synced from another device -> {m.Conversation.ContactId}] {m.Text}{suffix}");
        }
        else if (m.IsFile)
        {
            PrintNotification($"[{m.FromId}] {m.Text} — 'download {m.FromId} #{m.HistoryIndex}' to download");
        }
        else
        {
            PrintNotification($"[{m.FromId}] {m.Text}{suffix}");
        }
    }

    // ------------------------------------------------------------------
    // Commands: identity, servers, profile
    // ------------------------------------------------------------------

    private void PrintIdentity()
    {
        Print($"  Nickname      : {_client.Identity.Nickname}");
        Print($"  ID            : {_client.Identity.DiscoveryId}");
        Print($"  Created       : {_client.Identity.CreatedAt:u}");
        PrintServers();
    }

    private void PrintServers()
    {
        foreach (var s in _client.Servers)
            Print($"  Server        : [{s.Server.Priority}] {s.Server.Url} ({(s.Connected ? "connected" : s.Refusal switch
            {
                "private_server" => "private: invitation or 'server request <url>' needed",
                "access_pending" => "access requested, waiting for the administrator",
                "access_refused" => "access refused",
                _ => "DOWN",
            })})");
    }

    /// <summary><c>server list|add|remove</c> (§8): the servers this identity advertises and stays registered on; contacts are notified after a change.</summary>
    private async Task HandleServerAsync(string[] parts)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "list":
                PrintServers();
                return;

            case "add" when parts.Length >= 3:
            {
                // "server add <url> [priority] [invitation]": the invitation token (fs-inv-...) comes from the administrator of a private server.
                var invitation = parts.Skip(3).FirstOrDefault(x => x.StartsWith("fs-inv-", StringComparison.Ordinal));
                var priority = parts.Skip(3).Where(x => int.TryParse(x, out _)).Select(int.Parse).Cast<int?>().FirstOrDefault();
                var s = await _client.AddServerAsync(parts[2], priority, invitation);
                Print(s.Connected ? $"Added {s.Server.Url} (priority {s.Server.Priority}), connected."
                    : s.Refusal == "private_server" ? $"Added {s.Server.Url} (priority {s.Server.Priority}): private server — add its invitation (server add <url> <fs-inv-...>) or ask for access (server request <url>)."
                    : s.Refusal == "access_refused" ? $"Added {s.Server.Url} (priority {s.Server.Priority}): the server refused this identity."
                    : $"Added {s.Server.Url} (priority {s.Server.Priority}) but it is unreachable right now — will retry.");
                break;
            }

            case "request" when parts.Length >= 3:
            {
                // Sends my name and a note to the administrator of a private server (the user typed this command: that is the consent).
                var s = await _client.RequestServerAccessAsync(parts[2], parts.Length >= 4 ? string.Join(' ', parts.Skip(3)) : null);
                Print(s.Connected ? "Already allowed: connected."
                    : s.Refusal == "access_pending" ? "Request sent: the administrator has to approve it. The server shows 'access requested' until then."
                    : s.Refusal == "access_refused" ? "The server refused this identity."
                    : "The server did not take the request (closed or full): ask the administrator for an invitation.");
                return;
            }

            case "remove" when parts.Length >= 3:
                Print($"Removed {await _client.RemoveServerAsync(parts[2])}.");
                break;

            default:
                PrintError("Usage: server list | server add <url> [priority] [fs-inv-...] | server request <url> [note] | server remove <url>");
                return;
        }

        Print($"Notifying {_client.Contacts.Count} confirmed contact(s)...");
        await _client.NotifyContactsAsync();
    }

    /// <summary><c>profile show | name | about | photo</c>: my name, description and picture, sent to every contact and group.</summary>
    private async Task HandleProfileAsync(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "show";
        var arg = parts.Length >= 3 ? parts[2].Trim() : "";
        var clear = arg.Equals("clear", StringComparison.OrdinalIgnoreCase);

        int contacts;
        switch (sub)
        {
            case "show":
                var me = _client.Identity;
                Print($"  Name  : {me.Nickname}");
                Print($"  Photo : {PhotoSize(me.Photo)}");
                Print($"  About : {me.Description ?? "(none)"}");
                return;
            case "name" when arg.Length > 0: contacts = _client.SetNickname(arg); break;
            case "about" when arg.Length > 0: contacts = _client.SetDescription(clear ? null : arg); break;
            case "photo" when arg.Length > 0: contacts = _client.SetPhoto(clear ? null : arg); break;
            default:
                PrintError("Usage: profile show | name <nickname> | about <text>|clear | photo <path>|clear");
                return;
        }

        Print($"Profile updated. Notifying {contacts} contact(s) and your groups...");
        await _client.PublishProfileAsync();
    }

    private static string PhotoSize(string? photo) => photo == null ? "none" : $"{Convert.FromBase64String(photo).Length / 1024} KiB";

    // ------------------------------------------------------------------
    // Commands: messages, files, history
    // ------------------------------------------------------------------

    private void PrintSent(SentMessage m, string? replyDisplay = null)
    {
        if (m.To.Group is { } g)
        {
            Print(m.IsFile ? $"  -> [{g.Name}] {m.Text} (offer live to {m.Delivered}/{m.Recipients}; members download from any member holding it)"
                : replyDisplay != null ? $"  -> [{g.Name}] {m.Text} (re: {replyDisplay}; live to {m.Delivered}/{m.Recipients})"
                : $"  -> [{g.Name}] {m.Text} (live to {m.Delivered}/{m.Recipients}; the others catch up from their peers)");
            return;
        }

        var to = m.To.ContactId;
        switch (m.Status)
        {
            case SendStatus.Delivered: Print($"  -> {to}: {m.Text}{(replyDisplay != null ? $" (re: {replyDisplay})" : "")}"); break;
            case SendStatus.QueuedRecipientOffline: PrintError($"{to} is not connected right now — queued, and a WAITING flag was set."); break;
            default: PrintError("No server reachable — queued, will retry automatically."); break;
        }
    }

    private async Task HandleSendAsync(string[] parts)
    {
        if (parts.Length < 3) { PrintError("Usage: send <name|id> <text>"); return; }
        PrintSent(await _client.SendAsync(_client.ContactConversation(parts[1]), string.Join(' ', parts[2..])));
    }

    private async Task HandleReplyAsync(string[] parts)
    {
        if (parts.Length < 4 || !int.TryParse(parts[2], out var index))
        { PrintError("Usage: reply <name|id> <history index> <text> — see 'history <name|id>' for indices"); return; }
        PrintSent(await _client.ReplyAsync(_client.ContactConversation(parts[1]), index, string.Join(' ', parts[3..])), $"#{index}");
    }

    /// <summary>upload &lt;name|id&gt; &lt;path&gt; — path may contain spaces (everything after the contact).</summary>
    private async Task HandleUploadAsync(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { PrintError("Usage: upload <name|id> <path>"); return; }
        PrintSent(await _client.UploadAsync(_client.ContactConversation(parts[1]), parts[2]));
    }

    private async Task HandleDownloadAsync(string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2].TrimStart('#'), out var index))
        { PrintError("Usage: download <name|id> <history index> — see 'history <name|id>'"); return; }
        Print(await _client.DownloadAsync(_client.ContactConversation(parts[1]), index));
    }

    /// <summary><c>forward &lt;from&gt; &lt;#&gt; &lt;to&gt;</c>: message or file #N of a contact/group conversation to another contact or group.</summary>
    private async Task HandleForwardAsync(string[] parts)
    {
        if (parts.Length < 4 || !int.TryParse(parts[2].TrimStart('#'), out var index))
        { PrintError("Usage: forward <contact|group> <history index> <contact|group>  (prefix a group with 'group:' if a contact has the same name)"); return; }
        PrintSent(await _client.ForwardAsync(_client.ResolveConversation(parts[1]), index, _client.ResolveConversation(parts[3])));
    }

    private static string ChangeUsage(string verb, string who) => verb switch
    {
        "edit" => $"edit {who} <#> <new text>",
        "react" => $"react {who} <#> <emoji>",
        _ => $"{verb} {who} <#>",
    };

    /// <summary>
    /// <c>delete</c> / <c>dropfile</c>: on this device only. <c>retract</c> / <c>retractfile</c> / <c>edit</c>: for everyone,
    /// author only. A file message loses its file with <c>delete</c> and <c>retract</c>; <c>dropfile</c> / <c>retractfile</c> keep the line.
    /// </summary>
    private async Task HandleChangeAsync(Conversation conv, string verb, string[] args, string usage)
    {
        if (args.Length < 1 || !int.TryParse(args[0].TrimStart('#'), out var index) || (verb is "edit" or "react" && args.Length < 2))
        { PrintError($"Usage: {usage}"); return; }

        switch (verb)
        {
            case "delete": _client.DeleteMessage(conv, index); Print($"Message #{index} deleted on this device."); break;
            case "dropfile": _client.DeleteFile(conv, index); Print($"File of #{index} deleted from this device (the line stays)."); break;
            case "retract": PrintSent(await _client.RetractMessageAsync(conv, index)); break;
            case "retractfile": PrintSent(await _client.RetractFileAsync(conv, index)); break;
            case "react": await _client.ReactAsync(conv, index, string.Join(' ', args[1..])); Print($"Reaction on #{index} sent."); break;
            default: PrintSent(await _client.EditMessageAsync(conv, index, string.Join(' ', args[1..]))); break;
        }
    }

    /// <summary>`safety <contact> [verified|unverified]` — the number to compare with the contact (§3), and the verified mark.</summary>
    private void HandleSafety(string[] parts)
    {
        if (parts.Length < 2) { Print("Usage: safety <contact> [verified|unverified]"); return; }
        var contact = _client.RequireContact(parts[1]);
        if (parts.Length > 2) { _client.SetVerified(contact, parts[2] == "verified"); Print($"{contact.Nickname}: {(contact.VerifiedAt != null ? "marked verified" : "verification removed")}."); return; }
        Print($"Safety number with {contact.Nickname}{(contact.VerifiedAt != null ? " (verified)" : "")}:\n{_client.SafetyNumberOf(contact)}");
    }

    /// <summary>What a history line shows: the text, with its edit or deletion status.</summary>
    private static string Shown(HistoryEntry e) => e.State switch
    {
        MessageState.Retracted => "[message deleted]",
        MessageState.FileRetracted => "[file removed by the sender]",
        MessageState.FileDeleted => $"{e.Text} [file deleted on this device]",
        _ => e.Edited ? $"{e.Text} (edited)" : e.Text,
    };

    private void OnMessageChanged(MessageChange c)
    {
        var where = c.Conversation.Group is { } g ? $"[{g.Name}] {c.AuthorName}" : $"[{c.AuthorId}]";
        var at = c.HistoryIndex > 0 ? $" (#{c.HistoryIndex})" : "";
        PrintNotification(c.Kind switch
        {
            MessageChangeKind.Edited => $"{where} edited a message{at}: {c.NewText}",
            MessageChangeKind.Retracted => $"{where} deleted a message{at}",
            MessageChangeKind.Reacted => c.NewText is { Length: > 0 } ? $"{where} reacted {c.NewText} to a message{at}" : $"{where} took back a reaction{at}",
            _ => $"{where} removed a file{at}",
        });
    }

    private void HandleFiles()
    {
        var store = _client.Attachments.Store;
        if (store.All.Count == 0) { Print("No files."); return; }
        foreach (var r in store.All)
        {
            var progress = r.State == AttachmentState.Receiving ? $" {store.PartIds(r.Ref).Count}/{r.Chunks} chunks" : "";
            Print($"  {(r.Direction == AttachmentDirection.Sent ? "->" : "<-")} {r.ContactId[..8]}… {r.Name} ({r.Size} bytes) {r.State}{progress}{(r.State == AttachmentState.Complete && r.Direction == AttachmentDirection.Received ? " " + r.Path : "")}");
        }
    }

    private const string HistoryUsage = "[last [n] | before <#> [n] | after <#> [n] | around <#> [n]]";

    /// <summary>
    /// Shows a conversation: all of it, or a window the way a mobile screen reads it — the newest
    /// messages (<c>last</c>), older ones before a message (<c>before</c>), newer ones after it
    /// (<c>after</c>), or a window around one (<c>around</c>). Indices are the usual "#N".
    /// </summary>
    private void ShowHistory(Conversation conv, string command, string[] opts, Func<HistoryLine, string> format, string emptyMessage)
    {
        IReadOnlyList<HistoryLine> lines;
        HistoryPage? page = null;
        if (opts.Length == 0)
        {
            lines = _client.GetHistory(conv);
        }
        else
        {
            var mode = opts[0].ToLowerInvariant();
            var hasIndex = mode != "last";
            int index = 0, size = 20;
            if ((hasIndex && (opts.Length < 2 || !int.TryParse(opts[1].TrimStart('#'), out index)))
                || (opts.Length > (hasIndex ? 2 : 1) && !int.TryParse(opts[hasIndex ? 2 : 1], out size))
                || size < 1 || mode is not ("last" or "before" or "after" or "around"))
            { PrintError($"Usage: {command} {HistoryUsage}"); return; }

            page = mode switch
            {
                "last" => _client.GetLatestMessages(conv, size),
                "before" => _client.GetMessagesBefore(conv, _client.CursorAt(conv, index), size),
                "after" => _client.GetMessagesAfter(conv, _client.CursorAt(conv, index), size),
                _ => _client.GetMessagesAround(conv, _client.CursorAt(conv, index).Id, size),
            };
            lines = _client.GetLines(conv, page);
        }

        if (lines.Count == 0) { Print(emptyMessage); return; }
        foreach (var l in lines) Print(format(l));
        if (page?.HasMoreBefore == true) Print($"  ... older messages: {command} before #{lines[0].Index}");
        if (page?.HasMoreAfter == true) Print($"  ... newer messages: {command} after #{lines[^1].Index}");
    }

    private void HandleHistory(string[] parts)
    {
        if (parts.Length < 2) { PrintError($"Usage: history <name|id> {HistoryUsage}"); return; }
        ShowHistory(_client.ContactConversation(parts[1]), $"history {parts[1]}", parts[2..], l =>
        {
            var arrow = l.Entry.Direction == MessageDirection.Sent ? "->" : "<-";
            var reply = l.ReplyIndex == null ? "" : l.ReplyIndex == 0 ? " (re: ?)" : $" (re: #{l.ReplyIndex})";
            return $"  #{l.Index} [{l.Entry.At:u}] {arrow} {Shown(l.Entry)}{reply}";
        }, "No history with this contact.");
    }
    private void HandleOutbox()
    {
        if (_client.Outbox.Count == 0) { Print("Outbox empty."); return; }
        foreach (var pm in _client.Outbox)
            Print($"  -> {pm.To}: {pm.Text} (queued {pm.CreatedAt:u}, last attempt {pm.LastAttemptAt?.ToString("u") ?? "never"})");
    }

    // ------------------------------------------------------------------
    // Commands: presence and flags
    // ------------------------------------------------------------------

    private async Task HandleOnlineAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: online <name|id>"); return; }
        Print($"{_client.ResolveContact(parts[1])}: {(await _client.IsOnlineAsync(parts[1]) ? "online" : "offline")}");
    }

    private static void ReportFlagResults(IReadOnlyList<FlagResult> results, string okMessage)
    {
        foreach (var r in results)
        {
            if (r.Ok) Print($"{okMessage} [{r.Url}]");
            else PrintError($"{r.Url}: {r.Error}");
        }
    }

    private async Task HandleWaitAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: wait <name|id>"); return; }
        ReportFlagResults(await _client.SetWaitingAsync(parts[1]), $"WAITING set for {_client.ResolveContact(parts[1])}.");
    }

    private async Task HandleReadyAsync(string[] parts)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], out var seconds))
        { PrintError("Usage: ready <name|id> <seconds>"); return; }
        ReportFlagResults(await _client.SetReadyAsync(parts[1], seconds), $"READY set for {_client.ResolveContact(parts[1])} at {DateTime.UtcNow.AddSeconds(seconds):HH:mm:ss} UTC.");
    }

    private async Task HandleFlagsAsync()
    {
        var (flags, errors) = await _client.GetFlagsAsync();
        foreach (var e in errors) PrintError($"{e.Url}: {e.Error}");
        foreach (var f in flags) Print($"  [{f.Url}] {f.From} -> {f.To} state={f.State} nextConnect={f.NextConnectAt?.ToString("u") ?? "-"}");
        if (flags.Count == 0) Print("No flags.");
    }

    private async Task HandleClearAsync(string[] parts)
    {
        if (parts.Length < 3) { PrintError("Usage: clear <from> <to>"); return; }
        ReportFlagResults(await _client.ClearFlagAsync(parts[1], parts[2]), "Flag removed.");
    }

    // ------------------------------------------------------------------
    // Commands: contacts
    // ------------------------------------------------------------------

    private void HandleAlias(string[] parts)
    {
        if (parts.Length < 3) { PrintError("Usage: alias <name> <discoveryId>"); return; }
        _client.SetAlias(parts[1], parts[2]);
        Print($"Alias '{parts[1]}' -> {parts[2]}");
    }

    /// <summary>card &lt;contact&gt;: everything we hold about a confirmed contact.</summary>
    private void HandleCard(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: card <name|id>"); return; }
        var c = _client.FindContact(parts[1]);
        if (c == null) { PrintError($"'{parts[1]}' is not a confirmed contact."); return; }
        Print($"  Name    : {c.Nickname}");
        Print($"  ID      : {c.DiscoveryId}");
        Print($"  About   : {c.Description ?? "(none)"}");
        Print($"  Photo   : {PhotoSize(c.Photo)}");
        Print($"  Servers : {string.Join(", ", c.Servers.OrderBy(s => s.Priority).Select(s => s.Url))}");
        Print($"  Since   : {c.ConfirmedAt:u}");
    }

    private void HandleContacts()
    {
        if (_client.Contacts.Count == 0 && _client.Aliases.Count == 0) { Print("No contacts."); return; }

        foreach (var c in _client.Contacts)
            Print($"  {c.Nickname} -> {c.DiscoveryId} (confirmed)");
        foreach (var (name, id) in _client.Aliases)
            Print($"  {name} -> {id} (alias)");
    }

    private void HandleExport(string[] parts)
    {
        var flag = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "manual";
        var base64 = _client.ExportCard(flag);
        Print($"Share this with your contact ({flag}, valid {(flag == "qr" ? "10 min" : "1 month")}):");
        Print(base64);
    }

    private async Task HandleImportAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: import <base64>"); return; }
        Print($"Import sent to {await _client.ImportCardAsync(parts[1])} — will confirm once both sides are online.");
    }

    private void HandleRequests()
    {
        var pending = _client.PendingRequests;
        if (pending.Count == 0) { Print("No pending contact requests."); return; }
        foreach (var c in pending)
            Print($"  {c.Nickname} [{c.DiscoveryId}]");
    }

    private async Task HandleConfirmAsync(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: confirm <name|id>"); return; }
        Print($"Contact confirmed: {await _client.ConfirmAsync(parts[1])}");
    }

    // ------------------------------------------------------------------
    // Commands: backup
    // ------------------------------------------------------------------

    /// <summary><c>backup [path] [none|6m|all]</c> (§2): default = files of the last 6 months; <c>none</c> = no files; <c>all</c> = everything.</summary>
    private void HandleBackup(string[] parts)
    {
        string? path = null;
        var mode = AttachmentBackupMode.Recent;
        var months = 6;
        foreach (var arg in parts.Skip(1))
        {
            var a = arg.ToLowerInvariant();
            if (a == "none") mode = AttachmentBackupMode.None;
            else if (a == "all") mode = AttachmentBackupMode.All;
            else if (a.Length > 1 && a.EndsWith('m') && int.TryParse(a[..^1], out var m) && m > 0) { mode = AttachmentBackupMode.Recent; months = m; }
            else path = arg;
        }

        path ??= _client.DefaultBackupPath;
        var s = _client.CreateBackup(path, mode, months);
        Print($"Backup written to {path} ({s.Bytes / 1024} KiB): {s.Contacts} contact(s), {s.Aliases} alias(es), {s.Messages} message(s), {s.AttachmentFilesIncluded} attachment file(s) included.");
        if (s.AttachmentFilesLeftOut > 0)
            Print($"  {s.AttachmentFilesLeftOut} attachment file(s) left out ({(mode == AttachmentBackupMode.None ? "mode none" : $"older than {months} months")}) — received ones can be re-fetched with 'download'; sent ones can no longer be served once this device is gone. Use 'backup all' to keep everything.");
    }

    private void HandleRestore(string[] parts)
    {
        if (parts.Length < 2) { PrintError("Usage: restore <path>"); return; }
        var s = _client.RestoreBackup(string.Join(' ', parts[1..]));
        Print($"Backup merged: {s.Contacts} contact(s), {s.Aliases} alias(es), {s.Messages} message(s) (duplicates skipped).");
        Print($"  attachments: {s.AttachmentFilesRestored} file(s) restored, {s.ReceivedNowDownloadable} received file(s) available to re-download with 'download', {s.SentDropped} sent file(s) not in the backup (no longer served), {s.Skipped} already present/invalid.");
    }

    // ------------------------------------------------------------------
    // Commands: groups (§7)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>group create|list|members|add|remove|promote|demote|transfer|leave|forget|send|reply|upload|download|ban|unban|bans|rename|photo|about|info|history|sync</c>.
    /// Composition changes rotate the group key; messages go to every member and the ones who were offline
    /// catch up from the other members (no server storage).
    /// </summary>
    private static FreeSpeech.Client.Groups.PendingGroupInvite FindInvite(FreeSpeech.Client.Groups.GroupService groups, string idOrName) =>
        groups.PendingGroupInvites.FirstOrDefault(i => i.GroupId.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Name, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ClientException($"No invitation for '{idOrName}' (see 'group invites').");

    private async Task HandleGroupAsync(string[] parts)
    {
        var groups = _client.Groups;
        var me = _client.Identity.DiscoveryId;
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "create" when parts.Length >= 3:
            {
                var rec = await _client.CreateGroupAsync(parts[2], parts[3..]);
                Print($"Group '{rec.Name}' created ({rec.GroupId[..8]}), {parts.Length - 3 + 1} member(s); invitations sent (queued for anyone offline).");
                return;
            }

            case "invites":
                if (groups.PendingGroupInvites.Count == 0) { Print("No group invitations waiting."); return; }
                foreach (var i in groups.PendingGroupInvites)
                    Print($"  {i.Name} [{i.GroupId[..8]}] — invited by {i.InviterName}, {i.Members} member(s)   (group accept|decline {i.GroupId[..8]})");
                return;

            case "accept" when parts.Length >= 3:
            {
                var invite = FindInvite(groups, parts[2]);
                var rec = await groups.AcceptInvitationAsync(invite.GroupId);
                Print($"You joined '{rec.Name}' ({rec.GroupId[..8]}).");
                return;
            }

            case "decline" when parts.Length >= 3:
            {
                var invite = FindInvite(groups, parts[2]);
                groups.DeclineInvitation(invite.GroupId);
                Print($"Invitation to '{invite.Name}' declined.");
                return;
            }

            case "list":
                if (groups.Store.All.Count == 0) { Print("No groups."); return; }
                foreach (var g in groups.Store.All)
                {
                    var state = groups.CurrentState(g);
                    Print($"  {g.Name} [{g.GroupId[..8]}] epoch {state.Epoch}, {state.Members.Count} member(s){(groups.IsModerator(g) ? ", moderator" : "")}"
                          + $"{(g.Active ? "" : " (left/excluded)")}{(groups.HasCurrentKey(g) ? "" : " — waiting for the group key")}");
                }
                return;

            case "members" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var state = groups.CurrentState(rec);
                foreach (var m in groups.Members(rec))   // nicknames as members chose to display them
                    Print($"  {m.Nickname} [{m.DiscoveryId[..8]}]{(groups.MemberDescription(rec, m.DiscoveryId) is { } about ? $" — {about}" : "")}{(state.Moderators.Contains(m.DiscoveryId) ? " moderator" : "")}{(m.DiscoveryId == state.CreatedBy ? " creator" : "")}{(m.DiscoveryId == me ? " (you)" : "")}");
                return;
            }

            case "add" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var c = _client.RequireContact(parts[3]);
                await groups.AddMemberAsync(rec, FreeSpeechClient.ToGroupMember(c));
                Print($"{c.Nickname} added to '{rec.Name}' (epoch {groups.CurrentEpoch(rec)}, key rotated).");
                return;
            }

            case "remove" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var m = _client.ResolveGroupMember(rec, parts[3]);
                await groups.RemoveMemberAsync(rec, m.DiscoveryId);
                Print($"{m.Nickname} removed from '{rec.Name}' (epoch {groups.CurrentEpoch(rec)}, key rotated: they cannot read what follows).");
                return;
            }

            case "promote" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var m = _client.ResolveGroupMember(rec, parts[3]);
                await groups.PromoteAsync(rec, m.DiscoveryId);
                Print($"{m.Nickname} is now a moderator of '{rec.Name}'.");
                return;
            }

            case "demote" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var m = _client.ResolveGroupMember(rec, parts[3]);
                await groups.DemoteAsync(rec, m.DiscoveryId);
                Print($"{m.Nickname} is no longer a moderator of '{rec.Name}' (still a member).");
                return;
            }

            case "transfer" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var m = _client.ResolveGroupMember(rec, parts[3]);
                await groups.TransferCreatorAsync(rec, m.DiscoveryId);
                Print($"{m.Nickname} is now the creator of '{rec.Name}' (you stay a moderator).");
                return;
            }

            case "rename" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                await groups.SetProfileAsync(rec, string.Join(' ', parts[3..]), photo: null);
                Print($"Group renamed '{rec.Name}' (members notified; no key rotation).");
                return;
            }

            case "photo" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var arg = string.Join(' ', parts[3..]);
                await _client.SetGroupPhotoAsync(rec, arg.Equals("clear", StringComparison.OrdinalIgnoreCase) ? null : arg);
                Print($"Group picture updated for '{rec.Name}'.");
                return;
            }

            case "about" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var arg = string.Join(' ', parts[3..]);
                if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase)) await groups.SetProfileAsync(rec, name: null, photo: null, clearDescription: true);
                else await groups.SetProfileAsync(rec, name: null, photo: null, description: arg);
                Print($"Description of '{rec.Name}' updated (members notified; no key rotation).");
                return;
            }

            case "info" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var state = groups.CurrentState(rec);
                Print($"  Name    : {rec.Name} [{rec.GroupId[..8]}]");
                Print($"  Picture : {PhotoSize(groups.GroupPhoto(rec))}");
                Print($"  About   : {groups.GroupDescription(rec) ?? "(none)"}");
                Print($"  Creator : {groups.Members(rec).FirstOrDefault(m => m.DiscoveryId == state.CreatedBy)?.Nickname ?? state.CreatedBy[..8]}");
                Print($"  Epoch   : {state.Epoch}, {state.Members.Count} member(s), {groups.Bans(rec).Count} blacklisted");
                return;
            }

            case "forget" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                groups.Forget(rec);
                Print($"Local data of '{rec.Name}' deleted.");
                return;
            }

            case "leave" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                await groups.LeaveAsync(rec);
                Print($"You left '{rec.Name}'.");
                return;
            }

            case "send" when parts.Length >= 4:
                PrintSent(await _client.SendAsync(FreeSpeechClient.GroupConversation(_client.ResolveGroup(parts[2])), string.Join(' ', parts[3..])));
                return;

            case "delete" or "retract" or "edit" or "dropfile" or "retractfile" or "react" when parts.Length >= 4:
                await HandleChangeAsync(FreeSpeechClient.GroupConversation(_client.ResolveGroup(parts[2])), sub, parts[3..], "group " + ChangeUsage(sub, "<g>"));
                return;

            case "reply" when parts.Length >= 5 && int.TryParse(parts[3].TrimStart('#'), out var replyIndex):
                PrintSent(await _client.ReplyAsync(FreeSpeechClient.GroupConversation(_client.ResolveGroup(parts[2])), replyIndex, string.Join(' ', parts[4..])), $"#{replyIndex}");
                return;

            case "upload" when parts.Length >= 4:
                PrintSent(await _client.UploadAsync(FreeSpeechClient.GroupConversation(_client.ResolveGroup(parts[2])), string.Join(' ', parts[3..])));
                return;

            case "download" when parts.Length >= 4 && int.TryParse(parts[3].TrimStart('#'), out var dlIndex):
                Print(await _client.DownloadAsync(FreeSpeechClient.GroupConversation(_client.ResolveGroup(parts[2])), dlIndex));
                return;

            case "history" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var members = groups.Members(rec);
                ShowHistory(FreeSpeechClient.GroupConversation(rec), $"group history {parts[2]}", parts[3..], l =>
                {
                    var from = l.Entry.From ?? "?";
                    var who = from == me ? "me" : members.FirstOrDefault(m => m.DiscoveryId == from)?.Nickname ?? from[..Math.Min(8, from.Length)];
                    return $"  #{l.Index} [{l.Entry.At:u}] {who}: {Shown(l.Entry)}{ReplySuffix(l.ReplyIndex)}";
                }, "No messages in this group yet.");
                return;
            }
            case "ban" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var m = _client.ResolveGroupMember(rec, parts[3]);
                await groups.BanAsync(rec, m.DiscoveryId);
                Print($"{m.Nickname} blacklisted in '{rec.Name}': their messages are no longer shown or relayed (still a member — use 'group remove' to rotate the key).");
                return;
            }

            case "unban" when parts.Length >= 4:
            {
                var rec = _client.ResolveGroup(parts[2]);
                string id;
                try { id = _client.ResolveGroupMember(rec, parts[3]).DiscoveryId; } catch (ClientException) { id = parts[3]; }   // a banned id may no longer be a member
                await groups.UnbanAsync(rec, id);
                Print("Blacklist entry lifted.");
                return;
            }

            case "bans" when parts.Length >= 3:
            {
                var rec = _client.ResolveGroup(parts[2]);
                var bans = groups.Bans(rec);
                if (bans.Count == 0) { Print("Blacklist empty."); return; }
                var members = groups.Members(rec);
                foreach (var id in bans) Print($"  {members.FirstOrDefault(x => x.DiscoveryId == id)?.Nickname ?? "(not a member)"} [{id[..8]}]");
                return;
            }

            case "sync":
                if (parts.Length >= 3) await groups.SyncAsync(_client.ResolveGroup(parts[2]));
                else await groups.SyncAllAsync();
                Print("Sync requested from the online members.");
                return;

            default:
                PrintError("Usage: group create <name> [contact...] | invites | accept|decline <g> | list | members <g> | add|remove|promote <g> <x> | leave <g> | send <g> <text> | upload <g> <path> | download <g> <#> | ban|unban <g> <x> | bans <g> | history <g> | sync [g]");
                return;
        }
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private static void Print(string msg) => System.Console.WriteLine(msg);
    private static void PrintError(string msg) => System.Console.WriteLine($"[!] {msg}");

    /// <summary>Prints an async notification without corrupting the current input line.</summary>
    private void PrintNotification(string msg)
    {
        System.Console.WriteLine();
        System.Console.WriteLine(msg);
        System.Console.Write($"[{_profileName}]> ");
    }

    // ------------------------------------------------------------------
    // Identity creation
    // ------------------------------------------------------------------

    private void CreateIdentity()
    {
        if (_client.NeedsPhrases) Print("This profile is locked on this machine. Enter your 3 recovery phrases to unlock it.\n");
        else
        {
            Print("No identity found for this profile. Let's create one.");
            Print("Enter 3 different recovery phrases (>= 75 characters each).");
            Print("There is no recovery if you lose them — write them down.\n");
        }

        while (true)
        {
            var p1 = ReadPhrase("Phrase 1: ");
            var p2 = ReadPhrase("Phrase 2: ");
            var p3 = ReadPhrase("Phrase 3: ");

            var unlocking = _client.NeedsPhrases;
            var nickname = _profileName;
            if (!unlocking)
            {
                System.Console.Write($"Nickname [{_profileName}]: ");
                nickname = ConsoleLine.Read()?.Trim();
                if (string.IsNullOrEmpty(nickname)) nickname = _profileName;
            }

            try
            {
                var identity = _client.CreateIdentity(p1, p2, p3, nickname);
                Print($"Identity {(unlocking ? "unlocked" : "created")}: {identity.DiscoveryId}");
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or ClientException)
            {
                PrintError($"{ex.Message} Let's try again.\n");
            }
        }
    }

    private static string ReadPhrase(string prompt)
    {
        while (true)
        {
            System.Console.Write(prompt);
            var rawLine = ConsoleLine.Read();
            if (rawLine == null)
            {
                // Stdin closed (EOF) — avoid spinning forever on a null read.
                System.Console.WriteLine();
                Environment.Exit(0);
            }
            var phrase = rawLine.Trim();
            if (FreeSpeechClient.IsPhraseValid(phrase)) return phrase;
            System.Console.WriteLine($"  Too short ({phrase.Length} chars, need >= 75). Try again.");
        }
    }

    private static void PrintBanner()
    {
        System.Console.WriteLine("=== FreeSpeech Console (protocol smoke-test client) ===");
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine("""

  show                    Identity info
  log [level]            Diagnostic trace of the client (off|error|warning|info|debug|trace); also --log / --log-file at start
  send <to> <text>       Relay a message live (fails if recipient offline)
  online <id>            Check presence via the relay
  wait <to>              Set a WAITING flag for <to>
  ready <to> <seconds>   Set a READY flag, rendezvous in N seconds
  flags                  Poll flags addressed to me
  clear <from> <to>      Remove a flag
  history <name|id> [last [n] | before <#> [n] | after <#> [n] | around <#> [n]]   Conversation with a contact (numbered); a window of it like a phone screen
  upload <name|id> <path>  Offer a file (recipient downloads on demand, chunked+encrypted)
  download <name|id> <#> Download the file offered in history message #<#> (resumes automatically)
  forward <from> <#> <to>  Re-send message #<#> of a contact/group conversation to a contact or a group (a file is re-offered without duplicating it)
  files                  List sent/received files and download progress
  edit <name|id> <#> <text>   Edit one of your own messages, for everyone
  delete <name|id> <#>   Delete message #<#> on this device only (a file is deleted too)
  retract <name|id> <#>  Delete one of your own messages for everyone (a "message deleted" line stays)
  dropfile <name|id> <#> Delete the file of message #<#> from this device; the line stays
  retractfile <name|id> <#>  Remove the file of one of your own messages for everyone
  group edit|delete|retract|dropfile|retractfile <g> <#> [text]   The same five, in a group
  group create <name> [contact...]   Create a group (you are its moderator); members get an invitation
  group list | members <g> | history <g> [last|before|after|around ...]   Groups you are in, their members, their messages (same windows as history)
  group add|remove|promote <g> <x>   Change the composition (moderators only; every change rotates the key)
  group send <g> <text>  Message every member (offline ones catch up from the others)
  group upload <g> <path> | download <g> <#>   Share a file with the group / fetch it from any member holding it
  group ban|unban <g> <x> | bans <g>   Blacklist (moderators; only the creator lifts it): hidden and not relayed, no key rotation
  group rename <g> <name> | photo <g> <path>|clear | info <g>   Creator: change the group's name/picture (no key rotation)
  profile show | name <nickname> | about <text>|clear | photo <path>|clear   My name, description and picture, sent to contacts and groups
  card <contact>         Everything known about a contact (name, about, picture, servers)
  group about <g> <text>|clear   Creator: describe the group (no key rotation)
  group reply <g> <#> <text>  Answer message #<#> of the group (see 'group history')
  group demote <g> <x> | forget <g>   Creator: take a moderator role away / delete the local copy of a group you left
  group transfer <g> <x>  Hand the creator role to another member (you stay a moderator)
  group leave <g> | sync [g]   Leave a group (a creator hands the role over) / ask the online members what you missed
  reply <name|id> <#> <text>  Reply to message #<#> from 'history' (any past message, not just the last)
  alias <name> <id>      Remember a discovery_id under a short name
  export [manual|qr]     Generate a ContactCard to share out-of-band (default: manual)
  import <base64>        Import a ContactCard received out-of-band
  requests               List manual contact requests awaiting your decision
  confirm <name|id>      Accept a pending manual contact request
  outbox                 List messages queued for a currently-offline recipient
  contacts               List confirmed contacts and known aliases
  backup [path] [none|6m|all]  Encrypted backup: contacts, aliases, history + attachment files (default: last 6 months; none = no files; all = everything)
  restore <path>          Merge a backup into this profile (same identity only); left-out received files stay re-downloadable with download
  server list            Show my servers (advertised to contacts, registered on all simultaneously)
  server add <url> [prio] [fs-inv-...]  Add a server (private or public; the token is the administrator's invitation), notify contacts via identity_update
  server request <url> [note]  Ask the administrator of a private server to allow you (sends your name and the note)
  server remove <url>    Remove a server, notify contacts
  raw on [full]|off | send [url] <json> | http [url] <METHOD> <path> [json] | example   Show every wire frame; send frames as is (protocol debugging)
  chats                  Conversation list (contacts and groups, unread counts)
  search <text> [in <contact|group>]   Search messages
  read <contact|group>   Mark as read (a contact gets a read receipt unless 'settings receipts off')
  typing <contact|group> Tell the conversation that you are typing
  settings [receipts|typing|auto-accept-migration|auto-exclude-migrated on|off]   Privacy and migration switches
  pin|unpin <g> <#> | pins <g>   Pin / release a group message (moderators); list the pinned ones
  contact remove <x> | rename <x> <private name>|clear   Remove a contact (conversation archived) / name only you see
  archive list | delete <x>   Contacts removed and groups left, readable until deleted for good
  filearchive preview|create <path> [months <n>] [with <x>] [kind image|video|audio|other] [min <MB>] | restore <path>   Move attachment files into an encrypted archive file / bring them back
  sync request | list | approve <id> <code> | reject <id>   Mirror another device of this identity
  migrate send | switch | list | accept <id> | decline <id>   Move to a new identity / follow a contact who moved
  wipe check | enable | send   Erase the other devices of this identity (asks the 3 phrases)
  help                   This help
  quit                   Exit
""");
    }
}
