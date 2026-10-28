/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>What this server is towards other servers.</summary>
/// <param name="Kind">"relay" or "push".</param>
/// <param name="Kinds">The kinds of server this one may be linked with (a relay: push gateways; a gateway: relays).</param>
/// <param name="Name">How this server presents itself (<c>Peer:Name</c>, default the machine name).</param>
/// <param name="PublicUrl">The address other servers reach this one at (<c>Peer:PublicUrl</c>); sent with the requests it makes.</param>
/// <param name="Contact">How the other administrators can reach this one to check who is asking (<c>Peer:Contact</c>, free text such as a mail address); sent with the requests it makes.</param>
public sealed class PeerOptions(string kind, string[] kinds, string name, string? publicUrl, string? contact = null)
{
    public string Kind { get; } = kind;
    public string[] Kinds { get; } = kinds;
    /// <summary>Changed while the server runs (<c>admin self set</c>): read at each use.</summary>
    public string Name { get => _name; set => _name = value; }
    public string? PublicUrl { get => _publicUrl; set => _publicUrl = value; }
    public string? Contact { get => _contact; set => _contact = value; }
    private volatile string? _contact = contact;
    private volatile string _name = name;
    private volatile string? _publicUrl = publicUrl;
}

/// <summary>Body of POST /peer/request: a server asks another one for a link. The signature covers the text of <see cref="PeerSigning.RequestText"/>.</summary>
public sealed class PeerRequestBody
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("contact")] public string? Contact { get; set; }
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }
}

public sealed record PeerIdentityInfo(string Kind, string? Name, string PublicKey, string Fingerprint);

/// <summary>The outgoing side of linking servers: look the other one up, ask it, and learn its answer later.</summary>
public sealed class PeerClient
{
    private readonly IHttpClientFactory _http;
    private readonly PeerIdentity _identity;
    private readonly PeerOptions _options;
    private readonly PeerStore _store;
    private readonly ILogger<PeerClient> _logger;
    private readonly LinkStatus? _links;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _checkFailures = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _alerted = new();
    private const int FailuresToAlert = 3;

    public PeerClient(IHttpClientFactory http, PeerIdentity identity, PeerOptions options, PeerStore store, ILogger<PeerClient> logger, LinkStatus? links = null)
        => (_http, _identity, _options, _store, _logger, _links) = (http, identity, options, store, logger, links);

    /// <summary>The server may veto a kind of link for now (a private relay takes no part in server-to-server traffic): returns why, or null.</summary>
    public Func<string, string?>? Precheck { get; set; }

    public sealed class PeerException(string code, string message, int status = 400) : Exception(message)
    {
        public string Code { get; } = code;
        public int Status { get; } = status;
    }

    /// <summary>https is required; plain http only towards this machine or a private network (or when the administrator insists).</summary>
    public static bool IsAcceptableUrl(string? url, bool insecure, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 200 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (u.Scheme == Uri.UriSchemeHttp && !insecure && !IsPrivateHost(u.Host)) return false;
        uri = u;
        return true;
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Asks a server who it is (<c>GET /peer/identity</c>) and checks the answer is coherent: a FreeSpeech server of the kind expected, whose fingerprint is the one of the key it shows.</summary>
    public async Task<PeerIdentityInfo> FetchIdentityAsync(string url, string expectedKind, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.CreateClient("peer").GetAsync(url.TrimEnd('/') + "/peer/identity", ct);
            if (!response.IsSuccessStatusCode) throw new PeerException("not_a_peer", $"{url} did not answer like a FreeSpeech server ({(int)response.StatusCode}) — an older version, or the address is wrong", 502);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            string? Get(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var kind = Get("kind"); var publicKey = Get("public_key");
            if (Get("software") != "FreeSpeech" || kind == null || !PeerIdentity.IsValidPublicKey(publicKey)) throw new PeerException("not_a_peer", $"{url} did not present a FreeSpeech identity", 502);
            if (kind != expectedKind) throw new PeerException("wrong_kind", $"{url} is a {kind} server, not a {expectedKind} server", 400);
            var fingerprint = PeerIdentity.FingerprintOf(publicKey!);
            if (Get("fingerprint") != fingerprint) throw new PeerException("bad_identity", $"{url} presented an incoherent identity", 502);
            return new PeerIdentityInfo(kind, PeerStore.Clean(Get("name"), 64), publicKey!, fingerprint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new PeerException("unreachable", $"cannot reach {url}: {ex.Message}", 502);
        }
    }

    /// <summary>
    /// <c>add &lt;kind&gt; &lt;url&gt;</c>: looks the server up, pins its key (the fingerprint is shown to the administrator, and may be given to
    /// check it) and sends it a signed request. Its administrator has to accept; the answer is learnt later (<see cref="RefreshAsync"/>).
    /// </summary>
    public async Task<Peer> AddAsync(string kind, string url, string? fingerprint, bool insecure, CancellationToken ct = default)
    {
        if (!_options.Kinds.Contains(kind)) throw new PeerException("kind", $"a {_options.Kind} server links with: {string.Join(", ", _options.Kinds)}");
        if (Precheck?.Invoke(kind) is { } unavailable) throw new PeerException("not_available", unavailable);
        // The other server will call this one (a relay calls its gateway and its sibling relays; a gateway is never the caller of a relay,
        // but a relay that accepts a gateway needs its address): it must be told this server's address.
        if ((_options.Kind == "push" || kind is "relay" or "directory") && string.IsNullOrEmpty(_options.PublicUrl))
            throw new PeerException("public_url", $"set \"Peer:PublicUrl\" in {Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json")} first: the other server needs this server's address to call it");
        if (!IsAcceptableUrl(url, insecure, out var uri)) throw new PeerException("url", "the address must be https:// (http:// only for this machine or a private network, or with --insecure)");
        url = uri!.ToString().TrimEnd('/');

        var info = await FetchIdentityAsync(url, kind, ct);
        if (!string.IsNullOrWhiteSpace(fingerprint) && !info.Fingerprint.StartsWith(PeerStore.Norm(fingerprint), StringComparison.Ordinal))
            throw new PeerException("fingerprint", $"the server at {url} has fingerprint {info.Fingerprint[..16]}…, not the one you gave — refused");

        if (info.Fingerprint == _identity.Fingerprint) throw new PeerException("self", "that is this server itself");
        var peer = _store.AddOutgoing(kind, info.Name, url, info.PublicKey);
        if (peer.State == PeerState.Accepted) return peer;   // it had already asked me: my request is my yes
        await SendRequestAsync(peer, ct);
        return _store.Find(peer.Id) ?? peer;
    }

    private async Task SendRequestAsync(Peer peer, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = _options.PublicUrl ?? "";
        var body = new PeerRequestBody
        {
            Kind = _options.Kind, Name = _options.Name, Url = url, Contact = _options.Contact, PublicKey = _identity.PublicKey, Ts = ts,
            Sig = _identity.Sign(PeerSigning.RequestText(_options.Kind, _options.Name, url, ts, _options.Contact)),
        };
        try
        {
            using var response = await _http.CreateClient("peer").PostAsJsonAsync(peer.Url + "/peer/request", body, ct);
            var state = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)) is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("state", out var s) ? s.GetString() : null;
            switch (state)
            {
                case "pending": _store.MarkSent(peer.Id); break;
                case "accepted": _store.MarkSent(peer.Id); _store.SetState(peer.Id, PeerState.Accepted); break;
                case "banned": _store.MarkSent(peer.Id); _store.SetState(peer.Id, PeerState.Refused); throw new PeerException("banned", "that server refuses this one", 403);
                case "closed": _store.Remove(peer.Id); throw new PeerException("closed", "that server does not take requests at the moment", 403);
                case "full": _store.Remove(peer.Id); throw new PeerException("full", "that server's queue of requests is full, try later", 429);
                default: throw new PeerException("refused", $"that server answered {(int)response.StatusCode}", 502);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Not delivered: the peer stays "pending_out" and the refresh loop sends the request again.
            _logger.LogWarning("Request to {Peer} not delivered yet: {Message}", peer.Url, ex.Message);
        }
    }

    /// <summary>Learns the answer of every server I asked: accepted, still waiting, or turned down. Requests that did not get through are sent again.</summary>
    /// <param name="includeAccepted">Also asks every linked server whether it still knows this one: one that forgot or banned it is shown as refused (no call to it could succeed any more).</param>
    public async Task RefreshAsync(bool includeAccepted = false, CancellationToken ct = default, TimeSpan? skipIfWorkedWithin = null)
    {
        if (includeAccepted)
            foreach (var peer in _options.Kinds.SelectMany(_store.Accepted).Where(p => p.Url.Length > 0).ToList())
            {
                // An exchange that worked a moment ago says as much as a check would: no call is made.
                if (skipIfWorkedWithin is { } within && _links?.Get(peer.Url) is { LastOk: { } ok } s && DateTime.UtcNow - ok < within && (s.LastFail == null || s.LastFail < ok)) { Settle(peer, null); continue; }
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, peer.Url + "/peer/status") { Content = new ByteArrayContent([]) };
                    PeerSigning.Sign(request, _identity, "/peer/status", []);
                    using var response = await _http.CreateClient("peer").SendAsync(request, ct);
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                    {
                        _store.SetState(peer.Id, PeerState.Refused);
                        _logger.LogWarning("Peer {Peer} no longer knows this server: marked refused", peer.Url);
                    }
                    Settle(peer, response.IsSuccessStatusCode ? null : response.StatusCode == HttpStatusCode.TooManyRequests ? "throttled" : (int)response.StatusCode >= 500 ? "error " + (int)response.StatusCode : "refused " + (int)response.StatusCode);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Settle(peer, "unreachable"); /* unreachable is not a refusal: nothing else changes */ }
            }

        foreach (var peer in _store.PendingOut())
        {
            try
            {
                if (!peer.Sent) { await SendRequestAsync(peer, ct); continue; }

                using var request = new HttpRequestMessage(HttpMethod.Post, peer.Url + "/peer/status") { Content = new ByteArrayContent([]) };
                PeerSigning.Sign(request, _identity, "/peer/status", []);
                using var response = await _http.CreateClient("peer").SendAsync(request, ct);
                var state = response.StatusCode == HttpStatusCode.NotFound ? "unknown"
                    : (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)) is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state == "accepted") _store.SetState(peer.Id, PeerState.Accepted);
                else if (state is "unknown" or "banned") _store.SetState(peer.Id, PeerState.Refused);
            }
            catch (PeerException) { /* answered no: already recorded */ }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogDebug("Peer {Peer} not reachable for now: {Message}", peer.Url, ex.Message);
            }
        }
    }

    /// <summary>
    /// Records the result of a check (<paramref name="failure"/> null = it worked) and tells the administrator when a link has failed several checks in a
    /// row, then when it answers again. One failed check says nothing: a server restarts.
    /// </summary>
    private void Settle(Peer peer, string? failure)
    {
        if (failure == null)
        {
            _links?.Ok(peer.Url);
            _checkFailures[peer.Id] = 0;
            if (_alerted.TryRemove(peer.Id, out _))
                Notifier.Raise(NotifyKind.Peers, $"Link restored: {peer.Name ?? peer.Url}", $"The {peer.Kind} server {peer.Url} answers again.");
            return;
        }
        _links?.Fail(peer.Url, failure);
        var failures = _checkFailures.AddOrUpdate(peer.Id, 1, (_, n) => n + 1);
        if (failures >= FailuresToAlert && _alerted.TryAdd(peer.Id, true))
            Notifier.Raise(NotifyKind.Peers, $"Link down: {peer.Name ?? peer.Url}", $"The {peer.Kind} server {peer.Url} failed {failures} checks in a row (last: {failure}).");
    }

    public enum Claim { Verified, Unverified, Mismatch }

    /// <summary>
    /// Before accepting a request: the address it gives must show the key it asked with (so nobody can ask in the name of an address they do
    /// not hold). Unreachable or no address: accepted still, but reported as unverified. Called only once the administrator chose to accept.
    /// </summary>
    public async Task<Claim> VerifyClaimAsync(Peer peer, CancellationToken ct = default)
    {
        if (!IsAcceptableUrl(peer.Url, insecure: false, out _)) return Claim.Unverified;
        try { return (await FetchIdentityAsync(peer.Url, peer.Kind, ct)).Fingerprint == peer.Id ? Claim.Verified : Claim.Mismatch; }
        catch (PeerException ex) when (ex.Code is "unreachable" or "not_a_peer") { return Claim.Unverified; }
        catch (PeerException) { return Claim.Mismatch; }
    }
}

/// <summary>
/// Learns the answer of the servers this one asked and sends again a request that did not get through (every 20 s while some wait), and checks the linked
/// servers every <see cref="PeerCheckSettings.Seconds"/> seconds (a server that forgot this one is shown as refused; one that stops answering is reported).
/// </summary>
public sealed class PeerRefreshService(PeerClient client, PeerStore store, PeerCheckSettings check) : BackgroundService
{
    private static readonly TimeSpan PendingEvery = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastFull = DateTime.UtcNow;
        var lastPending = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var now = DateTime.UtcNow;
            var every = TimeSpan.FromSeconds(check.Seconds);
            var full = now - lastFull >= every;
            if (full) lastFull = now;
            var pending = store.PendingOut().Count > 0 && now - lastPending >= PendingEvery;
            if (pending) lastPending = now;
            if (full || pending) await client.RefreshAsync(full, stoppingToken, full ? every : null);
        }
    }
}
