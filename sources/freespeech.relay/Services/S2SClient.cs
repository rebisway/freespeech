/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FreeSpeech.Relay.Models;

namespace FreeSpeech.Relay.Services;

public class S2SClient : IS2SClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<S2SClient> _logger;

    private readonly bool _allowInsecure;
    private readonly FreeSpeech.Ops.PeerIdentity _identity;
    private readonly FreeSpeech.Ops.LinkStatus _links;

    public S2SClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, FreeSpeech.Ops.PeerIdentity identity, FreeSpeech.Ops.LinkStatus links, ILogger<S2SClient> logger)
    {
        _identity = identity;
        _links = links;
        _allowInsecure = configuration.GetValue("Server:AllowInsecureSiblings", false);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> LookupAsync(Sibling sibling, string discoveryId, CancellationToken ct)
    {
        try
        {
            using var response = await PostSignedAsync(sibling, "/s2s/lookup", new { discovery_id = discoveryId }, ct);
            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<LookupResponse>(ct);
            return result?.Online ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("S2S lookup to {Url} failed: {Message}", sibling.Url, ex.Message);
            return false;
        }
    }

    public async Task<bool> RelayAsync(Sibling sibling, Envelope envelope, CancellationToken ct)
    {
        try
        {
            using var response = await PostSignedAsync(sibling, "/s2s/relay", new { envelope }, ct);
            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<RelayResponse>(ct);
            return result?.Delivered ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("S2S relay to {Url} failed: {Message}", sibling.Url, ex.Message);
            return false;
        }
    }

    public async Task<int?> CapacityAsync(Sibling sibling, CancellationToken ct)
    {
        try
        {
            using var response = await PostSignedAsync(sibling, "/s2s/capacity", new { }, ct);
            if (!response.IsSuccessStatusCode) return null;
            return (await response.Content.ReadFromJsonAsync<CapacityResponse>(ct))?.FreeSlots;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("S2S capacity to {Url} failed: {Message}", sibling.Url, ex.Message);
            return null;
        }
    }

    public async Task PropagateFlagAsync(Sibling sibling, PendingFlag flag, CancellationToken ct)
    {
        try
        {
            using var response = await PostSignedAsync(sibling, "/s2s/flag", flag, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("S2S flag propagation to {Url} failed: {Message}", sibling.Url, ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>POST signed with this server's key (see <see cref="FreeSpeech.Ops.PeerSigning"/>).</summary>
    private async Task<HttpResponseMessage> PostSignedAsync(Sibling sibling, string path, object payload, CancellationToken ct)
    {
        if (!S2SAuth.IsAcceptableUrl(sibling.Url, _allowInsecure, out var error))
            throw new InvalidOperationException($"sibling {sibling.Url} refused: {error}");

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var http = _httpClientFactory.CreateClient("S2S");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(sibling.Url), path)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        FreeSpeech.Ops.PeerSigning.Sign(request, _identity, path, body);
        try
        {
            var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) _links.Ok(sibling.Url); else _links.Fail(sibling.Url, "refused " + (int)response.StatusCode);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _links.Fail(sibling.Url, "unreachable");
            throw;
        }
    }

    private class LookupResponse { public bool Online { get; set; } }
    private class RelayResponse { public bool Delivered { get; set; } }
    private class CapacityResponse { [System.Text.Json.Serialization.JsonPropertyName("free_slots")] public int FreeSlots { get; set; } }
}
