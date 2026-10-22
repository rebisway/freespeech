/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Client;

/// <summary>
/// Protocol debugging: what goes over the wire and a way to send what the client would never send itself. Used by the console's
/// <c>raw</c> command to test a server or another implementation; applications do not need it.
/// </summary>
public sealed partial class FreeSpeechClient
{
    /// <summary>Every frame of every server: url, direction ("->", "<-", "http ->", "http <-") and the JSON or the HTTP line.</summary>
    public event Action<string, string, string>? WireFrame
    {
        add => _relay.OnFrame += value;
        remove => _relay.OnFrame -= value;
    }

    /// <summary>Sends <paramref name="json"/> to a server as one WebSocket frame, as is (invalid frames included). The answer shows through <see cref="WireFrame"/>.</summary>
    public Task SendRawFrameAsync(string url, string json) => _relay.SendRawAsync(url, json);

    /// <summary>One HTTP call to a server's endpoints (flags, sessions) with this identity's token.</summary>
    public Task<(int Status, string Body)> SendRawHttpAsync(string url, string method, string path, string? json = null) => _relay.RawHttpAsync(url, method, path, json);
}
