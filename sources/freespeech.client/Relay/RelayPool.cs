/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using FreeSpeech.Client.Contacts;

namespace FreeSpeech.Client.Relay;

/// <summary>Outcome of <see cref="RelayPool.SendAsync"/>. <see cref="AnyReachable"/> false = no server could even be contacted (network problem); true with <see cref="Delivered"/> false = recipient absent everywhere we could reach.</summary>
public record PoolSendResult(bool Delivered, bool AnyReachable, IReadOnlyList<string> ReachableUrls, string? DeliveredVia, bool SelfMultiDevice);

/// <summary>
/// Multi-server client (protocol §8): one <see cref="RelayClient"/> per
/// server URL. "Own" servers (our advertised list) are registered on
/// permanently and re-connected by <see cref="MaintainAsync"/>; any other
/// server (a contact's) is connected to lazily when a send needs it, and
/// dropped rather than reconnected if it later fails. An unreachable server
/// is skipped for a short while so a dead URL doesn't cost a timeout on
/// every single send.
/// </summary>
public class RelayPool : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnreachableBackoff = TimeSpan.FromSeconds(30);
    private const int SeenCapacity = 500;

    private sealed class Entry
    {
        public required string Url { get; init; }
        public required RelayClient Client { get; init; }
        public required HttpClient Http { get; init; }
    }

    private readonly string _discoveryId;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, DateTime> _unreachableUntil = new();
    private readonly HashSet<string> _own = [];
    private readonly object _lock = new();

    // The same envelope can legitimately reach us through two servers (sender fell back
    // mid-send, or we're registered on both) — drop the duplicate before the app sees it.
    /// <summary>Raised for an envelope seen before (the same letter sent again, or delivered by two servers): it is not handled twice, but its sender may need to be told it was kept.</summary>
    public event Action<Envelope>? OnDuplicate;

    /// <summary>Test aid: envelopes that arrive are dropped unseen, as if the connection they came through was dead.</summary>
    internal bool DropIncoming { get; set; }

    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();

    /// <summary>Raised for every server-pushed envelope, de-duplicated across servers.</summary>
    public event Action<Envelope>? OnMessage;

    /// <summary>A connection to this server URL dropped.</summary>
    public event Action<string>? OnDisconnected;

    /// <summary>A contact we have a message waiting for just connected to one of our servers (protocol §9).</summary>
    public event Action<string>? OnPeerOnline;

    /// <summary>Every WebSocket frame of every server (url, direction, JSON) and every HTTP call (url, "-> METHOD path", "<- status body"). For protocol debugging tools.</summary>
    public event Action<string, string, string>? OnFrame;

    /// <summary>One of our servers suggests moving to a sibling: (server url, candidates best first, reason).</summary>
    public event Action<string, IReadOnlyList<string>, string>? OnRedirect;

    /// <summary>A server refused a send for its volume limits: (server url, code "quota_exceeded" or "rate_limited", seconds until sending may resume). Raised once per refusal; sends to that server are held back until then.</summary>
    public event Action<string, string, int>? OnSendLimited;

    private readonly ConcurrentDictionary<string, DateTime> _limitedUntil = new();

    /// <summary>When sending to this server may resume, if it has refused me for its volume limits.</summary>
    public DateTime? LimitedUntil(string url) => _limitedUntil.TryGetValue(Normalize(url), out var t) && t > DateTime.UtcNow ? t : null;

    /// <summary>The smallest number of bytes I may still send today across my connected own servers (null when none of them has a quota).</summary>
    public long? QuotaLeft => OwnUrls.Select(u => _entries.TryGetValue(u, out var e) ? e.Client.QuotaLeft : null).Where(q => q != null).Min();

    private PushTokenInfo? _push;

    /// <summary>Sets (or clears, with null) the push registration on every server, now and after each reconnect.</summary>
    public async Task SetPushAsync(PushTokenInfo? info)
    {
        _push = info;
        foreach (var e in _entries.Values)
        {
            e.Client.Push = info;
            if (info == null) await e.Client.UnregisterPushAsync();
            else if (e.Client.IsConnected) await e.Client.RegisterPushAsync();
        }
    }

    /// <summary>An own server that had dropped is registered again.</summary>
    public event Action<string>? OnReconnected;

    /// <summary>An own server told who may sign up on it: its URL and "open" or "allowlist".</summary>
    public event Action<string, string?>? OnServerAccess;

    /// <summary>A private server that had my access request waiting now lets me in (the administrator accepted it): the URL.</summary>
    public event Action<string>? OnAccessGranted;

    private readonly RegisterAuth? _auth;

    public RelayPool(string discoveryId, RegisterAuth? auth = null)
    {
        _discoveryId = discoveryId;
        _auth = auth;
    }

    public static string Normalize(string url) => url.Trim().TrimEnd('/');

    public IReadOnlyCollection<string> OwnUrls { get { lock (_lock) return _own.ToList(); } }

    public bool IsConnected(string url) => _entries.TryGetValue(Normalize(url), out var e) && e.Client.IsConnected;

    /// <summary>True when at least one own server currently has a live connection.</summary>
    public bool AnyOwnConnected => OwnUrls.Any(IsConnected);

    /// <summary>
    /// Ordered candidate URLs to reach a contact: their advertised servers by
    /// priority (§8), then our own servers as a last resort — a public own
    /// server can still reach them through the S2S cascade (§10).
    /// </summary>
    public IReadOnlyList<string> Candidates(IEnumerable<ServerEntry>? theirServers)
    {
        var list = new List<string>();
        if (theirServers != null)
            list.AddRange(theirServers.OrderBy(s => s.Priority).Select(s => Normalize(s.Url)));
        list.AddRange(OwnUrls);
        return list.Distinct().ToList();
    }

    // ------------------------------------------------------------------
    // Own servers
    // ------------------------------------------------------------------

    /// <summary>Registers a URL as one of ours and tries to connect now — never throws; a failure is retried by <see cref="MaintainAsync"/>.</summary>
    public async Task AddOwnAsync(string url, string? invitation = null)
    {
        url = Normalize(url);
        lock (_lock) _own.Add(url);
        // Invitation of a private server (§10 "Access control"): single use, so it only matters until the first registration succeeds — kept in memory only.
        if (!string.IsNullOrWhiteSpace(invitation))
        {
            _invitations[url] = invitation.Trim();
            if (_entries.TryGetValue(url, out var known)) known.Client.Invitation = invitation.Trim();   // a server that already turned me away
        }
        _unreachableUntil.TryRemove(url, out _);
        await GetEntryAsync(url);
    }

    /// <summary>
    /// The user agreed to ask the administrator of a private server for access (§10 "Access requests"): the next registration on this
    /// server carries my name and a note. Returns the refusal afterwards: "access_pending" (waiting), "private_server" (queue closed or
    /// full), "access_refused" (banned) or null (I am in).
    /// </summary>
    public async Task<string?> RequestAccessAsync(string url, string? name, string? note)
    {
        url = Normalize(url);
        lock (_lock) _own.Add(url);
        if (!_entries.TryGetValue(url, out var entry))
        {
            // Never reached yet: the first registration goes out with the request.
            _pendingAsks[url] = (name, note);
            _unreachableUntil.TryRemove(url, out _);
            await GetEntryAsync(url);
            return RefusalOf(url);
        }
        entry.Client.AccessRequest = (name, note);
        _unreachableUntil.TryRemove(url, out _);
        await GetEntryAsync(url);
        return RefusalOf(url);
    }

    private readonly ConcurrentDictionary<string, (string? Name, string? Note)> _pendingAsks = new();
    private readonly ConcurrentDictionary<string, string> _invitations = new();
    private readonly ConcurrentDictionary<string, string> _refusals = new();

    /// <summary>"private_server" (an invitation is needed) or "access_refused" (banned), when this server turned my last registration down; otherwise null.</summary>
    /// <summary>"open" or "allowlist" (private) as the server said when I last connected to it; null if never connected or it did not say.</summary>
    public string? AccessOf(string url) => _entries.TryGetValue(Normalize(url), out var entry) ? entry.Client.ServerAccess : null;

    /// <summary>True when a server I am connected to names this identity as one of its administrator's tools. Only a connected server counts: it is the server I joined that vouches.</summary>
    public bool IsProvisioner(string discoveryId) => FindProvisioner(discoveryId) != null;

    /// <summary>The identity and public keys of an administrator tool named by a server I am connected to, or null.</summary>
    public ProvisionerInfo? FindProvisioner(string discoveryId)
    {
        var id = discoveryId.ToLowerInvariant();
        return _entries.Values.Where(e => e.Client.IsConnected).SelectMany(e => e.Client.ServerProvisioners).FirstOrDefault(p => p.Id == id);
    }

    public string? RefusalOf(string url) => _refusals.TryGetValue(Normalize(url), out var r) ? r : null;

    public async Task RemoveOwnAsync(string url)
    {
        url = Normalize(url);
        lock (_lock) _own.Remove(url);
        await DropAsync(url);
    }

    /// <summary>Background tick: re-establishes every own server that is currently disconnected.</summary>
    public async Task MaintainAsync()
    {
        foreach (var url in OwnUrls)
        {
            if (IsConnected(url)) continue;
            _unreachableUntil.TryRemove(url, out _);
            await GetEntryAsync(url);
        }
    }

    // ------------------------------------------------------------------
    // Messaging
    // ------------------------------------------------------------------

    /// <summary>
    /// Tries the URLs in order (§8): unreachable → next immediately; reachable
    /// but recipient absent → also next (they may be registered only on a
    /// subset of their servers), remembering that this server was reachable
    /// so the caller can set a WAITING flag there.
    /// </summary>
    public async Task<PoolSendResult> SendAsync(IEnumerable<string> urls, Envelope envelope)
    {
        var reachable = new List<string>();
        foreach (var url in urls.Select(Normalize).Distinct())
        {
            if (LimitedUntil(url) != null) continue;   // refused for its volume limits a moment ago: not asked again before it said
            var entry = await GetEntryAsync(url);
            if (entry == null) continue;
            try
            {
                var r = await entry.Client.SendMessageAsync(envelope);
                reachable.Add(url);
                if (r.Delivered) return new(true, true, reachable, url, r.SelfMultiDevice);
            }
            catch (RelayLimitException limit)
            {
                _limitedUntil[url] = DateTime.UtcNow.AddSeconds(limit.RetryAfterSeconds);
                OnSendLimited?.Invoke(url, limit.Code, limit.RetryAfterSeconds);
            }
            catch
            {
                // Connection dropped mid-request — treat like unreachable.
            }
        }
        return new(false, reachable.Count > 0, reachable, null, false);
    }

    /// <summary>Sends on one specific server (e.g. the self-sync copy goes where the server said another device of ours is connected).</summary>
    public async Task SendOnAsync(string url, Envelope envelope)
    {
        var entry = await GetEntryAsync(Normalize(url)) ?? throw new InvalidOperationException($"{url} unreachable");
        await entry.Client.SendMessageAsync(envelope);
    }


    /// <summary>Protocol debugging: one raw WebSocket text frame to a server, exactly as given. Connects first when needed.</summary>
    public async Task SendRawAsync(string url, string json)
    {
        var entry = await GetEntryAsync(Normalize(url)) ?? throw new InvalidOperationException($"{url} is unreachable.");
        await entry.Client.SendRawAsync(json);
    }

    /// <summary>Protocol debugging: one raw HTTP call (flags, session endpoints) with this identity's token. Returns status and body.</summary>
    public async Task<(int Status, string Body)> RawHttpAsync(string url, string method, string path, string? json)
    {
        var entry = await GetEntryAsync(Normalize(url)) ?? throw new InvalidOperationException($"{url} is unreachable.");
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), path);
        if (json != null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await entry.Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }
    public async Task<bool> IsOnlineAsync(IEnumerable<string> urls, string discoveryId)
    {
        foreach (var url in urls.Select(Normalize).Distinct())
        {
            var entry = await GetEntryAsync(url);
            if (entry == null) continue;
            try
            {
                if (await entry.Client.IsOnlineAsync(discoveryId)) return true;
            }
            catch
            {
                // dropped mid-request — try the next one
            }
        }
        return false;
    }

    /// <summary>Runs an HTTP call (flags endpoints) against each reachable URL; unreachable ones come back with an error string instead of a response.</summary>
    public async Task<List<(string Url, HttpResponseMessage? Resp, string? Error)>> HttpAsync(
        IEnumerable<string> urls, Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        var results = new List<(string, HttpResponseMessage?, string?)>();
        foreach (var url in urls.Select(Normalize).Distinct())
        {
            var entry = await GetEntryAsync(url);
            if (entry == null) { results.Add((url, null, "unreachable")); continue; }
            try { results.Add((url, await call(entry.Http), null)); }
            catch (HttpRequestException ex) { results.Add((url, null, ex.Message)); }
        }
        return results;
    }

    // ------------------------------------------------------------------
    // Connection management
    // ------------------------------------------------------------------

    private async Task<Entry?> GetEntryAsync(string url)
    {
        if (_entries.TryGetValue(url, out var existing))
        {
            if (existing.Client.IsConnected) return existing;

            bool own;
            lock (_lock) own = _own.Contains(url);
            if (!own) await DropAsync(url); // foreign entry died — start over below
            else
            {
                using var cts = new CancellationTokenSource(ConnectTimeout);
                if (!await existing.Client.TryReconnectAsync(cts.Token)) { NoteRefusal(url, existing.Client.LastError); return null; }
                if (_refusals.TryRemove(url, out var was) && was == "access_pending") OnAccessGranted?.Invoke(url);   // the administrator said yes
                OnReconnected?.Invoke(url); // raised here, not only in MaintainAsync: a send can win the race against the tick
                return existing;
            }
        }

        if (_unreachableUntil.TryGetValue(url, out var until) && until > DateTime.UtcNow) return null;

        var entry = Create(url);
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await entry.Client.ConnectAsync($"{ToWsUrl(url)}/ws/relay", _discoveryId, cts.Token);
        }
        catch (Exception ex)
        {
            ClientLog.Info("pool", $"{ClientLog.Host(url)}: unreachable ({ex.GetType().Name}), retry in {UnreachableBackoff.TotalSeconds:0} s if not one of my servers");
            NoteRefusal(url, ex);
            bool own;
            lock (_lock) own = _own.Contains(url);
            if (own)
            {
                // Keep the (disconnected) client: MaintainAsync retries it via TryReconnectAsync.
                _entries[url] = entry;
            }
            else
            {
                _unreachableUntil[url] = DateTime.UtcNow + UnreachableBackoff;
                await entry.Client.DisposeAsync();
                entry.Http.Dispose();
            }
            return null;
        }

        _entries[url] = entry;
        _refusals.TryRemove(url, out _);
        _invitations.TryRemove(url, out _);   // spent (or not needed): the identity is known to the server now
        return entry;
    }

    private void NoteRefusal(string url, Exception? ex)
    {
        var message = ex?.Message ?? "";
        if (message.Contains("access_pending")) _refusals[url] = "access_pending";
        else if (message.Contains("private_server")) _refusals[url] = "private_server";
        else if (message.Contains("access_refused")) _refusals[url] = "access_refused";
        else _refusals.TryRemove(url, out _);
    }

    private Entry Create(string url)
    {
        var client = new RelayClient();
        client.OnMessage += env =>
        {
            if (DropIncoming) return;
            if (FirstTimeSeen(env)) OnMessage?.Invoke(env);
            else OnDuplicate?.Invoke(env);
        };
        client.OnDisconnected += () => OnDisconnected?.Invoke(url);
        client.OnAccessChanged += access => OnServerAccess?.Invoke(url, access);
        client.OnPeerOnline += id => OnPeerOnline?.Invoke(id);
        client.OnFrame += (dir, json) => OnFrame?.Invoke(url, dir, json);
        client.OnRedirect += (servers, reason) => OnRedirect?.Invoke(url, servers, reason);
        client.Push = _push;
        client.Auth = _auth;
        client.Invitation = _invitations.TryGetValue(url, out var invitation) ? invitation : null;
        if (_pendingAsks.TryRemove(url, out var ask)) client.AccessRequest = ask;

        var http = new HttpClient(new FlagTokenHandler(() => client.HttpToken, (dir, text) => OnFrame?.Invoke(url, "http " + dir, text)) { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.Add("X-Discovery-Id", _discoveryId);
        return new Entry { Url = url, Client = client, Http = http };
    }

    /// <summary>Adds the connection's current X-Flag-Token to every HTTP call (it changes whenever the relay connection is re-established).</summary>
    private sealed class FlagTokenHandler(Func<string?> token, Action<string, string>? tap = null) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (token() is { } t) { request.Headers.Remove("X-Flag-Token"); request.Headers.Add("X-Flag-Token", t); }
            tap?.Invoke("->", $"{request.Method} {request.RequestUri?.PathAndQuery}{(request.Content == null ? "" : " " + await request.Content.ReadAsStringAsync(ct))}");
            var response = await base.SendAsync(request, ct);
            if (tap != null) tap("<-", $"{(int)response.StatusCode} {(response.Content == null ? "" : await response.Content.ReadAsStringAsync(ct))}");
            return response;
        }
    }

    private async Task DropAsync(string url)
    {
        if (!_entries.TryRemove(url, out var e)) return;
        await e.Client.DisposeAsync();
        e.Http.Dispose();
    }

    private bool FirstTimeSeen(Envelope env)
    {
        var key = $"{env.From}|{env.To}|{env.Ts}|{env.Crypto}";
        lock (_seen)
        {
            if (!_seen.Add(key)) return false;
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > SeenCapacity) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private static string ToWsUrl(string httpUrl) =>
        httpUrl.StartsWith("https://") ? "wss://" + httpUrl[8..] : "ws://" + httpUrl[7..];

    public async ValueTask DisposeAsync()
    {
        foreach (var url in _entries.Keys.ToList()) await DropAsync(url);
    }
}

