/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Controls;
using FreeSpeech.Desktop.Services;
using FreeSpeech.Desktop.ViewModels;

namespace FreeSpeech.Desktop.Views;

public partial class MainWindow : Window
{
	public MainViewModel ViewModel { get; } = new();

	/// <summary>Set once the desktop frame (tray, notifications) exists.</summary>
	public Shell? Shell { get; set; }

	public MainWindow()
	{
		InitializeComponent();
		DataContext = ViewModel;
		Ui.Main = this;
		WindowPlacement.Restore(this);
		WindowPlacement.Attach(this);
		// Started with the system: the window opens invisible, then hides in the tray.
		if (Options.StartHidden) { Opacity = 0; Opened += (_, _) => { Hide(); Opacity = 1; }; }
		SizeChanged += (_, e) => ViewModel.IsCompact = e.NewSize.Width < MainViewModel.CompactWidth;
		Opened += async (_, _) => { await ViewModel.StartAsync(); await ViewModel.ShowLockIfDueAsync(); };
		// The delay of the lock counts from the moment the window leaves the screen.
		Activated += async (_, _) => await ViewModel.ShowLockIfDueAsync();
		Deactivated += (_, _) => { AppLock.MarkLeft(); WindowFocus.IsActive = false; };
		Activated += (_, _) => WindowFocus.IsActive = true;
		Closing += async (_, e) =>
		{
			// Closing the window hides it: the client stays connected and keeps notifying. Quit is in the tray menu.
			if (Shell is { Quitting: false })
			{
				e.Cancel = true;
				WindowFocus.IsActive = false;
				Hide();
				return;
			}
			await ViewModel.Session.ShutdownAsync();
		};
	}
}
