/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.WebSockets;
using System.Text.Json;
using FreeSpeech.Ops;
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

public class MessageDeliveryService : IMessageDeliveryService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IPresenceService _presence;
    private readonly ISocketRegistry _sockets;
    private readonly TraceService _trace;
    private readonly ILogger<MessageDeliveryService> _logger;

    public MessageDeliveryService(IPresenceService presence, ISocketRegistry sockets, TraceService trace, ILogger<MessageDeliveryService> logger)
    {
        _trace = trace;
        _presence = presence;
        _sockets = sockets;
        _logger = logger;
    }

    public async Task<bool> DeliverLocallyAsync(Envelope envelope, string? excludeConnectionId, CancellationToken ct)
    {
        var recipientConnections = await _presence.GetConnectionsAsync(envelope.To);
        var push = new WsMessage { Type = "message", Envelope = envelope };

        var delivered = false;
        foreach (var connectionId in recipientConnections)
        {
            if (excludeConnectionId != null && connectionId == excludeConnectionId) continue;

            var socket = _sockets.Get(connectionId);
            if (socket == null || socket.State != WebSocketState.Open) continue;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(push, Json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            delivered = true;
        }

        if (delivered)
        {
            // Who talks to whom is not something a log should keep: ids appear (shortened) only at Debug level and in an explicit trace.
            _logger.LogDebug(
                "Message delivered locally from {From} to {To} ({Count} connection(s), crypto={Crypto})",
                TraceService.Short(envelope.From), TraceService.Short(envelope.To), recipientConnections.Count, envelope.Crypto);
            _trace.Write(envelope.To, $"received a message from {TraceService.Short(envelope.From)} ({recipientConnections.Count} connection(s), crypto={envelope.Crypto})");
        }

        return delivered;
    }
}
