/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.History;

namespace FreeSpeech.Client;

public enum MessageChangeKind { Edited, Retracted, FileRemoved, Reacted }

/// <summary>An earlier message was changed by its author: edited, deleted for everyone, or its file removed.</summary>
/// <param name="HistoryIndex">Position of the message in the conversation, or 0 if it is not displayed here.</param>
public sealed record MessageChange(Conversation Conversation, string MessageId, int HistoryIndex, string AuthorId, string AuthorName, MessageChangeKind Kind, string? NewText);

/// <summary>Payload of the three change Letters. Only the author of <see cref="Target"/> may send one (checked by every receiver).</summary>
internal sealed class MessageOp
{
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    /// <summary>Group only, on a delete: the seq the author gave the message, so peers can account for the gap it leaves in the catch-up log.</summary>
    [JsonPropertyName("seq")] public long Seq { get; set; }
}

// Edit, delete (for me / for everyone) and file removal — in 1-to-1 and in groups.
public sealed partial class FreeSpeechClient
{
    private const string TypeEdit = "message_edit";
    private const string TypeRetract = "message_retract";
    private const string TypeFileRetract = "attachment_retract";

    /// <summary>Raised when an earlier message was edited, deleted for everyone, or had its file removed by its author.</summary>
    public event Action<MessageChange>? MessageChanged;

    /// <summary>message_id is "{author}:{ts}".</summary>
    private static string AuthorOf(string messageId)
    {
        var i = messageId.LastIndexOf(':');
        return i <= 0 ? "" : messageId[..i];
    }

    private static long TsOf(string messageId) =>
        long.TryParse(messageId[(messageId.LastIndexOf(':') + 1)..], out var ts) ? ts : 0;

    private bool IsFileMessage(HistoryEntry e) => _attachments.Store.Find(e.Id) != null;

    private HistoryEntry OwnMessage(Conversation c, int index, string what)
    {
        var e = EntryAt(c, index);
        if (AuthorOf(e.Id) != _identity.DiscoveryId) throw new ClientException($"You can only {what} your own messages.", "msg_own_only");
        return e;
    }

    // ------------------------------------------------------------------
    // For me only
    // ------------------------------------------------------------------

    /// <summary>
    /// Deletes message #<paramref name="index"/> on this device only (a file message loses its file too, unless another
    /// conversation holds the same content). A message of mine still waiting in the outbox is not sent.
    /// In a group my sealed copy stays in the catch-up log so the others can still get the message from me.
    /// </summary>
    public void DeleteMessage(Conversation c, int index)
    {
        var e = EntryAt(c, index);
        if (IsFileMessage(e)) _attachments.RemoveContent(e.Id);
        if (c.Group == null) _pendingMessages.Remove(e.Id);
        _history.DeleteForMe(c.HistoryKey, e.Id);
    }

    /// <summary>Deletes the file of message #<paramref name="index"/> from this device; the line, with its name, stays. It can no longer be downloaded nor served from here.</summary>
    public void DeleteFile(Conversation c, int index)
    {
        var e = EntryAt(c, index);
        if (!IsFileMessage(e)) throw new ClientException($"#{index} is not a file.", "msg_not_file", index);
        _attachments.RemoveContent(e.Id);
        _history.FileDelete(c.HistoryKey, e.Id);
    }

    // ------------------------------------------------------------------
    // For everyone (author only)
    // ------------------------------------------------------------------

    private async Task<SentMessage> SendChangeAsync(Conversation c, string type, MessageOp op, long? ts, string label)
    {
        var payload = JsonSerializer.Serialize(op);
        if (c.Group != null)
        {
            var r = await _groups.SendAsync(c.Group, payload, type, ts);
            return new SentMessage(c, label, r.Ts, false, SendStatus.Delivered, r.Delivered, r.Recipients);
        }
        return (await SendLetterAsync(c, type, payload, null, ts, historyText: null)) with { Text = label };
    }

    /// <summary>
    /// Edits my own text message #<paramref name="index"/> for everyone. The id stays, so replies to it still resolve; it is
    /// shown as edited. A message not delivered yet is simply changed in the outbox. Only the author may edit.
    /// </summary>
    public async Task<SentMessage> EditMessageAsync(Conversation c, int index, string text)
    {
        var e = OwnMessage(c, index, "edit");
        text = text.Trim();
        if (text.Length == 0) throw new ClientException("The new text is empty.", "msg_text_empty");
        CheckTextLength(text);
        if (e.State != MessageState.Normal) throw new ClientException($"#{index} was deleted.", "msg_deleted", index);
        if (IsFileMessage(e)) throw new ClientException("Only a text message can be edited.", "msg_only_text_edit");

        var editTs = NextTs();
        SentMessage sent;
        if (c.Group == null && _pendingMessages.SetText(e.Id, text))
            sent = new SentMessage(c, text, editTs, false, SendStatus.QueuedRecipientOffline, 0, 1);   // not sent yet: what goes out is the new text
        else
            sent = await SendChangeAsync(c, TypeEdit, new MessageOp { Target = e.Id, Text = text }, editTs, text);

        _history.Edit(c.HistoryKey, e.Id, text, editTs);
        return sent;
    }

    /// <summary>
    /// Deletes my own message #<paramref name="index"/> for everyone (a file message loses its file). Every device erases the
    /// content and keeps a "message deleted" line; in a group the copy also leaves the catch-up logs. Best effort: a modified
    /// client can ignore it. Only the author may do it. A message not delivered yet is simply not sent.
    /// </summary>
    public async Task<SentMessage> RetractMessageAsync(Conversation c, int index)
    {
        var e = OwnMessage(c, index, "delete for everyone");
        if (e.State is MessageState.Retracted) throw new ClientException($"#{index} is already deleted.", "msg_already_deleted", index);

        SentMessage sent;
        if (c.Group == null && _pendingMessages.Unsent.Any(p => p.Id == e.Id && p.Type == "message"))
        {
            _pendingMessages.Remove(e.Id);
            sent = new SentMessage(c, "(not sent yet: removed from the outbox)", e.Ts, false, SendStatus.QueuedRecipientOffline, 0, 1);
        }
        else
        {
            if (c.Group == null) _pendingMessages.Remove(e.Id);   // delivered but not confirmed yet: it is not sent again, the deletion goes instead
            var seq = c.Group == null ? 0 : _groups.SeqOf(c.Group.GroupId, _identity.DiscoveryId, TsOf(e.Id));
            sent = await SendChangeAsync(c, TypeRetract, new MessageOp { Target = e.Id, Seq = seq }, null, "(message deleted for everyone)");
        }

        ApplyRetractLocally(c, e, filesToo: true);
        return sent;
    }

    /// <summary>Removes the file of my own message #<paramref name="index"/> for everyone; the line stays as "file removed". Only the author may do it; best effort like <see cref="RetractMessageAsync"/>.</summary>
    public async Task<SentMessage> RetractFileAsync(Conversation c, int index)
    {
        var e = OwnMessage(c, index, "remove the file of");
        if (!IsFileMessage(e)) throw new ClientException($"#{index} is not a file.", "msg_not_file", index);

        var seq = c.Group == null ? 0 : _groups.SeqOf(c.Group.GroupId, _identity.DiscoveryId, TsOf(e.Id));
        var sent = await SendChangeAsync(c, TypeFileRetract, new MessageOp { Target = e.Id, Seq = seq }, null, "(file removed for everyone)");

        _attachments.RemoveContent(e.Id);
        _history.FileRetract(c.HistoryKey, e.Id);
        if (c.Group != null) _groups.Redact(c.Group.GroupId, _identity.DiscoveryId, TsOf(e.Id), seq);
        return sent;
    }

    private void ApplyRetractLocally(Conversation c, HistoryEntry e, bool filesToo)
    {
        _history.Retract(c.HistoryKey, e.Id);
        if (filesToo && IsFileMessage(e)) _attachments.RemoveContent(e.Id);
        if (c.Group != null) _groups.Redact(c.Group.GroupId, _identity.DiscoveryId, TsOf(e.Id), _groups.SeqOf(c.Group.GroupId, _identity.DiscoveryId, TsOf(e.Id)));
    }

    // ------------------------------------------------------------------
    // Receiving a change
    // ------------------------------------------------------------------

    /// <summary>
    /// A change Letter arrived (1-to-1, or a group message of that type). It is honoured only if the sender wrote the
    /// target message. The target may not be stored yet (catch-up, several servers): the store parks the change and
    /// applies it the moment the message arrives.
    /// </summary>
    private void ApplyRemoteChange(Conversation c, string from, string type, string payload, long ts)
    {
        MessageOp? op;
        try { op = JsonSerializer.Deserialize<MessageOp>(payload); }
        catch (JsonException) { op = null; }

        if (op == null || op.Target.Length == 0) { RaiseNotice($"[!] Malformed '{type}' from {from} — ignored."); return; }
        if (AuthorOf(op.Target) != from) { RaiseNotice($"[!] '{type}' from {from} targets a message that is not theirs — ignored."); return; }

        var key = c.HistoryKey;
        bool applied;
        MessageChangeKind kind;
        string? newText = null;
        switch (type)
        {
            case TypeEdit:
                applied = _history.Edit(key, op.Target, op.Text ?? "", ts);
                kind = MessageChangeKind.Edited;
                newText = op.Text;
                break;
            case TypeRetract:
                applied = _history.Retract(key, op.Target);
                _attachments.RemoveContent(op.Target);
                if (c.Group != null) _groups.Redact(c.Group.GroupId, from, TsOf(op.Target), op.Seq);
                kind = MessageChangeKind.Retracted;
                break;
            default:
                applied = _history.FileRetract(key, op.Target);
                _attachments.RemoveContent(op.Target);
                if (c.Group != null) _groups.Redact(c.Group.GroupId, from, TsOf(op.Target), op.Seq);
                kind = MessageChangeKind.FileRemoved;
                break;
        }

        if (!applied) return;
        var name = c.Group != null ? _groups.Members(c.Group).FirstOrDefault(m => m.DiscoveryId == from)?.Nickname ?? from[..8]
            : _confirmedContacts.FindById(from)?.DisplayName ?? from;
        MessageChanged?.Invoke(new MessageChange(c, op.Target, _history.IndexOf(key, op.Target), from, name, kind, newText));
    }
}
