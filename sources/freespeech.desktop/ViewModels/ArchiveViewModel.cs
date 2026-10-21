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
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

public sealed record ArchivedItem(string Key, string Id, string Name, string? Photo, string Preview, bool IsGroup);

/// <summary>
/// The groups I left or was excluded from, and the contacts I removed. They no longer clutter the chat list, but their messages
/// stay readable here until I delete them for good (which also removes the files that came with them). Joining the group again
/// or adding the contact again brings the conversation back to the list.
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject, IDisposable
{
	private readonly MainViewModel _owner;

	[ObservableProperty] private string _status = "";

	public ArchiveViewModel(MainViewModel owner)
	{
		_owner = owner;
		owner.Session.ConversationsChanged += Refresh;
		Refresh();
	}

	public ObservableCollection<ArchivedItem> Items { get; } = [];

	public bool IsEmpty => Items.Count == 0;
	public string EmptyText => Loc.T("archive_empty");
	public string DeleteTip => Loc.T("a11y_delete");

	public void Dispose() => _owner.Session.ConversationsChanged -= Refresh;

	private void Refresh()
	{
		if (_owner.Session.State != SessionState.Ready) return;
		var client = _owner.Session.Client;
		Items.Clear();
		foreach (var s in client.GetConversations().Where(s => s.IsArchived))
			Items.Add(new ArchivedItem(s.Conversation.HistoryKey, s.Conversation.Group?.GroupId ?? s.Conversation.HistoryKey, s.Title, s.Photo, PreviewText.Latest(client, s.Conversation, s.Last).ReplaceLineEndings(" "), s.IsGroup));
		OnPropertyChanged(nameof(IsEmpty));
	}

	/// <summary>An archived conversation is read, not written in: the chat opens like any other.</summary>
	[RelayCommand] private void Open(ArchivedItem item) => _owner.OpenArchived(item.Key);

	[RelayCommand]
	private async Task DeleteAsync(ArchivedItem item)
	{
		if (!await Ui.ConfirmAsync(Loc.T("archive_delete_title"), Loc.T("archive_delete_text", item.Name), Loc.T("delete"), Loc.T("cancel"))) return;
		try
		{
			if (item.IsGroup) _owner.Session.Client.DeleteGroupPermanently(_owner.Session.Client.ResolveGroup(item.Id));
			else _owner.Session.Client.DeleteArchivedContactPermanently(item.Id);
			Status = Loc.T("archive_deleted", item.Name);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_owner.Session.RaiseConversationsChanged();
	}
}