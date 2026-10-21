/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;

namespace FreeSpeech.Client.Security;

/// <summary>
/// The app lock's pure logic (PROTOCOL §1, "App lock"): how a PIN is stored and checked, and when the lock is due.
/// The screen, the storage and the counters live in the app; nothing here touches the network or the profile.
/// </summary>
public static class PinLock
{
    public const int MinDigits = 4;
    public const int MaxDigits = 8;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 200_000;

    /// <summary>A PIN is 4 to 8 digits.</summary>
    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= MinDigits and <= MaxDigits } && pin.All(char.IsAsciiDigit);

    /// <summary>The value to store for a PIN: a fresh random salt and the PBKDF2-SHA256 hash, base64 of salt ‖ hash. The PIN itself is never stored.</summary>
    public static string Create(string pin)
    {
        if (!IsValidPin(pin)) throw new ArgumentException("The PIN must be 4 to 8 digits.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var stored = new byte[SaltBytes + HashBytes];
        salt.CopyTo(stored, 0);
        Hash(pin, salt).CopyTo(stored, SaltBytes);
        return Convert.ToBase64String(stored);
    }

    /// <summary>True when <paramref name="pin"/> is the PIN behind <paramref name="stored"/> (constant-time comparison).</summary>
    public static bool Verify(string pin, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !IsValidPin(pin)) return false;
        byte[] raw;
        try { raw = Convert.FromBase64String(stored); }
        catch (FormatException) { return false; }
        if (raw.Length != SaltBytes + HashBytes) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(pin, raw.AsSpan(0, SaltBytes)), raw.AsSpan(SaltBytes));
    }

    private static byte[] Hash(string pin, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    /// <summary>
    /// The lock is due once the app has been out of use for <paramref name="delay"/>. A clock set back (last use "in the future") counts
    /// as due: moving the clock must not be a way around the lock. A zero delay means "every time", with a short grace so a system
    /// dialog (file picker, permission) the app itself opened does not lock it on return.
    /// </summary>
    public static bool IsDue(DateTime lastActiveUtc, DateTime nowUtc, TimeSpan delay)
    {
        if (lastActiveUtc > nowUtc) return true;
        var limit = delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : delay;
        return nowUtc - lastActiveUtc >= limit;
    }
}
