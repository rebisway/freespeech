/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.History;

public enum MessageDirection
{
    Sent,
    Received,
}

/// <summary>
/// What became of a message after it was stored. <see cref="DeletedForMe"/> rows are kept only so a re-delivered
/// copy (group catch-up) is not stored again — every read hides them. The others stay visible as a line in the chat.
/// </summary>
public enum MessageState
{
    Normal = 0,
    /// <summary>I deleted it on this device: hidden everywhere, content erased.</summary>
    DeletedForMe = 1,
    /// <summary>The author deleted it for everyone: "message deleted" stays, content erased.</summary>
    Retracted = 2,
    /// <summary>I deleted the attached file from this device: the line and its name stay.</summary>
    FileDeleted = 3,
    /// <summary>The author removed the attached file for everyone: the line stays, content erased.</summary>
    FileRetracted = 4,
}

/// <summary>One line of a local conversation history with a given contact.</summary>
public class HistoryEntry
{
    /// <summary>
    /// message_id (protocol §5): "{senderDiscoveryId}:{ts}". Unique per
    /// sender+send, independent of any server clock — used to dedupe entries
    /// that can otherwise be recorded twice (e.g. the same push delivered to
    /// more than one of our own connections).
    /// </summary>
    [JsonPropertyName("id")] public required string Id { get; set; }

    [JsonPropertyName("direction")] public required MessageDirection Direction { get; set; }
    [JsonPropertyName("text")] public required string Text { get; set; }

    /// <summary>message_id of the entry this one replies to (protocol §5/§7), or null.</summary>
    [JsonPropertyName("reply_to")] public string? ReplyToId { get; set; }

    /// <summary>Author's discovery_id: set for group conversations (several senders); null in 1-to-1, where direction says it all.</summary>
    [JsonPropertyName("from")] public string? From { get; set; }

    /// <summary>Sender-local origin timestamp (UTC ticks) — used to sort history causally, not by arrival order.</summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }

    [JsonPropertyName("at")] public DateTime At { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("state")] public MessageState State { get; set; }

    /// <summary>Timestamp of the last edit by the author (the ts of the edit Letter), or null if never edited.</summary>
    [JsonPropertyName("edited_ts")] public long? EditedTs { get; set; }

    [JsonIgnore] public bool Edited => EditedTs != null;
}
