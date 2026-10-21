/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json;
using FreeSpeech.Client;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The user's settings (the ones of the mobile app's Settings screen), kept in a small JSON file of the profile. Each is read when
/// needed, so a change applies at once.
/// </summary>
public static class Prefs
{
	private static readonly object Gate = new();
	private static Dictionary<string, JsonElement>? _values;

	private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", Options.Profile, "settings.json");

	private static Dictionary<string, JsonElement> Values
	{
		get
		{
			if (_values != null) return _values;
			try { _values = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath)) ?? [] : []; }
			catch (Exception ex) when (ex is IOException or JsonException) { _values = []; }
			return _values;
		}
	}

	public static T Get<T>(string key, T fallback)
	{
		lock (Gate)
		{
			if (!Values.TryGetValue(key, out var element)) return fallback;
			try { return element.Deserialize<T>() ?? fallback; }
			catch (JsonException) { return fallback; }
		}
	}

	public static bool Has(string key) { lock (Gate) return Values.ContainsKey(key); }

	public static void Set<T>(string key, T value)
	{
		lock (Gate)
		{
			Values[key] = JsonSerializer.SerializeToElement(value);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(Values));
			}
			catch (IOException) { /* a setting is a convenience: not saved this time */ }
		}
	}

	/// <summary>Show "is typing…" to the other side, and see theirs (reciprocal; on by default).</summary>
	public static bool TypingIndicator { get => Get("typing_indicator", true); set => Set("typing_indicator", value); }

	/// <summary>Tell contacts when I read their messages (on by default).</summary>
	public static bool SendReadReceipts { get => Get("send_read_receipts", true); set => Set("send_read_receipts", value); }

	/// <summary>Notifications while the window is not shown (on by default). Muted conversations stay silent.</summary>
	public static bool NotificationsEnabled { get => Get("notifications", true); set => Set("notifications", value); }

	/// <summary>Files up to this size (MB) are downloaded as soon as they are offered; 0 = never.</summary>
	public static int AutoDownloadMb { get => Get("auto_download_mb", 50); set => Set("auto_download_mb", Math.Max(0, value)); }

	/// <summary>A file larger than this (MB, as it will be sent) is only sent after the user confirms; 0 = never asked.</summary>
	public static int ConfirmSendMb { get => Get("confirm_send_mb", 25); set => Set("confirm_send_mb", Math.Max(0, value)); }

	/// <summary>Follow a contact's verified migration to a new identity without asking (never a contact I verified). Off by default.</summary>
	public static bool AutoAcceptMigration { get => Get("auto_accept_migration", false); set => Set("auto_accept_migration", value); }

	/// <summary>As a moderator, cut a migrated member's old identity off from my groups at once (a key rotation). On by default.</summary>
	public static bool AutoExcludeMigrated { get => Get("auto_exclude_migrated", true); set => Set("auto_exclude_migrated", value); }

	/// <summary>The diagnostic journal of the client library (off by default).</summary>
	public static bool DiagLog { get => Get("diag_log", false); set => Set("diag_log", value); }

	/// <summary>Start FreeSpeech when the computer starts, hidden in the tray (on by default).</summary>
	public static bool LaunchAtStartup { get => Get("launch_at_startup", true); set => Set("launch_at_startup", value); }

	/// <summary>Shows "Outgoing" in the side menu: the letters waiting for their recipient (a debugging aid, hidden by default).</summary>
	public static bool ShowOutbox { get => Get("show_outbox", false); set { Set("show_outbox", value); ShowOutboxChanged?.Invoke(); } }
	public static event Action? ShowOutboxChanged;

	private static readonly HashSet<string> MutedKeys = [];
	private static bool _mutedLoaded;

	private static HashSet<string> Muted
	{
		get
		{
			if (!_mutedLoaded) { foreach (var k in Get("muted_chats", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)) MutedKeys.Add(k); _mutedLoaded = true; }
			return MutedKeys;
		}
	}

	/// <summary>A muted conversation raises no popup; its unread counter stays.</summary>
	public static bool IsMuted(string historyKey) { lock (Gate) return Muted.Contains(historyKey); }

	public static void SetMuted(string historyKey, bool muted)
	{
		lock (Gate) { if (muted ? !Muted.Add(historyKey) : !Muted.Remove(historyKey)) return; }
		Set("muted_chats", string.Join('\n', MutedKeys));
	}
}