/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreeSpeech.Push.Providers;

/// <summary>
/// Firebase Cloud Messaging (HTTP v1). A high-priority, data-only message: Android starts the app's process
/// for a short while, the app connects and fetches (protocol §9). Credentials: a service-account JSON file
/// (<c>Fcm:ServiceAccountFile</c>) kept outside the repository.
/// </summary>
public sealed class FcmProvider : IPushProvider
{
    private readonly HttpClient _http;
    private readonly string _projectId, _clientEmail, _privateKey;
    private string? _accessToken;
    private DateTime _accessExpiry;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly ILogger<FcmProvider> _logger;

    public string Platform => "fcm";

    public FcmProvider(HttpClient http, string serviceAccountFile, ILogger<FcmProvider> logger)
    {
        _http = http;
        _logger = logger;
        using var doc = JsonDocument.Parse(File.ReadAllText(serviceAccountFile));
        _projectId = doc.RootElement.GetProperty("project_id").GetString()!;
        _clientEmail = doc.RootElement.GetProperty("client_email").GetString()!;
        _privateKey = doc.RootElement.GetProperty("private_key").GetString()!;
    }

    public async Task<PushOutcome> SendWakeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_projectId}/messages:send")
            {
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        token,
                        data = new { kind = "wake" },
                        // collapse_key: wake-ups waiting for an unreachable phone merge into one; ttl: a phone off for longer than a week is
                        // woken by its periodic check or when the app is opened.
                        android = new { priority = "HIGH", collapse_key = "wake", ttl = "604800s" },
                    },
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return PushOutcome.Sent;

            var body = await response.Content.ReadAsStringAsync(ct);
            // UNREGISTERED (404) and INVALID_ARGUMENT on the token (400) mean the token is dead.
            if (response.StatusCode == HttpStatusCode.NotFound || (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
                return PushOutcome.InvalidToken;

            _logger.LogWarning("FCM answered {Status}: {Body}", (int)response.StatusCode, body);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("FCM unreachable: {Message}", ex.Message);
            return PushOutcome.Failed;
        }
    }

    /// <summary>OAuth2 access token from the service account (JWT bearer grant), cached until shortly before expiry.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpiry) return _accessToken;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var jwt = Jwt.SignRs256(new { alg = "RS256", typ = "JWT" },
                new { iss = _clientEmail, scope = "https://www.googleapis.com/auth/firebase.messaging", aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 },
                _privateKey);

            using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt,
            }), ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            _accessExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 120);
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }
}