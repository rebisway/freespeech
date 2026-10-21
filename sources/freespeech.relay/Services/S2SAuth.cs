/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Net;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Rules for the address of a sibling (protocol §10). S2S calls themselves are signed with the servers' keys
/// (<see cref="FreeSpeech.Ops.PeerSigning"/>).
/// </summary>
public static class S2SAuth
{
    /// <summary>
    /// A sibling must be reachable over https (TLS protects the metadata that S2S carries: who talks to whom) and must not point at this
    /// machine or a private network (SSRF). Both are relaxed by Server:AllowInsecureSiblings, for local development and tests only.
    /// </summary>
    public static bool IsAcceptableUrl(string url, bool allowInsecure, out string error)
    {
        error = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            error = "url must be an absolute http(s) URL";
            return false;
        }
        if (allowInsecure) return true;

        if (uri.Scheme != "https") { error = "sibling url must use https"; return false; }
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        { error = "sibling url must not be a loopback address"; return false; }
        if (IPAddress.TryParse(uri.Host, out var ip) && IsPrivate(ip))
        { error = "sibling url must not be a private address"; return false; }
        return true;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
}

/// <summary>Fixed-window request quota per sibling, so one misbehaving or compromised peer cannot flood this server.</summary>
public class S2SRateLimiter
{
    private readonly ConcurrentDictionary<string, (long Window, int Count)> _counters = new();

    public bool Allow(string keyId, int perMinute)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var entry = _counters.AddOrUpdate(keyId, (window, 1), (_, cur) => cur.Window == window ? (window, cur.Count + 1) : (window, 1));
        return entry.Count <= perMinute;
    }
}
