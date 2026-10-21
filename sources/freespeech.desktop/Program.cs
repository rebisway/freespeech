/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using Avalonia;

namespace FreeSpeech.Desktop;

internal static class Program
{
	/// <summary>Options: <c>--profile name</c> (a separate identity, to run two instances side by side), <c>--server url</c>.</summary>
	[STAThread]
	public static void Main(string[] args)
	{
		for (var i = 0; i + 1 < args.Length; i++)
		{
			if (args[i] == "--profile") Options.Profile = args[i + 1];
			else if (args[i] == "--server") Options.Server = args[i + 1];
		}
		foreach (var a in args)
		{
			if (a == "--hidden") Options.StartHidden = true;
		}
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

/// <summary>Start-up options from the command line.</summary>
internal static class Options
{
	public static string Profile { get; set; } = "default";
	public static readonly string DefaultServer = BuildDefaults.Get("DefaultServer");   // chosen at build time; a local relay is chosen with --server

	public static string Server { get; set; } = DefaultServer;

	/// <summary>Started with the system: the window stays hidden in the tray.</summary>
	public static bool StartHidden { get; set; }
}