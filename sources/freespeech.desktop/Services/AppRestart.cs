/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;

namespace FreeSpeech.Desktop.Services;

/// <summary>Starts the program again (a change that is read at start, such as the language).</summary>
public static class AppRestart
{
	public static void Now()
	{
		if (Autostart.ProgramPath() is { } program)
		{
			var arguments = Options.Profile == "default" ? "" : $"--profile {Options.Profile}";
			if (Options.Server != Options.DefaultServer) arguments += $" --server {Options.Server}";
			Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false });
		}
		Environment.Exit(0);
	}
}