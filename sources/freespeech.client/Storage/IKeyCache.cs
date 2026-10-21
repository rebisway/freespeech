/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Client.Storage;

/// <summary>
/// Where the database key is kept between runs so the recovery phrases are typed only once. The key itself
/// is derived from the phrases (<see cref="Identity.IdentityService.DeriveDatabaseKey"/>), so losing the
/// cache costs nothing but re-entering them. Each platform plugs in its secure store: DPAPI on Windows,
/// Keychain on iOS, Keystore on Android.
/// </summary>
public interface IKeyCache
{
    /// <summary>The cached key, or null when there is none (or it cannot be read).</summary>
    byte[]? Load();

    void Store(byte[] key);
}

/// <summary>
/// Keeps the key in a plain file next to the database — no protection at all, so anyone who can read the
/// profile folder can read the data. For tests and tools; real front-ends provide an OS-backed cache.
/// </summary>
public sealed class PlainFileKeyCache(string profileDir) : IKeyCache
{
    private readonly string _path = Path.Combine(profileDir, "dbkey.bin");

    public byte[]? Load() => File.Exists(_path) ? File.ReadAllBytes(_path) : null;

    public void Store(byte[] key)
    {
        Directory.CreateDirectory(profileDir);
        File.WriteAllBytes(_path, key);
    }
}
