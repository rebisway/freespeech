/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeSpeech.Desktop.ViewModels;
using FreeSpeech.Desktop.Views;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The desktop frame around the window: the tray icon with its menu of conversations, "close" that hides the window (the client
/// stays connected and notifies), and the system notifications with their quick reply.
/// </summary>
public sealed class Shell
{
	private const int MenuConversations = 12;

	private readonly MainWindow _window;
	private readonly MainViewModel _vm;
	private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
	private readonly INotifier _notifier;
	private TrayIcon? _tray;

	public bool Quitting { get; private set; }

	public Shell(MainWindow window, MainViewModel vm, IClassicDesktopStyleApplicationLifetime lifetime)
	{
		_window = window;
		_vm = vm;
		_lifetime = lifetime;
		_notifier = new PopupNotifier();
		_notifier.Opened += key => Avalonia.Threading.Dispatcher.UIThread.Post(() => { ShowWindow(); _vm.OpenConversation(key); });
		_notifier.Replied += (key, text) => _ = vm.Session.QuickReplyAsync(key, text);
		vm.Session.MessageReceived += OnMessage;
		vm.Session.FileCompleted += ShowPictureInNotice;
		vm.ChatOpened += key => _notifier.Cancel(key);
		CreateTray();
	}

	/// <summary>The picture of a message is on disk: the notification that names it shows it.</summary>
	private void ShowPictureInNotice(string messageId)
	{
		var rec = _vm.Session.Client.Attachments.Store.Find(messageId);
		if (rec is { State: FreeSpeech.Client.Attachments.AttachmentState.Complete, Path: { } path } && rec.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) _notifier.FileReady(messageId, path);
	}

	private void OnMessage(FreeSpeech.Client.ChatMessage m)
	{
		Diag.Log($"Message received mine={m.Mine} otherDevice={m.FromOtherDevice} windowActive={_window.IsActive} visible={_window.IsVisible}");
		if (m.Mine || m.FromOtherDevice || !Prefs.NotificationsEnabled || Prefs.IsMuted(m.Conversation.HistoryKey)) return;
		// The chat on screen, in a window the user is looking at, needs no notification.
		if (_window.IsActive && _window.IsVisible && _vm.Chat?.Key == m.Conversation.HistoryKey) return;
		var client = _vm.Session.Client;
		var title = m.Conversation.Group is { } g ? $"{m.FromName} · {g.Name}" : m.FromName;
		var last = client.GetLatestMessages(m.Conversation, 1).Entries.FirstOrDefault();
		_notifier.Show(title, last != null ? PreviewText.Of(client, last) : PreviewText.FileLabel(m.Text) ?? m.Text, m.Conversation.HistoryKey, last?.Id);
		// The file may already be there, or arrive a moment later (the event can come before the card exists).
		if (last != null && m.IsFile)
			foreach (var delay in new[] { 500, 2000, 5000 })
				_ = Task.Delay(delay).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowPictureInNotice(last.Id)));
	}

	public void ShowWindow()
	{
		WindowPlacement.EnsureVisible(_window);   // a screen may have been unplugged while the window was hidden
		if (!_window.IsVisible) _window.Show();
		if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
		_window.Activate();
	}

	public void Quit()
	{
		Quitting = true;
		_tray?.Dispose();
		_lifetime.Shutdown();
	}

	private void CreateTray()
	{
		var menu = new NativeMenu();
		menu.Opening += (_, _) => { FillMenu(menu); if (_tray != null) _tray.Icon = TrayImage(); };   // the system theme may have changed
		_tray = new TrayIcon { Icon = TrayImage(), ToolTipText = "FreeSpeech", Menu = menu, IsVisible = true };
		// Double left click shows the window (the right click opens the menu).
		var lastClick = 0L;
		_tray.Clicked += (_, _) =>
		{
			var now = Environment.TickCount64;
			if (now - lastClick <= 500) { lastClick = 0; ShowWindow(); }
			else lastClick = now;
		};
		FillMenu(menu);
		// A tray icon only reaches the system once the application owns it.
		if (Application.Current is { } app) TrayIcon.SetIcons(app, [_tray]);
	}

	/// <summary>The flag without background: gold on a dark bar, black on a light one (and in the macOS menu bar, which tints it).</summary>
	private static WindowIcon TrayImage()
	{
		var black = OperatingSystem.IsMacOS() || (OperatingSystem.IsWindows() && WindowsTaskbarIsLight());
		var name = black ? "freespeech-no-background-black.ico" : "freespeech-no-background.ico";
		using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://FreeSpeech/Assets/{name}"));
		return new WindowIcon(stream);
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static bool WindowsTaskbarIsLight()
	{
		try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int light && light == 1; }
		catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return false; }
	}

	/// <summary>The latest conversations (unread first), then Open and Quit. Rebuilt each time the menu opens.</summary>
	private void FillMenu(NativeMenu menu)
	{
		menu.Items.Clear();
		if (_vm.ShowMain)
		{
			foreach (var row in _vm.Recent(MenuConversations))
			{
				var key = row.Key;
				var item = new NativeMenuItem(row.HasUnread ? $"{row.Title} ({row.UnreadText})" : row.Title);
				item.Click += (_, _) => { ShowWindow(); _vm.OpenConversation(key); };
				menu.Items.Add(item);
			}
			menu.Items.Add(new NativeMenuItemSeparator());
		}
		var open = new NativeMenuItem(Loc.T("d_tray_open"));
		open.Click += (_, _) => ShowWindow();
		menu.Items.Add(open);
		menu.Items.Add(new NativeMenuItemSeparator());
		if (_vm.ShowMain)
		{
			var settings = new NativeMenuItem(Loc.T("nav_settings"));
			settings.Click += (_, _) => { ShowWindow(); _vm.Go(AppPage.Settings); };
			menu.Items.Add(settings);
		}
		var quit = new NativeMenuItem(Loc.T("d_tray_quit"));
		quit.Click += (_, _) => Quit();
		menu.Items.Add(quit);
	}
}
