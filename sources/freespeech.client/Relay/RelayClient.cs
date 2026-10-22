/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FreeSpeech.Client.Crypto;

namespace FreeSpeech.Client.Relay;

/// <summary>
/// Client for the raw WebSocket relay transport (protocol §11). Plain
/// <see cref="ClientWebSocket"/> + hand-rolled JSON framing — no SignalR,
/// so this logic is a direct template for a client in any other language.
///
/// Correlates request/response pairs by <c>request_id</c> since a WebSocket
/// has no built-in RPC concept and can interleave a push ("message") between
/// a request and its response.
/// </summary>
/// <summary>Result of a "send" request — <see cref="SelfMultiDevice"/> tells the caller whether to also send itself a self-encrypted copy for live multi-device sync (protocol §1/§5).</summary>
public record SendResult(bool Delivered, bool SelfMultiDevice);

/// <summary>The server refused a send because of its volume limits (PROTOCOL §11 "Volume limits"): <see cref="Code"/> is "quota_exceeded" or "rate_limited", and sending may resume in <see cref="RetryAfterSeconds"/>.</summary>
public sealed class RelayLimitException(string code, int retryAfterSeconds) : InvalidOperationException(code)
{
    public string Code { get; } = code;
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public class RelayClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<WsMessage>> _pending = new();

    // Re-created on every (re)connect — a ClientWebSocket is single-use, so
    // losing the relay (server restart, mobile network drop, §9) means
    // building a fresh one rather than reusing the dead one.
    private ClientWebSocket _socket = new();
    private CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    private string? _wsUrl;
    private string? _discoveryId;
    private volatile bool _connected;

    /// <summary>Raised for every server-pushed "message" (not a response to a request).</summary>
    public event Action<Envelope>? OnMessage;

    /// <summary>Raised when a contact with a message waiting from us has just connected to this server (protocol §9) — the outbox can resend now.</summary>
    public event Action<string>? OnPeerOnline;

    /// <summary>Every WebSocket text frame as it goes by: direction ("->" sent, "<-" received) and the JSON. For protocol debugging tools (the console's <c>raw</c>); nothing is kept.</summary>
    public event Action<string, string>? OnFrame;

    /// <summary>The server suggests siblings that can take this client instead (§10 "Moving a client"): servers best first, and why ("load"/"admin"). The caller decides.</summary>
    public event Action<IReadOnlyList<string>, string>? OnRedirect;

    /// <summary>Keys and signing seed used to prove my identity at every (re)connect; null only against a development server that accepts anonymous registration.</summary>
    public RegisterAuth? Auth { get; set; }

    /// <summary>Invitation token for a private server (PROTOCOL §10 "Access control"); sent with every registration, ignored by open servers. A used token stays harmless: the identity is already allowed.</summary>
    public string? Invitation { get; set; }

    /// <summary>
    /// A request for access to a private server (name, note), sent with the NEXT registration only — the user decides, once, to hand
    /// their name over; after that the server remembers the request and answers "access_pending" by itself.
    /// </summary>
    public (string? Name, string? Note)? AccessRequest { get; set; }

    /// <summary>Push registration sent after every (re)connect — the server keeps tokens in memory only.</summary>
    public PushTokenInfo? Push { get; set; }

    /// <summary>Raised when the receive loop ends unexpectedly (connection lost).</summary>
    public event Action? OnDisconnected;

    /// <summary>False after the relay connection drops, until <see cref="TryReconnectAsync"/> succeeds again.</summary>
    public bool IsConnected => _connected;

    /// <summary>Who may register on the server I am connected to: "open" or "allowlist" (a private server); null when it did not say (older server).</summary>
    public string? ServerAccess { get; private set; }

    /// <summary>Bytes an identity may send per 24 hours on this server (null = no quota, or the server did not say). Set at each registration.</summary>
    public long? DailyQuota { get; private set; }

    /// <summary>Bytes I may still send on this server in the current 24 hours, as of the last send (null = unknown or no quota).</summary>
    public long? QuotaLeft { get; private set; }

    /// <summary>Only entries whose keys really hash to their id are kept: the server cannot hand a client a key for somebody else's identity.</summary>
    private static IReadOnlyList<ProvisionerInfo> ValidProvisioners(List<ProvisionerInfo>? named) =>
        named is not { Count: > 0 } ? [] :
        [.. named.Where(x => Groups.GroupCrypto.MemberIdMatchesKeys(new Groups.GroupMember { DiscoveryId = x.Id, Nickname = "", EncryptionPublicKey = x.EncKey, SigningPublicKey = x.SignKey }))
                 .Select(x => new ProvisionerInfo { Id = x.Id.ToLowerInvariant(), EncKey = x.EncKey, SignKey = x.SignKey })];

    /// <summary>The identities this server says are its administrator's tools (§10 "Groups of a private server"); empty when it names none. Set at each registration.</summary>
    public IReadOnlyList<ProvisionerInfo> ServerProvisioners { get; private set; } = [];

    /// <summary>Token the server handed out at registration; HTTP calls (/api/flags) present it as X-Flag-Token (§11). Changes on every reconnect.</summary>
    public string? HttpToken { get; private set; }

    public async Task ConnectAsync(string wsUrl, string discoveryId, CancellationToken ct = default)
    {
        _wsUrl = wsUrl;
        _discoveryId = discoveryId;
        await ConnectCoreAsync(ct);
    }

    /// <summary>
    /// Re-establishes the relay connection after a drop, meant to be called
    /// periodically (e.g. from the background scheduler, §9's ~15 min
    /// fallback pattern) rather than awaited inline. Returns false — never
    /// throws — if the server is still unreachable, so a caller can just
    /// retry on the next tick.
    /// </summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        if (_connected) return true;
        if (_wsUrl == null || _discoveryId == null) return false;

        try
        {
            await ConnectCoreAsync(ct);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex;
            return false;
        }
    }

    /// <summary>Why the last <see cref="TryReconnectAsync"/> failed (e.g. "Registration failed: private_server"); null after a success.</summary>
    public Exception? LastError { get; private set; }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        await _socket.ConnectAsync(new Uri(_wsUrl!), ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        // Registration proves the identity (§11): the server answers my public keys with a nonce, and I sign it.
        var register = new WsMessage { Type = "register", DiscoveryId = _discoveryId, Invitation = Invitation };
        if (AccessRequest is { } ask)
        {
            register.RequestAccess = true;
            register.Name = ask.Name;
            register.Note = ask.Note;
            AccessRequest = null;   // one shot: the server keeps the request from here on
        }
        if (Auth != null)
        {
            register.EncKey = Convert.ToBase64String(Auth.EncPublicKey);
            register.SignKey = Convert.ToBase64String(Auth.SignPublicKey);
        }
        var response = await SendRequestAsync(register, ct);
        if (response.Type == "challenge" && Auth != null && response.Nonce != null)
        {
            var signature = Primitives.Ed25519Sign(Auth.SignSeed, RegisterAuth.Challenge(response.Nonce, _discoveryId!));
            response = await SendRequestAsync(new WsMessage { Type = "register_proof", Signature = Convert.ToBase64String(signature) }, ct);
        }
        if (response.Type != "registered")
        {
            ClientLog.Warn("relay", $"{ClientLog.Host(_wsUrl!)}: registration refused ({response.ErrorMessage ?? response.Type})");
            throw new InvalidOperationException($"Registration failed: {response.ErrorMessage ?? response.Type}");
        }

        HttpToken = response.HttpToken;
        ServerAccess = response.Access;
        DailyQuota = response.DailyQuota;
        QuotaLeft = response.DailyQuota;
        ServerProvisioners = ValidProvisioners(response.Provisioners);
        _connected = true;
        ClientLog.Info("relay", $"{ClientLog.Host(_wsUrl!)}: registered as {ClientLog.Short(_discoveryId)}{(Invitation != null ? " (with an invitation)" : "")}");
        if (Push != null) _ = RegisterPushAsync();   // best effort: polling remains the fallback
    }

    /// <summary>Registers (or refreshes) this device's push token on the server. False when the server refuses (e.g. gateway not allowed there).</summary>
    public async Task<bool> RegisterPushAsync(CancellationToken ct = default)
    {
        if (Push is not { } p) return false;
        try
        {
            var r = await SendRequestAsync(new WsMessage { Type = "push_register", Platform = p.Platform, Token = p.Token }, ct);
            ClientLog.Info("push", $"{ClientLog.Host(_wsUrl ?? "")}: push registration {(r.Ok == true ? "accepted" : "refused (" + (r.ErrorMessage ?? "no reason") + ")")}");
            return r.Ok == true;
        }
        catch { return false; }
    }

    public async Task<bool> UnregisterPushAsync(CancellationToken ct = default)
    {
        Push = null;
        if (!_connected) return false;
        try { return (await SendRequestAsync(new WsMessage { Type = "push_unregister" }, ct)).Ok == true; }
        catch { return false; }
    }

    public async Task<SendResult> SendMessageAsync(Envelope envelope, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(new WsMessage { Type = "send", Envelope = envelope }, ct);
        if (response.Type == "error" && response.Code is "quota_exceeded" or "rate_limited")
        {
            QuotaLeft = response.QuotaLeft ?? QuotaLeft;
            ClientLog.Warn("send", $"{ClientLog.Host(_wsUrl ?? "")}: send refused ({response.Code}, retry in {response.RetryAfter ?? 0} s)");
            throw new RelayLimitException(response.Code!, Math.Max(1, response.RetryAfter ?? 60));
        }
        if (response.Type == "error")
        {
            ClientLog.Warn("send", $"{ClientLog.Host(_wsUrl ?? "")}: send to {ClientLog.Short(envelope.To)} failed ({response.ErrorMessage})");
            throw new InvalidOperationException(response.ErrorMessage ?? "send failed");
        }
        ClientLog.Debug("send", $"{ClientLog.Host(_wsUrl ?? "")}: send to {ClientLog.Short(envelope.To)} delivered={response.Delivered ?? false} (crypto={envelope.Crypto})");
        QuotaLeft = response.QuotaLeft;
        return new SendResult(response.Delivered ?? false, response.SelfMultiDevice ?? false);
    }

    public async Task<bool> IsOnlineAsync(string discoveryId, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(new WsMessage { Type = "is_online", DiscoveryId = discoveryId }, ct);
        if (response.Type == "error") throw new InvalidOperationException(response.ErrorMessage ?? "is_online failed");
        return response.Online ?? false;
    }

    /// <summary>Sends <paramref name="json"/> as one text frame, exactly as given (it may be invalid on purpose: a tool checking how a server refuses it). No correlation: the answer, if any, comes through <see cref="OnFrame"/>.</summary>
    public async Task SendRawAsync(string json, CancellationToken ct = default)
    {
        if (!_connected) throw new InvalidOperationException("Not connected to this server.");
        var bytes = Encoding.UTF8.GetBytes(json);
        OnFrame?.Invoke("->", json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // ------------------------------------------------------------------
    // Request/response correlation
    // ------------------------------------------------------------------

    private async Task<WsMessage> SendRequestAsync(WsMessage request, CancellationToken ct)
    {
        request.RequestId ??= Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = tcs;

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            OnFrame?.Invoke("->", Encoding.UTF8.GetString(bytes));
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);

            using var registration = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReceiveFullTextMessageAsync(buffer, ct);
                if (text == null) break;
                OnFrame?.Invoke("<-", text);

                WsMessage? msg;
                try { msg = JsonSerializer.Deserialize<WsMessage>(text, Json); }
                catch (JsonException) { continue; }
                if (msg == null) continue;

                if (msg.Type == "message" && msg.Envelope != null)
                {
                    ClientLog.Debug("receive", $"{ClientLog.Host(_wsUrl ?? "")}: message from {ClientLog.Short(msg.Envelope.From)} (crypto={msg.Envelope.Crypto})");
                    // A handler that fails must not end the connection: the letter is lost, the next ones still arrive.
                    try { OnMessage?.Invoke(msg.Envelope); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ClientLog.Info("relay", $"{ClientLog.Host(_wsUrl ?? "")}: a message from {ClientLog.Short(msg.Envelope.From)} could not be handled ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                if (msg.Type == "server_state" && msg.Access != null)
                {
                    // The administrator changed who may sign up: what this server is shown as follows at once (the top bar reads it within seconds).
                    ServerAccess = msg.Access;
                    if (msg.Provisioners != null) ServerProvisioners = ValidProvisioners(msg.Provisioners);   // the administrator tools now named (an empty list: none)
                    ClientLog.Info("relay", $"{ClientLog.Host(_wsUrl ?? "")}: the server now says it is {msg.Access}");
                    continue;
                }

                if (msg.Type == "server_redirect" && msg.Servers is { Count: > 0 })
                {
                    ClientLog.Info("relay", $"{ClientLog.Host(_wsUrl ?? "")}: asked to move to another server ({msg.Reason ?? "load"}, {msg.Servers.Count} suggested)");
                    OnRedirect?.Invoke(msg.Servers, msg.Reason ?? "load");
                    continue;
                }

                if (msg.Type == "peer_online" && msg.DiscoveryId != null)
                {
                    OnPeerOnline?.Invoke(msg.DiscoveryId);
                    continue;
                }

                if (msg.RequestId != null && _pending.TryGetValue(msg.RequestId, out var tcs))
                    tcs.TrySetResult(msg);
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (WebSocketException) { /* connection dropped */ }
        finally
        {
            if (_connected) ClientLog.Info("relay", $"{ClientLog.Host(_wsUrl ?? "")}: connection lost");
            _connected = false;
            FailAllPending();
            OnDisconnected?.Invoke();
        }
    }

    /// <summary>
    /// A request awaiting a response when the connection drops would
    /// otherwise hang forever (the server can no longer answer it) — fail
    /// it instead so the caller (e.g. SendTextAsync) can fall back to the
    /// local outbox right away.
    /// </summary>
    private void FailAllPending()
    {
        foreach (var (id, tcs) in _pending)
        {
            if (tcs.TrySetException(new InvalidOperationException("Relay connection lost")))
                _pending.TryRemove(id, out _);
        }
    }

    private async Task<string?> ReceiveFullTextMessageAsync(byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* best effort */ }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { /* already logged via OnDisconnected */ }
        }

        _socket.Dispose();
        _cts.Dispose();
    }
}
