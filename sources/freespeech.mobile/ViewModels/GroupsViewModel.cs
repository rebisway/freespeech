/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

public sealed record GroupRow(string Key, string Name, string? Photo, string About = "")
{
	/// <summary>What the group is about, in small letters under its name (one line, cut short).</summary>
	public string Description => About.ReplaceLineEndings(" ").Trim();
	public bool HasDescription => Description.Length > 0;
}

/// <summary>My groups; a tap opens the conversation, the "+" button creates a new group.</summary>
public partial class GroupsViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	public ObservableCollection<GroupRow> Groups { get; } = [];

	public GroupsViewModel(ClientService service)
	{
		_service = service;
		service.ConversationsChanged += Refresh;
	}

	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		// Synced in place: clearing the list made every picture blink out while the page was being left.
		var rows = _service.Client.GetConversations().Where(c => c.IsGroup).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => new GroupRow(g.Conversation.HistoryKey, g.Title, g.Photo, g.Conversation.Group is { } group ? _service.Client.Groups.GroupDescription(group) ?? "" : "")).ToList();
		for (var i = 0; i < rows.Count; i++)
		{
			if (i >= Groups.Count) Groups.Add(rows[i]);
			else if (Groups[i] != rows[i]) Groups[i] = rows[i];
		}
		while (Groups.Count > rows.Count) Groups.RemoveAt(Groups.Count - 1);
	}

	[RelayCommand] private Task OpenAsync(GroupRow row) => Shell.Current.GoToAsync($"chat?key={Uri.EscapeDataString(row.Key)}");
	[RelayCommand] private Task NewAsync() => Shell.Current.GoToAsync("newgroup");
}
