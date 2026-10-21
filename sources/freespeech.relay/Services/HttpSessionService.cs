/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Proves that an HTTP call (/api/flags) comes from the connection that registered an identity (protocol §11).
/// The relay hands a random token in its "registered" answer; the client repeats it in "X-Flag-Token". Without it,
/// knowing a discovery id that is online would be enough to read or set that identity's flags. A token lives only
/// as long as its WebSocket connection.
/// </summary>
public interface IHttpSessionService
{
    string Issue(string discoveryId, string connectionId);
    void Revoke(string connectionId);
    bool IsValid(string discoveryId, string? token);
}

public class HttpSessionService : IHttpSessionService
{
    private readonly ConcurrentDictionary<string, (string DiscoveryId, string ConnectionId)> _byToken = new();

    public string Issue(string discoveryId, string connectionId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _byToken[token] = (discoveryId.ToLowerInvariant(), connectionId);
        return token;
    }

    public void Revoke(string connectionId)
    {
        foreach (var kv in _byToken)
            if (kv.Value.ConnectionId == connectionId) _byToken.TryRemove(kv.Key, out _);
    }

    public bool IsValid(string discoveryId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;
        // Constant-time on the lookup is unnecessary: the token is 192 random bits, not guessable by timing.
        return _byToken.TryGetValue(token, out var s) && s.DiscoveryId == discoveryId.ToLowerInvariant();
    }
}
