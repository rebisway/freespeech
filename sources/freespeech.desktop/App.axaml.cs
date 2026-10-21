/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FreeSpeech.Desktop.Views;

namespace FreeSpeech.Desktop;

public partial class App : Application
{
	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
		Services.ThemeService.Apply();
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;   // the tray keeps the app alive when the window is hidden
			Services.Autostart.Apply();
			Services.DiagLog.Apply();
			var window = new MainWindow();
			desktop.MainWindow = window;
			window.Shell = new Services.Shell(window, window.ViewModel, desktop);
		}
		base.OnFrameworkInitializationCompleted();
	}
}
