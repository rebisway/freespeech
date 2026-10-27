/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

public sealed record OutboxItem(string Id, string To, string Preview, string Detail, bool Parked, string ActionLabel, bool CanToggle, string ContactId = "", bool CanOpenFallback = false)
{
	public string OpenServerLabel => Loc.T("outbox_open_server");

	/// <summary>Stop / resume the wait: hidden when the open-server button takes its place.</summary>
	public bool ShowToggle => CanToggle && !CanOpenFallback;
}

/// <summary>
/// The letters waiting for their recipient. Stopping the wait leaves the letter queued (it still goes out when its contact comes online) but the
/// app no longer retries it on its own. The list is read again every few seconds.
/// </summary>
public sealed partial class OutboxViewModel : ObservableObject, IDisposable
{
	private readonly MainViewModel _owner;
	private readonly DispatcherTimer _timer;

	[ObservableProperty] private string _status = "";

	public OutboxViewModel(MainViewModel owner)
	{
		_owner = owner;
		_timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Refresh());
		_timer.Start();
		Refresh();
	}

	public ObservableCollection<OutboxItem> Items { get; } = [];

	public bool IsEmpty => Items.Count == 0;
	public string EmptyText => Loc.T("outbox_empty");

	public void Dispose() => _timer.Stop();

	private void Refresh()
	{
		if (_owner.Session.State != SessionState.Ready) return;
		var client = _owner.Session.Client;
		Items.Clear();
		foreach (var p in client.Outbox.OrderBy(p => p.CreatedAt))
		{
			var to = client.Contacts.FirstOrDefault(c => c.DiscoveryId == p.To)?.Nickname ?? p.To;
			var last = p.LastAttemptAt is { } t ? Loc.T("outbox_last", Ago(DateTime.UtcNow - t)) : Loc.T("outbox_never");
			var route = client.RouteStateOf(p.To);
			var privateDown = p.SentAt == null && route == FreeSpeech.Client.Relay.RouteState.PrivateDown;
			var state = p.SentAt != null ? Loc.T("outbox_delivered", p.Resends) : p.Parked ? Loc.T("outbox_parked") : privateDown ? Loc.T("outbox_private_wait") : Loc.T("outbox_waiting");
			var detail = Loc.T("outbox_detail", Ago(DateTime.UtcNow - p.CreatedAt), last, state);
			var preview = p.Type == "message" ? p.Text : $"[{p.Type}]";
			Items.Add(new OutboxItem(p.Id, to, preview.ReplaceLineEndings(" "), detail, p.Parked, Loc.T(p.Parked ? "outbox_resume" : "outbox_stop"), p.SentAt == null, p.To,
				privateDown && client.OpenFallbackOf(p.To) == FreeSpeech.Client.Relay.OpenFallbackMode.Ask && client.PeerAllowsOpenFallback(p.To)));
		}
		OnPropertyChanged(nameof(IsEmpty));
	}

	private static string Ago(TimeSpan span) =>
		span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
		: span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
		: span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes:00}"
		: $"{(int)span.TotalDays} j {span.Hours} h";

	/// <summary>The user says yes to an open server for this contact's waiting messages.</summary>
	[RelayCommand]
	private void UseOpenServer(OutboxItem item)
	{
		_owner.Session.Client.ApproveOpenFallback(item.ContactId);
		Refresh();
	}

	[RelayCommand]
	private void Toggle(OutboxItem item)
	{
		Status = _owner.Session.Client.SetOutboxParked(item.Id, !item.Parked) ? "" : Loc.T("outbox_gone");
		Refresh();
	}
}