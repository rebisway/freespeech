/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeSpeech.Client;

// "Is typing" indicator. A throw-away letter: never stored in the history, never queued in the outbox, never sets a WAITING flag (so it
// can never wake a phone). If the contact is not connected it is simply lost, which is the point.
public sealed partial class FreeSpeechClient
{
    private const string TypeTyping = "typing";   // listed in PROTOCOL §5 "Types de Letter"

    /// <summary>One signal at most this often while the user keeps typing.</summary>
    public static readonly TimeSpan TypingEvery = TimeSpan.FromSeconds(4);

    /// <summary>A group larger than this gets no indicator: it would cost one letter per member at every signal.</summary>
    public const int MaxTypingGroupMembers = 8;

    /// <summary>
    /// False = I neither send the indicator nor show anybody's: it is reciprocal, like the read receipts. A signal that arrives while
    /// it is off is dropped.
    /// </summary>
    public bool SendTypingIndicator { get; set; } = true;

    /// <summary>A contact (or a member of a group) is typing in this conversation: the conversation and who it is.</summary>
    public event Action<Conversation, string>? TypingReceived;

    private readonly Dictionary<string, DateTime> _typingSent = [];
    private readonly object _typingLock = new();

    private sealed class TypingOp
    {
        [JsonPropertyName("group")] public string? Group { get; set; }
    }

    /// <summary>The user is typing in <paramref name="c"/>: tells the other side, at most once every <see cref="TypingEvery"/>. Best effort, never throws.</summary>
    public async Task SendTypingAsync(Conversation c)
    {
        if (!SendTypingIndicator) return;
        lock (_typingLock)
        {
            var now = DateTime.UtcNow;
            if (_typingSent.TryGetValue(c.HistoryKey, out var last) && now - last < TypingEvery) return;
            _typingSent[c.HistoryKey] = now;
        }

        try
        {
            if (c.Group is { Active: true } g)
            {
                var others = _groups.Members(g).Where(m => m.DiscoveryId != _identity.DiscoveryId).ToList();
                if (others.Count == 0 || others.Count > MaxTypingGroupMembers) return;
                var payload = JsonSerializer.Serialize(new TypingOp { Group = g.GroupId });
                await Task.WhenAll(others.Select(m => SendLetterOnceAsync(m.DiscoveryId, TypeTyping, payload)));
            }
            else if (c.ContactId != null)
            {
                await SendLetterOnceAsync(c.ContactId, TypeTyping, JsonSerializer.Serialize(new TypingOp()));
            }
        }
        catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException or TaskCanceledException) { /* best effort */ }
    }

    private void HandleTyping(string from, string payload)
    {
        if (!SendTypingIndicator) return;
        TypingOp? op;
        try { op = JsonSerializer.Deserialize<TypingOp>(payload); }
        catch (JsonException) { return; }

        if (!string.IsNullOrEmpty(op?.Group))
        {
            // A member of a group I am in, nobody else: the signal names the group, the signature (§5) names the author.
            if (!_groups.IsMember(op.Group, from) || _groups.Store.Find(op.Group) is not { Active: true } rec) return;
            TypingReceived?.Invoke(GroupConversation(rec), from);
        }
        else if (_confirmedContacts.FindById(from) != null)
        {
            TypingReceived?.Invoke(new Conversation(from, from, null), from);
        }
    }
}