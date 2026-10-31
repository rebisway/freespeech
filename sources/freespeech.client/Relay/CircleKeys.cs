/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using FreeSpeech.Client.Contacts;

namespace FreeSpeech.Client.Relay;

/// <summary>
/// How a restricted server is named in a card without its address (protocol §8 "Restricted servers"): the members of the server hold a key their
/// administrator gave them, and the card carries <c>HMAC(key, owner)</c>. A member who has the same server finds the same identifier.
/// </summary>
public static class CircleKeys
{
    /// <summary>The address written in a card entry that has an identifier: it never resolves, so a client that does not know identifiers fails to reach it quietly.</summary>
    public const string Placeholder = "https://circle.invalid";

    /// <summary>How long the key before a renewal still serves to recognise the cards already sent.</summary>
    public static readonly TimeSpan KeepOldKey = TimeSpan.FromDays(30);

    public static string Tag(string circleKey, string ownerId) =>
        Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(circleKey), Encoding.UTF8.GetBytes("freespeech-circle|" + ownerId)));

    /// <summary>The address of my own restricted server that this identifier names for this owner, or null when none of my servers does.</summary>
    public static string? Match(string ownerId, string circle, IEnumerable<ServerEntry> mine)
    {
        var now = DateTime.UtcNow;
        foreach (var server in mine)
        {
            if (!string.IsNullOrEmpty(server.CircleKey) && TagIs(server.CircleKey, ownerId, circle)) return RelayPool.Normalize(server.Url);
            if (!string.IsNullOrEmpty(server.OldCircleKey) && server.OldCircleKeyUntil > now && TagIs(server.OldCircleKey, ownerId, circle)) return RelayPool.Normalize(server.Url);
        }
        return null;
    }

    private static bool TagIs(string key, string ownerId, string circle)
    {
        try { return Tag(key, ownerId) == circle; }
        catch (FormatException) { return false; }
    }

    /// <summary>Remembers a key the server gave; the previous one stays usable for a while. True when something changed.</summary>
    public static bool Learn(ServerEntry server, string key)
    {
        if (server.CircleKey == key) return false;
        if (server.CircleKey != null) { server.OldCircleKey = server.CircleKey; server.OldCircleKeyUntil = DateTime.UtcNow + KeepOldKey; }
        server.CircleKey = key;
        return true;
    }
}
