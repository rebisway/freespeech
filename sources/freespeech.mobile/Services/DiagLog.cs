/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Diagnostic journal of the client library, for a bug report: off by default, switched on by the user in Settings, kept in a small
/// file on the phone (never sent anywhere) and shared by hand. Lines describe connections, registrations, refusals and send results;
/// they never carry message content, keys, phrases or full identifiers (see <see cref="ClientLog"/>).
/// </summary>
public static class DiagLog
{
	private const long MaxBytes = 256 * 1024;
	private static readonly object Gate = new();
	private static bool _attached;

	public static string FilePath => Path.Combine(FileSystem.CacheDirectory, "diagnostic.log");

	public static bool Enabled
	{
		get => Preferences.Default.Get("diag_log", false);
		set { Preferences.Default.Set("diag_log", value); Apply(); }
	}

	/// <summary>Applies the saved choice: call at start and after each change.</summary>
	public static void Apply()
	{
		if (!_attached) { ClientLog.Written += Write; _attached = true; }
		ClientLog.Level = Enabled ? ClientLogLevel.Debug : ClientLogLevel.Off;
		if (!Enabled) Clear();   // switching it off also forgets what was recorded
	}

	public static bool HasContent => File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

	public static void Clear()
	{
		lock (Gate) { try { File.Delete(FilePath); } catch (IOException) { } }
	}

	private static void Write(DateTime time, ClientLogLevel level, string category, string text)
	{
		var line = $"{time:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[..3]}] {category}: {text}";
		Diag.Log(line);
		lock (Gate)
		{
			try
			{
				File.AppendAllText(FilePath, line + Environment.NewLine);
				var info = new FileInfo(FilePath);
				if (info.Length > MaxBytes)
				{
					// Keep the newest half.
					var lines = File.ReadAllLines(FilePath);
					File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
				}
			}
			catch (IOException) { /* a full or locked disk must never break the app */ }
		}
	}
}
