/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Relay.Services;

/// <summary>
/// Periodically purges expired WAITING/READY flags so the in-memory
/// dictionary does not grow unbounded between server restarts.
/// </summary>
public class FlagExpiryService : BackgroundService
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);

    private readonly IPendingFlagService _flags;

    public FlagExpiryService(IPendingFlagService flags)
    {
        _flags = flags;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PurgeInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _flags.PurgeExpired();
        }
    }
}
