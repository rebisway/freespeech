/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Desktop.Services;

/// <summary>Diagnostic lines (never message content). <c>FS_DIAG_FILE=&lt;path&gt;</c> appends them to that file.</summary>
public static class Diag
{
	public static void Log(string text)
	{
		System.Diagnostics.Debug.WriteLine(text);
		if (Environment.GetEnvironmentVariable("FS_DIAG_FILE") is not { Length: > 0 } file) return;
		try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}"); } catch (IOException) { }
	}
}
