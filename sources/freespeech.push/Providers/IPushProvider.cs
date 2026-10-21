/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Push.Providers;

public enum PushOutcome
{
    /// <summary>Handed to the push network.</summary>
    Sent,
    /// <summary>The network says this token is dead (app uninstalled, token rotated): the relay server must forget it.</summary>
    InvalidToken,
    /// <summary>Temporary failure (network, credentials, quota): nothing to forget, try again later.</summary>
    Failed,
}

/// <summary>
/// One push network (protocol §9). The gateway only knows this interface: adding a network means one new class
/// registered in <c>Program.cs</c>, no change to the endpoint, the relay servers or the clients.
/// The payload is always generic — a wake-up, never a sender or a preview.
/// </summary>
public interface IPushProvider
{
    /// <summary>Value of <c>platform</c> in the request: "fcm", "apns"…</summary>
    string Platform { get; }

    Task<PushOutcome> SendWakeAsync(string token, CancellationToken ct);
}