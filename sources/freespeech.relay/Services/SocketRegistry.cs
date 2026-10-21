/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace FreeSpeech.Relay.Services;

/// <summary>Thread-safe in-memory implementation of <see cref="ISocketRegistry"/>.</summary>
public class SocketRegistry : ISocketRegistry
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();

    public void Add(string connectionId, WebSocket socket) => _sockets[connectionId] = socket;

    public void Remove(string connectionId) => _sockets.TryRemove(connectionId, out _);

    public IReadOnlyList<string> ConnectionIds => _sockets.Keys.ToList();

    public WebSocket? Get(string connectionId) =>
        _sockets.TryGetValue(connectionId, out var socket) ? socket : null;
}
