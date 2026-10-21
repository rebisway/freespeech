/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Relay.Models;

/// <summary>
/// A sibling relay (protocol §10): a relay both administrators accepted, reached at its address. Only public servers have siblings; a
/// private server never accepts or performs S2S at all.
/// </summary>
public class Sibling
{
    [JsonPropertyName("url")] public required string Url { get; set; }
}

/// <summary>
/// A sibling URL suggested by an already-trusted sibling (S2S
/// "siblings/sync") or by a client — never auto-promoted to
/// <see cref="Sibling"/>. Purely informational until an admin reviews it
/// and, if they agree, asks for the link (<c>add relay</c>). This is what
/// stops one compromised sibling (or a malicious client) from silently
/// expanding this server's trust boundary.
/// </summary>
public class SiblingCandidate
{
    [JsonPropertyName("url")] public required string Url { get; set; }
    [JsonPropertyName("suggested_by")] public string? SuggestedBy { get; set; }
    [JsonPropertyName("suggested_at")] public DateTime SuggestedAt { get; set; }
}

/// <summary>A URL an admin has explicitly banned — never accepted as a sibling or candidate again until unbanned.</summary>
public class BlacklistEntry
{
    [JsonPropertyName("url")] public required string Url { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("banned_at")] public DateTime BannedAt { get; set; }
}
