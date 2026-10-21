/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Security;

namespace FreeSpeech.Mobile.Services;

public enum PinResult { Ok, Wrong, Wiped }

/// <summary>
/// The optional app lock: a PIN asked when the app comes back after a delay of non-use (off by default, one week when switched on).
/// It guards the screen only: the background check and the push wake keep receiving, so messages still arrive on a locked phone.
/// The PIN is stored as a salted hash in the platform's secure store; the failures are counted, and after N of them the app can
/// erase this phone (the identity then comes back from the three phrases).
/// </summary>
public static class AppLock
{
	private const string PinKey = "freespeech.pin";
	public const int DefaultDelayMinutes = 7 * 24 * 60;

	private static bool _showing;
	private static TaskCompletionSource _unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>The lock screen is up: the app is not to be used, and nothing counts as read.</summary>
	public static bool Showing
	{
		get => _showing;
		set
		{
			_showing = value;
			if (!value) _unlocked.TrySetResult();
			else if (_unlocked.Task.IsCompleted) _unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}
	}

	/// <summary>Completes once the lock screen is gone (at once when it is not up). The start-up navigation waits for it: it would otherwise take the lock screen off.</summary>
	public static Task WaitUnlockedAsync() => _showing ? _unlocked.Task : Task.CompletedTask;

	public static bool Enabled
	{
		get => Preferences.Default.Get("lock_enabled", false);
		private set => Preferences.Default.Set("lock_enabled", value);
	}

	/// <summary>Minutes of non-use before the PIN is asked; 0 = every time the app comes back.</summary>
	public static int DelayMinutes
	{
		get => Preferences.Default.Get("lock_delay_minutes", DefaultDelayMinutes);
		set => Preferences.Default.Set("lock_delay_minutes", Math.Max(0, value));
	}

	/// <summary>Wrong PINs in a row after which this phone is erased; 0 = never.</summary>
	public static int WipeAfter
	{
		get => Preferences.Default.Get("lock_wipe_after", 0);
		set => Preferences.Default.Set("lock_wipe_after", Math.Max(0, value));
	}

	private static int Failures
	{
		get => Preferences.Default.Get("lock_failures", 0);
		set => Preferences.Default.Set("lock_failures", value);
	}

	private static DateTime LastActive
	{
		get => new(Preferences.Default.Get("lock_last_active", DateTime.UtcNow.Ticks), DateTimeKind.Utc);
		set => Preferences.Default.Set("lock_last_active", value.Ticks);
	}

	/// <summary>The app leaves the screen: this is the moment the delay counts from.</summary>
	public static void MarkLeft()
	{
		if (Enabled && !Showing) LastActive = DateTime.UtcNow;
	}

	/// <summary>The PIN is due: switched on, and the app was out of use for the delay.</summary>
	public static bool IsDue() => Enabled && !Showing && PinLock.IsDue(LastActive, DateTime.UtcNow, TimeSpan.FromMinutes(DelayMinutes));

	/// <summary>Switches the lock on with this PIN (or changes it).</summary>
	public static async Task SetPinAsync(string pin)
	{
		var stored = await Task.Run(() => PinLock.Create(pin));
		await SecureStorage.Default.SetAsync(PinKey, stored);
		Failures = 0;
		LastActive = DateTime.UtcNow;
		Enabled = true;
	}

	/// <summary>Checks a PIN, counting the failures. Too many wrong ones in a row erases the phone when that option is on.</summary>
	public static async Task<PinResult> VerifyAsync(string pin)
	{
		string? stored = null;
		try { stored = await SecureStorage.Default.GetAsync(PinKey); }
		catch (Exception) { /* the secure store was reset: no PIN can match, the phrases are the way back in */ }

		if (await Task.Run(() => PinLock.Verify(pin, stored)))
		{
			Failures = 0;
			return PinResult.Ok;
		}
		Failures++;
		if (WipeAfter > 0 && Failures >= WipeAfter)
		{
			WipeAndRestart();
			return PinResult.Wiped;
		}
		return PinResult.Wrong;
	}

	/// <summary>Wrong PINs left before the phone is erased, or null when that option is off.</summary>
	public static int? AttemptsLeft => WipeAfter > 0 ? Math.Max(0, WipeAfter - Failures) : null;

	/// <summary>Switches the lock off and forgets the PIN.</summary>
	public static void Disable()
	{
		Enabled = false;
		Failures = 0;
		try { SecureStorage.Default.Remove(PinKey); }
		catch (Exception) { /* nothing to forget */ }
	}

	/// <summary>Erases everything this app keeps on the phone, then starts over on the welcome screen. The identity itself survives in the three phrases.</summary>
	public static void WipeAndRestart()
	{
		try { SecureStorage.Default.RemoveAll(); } catch (Exception) { /* best effort */ }
		Preferences.Default.Clear();
		foreach (var dir in new[] { FileSystem.AppDataDirectory, FileSystem.CacheDirectory })
		{
			try
			{
				foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
				foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diag.Log($"Wipe: {ex.Message}"); }
		}
		AppRestart.Now();
	}
}
