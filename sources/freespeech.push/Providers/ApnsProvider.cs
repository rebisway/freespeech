/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Net;
using System.Text;
using System.Text.Json;

namespace FreeSpeech.Push.Providers;

/// <summary>
/// Apple Push Notification service (token-based auth, HTTP/2). iOS does not reliably wake an app for a silent
/// push, so this sends a visible but generic alert with <c>mutable-content</c>: the app's notification service
/// extension may then run briefly to fetch and replace the text (protocol §9). Credentials: the .p8 key
/// (<c>Apns:KeyFile</c>), its key id, the team id and the bundle id.
/// </summary>
public sealed class ApnsProvider : IPushProvider
{
    private readonly HttpClient _http;
    private readonly string _keyPem, _keyId, _teamId, _bundleId, _host;
    private string? _jwt;
    private DateTime _jwtExpiry;
    private readonly ILogger<ApnsProvider> _logger;

    public string Platform => "apns";

    public ApnsProvider(HttpClient http, string keyFile, string keyId, string teamId, string bundleId, bool production, ILogger<ApnsProvider> logger)
    {
        _http = http;
        _keyPem = File.ReadAllText(keyFile);
        _keyId = keyId;
        _teamId = teamId;
        _bundleId = bundleId;
        _host = production ? "https://api.push.apple.com" : "https://api.sandbox.push.apple.com";
        _logger = logger;
    }

    public async Task<PushOutcome> SendWakeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/3/device/{token}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent("{\"aps\":{\"alert\":{\"title\":\"FreeSpeech\",\"body\":\"\\u2026\"},\"mutable-content\":1,\"sound\":\"default\"}}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("authorization", "bearer " + Jwt_());
            request.Headers.Add("apns-topic", _bundleId);
            request.Headers.Add("apns-push-type", "alert");
            request.Headers.Add("apns-priority", "10");

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return PushOutcome.Sent;
            if (response.StatusCode == HttpStatusCode.Gone) return PushOutcome.InvalidToken;

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("BadDeviceToken") || body.Contains("Unregistered") || body.Contains("DeviceTokenNotForTopic")) return PushOutcome.InvalidToken;

            _logger.LogWarning("APNs answered {Status}: {Body}", (int)response.StatusCode, body);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("APNs unreachable: {Message}", ex.Message);
            return PushOutcome.Failed;
        }
    }

    /// <summary>Provider token, renewed every 40 minutes (Apple accepts 20–60).</summary>
    private string Jwt_()
    {
        if (_jwt != null && DateTime.UtcNow < _jwtExpiry) return _jwt;
        _jwt = Jwt.SignEs256(new { alg = "ES256", kid = _keyId }, new { iss = _teamId, iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, _keyPem);
        _jwtExpiry = DateTime.UtcNow.AddMinutes(40);
        return _jwt;
    }
}