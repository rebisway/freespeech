/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Linq;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IPresenceService"/>.
/// Two maps for O(1) lookups in both directions; the forward map holds a set
/// of connection ids per discovery id to support multi-device presence.
/// </summary>
public class PresenceService : IPresenceService
{
    // discoveryId -> set of connectionIds (ConcurrentDictionary used as a set)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionsByDiscoveryId = new();

    // connectionId -> discoveryId
    private readonly ConcurrentDictionary<string, string> _discoveryIdByConnection = new();

    private readonly ILogger<PresenceService> _logger;
    private readonly FreeSpeech.Ops.TraceService _trace;

    public PresenceService(FreeSpeech.Ops.TraceService trace, ILogger<PresenceService> logger)
    {
        _trace = trace;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task RegisterAsync(string discoveryId, string connectionId)
    {
        var connections = _connectionsByDiscoveryId.GetOrAdd(discoveryId, _ => new ConcurrentDictionary<string, byte>());
        connections[connectionId] = 0;
        _discoveryIdByConnection[connectionId] = discoveryId;

        _logger.LogInformation(
            "Registered {DiscoveryId} ({Count} active connection(s))",
            FreeSpeech.Ops.TraceService.Short(discoveryId), connections.Count);
        _trace.Write(discoveryId, $"connected ({connections.Count} active connection(s))");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string?> UnregisterAsync(string connectionId)
    {
        if (!_discoveryIdByConnection.TryRemove(connectionId, out var discoveryId))
            return Task.FromResult<string?>(null);

        if (_connectionsByDiscoveryId.TryGetValue(discoveryId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
            if (connections.IsEmpty)
                _connectionsByDiscoveryId.TryRemove(discoveryId, out _);
        }

        _logger.LogInformation("Unregistered {DiscoveryId}", FreeSpeech.Ops.TraceService.Short(discoveryId));
        _trace.Write(discoveryId, "disconnected");
        return Task.FromResult<string?>(discoveryId);
    }

    /// <inheritdoc/>
    public Task<string?> GetDiscoveryIdAsync(string connectionId)
    {
        _discoveryIdByConnection.TryGetValue(connectionId, out var discoveryId);
        return Task.FromResult(discoveryId);
    }

    /// <inheritdoc/>
    public Task<bool> IsOnlineAsync(string discoveryId) =>
        Task.FromResult(_connectionsByDiscoveryId.TryGetValue(discoveryId, out var c) && !c.IsEmpty);

    /// <inheritdoc/>
    public Task<int> GetRegisteredCountAsync() => Task.FromResult(_connectionsByDiscoveryId.Count);

    /// <inheritdoc/>
    public IReadOnlyList<(string DiscoveryId, int Connections)> Connected() =>
        [.. _connectionsByDiscoveryId.Where(p => !p.Value.IsEmpty).Select(p => (p.Key, p.Value.Count)).OrderBy(p => p.Key, StringComparer.Ordinal)];

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetConnectionsAsync(string discoveryId)
    {
        IReadOnlyList<string> result = _connectionsByDiscoveryId.TryGetValue(discoveryId, out var connections)
            ? connections.Keys.ToList()
            : [];
        return Task.FromResult(result);
    }
}
