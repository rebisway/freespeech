/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Services;

/// <summary>
/// What a start leaves behind when it goes wrong, whatever the diagnostic switch says. <c>startup.log</c> lists the steps of the last start (one
/// short line each, never any content): a start that hangs shows the last step it reached. <c>crash.log</c> keeps the full exception, inner
/// ones included, of every error nothing caught (the system's own crash report keeps only the outer one). Both stay in the app's private
/// folder and go out only with "Share the journal" in Settings.
/// </summary>
public static class StartupTrace
{
	private const long MaxCrashBytes = 64 * 1024;
	private static readonly object Gate = new();
	private static bool _begun;

	public static string StartupPath => Path.Combine(FileSystem.AppDataDirectory, "startup.log");
	public static string CrashPath => Path.Combine(FileSystem.AppDataDirectory, "crash.log");

	/// <summary>The activity is being created: a background check that starts now must leave the profile to the screen.</summary>
	public static volatile bool ActivityCreated;

	/// <summary>The screen has finished opening the profile (or found nothing to open): a background check may run again.</summary>
	public static volatile bool UiReady;

	/// <summary>Starts a new trace (the file of the previous start is replaced) and listens for unhandled errors. Call once, first thing.</summary>
	public static void Begin()
	{
		if (_begun) return;
		_begun = true;
		try { lock (Gate) File.WriteAllText(StartupPath, ""); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

		AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("unhandled", e.ExceptionObject as Exception);
		TaskScheduler.UnobservedTaskException += (_, e) => Crash("unobserved task", e.Exception);
#if ANDROID
		Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Crash("android unhandled", e.Exception);
#endif
		Mark("process start");
	}

	/// <summary>One line: the time, the thread, the step. Never throws.</summary>
	public static void Mark(string step)
	{
		var line = $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId}] {step}";
		Diag.Log("start: " + step);
		try { lock (Gate) File.AppendAllText(StartupPath, line + "\n"); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
	}

	/// <summary>The whole exception, with its inner ones, in <c>crash.log</c> (the oldest lines go when it grows past 64 KB). Never throws.</summary>
	public static void Crash(string source, Exception? ex)
	{
		try
		{
			lock (Gate)
			{
				var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}]\n{ex?.ToString() ?? "(no exception object)"}\n\n";
				var old = File.Exists(CrashPath) ? File.ReadAllText(CrashPath) : "";
				var all = old + text;
				if (all.Length > MaxCrashBytes) all = all[^(int)MaxCrashBytes..];
				File.WriteAllText(CrashPath, all);
			}
		}
		catch (Exception) { /* a crash handler must not add its own crash */ }
	}
}
