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

public sealed record ShareTarget(Conversation Conversation, string Title, string? Photo, bool IsGroup);

/// <summary>Another app shared something with FreeSpeech: shows what, and lets the user tick the conversations it goes to.</summary>
public partial class ShareViewModel : ObservableObject
{
	private readonly ClientService _service;

	[ObservableProperty] private string _summary = "";
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private bool _isBusy;

	public ObservableCollection<ShareTarget> Targets { get; } = [];
	public ObservableCollection<object> Selected { get; } = [];
	public ObservableCollection<SharedItem> Items { get; } = [];

	public ShareViewModel(ClientService service) => _service = service;

	/// <summary>Called each time the screen opens.</summary>
	public void Load()
	{
		Items.Clear();
		foreach (var i in ShareInbox.Items) Items.Add(i);
		Summary = Loc.T("share_summary", Items.Count);
		Status = "";
		Selected.Clear();
		Targets.Clear();
		if (_service.State != AppState.Ready) return;
		foreach (var s in _service.Client.GetConversations().Where(s => !s.IsArchived && !(s.Conversation.Group is { Active: false })))
			Targets.Add(new ShareTarget(s.Conversation, s.Title, s.Photo, s.IsGroup));
	}

	[RelayCommand]
	private async Task SendAsync()
	{
		var targets = Selected.OfType<ShareTarget>().ToList();
		if (targets.Count == 0) { Status = Loc.T("share_pick_one"); return; }
		if (!await _service.ConfirmLargeSendAsync(Items.Where(i => i.IsFile).Select(i => i.Path!))) return;
		IsBusy = true;
		var failed = 0;
		foreach (var t in targets)
		{
			foreach (var item in Items)
			{
				try
				{
					if (item.IsFile) await _service.Client.UploadAsync(t.Conversation, item.Path!);
					else await _service.Client.SendAsync(t.Conversation, item.Text!, null);
				}
				catch (Exception ex) when (ex is ClientException or HttpRequestException or IOException or InvalidOperationException) { failed++; }
			}
		}
		IsBusy = false;
		ShareInbox.Items = [];
		_service.NotifyConversationsChanged();
		if (failed > 0) { Status = Loc.T("share_failed", failed); return; }
		await Shell.Current.GoToAsync("//conversations");
	}

	[RelayCommand]
	private async Task CancelAsync()
	{
		ShareInbox.Items = [];
		await Shell.Current.GoToAsync("//conversations");
	}
}