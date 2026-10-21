/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Globalization;

namespace FreeSpeech.Client.Attachments;

/// <summary>A size in bytes written for a person: "850 KB", "12.4 MB", "1.2 GB" (powers of 1024), with the units of the language.</summary>
public static class ByteSize
{
    /// <summary>The units, smallest first, separated by '|' ("o|Ko|Mo|Go" in French).</summary>
    public const string EnglishUnits = "B|KB|MB|GB";

    public static string Format(long bytes, string? units = null)
    {
        var u = (units ?? EnglishUnits).Split('|');
        if (u.Length < 4) u = EnglishUnits.Split('|');

        double value = Math.Max(0, bytes);
        var i = 0;
        while (value >= 1024 && i < 3) { value /= 1024; i++; }
        var text = value.ToString(i == 0 || value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture);
        return $"{text} {u[i]}";
    }
}
