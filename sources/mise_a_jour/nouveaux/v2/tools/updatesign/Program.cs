/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

// Makes and signs the update manifest the applications check (see scripts/make-update-manifest.ps1).
//   updatesign newkey <key-file>                                  creates the Ed25519 key (32-byte seed, base64) and prints its public key
//   updatesign publickey <key-file>                               prints the public key (base64) to put in FsUpdateKey
//   updatesign make <key-file> <version.props> <site> <out-dir>   writes <out-dir>/latest.json and latest.json.sig
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FreeSpeech.Client.Crypto;

static string PublicKey(string keyFile) => Convert.ToBase64String(Primitives.Ed25519PublicKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim())));

try
{
    switch (args.FirstOrDefault())
    {
        case "newkey" when args.Length == 2:
            if (File.Exists(args[1])) throw new InvalidOperationException($"{args[1]} already exists: a key is never replaced (the applications carry its public half).");
            File.WriteAllText(args[1], Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            Console.WriteLine("Public key (FsUpdateKey): " + PublicKey(args[1]));
            Console.WriteLine("Keep " + args[1] + " outside the repository and back it up: the applications trust this key for good.");
            return 0;

        case "publickey" when args.Length == 2:
            Console.WriteLine(PublicKey(args[1]));
            return 0;

        case "make" when args.Length == 5:
        {
            var props = XDocument.Load(args[2]).Descendants().Where(e => !e.HasElements).ToDictionary(e => e.Name.LocalName, e => e.Value.Trim());
            string P(string k) => props.TryGetValue(k, out var v) && v.Length > 0 ? v : throw new InvalidOperationException($"{k} is missing in {args[2]}");

            var programs = new Dictionary<string, object>
            {
                ["android-apk"] = new { version = P("FsMobileVersion"), build = int.Parse(P("FsMobileBuild")), date = P("FsMobileVersionDate") },
                ["windows"] = new { version = P("FsDesktopVersion"), date = P("FsDesktopVersionDate") },
                ["linux"] = new { version = P("FsDesktopVersion"), date = P("FsDesktopVersionDate") },
            };
            var manifest = new
            {
                format = 1,
                published = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                programs,
                download = args[3].TrimEnd('/') + "/{lang}/download/",
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var seed = Convert.FromBase64String(File.ReadAllText(args[1]).Trim());
            Directory.CreateDirectory(args[4]);
            File.WriteAllBytes(Path.Combine(args[4], "latest.json"), json);
            File.WriteAllText(Path.Combine(args[4], "latest.json.sig"), Convert.ToBase64String(Primitives.Ed25519Sign(seed, json)));
            Console.WriteLine("Written " + Path.Combine(args[4], "latest.json") + " (+ .sig), signed with the key " + PublicKey(args[1]));
            return 0;
        }

        default:
            Console.Error.WriteLine("Usage: updatesign newkey <key-file> | publickey <key-file> | make <key-file> <version.props> <site> <out-dir>");
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
