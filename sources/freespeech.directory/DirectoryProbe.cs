/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;

namespace FreeSpeech.Directory;

/// <summary>
/// Looks at the address of every accepted relay (<c>GET /peer/identity</c>) and keeps whether it answers with the key it was accepted with. A relay is
/// offered to clients only after a successful look, and no longer once it fails one: a relay that moved, went down or changed hands is not sent to
/// newcomers. The look is made at the first heartbeat of a relay and then every <c>Directory:ProbeMinutes</c> (30).
/// </summary>
public sealed class DirectoryProbe(PeerStore peers, PeerClient client, DirectoryRegistry registry, DirectorySettings settings, ILogger<DirectoryProbe> logger) : BackgroundService
{
    public async Task ProbeAsync(Peer peer, CancellationToken ct = default)
    {
        var ok = false;
        try { ok = (await client.FetchIdentityAsync(peer.Url, "relay", ct)).Fingerprint == peer.Id; }
        catch (PeerClient.PeerException ex) { logger.LogDebug("Relay {Url} not seen: {Message}", peer.Url, ex.Message); }
        if (registry.IsProbed(peer.Id) && !ok) logger.LogWarning("Relay {Url} no longer answers with its key: not offered to clients", peer.Url);
        registry.SetReachable(peer.Id, ok);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(settings.Current.ProbeMinutes), stoppingToken); }
            catch (OperationCanceledException) { return; }
            foreach (var peer in peers.Accepted("relay").Where(p => p.Url.Length > 0)) await ProbeAsync(peer, stoppingToken);
        }
    }
}
