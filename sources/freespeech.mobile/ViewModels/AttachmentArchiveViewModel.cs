/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Client.Archive;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>
/// Frees space: the downloaded files that match a selection (by default those of messages more than a year old) go into an encrypted archive
/// file the person puts where they want; the messages stay, and so do the thumbnails of pictures and videos. Loading an archive brings them back.
/// </summary>
public partial class AttachmentArchiveViewModel : ListViewModelBase
{
	private readonly ClientService _service;
	private List<string?> _conversationIds = [null];
	private int _refreshes;

	[ObservableProperty] private int _months = 12;
	[ObservableProperty] private bool _moreOptions;
	[ObservableProperty] private bool _useDates;
	[ObservableProperty] private DateTime _fromDate = DateTime.Today.AddYears(-2);
	[ObservableProperty] private DateTime _toDate = DateTime.Today.AddYears(-1);
	[ObservableProperty] private int _conversationIndex;
	[ObservableProperty] private int _kindIndex;
	[ObservableProperty] private int _minSizeMb;
	[ObservableProperty] private string _preview = "";
	[ObservableProperty] private bool _isBusy;
	[ObservableProperty] private bool _canArchive;

	public AttachmentArchiveViewModel(ClientService service) => _service = service;

	private FreeSpeechClient Client => _service.Client;

	/// <summary>The conversations to choose from, "all" first.</summary>
	public IReadOnlyList<string> Conversations { get; private set; } = [];

	/// <summary>The kinds of file to choose from; the order is the one of <see cref="Selection"/>.</summary>
	public IReadOnlyList<string> Kinds => [Loc.T("arch_kind_all"), Loc.T("arch_kind_photos"), Loc.T("arch_kind_videos"), Loc.T("arch_kind_audio"), Loc.T("arch_kind_other")];

	/// <summary>Called each time the screen opens.</summary>
	public void Load()
	{
		var summaries = _service.State == AppState.Ready ? Client.GetConversations().Where(s => s.Conversation.Group is not { Active: false }).ToList() : [];
		_conversationIds = [null, .. summaries.Select(s => s.Conversation.Group?.GroupId ?? s.Conversation.ContactId)];
		Conversations = [Loc.T("arch_all_conversations"), .. summaries.Select(s => s.Title)];
		OnPropertyChanged(nameof(Conversations));
		ConversationIndex = 0;
		Status = "";
		Refresh();
	}

	partial void OnMonthsChanged(int value) => Refresh();
	partial void OnUseDatesChanged(bool value) => Refresh();
	partial void OnFromDateChanged(DateTime value) => Refresh();
	partial void OnToDateChanged(DateTime value) => Refresh();
	partial void OnConversationIndexChanged(int value) => Refresh();
	partial void OnKindIndexChanged(int value) => Refresh();
	partial void OnMinSizeMbChanged(int value) => Refresh();

	[RelayCommand] private void ToggleOptions() => MoreOptions = !MoreOptions;

	/// <summary>What the choices make, as the library understands it.</summary>
	private ArchiveSelection Selection() => new()
	{
		Before = Months > 0 ? DateTime.UtcNow.AddMonths(-Months) : null,
		From = UseDates ? FromDate.ToUniversalTime() : null,
		To = UseDates ? ToDate.AddDays(1).ToUniversalTime() : null,   // the last day counts in full
		Conversation = ConversationIndex > 0 && ConversationIndex < _conversationIds.Count ? _conversationIds[ConversationIndex] : null,
		MimePrefix = KindIndex switch { 1 => "image/", 2 => "video/", 3 => "audio/", _ => null },
		OtherKindsOnly = KindIndex == 4,
		MinSize = Math.Max(0, MinSizeMb) * 1024L * 1024L,
	};

	/// <summary>Counts, off the screen thread, what the choices would archive. A newer answer replaces an older one that finishes late.</summary>
	private void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		var turn = ++_refreshes;
		var selection = Selection();
		_ = Task.Run(() => Client.Archive.Preview(selection)).ContinueWith(task =>
		{
			if (turn != _refreshes || task.IsFaulted) return;
			var preview = task.Result;
			MainThread.BeginInvokeOnMainThread(() =>
			{
				CanArchive = preview.Files > 0;
				Preview = preview.Files == 0
					? Loc.T("arch_none")
					: Loc.T("arch_preview", preview.Files, ByteSize.Format(preview.Bytes, Loc.T("size_units")));
			});
		});
	}

	[RelayCommand]
	private async Task ArchiveAsync()
	{
		if (ClientService.Documents is not { } picker) { Status = Loc.T("arch_no_picker"); return; }
		var selection = Selection();
		var preview = await Task.Run(() => Client.Archive.Preview(selection));
		if (preview.Files == 0) { Status = Loc.T("arch_none"); return; }

		var size = ByteSize.Format(preview.Bytes, Loc.T("size_units"));
		if (!await Dialogs.AlertAsync(Loc.T("arch_confirm_title"), Loc.T("arch_confirm_text", preview.Files, size), Loc.T("arch_go"), Loc.T("cancel"))) return;
		var pin = await PinConfirm.AskAsync();
		if (!pin.Ok) { Status = pin.Message; return; }

		IsBusy = true;
		try
		{
			var target = await picker.CreateAsync($"freespeech-archive-{DateTime.Now:yyyy-MM-dd}.fsarchive", "application/octet-stream");
			if (target == null) return;   // the person did not choose a place: nothing was touched

			var summary = await Task.Run(() =>
			{
				using var output = target.OpenWrite();
				return Client.Archive.Create(output, selection, target.OpenRead);
			});
			Status = Loc.T("arch_done", summary.Files, ByteSize.Format(summary.FreedBytes, Loc.T("size_units")), target.Name);
			_service.NotifyConversationsChanged();
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { Status = Loc.T("arch_failed", ex.Message); }
		finally { IsBusy = false; Refresh(); }
	}

	[RelayCommand]
	private async Task LoadArchiveAsync()
	{
		if (ClientService.Documents is not { } picker) { Status = Loc.T("arch_no_picker"); return; }
		IsBusy = true;
		try
		{
			var source = await picker.OpenAsync();
			if (source == null) return;

			var summary = await Task.Run(() => Client.Archive.Restore(source.OpenRead));
			Status = summary == null ? Loc.T("arch_load_bad") : Loc.T("arch_loaded", summary.Files, summary.Records, summary.Skipped);
			if (summary != null) _service.NotifyConversationsChanged();
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { Status = Loc.T("arch_failed", ex.Message); }
		finally { IsBusy = false; Refresh(); }
	}
}
