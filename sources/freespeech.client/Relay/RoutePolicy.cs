/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Contacts;

namespace FreeSpeech.Client.Relay;

/// <summary>What a user allows for a contact when the restricted servers they share cannot be reached (protocol §8 "Restricted servers").</summary>
public enum OpenFallbackMode
{
    /// <summary>Wait for the restricted server (default).</summary>
    Never = 0,
    /// <summary>Offer to send through an open server once, and wait for the user's yes.</summary>
    Ask = 1,
    /// <summary>Send through an open server after the delay, without asking.</summary>
    Auto = 2,
}

/// <summary>Where the app stands about the servers used to reach a contact.</summary>
public enum RouteState
{
    /// <summary>No restricted server in common: the contact's servers are used in the order of their card.</summary>
    Open,
    /// <summary>A restricted server is shared: only the shared ones are used.</summary>
    PrivateOnly,
    /// <summary>The shared restricted servers do not answer: messages wait.</summary>
    PrivateDown,
    /// <summary>Messages go through an open server because the restricted ones do not answer and both sides agreed.</summary>
    OpenFallback,
}

/// <summary>
/// Which servers are used to write to someone, and in which order. A server a card marks <c>restricted</c> is only used by those who are let in
/// on it; where one is shared with a contact, it is the only one used, unless both sides agreed to fall back on an open server while it does not answer.
/// Pure functions: nothing here touches the network.
/// </summary>
public static class RoutePolicy
{
    /// <summary>The servers of a card: the open ones first, then the restricted ones, each group in the owner's order of preference; the priorities are renumbered to say so.</summary>
    public static List<ServerEntry> CardServers(IEnumerable<ServerEntry> own) =>
        own.OrderBy(s => s.Restricted ? 1 : 0).ThenBy(s => s.Priority)
            .Select((s, i) => new ServerEntry { Url = s.Url, Priority = i, Restricted = s.Restricted }).ToList();

    /// <param name="Urls">The addresses to try, in order.</param>
    /// <param name="Shared">Those among them that are restricted servers I am let in on.</param>
    /// <param name="Strict">True when only the shared ones are used.</param>
    public sealed record Route(IReadOnlyList<string> Urls, IReadOnlyList<string> Shared, bool Strict);

    /// <param name="theirs">The servers of the card of the person written to.</param>
    /// <param name="iAmIn">Whether I am let in on this address, a server that restricts who signs up.</param>
    /// <param name="openAllowed">The open servers may be used although a restricted one is shared (the fallback is on).</param>
    /// <param name="ownOpenUrls">My own open servers, last resort: a public one may still reach them through the servers' links.</param>
    /// <param name="strictApplies">False for a group member who is not a contact: the restricted servers I am not let in on are left out, nothing more.</param>
    public static Route Choose(IEnumerable<ServerEntry>? theirs, Func<string, bool> iAmIn, bool openAllowed, IEnumerable<string> ownOpenUrls, bool strictApplies)
    {
        var ordered = (theirs ?? []).OrderBy(s => s.Priority).Select(s => (Url: RelayPool.Normalize(s.Url), s.Restricted)).DistinctBy(x => x.Url).ToList();
        var shared = ordered.Where(x => iAmIn(x.Url)).Select(x => x.Url).ToList();
        var open = ordered.Where(x => !x.Restricted && !iAmIn(x.Url)).Select(x => x.Url).ToList();   // a restricted server I am not let in on is never asked

        if (shared.Count > 0 && strictApplies && !openAllowed) return new Route(shared, shared, true);

        var urls = new List<string>(shared);
        urls.AddRange(open);
        urls.AddRange(ownOpenUrls);
        return new Route(urls.Distinct().ToList(), shared, false);
    }
}
