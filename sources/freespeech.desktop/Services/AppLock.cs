/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using FreeSpeech.Client.Security;

namespace FreeSpeech.Desktop.Services;

public enum PinResult { Ok, Wrong, Wiped }

/// <summary>
/// The optional app lock: a PIN asked when the window comes back after a delay of non-use (off by default, one week when switched on).
/// It guards the screen only: the client stays connected, so messages still arrive on a locked computer. The PIN is stored as a
/// salted hash (protected by DPAPI on Windows); the failures are counted, and after N of them the app can erase this computer's data
/// (the identity then comes back from the three phrases).
/// </summary>
public static class AppLock
{
	public const int DefaultDelayMinutes = 7 * 24 * 60;

	private static bool _showing;
	private static TaskCompletionSource _unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static string ProfileDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", Options.Profile);
	private static string PinFile => Path.Combine(ProfileDir, "pin.bin");

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

	public static bool Enabled
	{
		get => Prefs.Get("lock_enabled", false);
		private set => Prefs.Set("lock_enabled", value);
	}

	/// <summary>Minutes of non-use before the PIN is asked; 0 = every time the window comes back.</summary>
	public static int DelayMinutes
	{
		get => Prefs.Get("lock_delay_minutes", DefaultDelayMinutes);
		set => Prefs.Set("lock_delay_minutes", Math.Max(0, value));
	}

	/// <summary>Wrong PINs in a row after which this computer's data is erased; 0 = never.</summary>
	public static int WipeAfter
	{
		get => Prefs.Get("lock_wipe_after", 0);
		set => Prefs.Set("lock_wipe_after", Math.Max(0, value));
	}

	private static int Failures
	{
		get => Prefs.Get("lock_failures", 0);
		set => Prefs.Set("lock_failures", value);
	}

	private static DateTime LastActive
	{
		get => new(Prefs.Get("lock_last_active", DateTime.UtcNow.Ticks), DateTimeKind.Utc);
		set => Prefs.Set("lock_last_active", value.Ticks);
	}

	/// <summary>The window leaves the screen: this is the moment the delay counts from.</summary>
	public static void MarkLeft()
	{
		if (Enabled && !Showing) LastActive = DateTime.UtcNow;
	}

	/// <summary>The PIN is due: switched on, and the window was out of use for the delay.</summary>
	public static bool IsDue() => Enabled && !Showing && PinLock.IsDue(LastActive, DateTime.UtcNow, TimeSpan.FromMinutes(DelayMinutes));

	/// <summary>Switches the lock on with this PIN (or changes it).</summary>
	public static async Task SetPinAsync(string pin)
	{
		var stored = await Task.Run(() => PinLock.Create(pin));
		Directory.CreateDirectory(ProfileDir);
		await File.WriteAllBytesAsync(PinFile, Protect(System.Text.Encoding.UTF8.GetBytes(stored)));
		Failures = 0;
		LastActive = DateTime.UtcNow;
		Enabled = true;
	}

	/// <summary>Checks a PIN, counting the failures. Too many wrong ones in a row erases the data when that option is on.</summary>
	public static async Task<PinResult> VerifyAsync(string pin)
	{
		string? stored = null;
		try { if (File.Exists(PinFile)) stored = System.Text.Encoding.UTF8.GetString(Unprotect(await File.ReadAllBytesAsync(PinFile))); }
		catch (Exception ex) when (ex is IOException or CryptographicException) { /* the stored PIN is unreadable: no PIN can match, the phrases are the way back in */ }

		if (await Task.Run(() => PinLock.Verify(pin, stored)))
		{
			Failures = 0;
			return PinResult.Ok;
		}
		Failures++;
		if (WipeAfter > 0 && Failures >= WipeAfter)
		{
			await WipeAndRestartAsync();
			return PinResult.Wiped;
		}
		return PinResult.Wrong;
	}

	/// <summary>Wrong PINs left before the data is erased, or null when that option is off.</summary>
	public static int? AttemptsLeft => WipeAfter > 0 ? Math.Max(0, WipeAfter - Failures) : null;

	/// <summary>Switches the lock off and forgets the PIN.</summary>
	public static void Disable()
	{
		Enabled = false;
		Failures = 0;
		try { File.Delete(PinFile); } catch (IOException) { /* nothing to forget */ }
	}

	/// <summary>Erases everything this app keeps on the computer for this profile, then starts over on the welcome screen. The identity itself survives in the three phrases.</summary>
	public static async Task WipeAndRestartAsync()
	{
		try { if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: ViewModels.MainViewModel vm }) await vm.Session.ShutdownAsync(); }
		catch (Exception ex) when (ex is InvalidOperationException or IOException) { Diag.Log($"Wipe: {ex.Message}"); }
		try { Directory.Delete(ProfileDir, recursive: true); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diag.Log($"Wipe: {ex.Message}"); }
		AppRestart.Now();
	}

	private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows() ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser) : data;

	private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser) : data;
}
