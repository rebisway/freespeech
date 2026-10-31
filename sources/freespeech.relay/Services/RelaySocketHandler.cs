/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FreeSpeech.Ops;
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Drives one raw WebSocket connection for the relay transport (protocol
/// §11 — plain JSON-over-WebSocket, deliberately free of any framework-
/// specific protocol like SignalR, so any language can implement a client).
/// The server never decrypts — it only reads From/To on envelopes to route
/// them.
/// </summary>
public class RelaySocketHandler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IPresenceService _presence;
    private readonly ISocketRegistry _sockets;
    private readonly IMessageDeliveryService _delivery;
    private readonly ISiblingRegistry _siblings;
    private readonly IS2SClient _s2sClient;
    private readonly IPushRegistry _pushTokens;
    private readonly IPushNotifier _push;
    private readonly IPendingFlagService _flags;
    private readonly IHttpSessionService _http;
    private readonly IRedirectService _redirect;
    private readonly IClientAccess _access;
    private readonly ITrafficLimiter _traffic;
    private readonly TraceService _trace;
    private readonly ILogger<RelaySocketHandler> _logger;
    private readonly RelaySettings _settings;
    private int _maxMessageBytes => _settings.Current.MaxMessageBytes;   // changed while the server runs: read at each use
    private readonly bool _allowAnonymous;
    private string? _remoteAddress;   // this handler serves one connection

    /// <summary>A registration waiting for its proof: the keys the client claimed and the nonce it must sign.</summary>
    private sealed record PendingAuth(string DiscoveryId, byte[] SignKey, string Nonce, string? Invitation, bool RequestAccess = false, string? Name = null, string? Note = null, string? EncKeyB64 = null, string? SignKeyB64 = null);

    /// <summary>The identities whose group invitations a client of this server may take without knowing them (empty list = omitted from the message).</summary>
    private List<ProvisionerInfo>? ProvisionerList()
    {
        var ids = _access.Provisioners;
        if (ids.Count == 0) return null;
        // Only those whose keys the server holds (they have registered once): a client needs the signing key to check their letters.
        var list = _access.Allowed.Where(a => ids.Contains(a.Id) && a.EncKey != null && a.SignKey != null)
            .Select(a => new ProvisionerInfo { Id = a.Id, EncKey = a.EncKey!, SignKey = a.SignKey! }).ToList();
        return list.Count == 0 ? null : list;
    }

    public RelaySocketHandler(
        IPresenceService presence, ISocketRegistry sockets, IMessageDeliveryService delivery,
        ISiblingRegistry siblings, IS2SClient s2sClient, IPushRegistry pushTokens, IPushNotifier push, RelaySettings settings,
        IPendingFlagService flags, IHttpSessionService http, IRedirectService redirect, IClientAccess access, ITrafficLimiter traffic, TraceService trace, IConfiguration config, ILogger<RelaySocketHandler> logger)
    {
        _trace = trace;
        _traffic = traffic;
        _access = access;
        _redirect = redirect;
        _http = http;
        // One frame carries at most a 48 KiB file chunk or a profile photo (~170 KB) once encrypted and encoded: 512 KiB is plenty.
        _settings = settings;
        // Development only (raw scripts that cannot sign): lets a client register without proving its identity.
        _allowAnonymous = config.GetValue("Server:AllowAnonymousRegister", false);
        _pushTokens = pushTokens;
        _push = push;
        _flags = flags;
        _presence = presence;
        _sockets = sockets;
        _delivery = delivery;
        _siblings = siblings;
        _s2sClient = s2sClient;
        _logger = logger;
    }

    /// <summary>Owns the full lifecycle of one connection: register, message loop, cleanup.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken, string? remoteAddress = null)
    {
        _remoteAddress = remoteAddress;
        var connectionId = Guid.NewGuid().ToString("N");
        _sockets.Add(connectionId, socket);
        var openedAt = DateTime.UtcNow;
        string? discoveryId = null;
        PendingAuth? pending = null;

        try
        {
            var buffer = new byte[8 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var text = await ReceiveFullTextMessageAsync(socket, buffer, _maxMessageBytes, cancellationToken);
                if (text == null) break; // closed by peer, or the message was too big (the connection is closed)

                WsMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<WsMessage>(text, Json);
                }
                catch (JsonException)
                {
                    await SendAsync(socket, new WsMessage { Type = "error", ErrorMessage = "Invalid JSON" }, cancellationToken);
                    continue;
                }

                if (msg == null) continue;

                switch (msg.Type)
                {
                    case "register":
                        (discoveryId, pending) = await HandleRegisterAsync(socket, connectionId, msg, discoveryId, cancellationToken);
                        break;

                    case "register_proof":
                        (discoveryId, pending) = await HandleRegisterProofAsync(socket, connectionId, msg, discoveryId, pending, cancellationToken);
                        break;

                    case "send":
                        await HandleSendAsync(socket, connectionId, discoveryId, msg, cancellationToken);
                        break;

                    case "is_online":
                        await HandleIsOnlineAsync(socket, msg, cancellationToken);
                        break;

                    case "push_register":
                    case "push_refresh":
                    case "push_unregister":
                        await HandlePushAsync(socket, discoveryId, msg, cancellationToken);
                        break;

                    default:
                        await SendAsync(socket, new WsMessage
                        {
                            Type = "error",
                            RequestId = msg.RequestId,
                            ErrorMessage = $"Unknown message type '{msg.Type}'",
                        }, cancellationToken);
                        break;
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("WebSocket error on connection {ConnectionId}: {Message}", connectionId, ex.Message);
        }
        finally
        {
            _sockets.Remove(connectionId);
            _http.Revoke(connectionId);
            var gone = await _presence.UnregisterAsync(connectionId);
            if (gone != null) await WakeForFlagsRaisedWhileConnectedAsync(gone, openedAt);

            // CloseReceived: the client started the close handshake and waits for our answer.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { /* best effort */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Message handlers
    // ------------------------------------------------------------------

    /// <summary>
    /// First half of the registration: the client names its id and gives both public keys; the server checks they hash to that id
    /// and answers with a nonce to sign. Nothing is registered yet. Returns the (unchanged) current id and the pending proof.
    /// </summary>
    private async Task<(string? Id, PendingAuth? Pending)> HandleRegisterAsync(WebSocket socket, string connectionId, WsMessage msg, string? current, CancellationToken ct)
    {
        async Task<(string?, PendingAuth?)> Refuse(string why)
        {
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = why }, ct);
            return (current, null);
        }

        if (string.IsNullOrWhiteSpace(msg.DiscoveryId)) return await Refuse("discovery_id is required");

        if (string.IsNullOrEmpty(msg.EncKey) && string.IsNullOrEmpty(msg.SignKey))
        {
            if (!_allowAnonymous) return await Refuse("Authentication required: send enc_key and sign_key, then sign the challenge");

            // Development switch only (Server:AllowAnonymousRegister): the old, unauthenticated registration.
            if (_access.Check(msg.DiscoveryId, msg.Invitation) != AccessDecision.Allowed) return await Refuse("access_refused");
            await _presence.RegisterAsync(msg.DiscoveryId, connectionId);
            await SendAsync(socket, new WsMessage { Type = "registered", RequestId = msg.RequestId, HttpToken = _http.Issue(msg.DiscoveryId, connectionId), Access = _access.Mode, CircleKey = _access.CircleKey }, ct);
            await NotifySendersAsync(msg.DiscoveryId, ct);
            return (msg.DiscoveryId, null);
        }

        byte[] enc, sign;
        try { (enc, sign) = (Convert.FromBase64String(msg.EncKey ?? ""), Convert.FromBase64String(msg.SignKey ?? "")); }
        catch (FormatException) { return await Refuse("Invalid key encoding"); }

        if (!RegistrationAuth.KeysMatchId(msg.DiscoveryId, enc, sign)) return await Refuse("The keys do not match this discovery_id");

        var pending = new PendingAuth(msg.DiscoveryId.ToLowerInvariant(), sign, RegistrationAuth.NewNonce(), msg.Invitation, msg.RequestAccess == true, msg.Name, msg.Note, msg.EncKey, msg.SignKey);
        await SendAsync(socket, new WsMessage { Type = "challenge", RequestId = msg.RequestId, Nonce = pending.Nonce, DiscoveryId = pending.DiscoveryId }, ct);
        return (current, pending);
    }

    /// <summary>Second half: the signature of the nonce proves the client holds the signing key of that id. Only then is it registered.</summary>
    private async Task<(string? Id, PendingAuth? Pending)> HandleRegisterProofAsync(WebSocket socket, string connectionId, WsMessage msg, string? current, PendingAuth? pending, CancellationToken ct)
    {
        async Task<(string?, PendingAuth?)> Refuse(string why)
        {
            _logger.LogWarning("Registration refused: {Why}", why);
            if (pending != null) _trace.Write(pending.DiscoveryId, $"registration refused: {why}");
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = why }, ct);
            return (current, null);   // a nonce is single-use: a failed proof starts over
        }

        if (pending == null) return await Refuse("No registration in progress");

        byte[] signature;
        try { signature = Convert.FromBase64String(msg.Signature ?? ""); }
        catch (FormatException) { return await Refuse("Invalid signature encoding"); }

        if (!RegistrationAuth.Verify(pending.DiscoveryId, pending.Nonce, pending.SignKey, signature)) return await Refuse("Invalid signature");

        // Access control comes after the proof, so an invitation is only ever spent by the identity that holds the keys.
        switch (_access.Check(pending.DiscoveryId, pending.Invitation))
        {
            case AccessDecision.Banned: return await Refuse("access_refused");
            case AccessDecision.PrivateServer:
                // Unknown identity, keys proven: it may leave a request (only when it asked to, after the user agreed) and wait for the administrator.
                // An identity already waiting that asks again just refreshes its request (no second line in the administrator's list).
                if (pending.RequestAccess && _access.Request(pending.DiscoveryId, pending.Name, pending.Note) is RequestResult.Stored or RequestResult.AlreadyPending) return await Refuse("access_pending");
                if (_access.IsPending(pending.DiscoveryId)) return await Refuse("access_pending");
                return await Refuse("private_server");
        }

        // An allowed identity's public keys are kept, for the administrator's tools (a group is built with them). Nothing for an identity that is not on the allow-list.
        if (pending.EncKeyB64 != null && pending.SignKeyB64 != null) _access.RememberKeys(pending.DiscoveryId, pending.EncKeyB64, pending.SignKeyB64);

        await _presence.RegisterAsync(pending.DiscoveryId, connectionId);
        await SendAsync(socket, new WsMessage { Type = "registered", RequestId = msg.RequestId, HttpToken = _http.Issue(pending.DiscoveryId, connectionId), Access = _access.Mode, CircleKey = _access.CircleKey, Provisioners = ProvisionerList(), DailyQuota = _traffic.DailyQuota > 0 ? _traffic.DailyQuota : null }, ct);

        await NotifySendersAsync(pending.DiscoveryId, ct);
        await RedirectIfFullAsync(socket, ct);
        // An administrator tool just connected: its keys are known now, so the clients already here learn it too.
        if (_access.Provisioners.Contains(pending.DiscoveryId)) await BroadcastStateAsync(ct);
        return (pending.DiscoveryId, null);
    }
    /// <summary>Over capacity (Server:MaxConnections): tells the client which siblings have room. If none has, the client stays — refusing it would leave it with nothing.</summary>
    private async Task RedirectIfFullAsync(WebSocket socket, CancellationToken ct)
    {
        if (!await _redirect.IsOverAsync()) return;
        var servers = await _redirect.PickSiblingsAsync(ct);
        if (servers.Count == 0) { _logger.LogWarning("Server is full and no sibling has room"); return; }
        await SendRedirectAsync(socket, servers, "load", ct);
    }

    /// <summary>Sends "server_state" (§10 "Access control") to one connection: the sign-up rule changed while the client is connected.</summary>
    public static Task SendStateAsync(WebSocket socket, string access, List<ProvisionerInfo> provisioners, string? circleKey, CancellationToken ct) =>
        SendAsync(socket, new WsMessage { Type = "server_state", Access = access, Provisioners = provisioners, CircleKey = circleKey }, ct);

    /// <summary>Tells every connected client the rule and the administrator tools now in force (an empty list means none).</summary>
    public async Task BroadcastStateAsync(CancellationToken ct)
    {
        var provisioners = ProvisionerList() ?? [];
        foreach (var id in _sockets.ConnectionIds)
            if (_sockets.Get(id) is { State: WebSocketState.Open } socket)
            {
                try { await SendStateAsync(socket, _access.Mode, provisioners, _access.CircleKey, ct); }
                catch (WebSocketException) { /* gone meanwhile */ }
            }
    }

    /// <summary>
    /// A connection ended (the keep-alive timeout reaps one whose phone vanished). A sender may have raised a WAITING flag while this
    /// connection still looked alive, which suppressed the wake-up: now that the identity is offline it gets it. Only flags raised
    /// during that connection count, so a client that connects and leaves is not woken again for an old flag.
    /// </summary>
    private async Task WakeForFlagsRaisedWhileConnectedAsync(string discoveryId, DateTime openedAt)
    {
        if (await _presence.IsOnlineAsync(discoveryId)) return;
        if (_flags.GetFlagsFor(discoveryId).Any(f => f.State == PendingFlagState.Waiting && f.UpdatedAt >= openedAt))
            _ = _push.WakeAsync(discoveryId);
    }

    /// <summary>Sends "server_redirect" (§10 "Moving a client") to one connection.</summary>
    public static Task SendRedirectAsync(WebSocket socket, IReadOnlyList<string> servers, string reason, CancellationToken ct) =>
        SendAsync(socket, new WsMessage { Type = "server_redirect", Servers = [.. servers], Reason = reason }, ct);

    /// <summary>
    /// Wake-up step 3 (protocol §9): <paramref name="discoveryId"/> just connected; every contact with a WAITING
    /// flag towards it is told so, and can resend its outbox at once. A sender that is itself offline is woken by push.
    /// </summary>
    private async Task NotifySendersAsync(string discoveryId, CancellationToken ct)
    {
        foreach (var flag in _flags.GetFlagsFor(discoveryId).Where(f => f.State == PendingFlagState.Waiting))
        {
            var connections = await _presence.GetConnectionsAsync(flag.From);
            if (connections.Count == 0)
            {
                _trace.Write(discoveryId, $"online: {TraceService.Short(flag.From)} waits for you and is not connected, waking them");
                _trace.Write(flag.From, $"{TraceService.Short(discoveryId)} is online and you wait for them, but you are not connected: waking you");
                _ = _push.WakeAsync(flag.From);
                continue;
            }
            _trace.Write(flag.From, $"{TraceService.Short(discoveryId)} is online: peer_online sent to your {connections.Count} connection(s)");

            foreach (var connectionId in connections)
            {
                var other = _sockets.Get(connectionId);
                if (other is { State: WebSocketState.Open })
                    await SendAsync(other, new WsMessage { Type = "peer_online", DiscoveryId = discoveryId }, ct);
            }
        }
    }

    private async Task HandlePushAsync(WebSocket socket, string? discoveryId, WsMessage msg, CancellationToken ct)
    {
        async Task Reply(bool ok, string? why = null) => await SendAsync(socket, new WsMessage { Type = "push_result", RequestId = msg.RequestId, Ok = ok, ErrorMessage = why }, ct);

        if (discoveryId == null) { await Reply(false, "Register first"); return; }

        if (msg.Type == "push_unregister") { _pushTokens.Remove(discoveryId); await Reply(true); return; }

        var platform = msg.Platform;
        if (platform is not ("fcm" or "apns")) { await Reply(false, "platform must be fcm or apns"); return; }
        if (string.IsNullOrWhiteSpace(msg.Token) || msg.Token.Length > 4096) { await Reply(false, "invalid token"); return; }

        // The client names no gateway: the relay uses the ones it is linked with (several are interchangeable).
        if (!_push.HasGateway) { _trace.Write(discoveryId, "push refused: no push gateway linked on this server"); await Reply(false, "no push gateway linked on this server"); return; }

        _pushTokens.Set(discoveryId, new PushRegistration(platform, msg.Token));
        _trace.Write(discoveryId, $"push registered ({platform})");
        await Reply(true);
    }

    private async Task HandleSendAsync(WebSocket socket, string senderConnectionId, string? senderDiscoveryId, WsMessage msg, CancellationToken ct)
    {
        if (senderDiscoveryId == null)
        {
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = "Register first" }, ct);
            return;
        }

        var envelope = msg.Envelope;
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.To) || string.IsNullOrWhiteSpace(envelope.EncryptedMessage))
        {
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = "Invalid envelope: to and msg are required" }, ct);
            return;
        }

        // The caller can only send as the identity it registered with.
        if (envelope.From != senderDiscoveryId)
        {
            _logger.LogWarning("Send from {Registered} claims another sender", TraceService.Short(senderDiscoveryId));
            _trace.Write(senderDiscoveryId, "send refused: sender identity mismatch");
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = "Sender identity mismatch" }, ct);
            return;
        }

        // Volume limits (Server:MaxBytes...): the relay cannot tell a file chunk from a message, so it counts bytes.
        var verdict = _traffic.Check(senderDiscoveryId, _remoteAddress, envelope.EncryptedMessage.Length);
        if (!verdict.Allowed)
        {
            _logger.LogWarning("Send from {Sender} refused: {Code}, retry in {Seconds} s", TraceService.Short(senderDiscoveryId), verdict.Code, verdict.RetryAfterSeconds);
            _trace.Write(senderDiscoveryId, $"send refused: {verdict.Code} (retry in {verdict.RetryAfterSeconds} s)");
            await SendAsync(socket, new WsMessage { Type = "error", RequestId = msg.RequestId, ErrorMessage = verdict.Code, Code = verdict.Code, RetryAfter = verdict.RetryAfterSeconds, QuotaLeft = verdict.QuotaLeft }, ct);
            return;
        }
        // Going faster than the allowed throughput: the next frames wait (the socket's own flow control slows the client down) rather than being cut.
        if (verdict.Delay > TimeSpan.Zero) await Task.Delay(verdict.Delay, ct);

        // Debug level shows the shape of what passes (crypto mode, size), never the payload itself: a log is not a place for content, even encrypted.
        _logger.LogDebug("Envelope (crypto={Crypto}, {Length} characters)", envelope.Crypto, envelope.EncryptedMessage.Length);

        // A self-addressed send (protocol §1/§5 self-sync copy, To ==
        // From) must not echo back to the very connection that sent it — it
        // exists to reach the sender's OTHER devices only.
        var excludeConnectionId = envelope.To == senderDiscoveryId ? senderConnectionId : null;
        var delivered = await _delivery.DeliverLocallyAsync(envelope, excludeConnectionId, ct);

        if (!delivered && _siblings.IsPublic)
            delivered = await TryDeliverViaSiblingsAsync(envelope, ct);

        if (!delivered)
            _logger.LogDebug("Recipient {To} has no active connection anywhere reachable — caller should set WAITING", TraceService.Short(envelope.To));

        // Multi-device self-sync (protocol §1/§5): with real E2E crypto=1,
        // the envelope is encrypted for the recipient's public key only — the
        // server can no longer usefully echo this exact ciphertext to the
        // sender's other connections, they have no way to decrypt it. Instead
        // it just reports whether the sender has other active connections, so
        // the client can decide whether to additionally send itself a
        // self-encrypted copy (see WsMessage.SelfMultiDevice).
        var senderConnections = await _presence.GetConnectionsAsync(senderDiscoveryId);
        var selfMultiDevice = senderConnections.Any(c => c != senderConnectionId);

        _trace.Write(senderDiscoveryId, $"send to {TraceService.Short(envelope.To)} delivered={delivered} (crypto={envelope.Crypto}, {envelope.EncryptedMessage.Length} characters)");

        await SendAsync(socket, new WsMessage
        {
            Type = "send_result",
            RequestId = msg.RequestId,
            Delivered = delivered,
            SelfMultiDevice = selfMultiDevice,
            QuotaLeft = verdict.QuotaLeft,
        }, ct);
    }

    /// <summary>
    /// Cascade discovery + forward (protocol §10): tried only when the
    /// recipient has no local connection and this server is public. Asks
    /// each whitelisted sibling in turn (priority = whitelist order) "do you
    /// have them locally", and forwards to the first one that says yes.
    /// Single-hop — a sibling never asks ITS OWN siblings on our behalf —
    /// so this never loops and never reaches further than direct siblings.
    /// </summary>
    private async Task<bool> TryDeliverViaSiblingsAsync(Envelope envelope, CancellationToken ct)
    {
        var sibling = await FindSiblingWithAsync(envelope.To, ct);
        if (sibling == null) return false;

        if (!await _s2sClient.RelayAsync(sibling, envelope, ct)) return false;

        _logger.LogDebug("Message forwarded from {From} to {To} via sibling {Sibling}", TraceService.Short(envelope.From), TraceService.Short(envelope.To), sibling.Url);
        _trace.Write(envelope.From, $"forwarded to {TraceService.Short(envelope.To)} via sibling {sibling.Url}");
        return true;
    }

    /// <summary>Asks each whitelisted sibling in turn (priority = whitelist order) whether it has <paramref name="discoveryId"/> locally; returns the first one that says yes, or null.</summary>
    private async Task<Sibling?> FindSiblingWithAsync(string discoveryId, CancellationToken ct)
    {
        foreach (var sibling in _siblings.Whitelist)
        {
            if (await _s2sClient.LookupAsync(sibling, discoveryId, ct))
                return sibling;
        }
        return null;
    }

    /// <summary>
    /// Cascades to siblings (protocol §10) same as the "send" fallback —
    /// without this, a client whose target is only reachable via a sibling
    /// would see is_online=false and never even attempt the send that would
    /// otherwise succeed via <see cref="TryDeliverViaSiblingsAsync"/> (this
    /// gates both OTC contact requests and the message outbox flush,
    /// ContactExchangeService/PendingMessageStore on the client side).
    /// </summary>
    private async Task HandleIsOnlineAsync(WebSocket socket, WsMessage msg, CancellationToken ct)
    {
        var online = false;
        if (!string.IsNullOrWhiteSpace(msg.DiscoveryId))
        {
            online = await _presence.IsOnlineAsync(msg.DiscoveryId);
            if (!online && _siblings.IsPublic)
                online = await FindSiblingWithAsync(msg.DiscoveryId, ct) != null;
        }

        await SendAsync(socket, new WsMessage
        {
            Type = "is_online_result",
            RequestId = msg.RequestId,
            DiscoveryId = msg.DiscoveryId,
            Online = online,
        }, ct);
    }

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    private static async Task SendAsync(WebSocket socket, WsMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>Reads one full text message, reassembling multi-frame payloads. Returns null on close.</summary>
    /// <summary>Reads one text message. A message over <paramref name="maxBytes"/> closes the connection (1009) instead of filling the server's memory.</summary>
    private static async Task<string?> ReceiveFullTextMessageAsync(WebSocket socket, byte[] buffer, int maxBytes, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (stream.Length + result.Count > maxBytes)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None); } catch { /* best effort */ }
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }}

