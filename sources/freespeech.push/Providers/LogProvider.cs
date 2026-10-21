/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Push.Providers;

/// <summary>Development stand-in: logs the wake-up instead of sending it. Enabled with <c>Push:Fake = true</c> for a platform that has no real provider.</summary>
public sealed class LogProvider : IPushProvider
{
    private readonly ILogger<LogProvider> _logger;

    public LogProvider(string platform, ILogger<LogProvider> logger) { Platform = platform; _logger = logger; }

    public string Platform { get; }

    public Task<PushOutcome> SendWakeAsync(string token, CancellationToken ct)
    {
        _logger.LogInformation("[fake {Platform}] wake-up sent (token not logged)", Platform);   // a device token never goes into a log, not even a prefix
        return Task.FromResult(token.StartsWith("dead", StringComparison.Ordinal) ? PushOutcome.InvalidToken : PushOutcome.Sent);   // "dead…" simulates an unregistered token
    }
}