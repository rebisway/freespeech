/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using System.Security.Cryptography;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// Keeps the database key between runs. Windows: protected by DPAPI (current user), useless to anyone else or on another machine.
/// Linux and macOS: a file readable by the current user only (mode 600) in the profile folder, so the application can start hidden at login.
/// </summary>
internal sealed class DesktopKeyCache(string profileDir) : IKeyCache
{
	private readonly string _path = Path.Combine(profileDir, "dbkey.bin");

	/// <summary>The key cache of the profile in use.</summary>
	public static DesktopKeyCache ForProfile() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", Options.Profile));

	public byte[]? Load()
	{
		if (!File.Exists(_path)) return null;
		try
		{
			var data = File.ReadAllBytes(_path);
			return OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser) : data;
		}
		catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
	}

	public void Store(byte[] key)
	{
		Directory.CreateDirectory(profileDir);
		if (OperatingSystem.IsWindows()) { File.WriteAllBytes(_path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)); return; }
		// Created with owner-only rights from the start (no window where the key is readable by others).
		using var stream = new FileStream(_path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
		stream.Write(key);
		File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // also when the file already existed with wider rights
	}
}