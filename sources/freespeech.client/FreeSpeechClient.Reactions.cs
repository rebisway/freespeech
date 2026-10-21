/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Globalization;
using System.Text.Json;
using FreeSpeech.Client.History;

namespace FreeSpeech.Client;

/// <summary>One person's emoji on a message.</summary>
public sealed record Reaction(string WhoId, string WhoName, string Emoji, bool Mine);

// Emoji reactions to a message or a file, in 1-to-1 and in groups. Unlike an edit, anyone in the conversation may react.
public sealed partial class FreeSpeechClient
{
    private const string TypeReaction = "message_react";
    private const int MaxEmojiTextElements = 4;   // one emoji can be several code points (flags, skin tones, families)

    /// <summary>
    /// Reacts to message #<paramref name="index"/> with <paramref name="emoji"/>. One reaction per person per message:
    /// a new emoji replaces the old one, the same emoji again (or an empty one) takes it back. Best effort, like every change.
    /// </summary>
    public async Task ReactAsync(Conversation c, int index, string emoji)
    {
        emoji = emoji.Trim();
        if (!ValidEmoji(emoji)) throw new ClientException("A reaction is a single emoji.", "msg_reaction_single");
        var e = EntryAt(c, index);
        if (e.State is MessageState.Retracted or MessageState.DeletedForMe) throw new ClientException($"#{index} was deleted.", "msg_deleted", index);

        var mine = _identity.DiscoveryId;
        var current = _history.Reactions(c.HistoryKey).GetValueOrDefault(e.Id)?.FirstOrDefault(r => r.Who == mine).Emoji;
        var next = current == emoji ? "" : emoji;   // tapping the same emoji again removes it

        var ts = NextTs();
        _history.SetReaction(c.HistoryKey, e.Id, mine, next, ts);
        await SendChangeAsync(c, TypeReaction, new MessageOp { Target = e.Id, Text = next }, ts, next.Length == 0 ? "(reaction removed)" : $"(reaction {next})");
    }

    /// <summary>The reactions of a conversation: message id → who reacted with what (oldest first).</summary>
    public IReadOnlyDictionary<string, List<Reaction>> GetReactions(Conversation c)
    {
        var mine = _identity.DiscoveryId;
        var result = new Dictionary<string, List<Reaction>>();
        foreach (var (target, list) in _history.Reactions(c.HistoryKey))
            result[target] = list.Select(r => new Reaction(r.Who, r.Who == mine ? "" : NameOf(c, r.Who), r.Emoji, r.Who == mine)).ToList();
        return result;
    }

    /// <summary>The reactions of some messages only (the lines a screen shows); messages without one are absent.</summary>
    public IReadOnlyDictionary<string, List<Reaction>> GetReactions(Conversation c, IReadOnlyCollection<string> messageIds)
    {
        var mine = _identity.DiscoveryId;
        var result = new Dictionary<string, List<Reaction>>();
        foreach (var (target, list) in _history.Reactions(c.HistoryKey, messageIds))
            result[target] = list.Select(r => new Reaction(r.Who, r.Who == mine ? "" : NameOf(c, r.Who), r.Emoji, r.Who == mine)).ToList();
        return result;
    }

    private string NameOf(Conversation c, string id) =>
        c.Group != null ? _groups.Members(c.Group).FirstOrDefault(m => m.DiscoveryId == id)?.Nickname ?? id[..8]
        : _confirmedContacts.FindById(id)?.DisplayName ?? id;

    private static bool ValidEmoji(string s)
    {
        if (s.Length == 0) return true;   // "take back"
        if (s.Length > 32) return false;
        var si = new StringInfo(s);
        return si.LengthInTextElements <= MaxEmojiTextElements && !s.Any(char.IsLetterOrDigit);   // not text: words would turn a reaction into a message
    }

    private void ApplyRemoteReaction(Conversation c, string from, string payload, long ts)
    {
        MessageOp? op;
        try { op = JsonSerializer.Deserialize<MessageOp>(payload); }
        catch (JsonException) { op = null; }
        if (op == null || op.Target.Length == 0 || !ValidEmoji(op.Text ?? "")) { RaiseNotice($"[!] Malformed reaction from {from} — ignored."); return; }

        if (!_history.SetReaction(c.HistoryKey, op.Target, from, op.Text ?? "", ts)) return;
        MessageChanged?.Invoke(new MessageChange(c, op.Target, _history.IndexOf(c.HistoryKey, op.Target), from, NameOf(c, from), MessageChangeKind.Reacted, op.Text));
    }
}