/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Views;

namespace FreeSpeech.Mobile;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		RegisterRoutesOnce();
		Navigating += OnNavigating;
		Navigated += (_, _) => { if (Services.AppLock.Showing) _ = App.EnsureLockOnTopAsync(); };   // a navigation clears the modal lock screen
	}

	private static bool _routesRegistered;

	/// <summary>
	/// Pages pushed on top of a main page (the main pages are the side-menu entries declared in the XAML). The routes belong to the process: when
	/// the activity is created again in a process that is still alive (started by a push wake-up, then opened), registering them a second
	/// time throws "Duplicated Route" and the app closes at launch.
	/// </summary>
	private static void RegisterRoutesOnce()
	{
		if (_routesRegistered) return;
		_routesRegistered = true;
		Routing.RegisterRoute("chat", new Services.ChatRoute());
		Routing.RegisterRoute("newgroup", typeof(NewGroupPage));
		Routing.RegisterRoute("photocrop", typeof(PhotoCropPage));
		Routing.RegisterRoute("safety", typeof(SafetyPage));
		Routing.RegisterRoute("scan", typeof(ScanPage));
		Routing.RegisterRoute("phrases", typeof(PhrasesBackupPage));
		Routing.RegisterRoute("wipe", typeof(WipePage));
		Routing.RegisterRoute("migrate", typeof(MigratePage));
		Routing.RegisterRoute("groupinfo", typeof(GroupInfoPage));
		Routing.RegisterRoute("groupjoin", typeof(GroupJoinPage));
		Routing.RegisterRoute("viewer", typeof(ImageViewerPage));
		Routing.RegisterRoute("share", typeof(SharePage));
		Routing.RegisterRoute("attachmentarchive", typeof(AttachmentArchivePage));
		Routing.RegisterRoute("video", typeof(VideoPage));
	}

	/// <summary>Leaving the settings with a name or a description not saved: asks first (the picture picker is not leaving, the fields are kept).</summary>
	private async void OnNavigating(object? sender, ShellNavigatingEventArgs e)
	{
		if (CurrentPage is not SettingsPage settings) return;
		if (e.Target.Location.OriginalString.Contains("photocrop", StringComparison.Ordinal)) return;
		var deferral = e.GetDeferral();
		try { if (!await settings.ConfirmLeaveAsync()) e.Cancel(); }
		finally { deferral.Complete(); }
	}

	/// <summary>
	/// Back: closes the side menu first; from a pushed page (chat, crop, scan...) goes back as usual; from any other main
	/// page goes to the chat list; from the chat list leaves the app.
	/// </summary>
	protected override bool OnBackButtonPressed()
	{
		if (FlyoutIsPresented) { FlyoutIsPresented = false; return true; }

		var segments = (CurrentState?.Location?.OriginalString ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 1 && segments[0] is "contacts" or "groups" or "invite" or "approve" or "archive" or "outbox" or "backup" or "settings")
		{
			_ = GoToAsync("//conversations");
			return true;
		}
		return base.OnBackButtonPressed();
	}
}
