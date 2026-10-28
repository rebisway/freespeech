/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Ops;

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Tells every directory this relay is linked with (<c>add directory &lt;url&gt;</c>, accepted by the directory's administrator) how it is, so that the
/// directory can offer it to clients who know no relay: its country (<c>settings set country</c>), the places it has left (up to
/// <c>max-connections</c>; -1 when it states no limit) and whether it takes new accounts. A private relay (<c>admin private</c>) says nothing: it is
/// not meant for strangers. It takes new accounts while it is public, its client access is open and it has room.
/// <c>Directory:HeartbeatSeconds</c> (default 120) sets the rhythm.
/// </summary>
public sealed class DirectoryHeartbeatService(PeerStore peers, PeerIdentity identity, IHttpClientFactory http, IPresenceService presence, ISiblingRegistry siblings,
    IClientAccess access, LinkStatus links, RelaySettings settings, IConfiguration config, ILogger<DirectoryHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Directory:HeartbeatSeconds", 120), 5, 3600));
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(first ? TimeSpan.FromSeconds(Math.Min(5, every.TotalSeconds)) : every, stoppingToken); }
            catch (OperationCanceledException) { return; }
            first = false;
            await SendOnceAsync(stoppingToken);
        }
    }

    public async Task SendOnceAsync(CancellationToken ct)
    {
        var directories = peers.Accepted("directory").Where(d => d.Url.Length > 0).ToList();
        if (directories.Count == 0 || !siblings.IsPublic) return;

        var max = settings.Current.MaxConnections;
        var free = max > 0 ? Math.Max(0, max - await presence.GetRegisteredCountAsync()) : -1;
        var body = JsonSerializer.SerializeToUtf8Bytes(new HeartbeatBody
        {
            Country = DirectoryRegistry.CleanCountry(settings.Current.Country),
            Free = free,
            AcceptsNew = access.Mode == ClientAccessService.Open && free != 0,
            Version = ReleaseInfo.Version,
            Date = ReleaseInfo.Date,
        });
        foreach (var directory in directories)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, directory.Url + "/directory/heartbeat") { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new("application/json");
                PeerSigning.Sign(request, identity, "/directory/heartbeat", body);
                using var response = await http.CreateClient("peer").SendAsync(request, ct);
                if (response.IsSuccessStatusCode) links.Ok(directory.Id);
                else
                {
                    links.Fail(directory.Id, "refused " + (int)response.StatusCode);
                    logger.LogWarning("Directory {Url} refused the heartbeat ({Status})", directory.Url, (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                links.Fail(directory.Id, "unreachable");
                logger.LogDebug("Directory {Url} not reachable: {Message}", directory.Url, ex.Message);
            }
        }
    }
}
