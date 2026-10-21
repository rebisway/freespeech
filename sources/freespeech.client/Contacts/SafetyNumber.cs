/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using System.Text;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// The number two people compare to be sure nobody sits between them (protocol §3). It is a hash of both public key pairs,
/// ordered so that both sides compute the same one: if someone swapped the keys while the contact cards were exchanged, each
/// side holds a different key for the other and the two numbers differ. Compare them aloud or side by side.
/// </summary>
public static class SafetyNumber
{
    private const int Groups = 12;     // 12 groups of 5 digits = 60 digits, about 199 bits
    private const int Rounds = 5200;   // makes brute-forcing a look-alike pair of keys expensive, still instant for a phone

    /// <summary>"12345 67890 …" in four lines of three groups, identical on both sides.</summary>
    public static string Compute(byte[] myEnc, byte[] mySign, byte[] theirEnc, byte[] theirSign)
    {
        var mine = Concat(myEnc, mySign);
        var theirs = Concat(theirEnc, theirSign);
        var (first, second) = ByteCompare(mine, theirs) <= 0 ? (mine, theirs) : (theirs, mine);

        var data = Concat(Encoding.UTF8.GetBytes("FreeSpeech-safety-v1"), first, second);
        var hash = SHA512.HashData(data);
        for (var i = 1; i < Rounds; i++) hash = SHA512.HashData(Concat(hash, first, second));

        var sb = new StringBuilder();
        for (var g = 0; g < Groups; g++)
        {
            // 5 bytes per group, reduced to 5 digits.
            var value = ((ulong)hash[g * 5] << 32 | (ulong)hash[g * 5 + 1] << 24 | (ulong)hash[g * 5 + 2] << 16 | (ulong)hash[g * 5 + 3] << 8 | hash[g * 5 + 4]) % 100000;
            sb.Append(value.ToString("D5"));
            sb.Append(g % 3 == 2 ? (g == Groups - 1 ? "" : "\n") : " ");
        }
        return sb.ToString();
    }

    private static int ByteCompare(byte[] a, byte[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}
