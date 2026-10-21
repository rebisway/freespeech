/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;

namespace FreeSpeech.Client;

/// <summary>
/// The <c>ts</c> of a message (protocol §5): UTC ticks, strictly increasing on this installation, whose lowest 12 bits are a
/// random tag drawn once per run. <c>message_id = from:ts</c> must stay unique even when two devices of the same identity send at
/// the same instant: they would need the same 0.4 ms slot AND the same tag (1 chance in 4096). Dates still read as dates.
/// </summary>
public static class MessageClock
{
    private const int TagBits = 12;
    private const long Step = 1L << TagBits;
    private static readonly long Tag = RandomNumberGenerator.GetInt32(1 << TagBits);
    private static readonly object Lock = new();
    private static long _last;

    public static long Next()
    {
        lock (Lock)
        {
            var slot = ((DateTime.UtcNow.Ticks >> TagBits) << TagBits) | Tag;
            _last = Math.Max(slot, _last + Step);   // never goes back, never repeats, keeps the tag
            return _last;
        }
    }
}