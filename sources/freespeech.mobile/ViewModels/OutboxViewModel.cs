/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

public sealed record OutboxItem(string Id, string To, string Preview, string Detail, bool Parked, string ActionLabel, bool CanToggle);

/// <summary>
/// The letters waiting for their recipient. Stopping the wait leaves the letter queued (it still goes out when its contact comes online) but the
/// app no longer retries it on its own nor stays awake for it, which lets a wake-up test run without waiting for the retry delays to elapse.
/// </summary>
public partial class OutboxViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	public ObservableCollection<OutboxItem> Items { get; } = [];

	public OutboxViewModel(ClientService service) => _service = service;

	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		var client = _service.Client;
		Items.Clear();
		foreach (var p in client.Outbox.OrderBy(p => p.CreatedAt))
		{
			var to = client.Contacts.FirstOrDefault(c => c.DiscoveryId == p.To)?.Nickname ?? p.To;
			var age = DateTime.UtcNow - p.CreatedAt;
			var last = p.LastAttemptAt is { } t ? Loc.T("outbox_last", Ago(DateTime.UtcNow - t)) : Loc.T("outbox_never");
			var state = p.SentAt != null ? Loc.T("outbox_delivered", p.Resends) : p.Parked ? Loc.T("outbox_parked") : Loc.T("outbox_waiting");
			var detail = Loc.T("outbox_detail", Ago(age), last, state);
			var preview = p.Type == "message" ? p.Text : $"[{p.Type}]";
			Items.Add(new OutboxItem(p.Id, to, preview, detail, p.Parked, Loc.T(p.Parked ? "outbox_resume" : "outbox_stop"), p.SentAt == null));
		}
	}

	private static string Ago(TimeSpan span) =>
		span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
		: span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
		: span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes:00}"
		: $"{(int)span.TotalDays} j {span.Hours} h";

	[RelayCommand]
	private void Toggle(OutboxItem item)
	{
		if (!_service.Client.SetOutboxParked(item.Id, !item.Parked)) Status = Loc.T("outbox_gone");
		Refresh();
	}
}
