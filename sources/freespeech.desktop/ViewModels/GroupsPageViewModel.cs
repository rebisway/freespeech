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
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

public sealed record GroupRow(string Key, string Name, string? Photo, string About = "")
{
	/// <summary>What the group is about, in small letters under its name (one line, cut short).</summary>
	public string Description => About.ReplaceLineEndings(" ").Trim();
	public bool HasDescription => Description.Length > 0;
}

/// <summary>My groups; a click opens the conversation, the "+" button creates a new group.</summary>
public sealed partial class GroupsPageViewModel : ObservableObject, IDisposable
{
	private readonly MainViewModel _owner;

	public GroupsPageViewModel(MainViewModel owner)
	{
		_owner = owner;
		owner.Session.ConversationsChanged += Refresh;
		Refresh();
	}

	public ObservableCollection<GroupRow> Groups { get; } = [];

	public bool IsEmpty => Groups.Count == 0;
	public string EmptyText => Loc.T("groups_empty");
	public string NewTip => Loc.T("a11y_new_group");

	public void Dispose() => _owner.Session.ConversationsChanged -= Refresh;

	private void Refresh()
	{
		if (_owner.Session.State != SessionState.Ready) return;
		var client = _owner.Session.Client;
		var rows = client.GetConversations().Where(c => c.IsGroup && !c.IsArchived).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => new GroupRow(g.Conversation.HistoryKey, g.Title, g.Photo, g.Conversation.Group is { } group ? client.Groups.GroupDescription(group) ?? "" : "")).ToList();
		// Synced in place: clearing the list made the rows blink.
		for (var i = 0; i < rows.Count; i++)
		{
			if (i >= Groups.Count) Groups.Add(rows[i]);
			else if (Groups[i] != rows[i]) Groups[i] = rows[i];
		}
		while (Groups.Count > rows.Count) Groups.RemoveAt(Groups.Count - 1);
		OnPropertyChanged(nameof(IsEmpty));
	}

	[RelayCommand] private void Open(GroupRow row) => _owner.OpenConversation(row.Key);

	[RelayCommand] private void New() => _owner.Push(new NewGroupViewModel(_owner), Loc.T("newgroup_title"));
}