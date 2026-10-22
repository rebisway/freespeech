/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;

namespace FreeSpeech.Client.Backup;

/// <summary>
/// Local backup content (protocol §2) — confirmed contacts, manual
/// aliases and conversation history. The identity itself is deliberately
/// not included: it's fully re-derivable from the 3 recovery phrases on any
/// device, so shipping the private keys in a file would only add risk for
/// no benefit. Groups will join this once their structure is settled (§7).
/// </summary>
public class BackupPayload
{
    [JsonPropertyName("version")] public string Version { get; set; } = "2";
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("contacts")] public List<Contact> Contacts { get; set; } = [];
    [JsonPropertyName("aliases")] public Dictionary<string, string> Aliases { get; set; } = [];
    [JsonPropertyName("history")] public Dictionary<string, List<HistoryEntry>> History { get; set; } = [];

    /// <summary>Attachment records (metadata only). Files travel as zip entries named by <see cref="BackupAttachment.FileEntry"/>.</summary>
    [JsonPropertyName("attachments")] public List<BackupAttachment> Attachments { get; set; } = [];

    /// <summary>Groups with their signed states, group keys and message log (§7). Sensitive — that is why the whole file is encrypted with a key only this identity can derive.</summary>
    [JsonPropertyName("groups")] public List<Groups.GroupRecord> Groups { get; set; } = [];

    /// <summary>My own name, description and picture (the keys are re-derived from the phrases, the profile is not): a restored identity looks like the original one.</summary>
    [JsonPropertyName("profile")] public BackupProfile? Profile { get; set; }

    /// <summary>My own servers, in priority order: a restored identity tries them before keeping the servers it was given when it was recreated.</summary>
    [JsonPropertyName("servers")] public List<string> Servers { get; set; } = [];

    /// <summary>The revocation keys pinned for my contacts and group peers (§12, identity migration): a restored phone can still check a migration notice.</summary>
    [JsonPropertyName("revocation_pins")] public List<BackupPin> RevocationPins { get; set; } = [];

    /// <summary>Where each conversation was read up to (mine and the peer's), so a restored conversation is not shown as unread. Null in a backup that carries none.</summary>
    [JsonPropertyName("read_marks")] public List<BackupReadMark>? ReadMarks { get; set; }
}

public class BackupReadMark
{
    [JsonPropertyName("conversation")] public required string Conversation { get; set; }
    [JsonPropertyName("side")] public int Side { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("id")] public required string Id { get; set; }
}

/// <summary>A block of history lines of one conversation: a zip entry <c>history/N</c> of a backup.</summary>
public class BackupHistoryBlock
{
    [JsonPropertyName("conversation")] public required string Conversation { get; set; }
    [JsonPropertyName("entries")] public List<HistoryEntry> Entries { get; set; } = [];
}

public class BackupPin
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("key")] public required string Key { get; set; }
}

public class BackupProfile
{
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Base64 picture, as held by the identity.</summary>
    [JsonPropertyName("photo")] public string? Photo { get; set; }
}

/// <summary><c>FileEntry</c> null = the file itself was left out (mode/age): a received one comes back as "offered" (re-downloadable with <c>download</c>), a sent one is dropped.</summary>
public class BackupAttachment
{
    [JsonPropertyName("record")] public required AttachmentRecord Record { get; set; }
    [JsonPropertyName("file_entry")] public string? FileEntry { get; set; }
}

