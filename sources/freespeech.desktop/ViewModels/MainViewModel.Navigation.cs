/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One entry of the side menu: its icon, its label (with a count for "Requests" and "Archives"), and whether it is the screen shown.</summary>
public sealed partial class MenuItem(AppPage page, string icon, string titleKey) : ObservableObject
{
	[ObservableProperty] private string _title = Loc.T(titleKey);
	[ObservableProperty] private bool _isSelected;
	[ObservableProperty] private bool _isVisible = true;

	public AppPage Page { get; } = page;
	public string TitleKey { get; } = titleKey;
	public Geometry? Icon { get; } = Application.Current is { } app && app.TryGetResource(icon, null, out var found) ? found as Geometry : null;
}

/// <summary>The side menu and the screens it leads to.</summary>
public sealed partial class MainViewModel
{
	[ObservableProperty, NotifyPropertyChangedFor(nameof(PageTitle), nameof(IsChatsPage), nameof(ShowTopBar), nameof(PageContent), nameof(CanPop))]
	private AppPage _page = AppPage.Chats;

	/// <summary>The screens opened from another one (a new group, a group's details): each one remembers the screen under it, and the back button returns to it.</summary>
	private readonly Stack<(AppPage Page, object? Content, string? Title)> _stack = new();

	private string? _pushedTitle;

	[ObservableProperty] private bool _menuOpen;

	/// <summary>The PIN screen, over the whole window, while it is shown.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(LockIsOff))] private LockViewModel? _lock;

	/// <summary>No PIN screen is up: the rest of the window takes the keyboard.</summary>
	public bool LockIsOff => Lock == null;

	/// <summary>Shows the PIN screen and says whether the PIN was accepted (or set).</summary>
	public async Task<bool> ShowLockAsync(LockMode mode)
	{
		if (Lock != null) return false;
		if (mode == LockMode.Unlock) AppLock.Showing = true;
		var screen = new LockViewModel(mode);
		Lock = screen;
		var ok = await screen.Result;
		Lock = null;
		return ok;
	}

	/// <summary>The PIN is asked when the window comes back after the chosen delay of non-use (also at a start).</summary>
	public async Task ShowLockIfDueAsync()
	{
		if (State == SessionState.Starting || !AppLock.IsDue()) return;
		await ShowLockAsync(LockMode.Unlock);
	}

	/// <summary>Requests and invitations waiting for an answer; archived conversations: the counts of the menu.</summary>
	private int Pending { get; set; }
	private int Archived { get; set; }

	public List<MenuItem> MenuItems { get; private set; } = [];

	public bool IsChatsPage => Page == AppPage.Chats && _content == null;

	/// <summary>A screen opened from another one is shown: the title bar has a back arrow.</summary>
	public bool CanPop => _stack.Count > 0;

	public string PageTitle => _pushedTitle ?? Page switch
	{
		AppPage.Chats => Loc.T("nav_chats"),
		AppPage.Contacts => Loc.T("nav_contacts"),
		AppPage.Groups => Loc.T("nav_groups"),
		AppPage.Invite => Loc.T("invite_heading"),
		AppPage.Approve => Loc.T("nav_approve"),
		AppPage.Archive => Loc.T("archive_title"),
		AppPage.Outbox => Loc.T("outbox_title"),
		AppPage.Backup => Loc.T("nav_backup"),
		_ => Loc.T("nav_settings"),
	};

	/// <summary>The view model of the current screen, other than the conversations (which have their own layout). Made when the screen is opened.</summary>
	public object? PageContent => _content;

	private object? _content;

	private void BuildMenu()
	{
		MenuItems =
		[
			new(AppPage.Chats, "Icon.chat", "nav_chats"),
			new(AppPage.Contacts, "Icon.person", "nav_contacts"),
			new(AppPage.Groups, "Icon.people", "nav_groups"),
			new(AppPage.Invite, "Icon.person_add", "nav_invite"),
			new(AppPage.Approve, "Icon.approve", "nav_approve"),
			new(AppPage.Archive, "Icon.archive", "archive_title"),
			new(AppPage.Outbox, "Icon.send", "outbox_title") { IsVisible = Prefs.ShowOutbox },
			new(AppPage.Backup, "Icon.backup", "nav_backup"),
			new(AppPage.Settings, "Icon.settings", "nav_settings"),
		];
		MenuItems[0].IsSelected = true;
		Prefs.ShowOutboxChanged += () => { foreach (var m in MenuItems.Where(m => m.Page == AppPage.Outbox)) m.IsVisible = Prefs.ShowOutbox; };
	}

	private void UpdateMenuTitles()
	{
		foreach (var item in MenuItems)
		{
			var count = item.Page switch { AppPage.Approve => Pending, AppPage.Archive => Archived, _ => 0 };
			item.Title = count > 0 ? $"{Loc.T(item.TitleKey)} ({count})" : Loc.T(item.TitleKey);
		}
	}

	[RelayCommand]
	private void ToggleMenu() => MenuOpen = !MenuOpen;

	[RelayCommand]
	private void CloseMenu() => MenuOpen = false;

	[RelayCommand]
	private void Navigate(MenuItem item)
	{
		MenuOpen = false;
		Go(item.Page);
	}

	/// <summary>Opens a screen over the current one (the back arrow returns).</summary>
	public void Push(object content, string title)
	{
		_stack.Push((Page, _content, _pushedTitle));
		_content = content;
		_pushedTitle = title;
		OnPropertyChanged(nameof(PageContent));
		OnPropertyChanged(nameof(PageTitle));
		OnPropertyChanged(nameof(IsChatsPage));
		OnPropertyChanged(nameof(CanPop));
		OnPropertyChanged(nameof(ShowTopBar));
	}

	[RelayCommand]
	public void Pop()
	{
		if (_stack.Count == 0) return;
		(_content as IDisposable)?.Dispose();
		(Page, _content, _pushedTitle) = _stack.Pop();
		OnPropertyChanged(nameof(PageContent));
		OnPropertyChanged(nameof(PageTitle));
		OnPropertyChanged(nameof(IsChatsPage));
		OnPropertyChanged(nameof(CanPop));
		OnPropertyChanged(nameof(ShowTopBar));
	}

	/// <summary>Shows a screen of the menu.</summary>
	public void Go(AppPage page)
	{
		if (page == Page && _stack.Count == 0 && (page == AppPage.Chats ? _content == null : _content != null)) return;
		while (_stack.Count > 0) { (_stack.Pop().Content as IDisposable)?.Dispose(); }
		_pushedTitle = null;
		(_content as IDisposable)?.Dispose();
		_content = page switch
		{
			AppPage.Chats => null,
			AppPage.Contacts => new ContactsPageViewModel(this),
			AppPage.Invite => new InviteViewModel(this),
			AppPage.Approve => new ApproveViewModel(this),
			AppPage.Groups => new GroupsPageViewModel(this),
			AppPage.Archive => new ArchiveViewModel(this),
			AppPage.Outbox => new OutboxViewModel(this),
			AppPage.Backup => new BackupViewModel(this),
			AppPage.Settings => new SettingsViewModel(this),
			_ => new SoonViewModel(page),
		};
		foreach (var item in MenuItems) item.IsSelected = item.Page == page;
		Page = page;
	}
}

/// <summary>A screen of the menu that the desktop client does not have yet.</summary>
public sealed class SoonViewModel(AppPage page)
{
	public string Text => Loc.T("d_soon");
	public AppPage Page { get; } = page;
}
