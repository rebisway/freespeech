/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Client.Archive;

/// <summary>
/// Which downloaded files to archive. Every criterion that is set must hold; none set = everything. A file shared by several
/// conversations (same content) is archived only when <b>all</b> the records that hold it match: the space is freed or not at all.
/// </summary>
public sealed class ArchiveSelection
{
    /// <summary>Only files of messages older than this date (UTC). The usual choice: a year ago.</summary>
    public DateTime? Before { get; set; }

    /// <summary>Only files of messages from this date on (UTC), with <see cref="To"/>: a stretch of time.</summary>
    public DateTime? From { get; set; }

    /// <summary>Only files of messages before this date (UTC, the day itself excluded).</summary>
    public DateTime? To { get; set; }

    /// <summary>Only the files of one conversation: a contact's identifier, or a group's.</summary>
    public string? Conversation { get; set; }

    /// <summary>Only one kind of file: "video/", "image/", "audio/"… (start of the media type).</summary>
    public string? MimePrefix { get; set; }

    /// <summary>Only the files that are neither pictures, videos nor voice or audio (documents, archives, anything else).</summary>
    public bool OtherKindsOnly { get; set; }

    /// <summary>Only files at least this big, in bytes.</summary>
    public long MinSize { get; set; }

    /// <summary>The usual starting point: files older than <paramref name="months"/> months.</summary>
    public static ArchiveSelection OlderThanMonths(int months, DateTime? nowUtc = null) => new() { Before = (nowUtc ?? DateTime.UtcNow).AddMonths(-Math.Max(0, months)) };
}

/// <summary>What an archive would take: files (a content counts once), the records that point at them, and the space they free.</summary>
public sealed record ArchivePreview(int Files, int Records, long Bytes);

/// <summary>What an archive took. <see cref="ArchiveBytes"/> is the size of the archive file; <see cref="FreedBytes"/> what left the device.</summary>
public sealed record ArchiveSummary(int Files, int Records, long FreedBytes, long ArchiveBytes);

/// <summary>What loading an archive brought back. <see cref="Skipped"/> = files whose content did not check out (damaged or forged).</summary>
public sealed record ArchiveRestoreSummary(int Files, int Records, int Skipped);

/// <summary>The index of an archive file (entry <c>archive.json</c>): one item per content, with the records that point at it.</summary>
public sealed class ArchiveManifest
{
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("items")] public List<ArchiveItem> Items { get; set; } = [];
}

public sealed class ArchiveItem
{
    /// <summary>Content identity (Merkle root): what a record is matched with, and what the file is checked against.</summary>
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>The zip entry holding the file (<c>files/N</c>).</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";

    /// <summary>The records that held this content (a content sent to two conversations has two), without their local path.</summary>
    [JsonPropertyName("records")] public List<AttachmentRecord> Records { get; set; } = [];
}
