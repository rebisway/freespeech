/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// This server's own public/private status, its siblings (the relays both administrators accepted: <c>add relay</c> / <c>accept relay</c>),
/// the suggestions of siblings and the banned addresses (protocol §10). The status, the suggestions and the bans are saved to disk so
/// that admin changes survive a restart; the siblings come from the peers file. Never changed by client traffic or by simply receiving a
/// sibling suggestion (see <see cref="SuggestCandidate"/>).
/// </summary>
public interface ISiblingRegistry
{
    /// <summary>Only a public server performs or accepts S2S at all (protocol §10).</summary>
    bool IsPublic { get; }

    void SetPublic(bool isPublic);

    /// <summary>The siblings this server may talk to: accepted relay peers with an address, minus banned addresses.</summary>
    IReadOnlyList<Sibling> Whitelist { get; }
    IReadOnlyList<SiblingCandidate> Candidates { get; }
    IReadOnlyList<BlacklistEntry> Blacklist { get; }

    /// <summary>Records a suggested URL for admin review. No-op if already a sibling, already a candidate, or banned.</summary>
    void SuggestCandidate(string url, string? suggestedBy);

    bool DismissCandidate(string url);

    /// <summary>Bans a URL — also removes it from the candidate list if present. A banned address is never a sibling nor a push gateway.</summary>
    void Ban(string url, string? reason);

    bool Unban(string url);

    bool IsBlacklisted(string url);
}
