/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreeSpeech.Push.Providers;

/// <summary>Minimal JWT signing for the two push networks (no Google or Apple library needed).</summary>
internal static class Jwt
{
    public static string SignRs256(object header, object claims, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return Sign(header, claims, data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static string SignEs256(object header, object claims, string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        return Sign(header, claims, data => ec.SignData(data, HashAlgorithmName.SHA256));   // IEEE P1363 (r||s), as JWT wants
    }

    private static string Sign(object header, object claims, Func<byte[], byte[]> sign)
    {
        var input = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        return input + "." + B64(sign(Encoding.ASCII.GetBytes(input)));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}