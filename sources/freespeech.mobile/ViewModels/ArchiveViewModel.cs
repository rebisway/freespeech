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

public sealed record ArchivedItem(string Key, string Id, string Name, string? Photo, string Preview, bool IsGroup);

/// <summary>
/// The groups I left or was excluded from, and the contacts I removed. They no longer clutter the chat list, but their messages
/// stay readable here until I delete them for good (which also removes the files that came with them). Joining the group again
/// or adding the contact again brings the conversation back to the list.
/// </summary>
public partial class ArchiveViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	public ObservableCollection<ArchivedItem> Items { get; } = [];

	public ArchiveViewModel(ClientService service)
	{
		_service = service;
		service.ConversationsChanged += Refresh;
	}

	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		var client = _service.Client;
		Items.Clear();
		foreach (var s in client.GetConversations().Where(s => s.IsArchived))
			Items.Add(new ArchivedItem(s.Conversation.HistoryKey, s.Conversation.Group?.GroupId ?? s.Conversation.HistoryKey, s.Title, s.Photo, PreviewText.Latest(client, s.Conversation, s.Last), s.IsGroup));
		_service.UpdateBadges();
	}

	[RelayCommand] private Task OpenAsync(ArchivedItem group) => Shell.Current.GoToAsync($"chat?key={Uri.EscapeDataString(group.Key)}");

	[RelayCommand]
	private async Task DeleteAsync(ArchivedItem item)
	{
		if (!await Dialogs.AlertAsync(Loc.T("archive_delete_title"), Loc.T("archive_delete_text", item.Name), Loc.T("delete"), Loc.T("cancel"))) return;
		try
		{
			if (item.IsGroup) _service.Client.DeleteGroupPermanently(_service.Client.ResolveGroup(item.Id));
			else _service.Client.DeleteArchivedContactPermanently(item.Id);
			Status = Loc.T("archive_deleted", item.Name);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_service.NotifyConversationsChanged();
		Refresh();
	}
}
