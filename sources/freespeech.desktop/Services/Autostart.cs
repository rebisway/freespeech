/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// "Start FreeSpeech when the system starts": the per-user way of each system (Windows: the Run key; macOS: a launch agent; Linux: an
/// autostart entry). The program starts hidden in the tray (<c>--hidden</c>). On by default: the first start turns it on.
/// </summary>
public static class Autostart
{
	private const string Name = "FreeSpeech";

	/// <summary>Applies the saved choice (on at the very first start) so the system entry matches it.</summary>
	public static void Apply()
	{
		#if DEBUG
		if (!Prefs.Has("launch_at_startup")) Prefs.LaunchAtStartup = false;   // debug builds stay off by default
#else
		if (!Prefs.Has("launch_at_startup")) Prefs.LaunchAtStartup = true;   // on by default
#endif
		Set(Prefs.LaunchAtStartup);
	}

	public static void Set(bool enabled)
	{
		try
		{
			var program = ProgramPath();
			if (program == null) return;
			if (OperatingSystem.IsWindows()) SetWindows(enabled, program);
			else if (OperatingSystem.IsMacOS()) SetMac(enabled, program);
			else if (OperatingSystem.IsLinux()) SetLinux(enabled, program);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Diag.Log($"Autostart: {ex.GetType().Name}"); }
	}

	/// <summary>The file to start again: the AppImage itself when run from one (the process path is then a temporary mount that is gone at exit).</summary>
	public static string? ProgramPath() => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image && File.Exists(image) ? image : Environment.ProcessPath;

	private static string Command(string program) => Options.Profile == "default" ? $"\"{program}\" --hidden" : $"\"{program}\" --profile {Options.Profile} --hidden";

	[SupportedOSPlatform("windows")]
	private static void SetWindows(bool enabled, string program)
	{
		using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
		if (key == null) return;
		var name = Options.Profile == "default" ? Name : $"{Name}-{Options.Profile}";
		if (enabled) key.SetValue(name, Command(program)); else key.DeleteValue(name, throwOnMissingValue: false);
	}

	private static void SetMac(bool enabled, string program)
	{
		var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "org.freespeech.desktop.plist");
		if (!enabled) { if (File.Exists(plist)) File.Delete(plist); return; }
		Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
		File.WriteAllText(plist, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict><key>Label</key><string>org.freespeech.desktop</string><key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(program)}</string><string>--hidden</string></array><key>RunAtLoad</key><true/></dict></plist>\n");
	}

	private static void SetLinux(bool enabled, string program)
	{
		var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
		var entry = Path.Combine(config, "autostart", Options.Profile == "default" ? "freespeech.desktop" : $"freespeech-{Options.Profile}.desktop");
		if (!enabled) { if (File.Exists(entry)) File.Delete(entry); return; }
		Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
		File.WriteAllText(entry, $"[Desktop Entry]\nType=Application\nName={(Options.Profile == "default" ? Name : $"{Name} ({Options.Profile})")}\nIcon=freespeech\nExec={Command(program)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
	}
}