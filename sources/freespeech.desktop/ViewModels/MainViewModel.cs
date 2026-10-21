/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>The screens of the side menu, the same as the mobile app's.</summary>
public enum AppPage { Chats, Contacts, Groups, Invite, Approve, Archive, Outbox, Backup, Settings }

/// <summary>
/// The window: start-up and first start, then a title bar with the side menu and the current screen. The conversations screen shows
/// the list beside the chat; in a narrow window (the compact mode) it shows one column at a time, with a way back.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
	/// <summary>Below this width the window shows one column.</summary>
	public const double CompactWidth = 700;

	private readonly DesktopSession _session = new();

	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowOnboarding), nameof(ShowMain), nameof(ShowList), nameof(ShowChat), nameof(ShowPlaceholder), nameof(ShowTopBar))]
	private SessionState _state = SessionState.Starting;

	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowChat), nameof(ShowPlaceholder), nameof(ListColumn), nameof(ChatColumn), nameof(ShowTopBar))]
	private bool _isCompact;

	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowChat), nameof(ShowPlaceholder), nameof(ListColumn), nameof(ChatColumn), nameof(ShowTopBar))]
	private ChatViewModel? _chat;

	[ObservableProperty] private ConversationRow? _selected;

	/// <summary>The screen that saves the drawn phrases (first connection), while it is shown.</summary>
	[ObservableProperty] private PhrasesSaveViewModel? _phrasesSave;
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _busy;
	[ObservableProperty] private string _search = "";

	[ObservableProperty] private string _phrase1 = "";
	[ObservableProperty] private string _phrase2 = "";
	[ObservableProperty] private string _phrase3 = "";
	[ObservableProperty] private string _nickname = "";
	[ObservableProperty] private string _server = Options.Server;

	/// <summary>What waits for my answer (requests, invitations, proposals), shown at the top of the conversation list.</summary>
	public ObservableCollection<ApprovalItem> Approvals { get; } = [];
	public bool HasApprovals => Approvals.Count > 0;

	[RelayCommand] private void OpenApprove() => Go(AppPage.Approve);

	/// <summary>The latest system notice, above the list: a click goes to Approve and closes it, the cross only closes it.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasBanner))] private string _bannerText = "";
	[ObservableProperty] private string _bannerTitle = "";
	public bool HasBanner => BannerText.Length > 0;

	[RelayCommand] private void OpenBanner() { DismissBanner(); Go(AppPage.Approve); }

	[RelayCommand] private void DismissBanner() => BannerText = "";

	private List<ConversationRow> _all = [];
	private bool _rebuilding;   // the list is being rebuilt: the selection changes are not the user opening a conversation

	public MainViewModel()
	{
		_session.ConversationsChanged += () => { Refresh(); RefreshSyncPending(); };
		_session.DeviceSyncAsked += _ => OnDeviceSyncAsked();
		_session.SystemNotice += (title, text) => { BannerTitle = title; BannerText = text; };
		_session.DeviceSyncStateChanged += OnDeviceSyncState;
		BuildMenu();
		// The state of the servers is read from memory (no network call), a few seconds apart.
		new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(4), Avalonia.Threading.DispatcherPriority.Background, (_, _) => RefreshServerBadge()).Start();
		ThemeService.Changed += () => OnPropertyChanged(nameof(FlagImage));
	}

	// ------------------------------------------------------------------
	// The badge of the title bar: where I stand on my servers
	// ------------------------------------------------------------------

	[ObservableProperty] private bool _badgeVisible;
	[ObservableProperty] private Avalonia.Media.Geometry? _badgeGlyph;
	[ObservableProperty] private Avalonia.Media.IBrush _badgeBrush = Avalonia.Media.Brushes.Gray;
	[ObservableProperty] private bool _badgeSlash;
	private List<ServerBadge> _badges = [];

	private void RefreshServerBadge()
	{
		if (_session.State != SessionState.Ready) { BadgeVisible = false; return; }
		var badges = _session.Client.Servers.Select(ServerBadges.Of).ToList();
		if (badges.SequenceEqual(_badges)) return;
		_badges = badges;
		var shown = ServerBadges.Summary(badges);
		BadgeVisible = shown != null;
		if (shown == null) return;
		if (Avalonia.Application.Current is { } app && app.TryGetResource(shown.Glyph, null, out var glyph)) BadgeGlyph = glyph as Avalonia.Media.Geometry;
		BadgeBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(shown.Color));
		BadgeSlash = shown.Slash;
	}

	/// <summary>The same thing in words, for every server: nothing is left to guess from a colour.</summary>
	[RelayCommand]
	private Task ExplainServersAsync()
	{
		var lines = _badges.Select(b => $"{(Uri.TryCreate(b.Url, UriKind.Absolute, out var u) ? u.Host : b.Url)}\n{Loc.T(b.TextKey)}");
		return Ui.AlertAsync(Loc.T("srv_title"), string.Join("\n\n", lines), Loc.T("ok"));
	}

	/// <summary>True while another device waits for its code to be typed here: a bar over the window offers the box.</summary>
	[ObservableProperty] private bool _syncPending;

	public string SyncBarText => Loc.T("sync_bar");

	private void RefreshSyncPending() => SyncPending = _session.State == SessionState.Ready && _session.Client.PendingDeviceSyncs.Count > 0;

	/// <summary>Another device of my identity asks to copy my data: the window comes forward with the box for the code that device shows.</summary>
	private void OnDeviceSyncAsked()
	{
		RefreshSyncPending();
		if (Ui.Main is { } window) { if (!window.IsVisible) window.Show(); window.Activate(); }
		_ = EnterSyncCodeAsync();
	}

	/// <summary>The box where the code shown on the other device is typed: the right one starts the transfer, a wrong one leaves a few tries.</summary>
	[RelayCommand]
	private async Task EnterSyncCodeAsync()
	{
		while (_session.State == SessionState.Ready && _session.Client.PendingDeviceSyncs.FirstOrDefault() is { } request)
		{
			var code = await Ui.PromptAsync(Loc.T("sync_title"), Loc.T("sync_enter_text"), Loc.T("sync_enter_ok"), Loc.T("cancel"), maxLength: 7);
			if (code == null) return;   // the request stays for a few minutes
			try
			{
				await _session.Client.ApproveDeviceSyncAsync(request.Id, code);
				RefreshSyncPending();
				await Ui.AlertAsync(Loc.T("sync_title"), Loc.T("sync_started"), Loc.T("ok"));
				return;
			}
			catch (ClientException ex) when (ex.Code == "sync_wrong_code")
			{
				await Ui.AlertAsync(Loc.T("sync_title"), Loc.T("sync_wrong", ex.Args.FirstOrDefault() ?? 0), Loc.T("ok"));
			}
			catch (ClientException ex) when (ex.Code is "sync_cancelled" or "sync_expired")
			{
				RefreshSyncPending();
				await Ui.AlertAsync(Loc.T("sync_title"), Loc.T(ex.Code), Loc.T("ok"));
				return;
			}
			catch (Exception ex) when (ex is ClientException or HttpRequestException) { RefreshSyncPending(); return; }
		}
		RefreshSyncPending();
	}

	private bool _syncResultShown;
	private Avalonia.Controls.Window? _syncCodeWindow;

	private async void OnDeviceSyncState(string state)
	{
		if (state is "done" or "refused") { _syncCodeWindow?.Close(); _syncCodeWindow = null; }
		switch (state)
		{
			case "received": Refresh(); break;
			case "done":
				Refresh();
				_session.ScanOffers(delayMs: 1000);   // the pictures and documents come from the other device: the download starts as the settings say
				// Several devices may answer one request: the result is told once.
				if (_syncResultShown) break;
				_syncResultShown = true;
				await Ui.AlertAsync(Loc.T("sync_title_mine"), Loc.T("sync_done", _session.Client.DeviceSyncMessages), Loc.T("ok"));
				break;
			case "refused": await Ui.AlertAsync(Loc.T("sync_title_mine"), Loc.T("sync_refused"), Loc.T("ok")); break;
		}
	}

	/// <summary>
	/// A new device whose identity has just been restored from a file: offers to be mirrored by another device of the same identity. On "yes" the request
	/// goes out and its code is shown; the person types it on their other device, which sends nothing without it.
	/// </summary>
	public async Task OfferDeviceSyncAsync()
	{
		if (_session.State != SessionState.Ready) return;
		if (!await Ui.ConfirmAsync(Loc.T("sync_prompt_title"), Loc.T("sync_prompt_text"), Loc.T("yes"), Loc.T("no"))) return;
		_syncResultShown = false;
		if (!await _session.AskDeviceSyncAsync()) { await Ui.AlertAsync(Loc.T("sync_prompt_title"), Loc.T("sync_none"), Loc.T("ok")); return; }
		// The box with the code goes away by itself when the transfer is over (or refused).
		await Ui.AlertAsync(Loc.T("sync_prompt_title"), Loc.T("sync_code_shown", _session.Client.DeviceSyncCode ?? ""), Loc.T("ok"), w => _syncCodeWindow = w);
		_syncCodeWindow = null;
	}
	/// <summary>The flag of the title bar and of the menu, in the colour of the theme.</summary>
	public Avalonia.Media.Imaging.Bitmap FlagImage => ThemeService.FlagImage;

	public ObservableCollection<ConversationRow> Rows { get; } = [];

	/// <summary>A conversation was opened (history key): its system notification can go.</summary>
	public event Action<string>? ChatOpened;

	public DesktopSession Session => _session;

	public bool ShowOnboarding => State is SessionState.NeedsOnboarding or SessionState.NeedsPhrases;
	public bool ShowMain => State == SessionState.Ready;
	private bool HasDetail => Chat != null;
	public bool ShowList => ShowMain && (!IsCompact || !HasDetail);
	public bool ShowChat => ShowMain && (!IsCompact || HasDetail);
	public bool ShowPlaceholder => ShowMain && !IsCompact && !HasDetail;
	public string NoChatText => Loc.T("d_no_chat");

	/// <summary>The title bar is replaced by the chat's own header in a narrow window, as on the mobile app.</summary>
	public bool ShowTopBar => ShowMain && !(IsChatsPage && IsCompact && HasDetail);

	/// <summary>Wide window: list (fixed) and chat side by side. Compact: one of the two takes the whole width.</summary>
	public GridLength ListColumn => !IsCompact ? new(320) : !HasDetail ? new(1, GridUnitType.Star) : new(0);

	public GridLength ChatColumn => !IsCompact ? new(1, GridUnitType.Star) : !HasDetail ? new(0) : new(1, GridUnitType.Star);

	public async Task StartAsync()
	{
		Busy = true;
		try { State = await _session.StartAsync(); Step = State == SessionState.NeedsPhrases ? OnboardingStep.Unlock : OnboardingStep.Choose; }
		catch (Exception ex) when (ex is ClientException or HttpRequestException or IOException or InvalidOperationException) { Error = Loc.T("d_error", ex.Message); State = SessionState.NeedsOnboarding; Step = OnboardingStep.Choose; }
		finally { Busy = false; }
		if (State == SessionState.Ready) Refresh();
	}

	partial void OnSearchChanged(string value) => ApplyFilter();

	partial void OnSelectedChanged(ConversationRow? value)
	{
		if (_rebuilding) return;
		Chat?.Detach();
		Chat = value == null ? null : new ChatViewModel(this, value) { ShowBack = IsCompact };
		if (Chat != null) Chat.BackRequested += CloseChat;
		if (value != null) ChatOpened?.Invoke(value.Key);
	}

	partial void OnIsCompactChanged(bool value)
	{
		if (Chat != null) Chat.ShowBack = value;
	}

	public string SearchLabel => Loc.T("d_search");

	[RelayCommand]
	private void Back() => Selected = null;

	/// <summary>Opens a conversation by its history key (a tray entry, a notification).</summary>
	public void OpenConversation(string key)
	{
		if (State != SessionState.Ready) return;
		Search = "";
		Go(AppPage.Chats);
		Selected = Rows.FirstOrDefault(r => r.Key == key);
	}

	/// <summary>Opens an archived conversation (a group I left, a contact I removed) to read it.</summary>
	public void OpenArchived(string key)
	{
		if (State != SessionState.Ready) return;
		var client = _session.Client;
		if (client.GetConversations().FirstOrDefault(s => s.Conversation.HistoryKey == key) is not { } summary) return;
		Go(AppPage.Chats);
		Chat?.Detach();
		Chat = new ChatViewModel(this, new ConversationRow(summary, 0, PreviewText.Latest(client, summary.Conversation, summary.Last))) { ShowBack = IsCompact };
		Chat.BackRequested += CloseChat;
	}

	/// <summary>
	/// Closes the open conversation (the back arrow, leaving a group). The conversation may no longer be in the list (a group I left), in which case clearing the
	/// selection changes nothing: the chat is closed explicitly.
	/// </summary>
	public void CloseChat()
	{
		Selected = null;
		Chat?.Detach();
		Chat = null;
	}

	/// <summary>The latest conversations for the tray menu: unread first, then newest activity.</summary>
	public IReadOnlyList<ConversationRow> Recent(int count) => _all.OrderByDescending(r => r.HasUnread).Take(count).ToList();

	private void Refresh()
	{
		if (State != SessionState.Ready) return;
		var client = _session.Client;
		Pending = client.PendingRequests.Count + client.Groups.PendingGroupInvites.Count + client.Groups.Suggestions.Count + client.PendingMigrations.Count;
		Archived = client.ArchivedContacts.Count(a => client.FindContact(a.DiscoveryId) == null) + client.Groups.Store.All.Count(g => !g.Active);
		UpdateMenuTitles();
		_all = client.GetConversations().Where(s => !s.IsArchived).Select(s => new ConversationRow(s, client.UnreadCount(s.Conversation), PreviewText.Latest(client, s.Conversation, s.Last))).ToList();
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		var key = Selected?.Key;
		var wanted = _all.Where(r => Search.Length == 0 || r.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase)).ToList();
		_rebuilding = true;
		try
		{
			Rows.Clear();
			foreach (var r in wanted) Rows.Add(r);
			// The list was rebuilt: keep the open conversation highlighted without reopening it.
			Selected = key == null ? null : Rows.FirstOrDefault(r => r.Key == key);
		}
		finally { _rebuilding = false; }
	}
}
