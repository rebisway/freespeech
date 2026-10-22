/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile;

public partial class App : Application
{
	public App()
	{
		Services.StartupTrace.Mark("App: start");
		Localization.Loc.Instance.Initialize();   // before any page binds to a text
		InitializeComponent();
		// One palette, dark: gold on a dark background (same charte as the other Rebis apps).
		UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Services.StartupTrace.Mark("CreateWindow: shell");
		var window = new Window(new AppShell());
		Services.StartupTrace.Mark("CreateWindow: shell built");
		// A message shown in a chat only counts as read while the app is on screen.
		window.Activated += (_, _) => { Services.ClientService.InForeground = !Services.AppLock.Showing; _ = ShowLockIfDueAsync(); };
		window.Deactivated += (_, _) => { Services.ClientService.InForeground = false; Services.AppLock.MarkLeft(); };
		window.Created += (_, _) => _ = ShowLockIfDueAsync();
		return window;
	}

	/// <summary>The optional PIN: asked over the whole app when it comes back after the chosen delay of non-use (also at a cold start).</summary>
	private static async Task ShowLockIfDueAsync()
	{
		if (!Services.AppLock.IsDue()) return;
		Services.AppLock.Showing = true;
		Services.ClientService.InForeground = false;
		// At a cold start the shell is not up yet: wait for it before putting the lock on top.
		for (var i = 0; i < 50 && Shell.Current?.CurrentPage == null; i++) await Task.Delay(100);
		if (Shell.Current == null) { Services.AppLock.Showing = false; return; }
		await EnsureLockOnTopAsync();
	}

	/// <summary>
	/// Puts the lock screen back when something took it off: a Shell navigation (the start-up one, a notification opening a chat) clears the
	/// modal pages, and the lock is not released until the PIN is right.
	/// </summary>
	public static async Task EnsureLockOnTopAsync()
	{
		if (!Services.AppLock.Showing || Shell.Current is not { } shell) return;
		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			if (!Services.AppLock.Showing || shell.Navigation.ModalStack.OfType<Views.LockPage>().Any()) return;
			await shell.Navigation.PushModalAsync(new Views.LockPage(Views.LockMode.Unlock), false);
		});
	}
}