/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

public sealed class ConversationItem(ConversationSummary summary, FreeSpeechClient client)
{
	public string Key { get; } = summary.Conversation.HistoryKey;
	public string Title { get; } = summary.Title;
	public string? Photo { get; } = summary.Photo;
	public string Preview { get; } = PreviewText.Latest(client, summary.Conversation, summary.Last);
	public bool IsGroup { get; } = summary.IsGroup;
	public int Unread { get; } = client.UnreadCount(summary.Conversation);
	public bool IsMuted { get; } = ClientService.IsMuted(summary.Conversation.HistoryKey);
	public bool HasUnread => Unread > 0;
	public Color UnreadColor => IsMuted ? (Color)Application.Current!.Resources["Gray300"] : (Color)Application.Current!.Resources["Primary"];   // a muted conversation counts quietly
	public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
	public FontAttributes TitleStyle => HasUnread ? FontAttributes.Bold : FontAttributes.None;
	public string TimeText { get; } = summary.Last == null ? "" : Format(summary.Last.At.ToLocalTime());

	/// <summary>What the row shows: two rows with the same signature look the same, so the existing one is kept (no flash, no lost picture).</summary>
	public string Signature => string.Join("|", Key, Title, Photo?.GetHashCode(), Preview, IsGroup, Unread, IsMuted, TimeText);

	private static string Format(DateTime t) => t.Date == DateTime.Today ? t.ToString("HH:mm") : t.Date == DateTime.Today.AddDays(-1) ? Loc.T("time_yesterday") : t.ToString("dd/MM");
}

/// <summary>The home screen: every contact and group, newest activity first, and the latest notification banner.</summary>
public partial class ConversationsViewModel : ObservableObject
{
	private readonly ClientService _service;
	private string _bannerTarget = "";

	[ObservableProperty] private bool _isEmpty;
	[ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBanner))] private string _bannerText = "";
	[ObservableProperty] private string _bannerTitle = "";

	public ObservableCollection<ConversationItem> Items { get; } = [];

	public bool HasBanner => BannerText.Length > 0;

	/// <summary>
	/// What waits for my answer (contact requests, group invitations, proposals) stays on top of the list until it is dealt
	/// with. Unlike the banner it is not a one-off: an invitation that arrived while the app was closed is shown too.
	/// </summary>
	public int PendingCount => AppBadges.Instance.PendingRequests;
	public bool HasPending => PendingCount > 0;
	public string PendingText => Loc.T(PendingCount == 1 ? "pending_one" : "pending_many", PendingCount);

	public ConversationsViewModel(ClientService service)
	{
		_service = service;
		service.ConversationsChanged += Refresh;
		ClientService.RouteRequested += () => MainThread.BeginInvokeOnMainThread(ConsumeRoute);
		AppBadges.Instance.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(AppBadges.PendingRequests)) MainThread.BeginInvokeOnMainThread(RefreshPending);
		};
		Loc.Instance.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshPending);
		service.ChatSeen += key =>
		{
			if (_bannerTarget == $"chat?key={Uri.EscapeDataString(key)}") DismissBanner();
		};
		service.Banner += b =>
		{
			BannerTitle = b.Title;
			BannerText = b.Text;
			_bannerTarget = b.Target;
		};
	}

	/// <summary>Another device of my identity waits for its code to be typed here (a few minutes): a bar on top of the list.</summary>
	public bool HasSyncPending => _service.HasDeviceSyncPending;
	[RelayCommand] private Task EnterSyncCodeAsync() => _service.EnterSyncCodeAsync();

	private void RefreshPending()
	{
		OnPropertyChanged(nameof(HasSyncPending));
		OnPropertyChanged(nameof(PendingCount));
		OnPropertyChanged(nameof(HasPending));
		OnPropertyChanged(nameof(PendingText));
	}

	[RelayCommand] private Task OpenApproveAsync() => Shell.Current.GoToAsync("//approve");
	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		var client = _service.Client;
		var fresh = client.GetConversations();   // already newest activity first
		// Bring the list in line without rebuilding it: a row that did not change stays the same object, so its picture does not blink
		// away (the chat being opened refreshes the list, right under the user's finger).
		var existing = Items.ToDictionary(i => i.Key);
		var target = fresh.Where(s => !s.IsArchived).Select(s => new ConversationItem(s, client))
			.Select(n => existing.TryGetValue(n.Key, out var old) && old.Signature == n.Signature ? old : n).ToList();
		var wanted = new HashSet<ConversationItem>(target);
		for (var k = Items.Count - 1; k >= 0; k--) if (!wanted.Contains(Items[k])) Items.RemoveAt(k);
		for (var k = 0; k < target.Count; k++)
		{
			if (k < Items.Count && ReferenceEquals(Items[k], target[k])) continue;
			var at = Items.IndexOf(target[k]);
			if (at >= 0) Items.Move(at, k); else Items.Insert(k, target[k]);
		}
		IsEmpty = Items.Count == 0;
		OnPropertyChanged(nameof(HasSyncPending));
		_service.UpdateBadges();   // the menu's Archives entry carries the number of archived conversations
		ConsumeRoute();            // the app was opened from a notification
		_ = ClientService.OfferBackgroundSetupAsync();   // once: lets the push wake-up start the app on phones that restrict it
	}

	private void ConsumeRoute()
	{
		if (_service.State != AppState.Ready || ClientService.PendingRoute is not { } route || Shell.Current == null) return;
		ClientService.PendingRoute = null;
		if (route == "sync") { _ = _service.EnterSyncCodeAsync(); return; }   // the notice of a device asking to be mirrored: the box for its code
		_ = Shell.Current.GoToAsync(route);
	}

	[RelayCommand] private Task OpenAsync(ConversationItem item) { FreeSpeech.Mobile.Services.Perf.Start("tap on a conversation"); return Shell.Current.GoToAsync($"chat?key={Uri.EscapeDataString(item.Key)}"); }

	/// <summary>A tap on the banner goes where it points (the chat, or the contact requests) and closes it.</summary>
	[RelayCommand]
	private async Task OpenBannerAsync()
	{
		var target = _bannerTarget;
		DismissBanner();
		if (target == "sync") await _service.EnterSyncCodeAsync();
		else if (target.Length > 0) await Shell.Current.GoToAsync(target);
	}

	/// <summary>Swipe to the left.</summary>
	[RelayCommand] private void DismissBanner() => BannerText = "";
}
