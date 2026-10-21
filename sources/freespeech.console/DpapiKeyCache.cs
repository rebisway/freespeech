/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Console;

/// <summary>
/// Keeps the database key protected by Windows DPAPI (current user): the file is useless to anyone else, or
/// on another machine — the recovery phrases then regenerate the key. A mobile front-end plugs the Keychain
/// or the Keystore in the same place.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DpapiKeyCache(string profileDir) : IKeyCache
{
    private readonly string _path = Path.Combine(profileDir, "dbkey.bin");

    public byte[]? Load()
    {
        if (!File.Exists(_path)) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }

    public void Store(byte[] key)
    {
        Directory.CreateDirectory(profileDir);
        File.WriteAllBytes(_path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
    }
}