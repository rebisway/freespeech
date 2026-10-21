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

public sealed partial class Candidate(string nickname) : ObservableObject
{
	public string Nickname { get; } = nickname;
	[ObservableProperty] private bool _isSelected;
}

/// <summary>Creating a group: a name and some confirmed contacts. The others can be added later by a moderator.</summary>
public sealed partial class NewGroupViewModel : ObservableObject
{
	private readonly MainViewModel _owner;

	[ObservableProperty] private string _name = "";
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _busy;

	public NewGroupViewModel(MainViewModel owner)
	{
		_owner = owner;
		foreach (var c in owner.Session.Client.Contacts)
		{
			var candidate = new Candidate(c.DisplayName);
			candidate.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Candidate.IsSelected)) Recount(); };
			Candidates.Add(candidate);
		}
		Reshow();
		Recount();
	}

	/// <summary>Every confirmed contact (the ticks live here); <see cref="Shown"/> is what the search lets through.</summary>
	public List<Candidate> Candidates { get; } = [];
	public ObservableCollection<Candidate> Shown { get; } = [];

	[ObservableProperty] private string _filter = "";
	[ObservableProperty] private string _selectedText = "";

	partial void OnFilterChanged(string value) => Reshow();

	private void Reshow()
	{
		var q = Filter.Trim();
		Shown.Clear();
		foreach (var c in Candidates.Where(c => q.Length == 0 || c.Nickname.Contains(q, StringComparison.CurrentCultureIgnoreCase))) Shown.Add(c);
	}

	private void Recount() => SelectedText = Loc.T("pick_count", Candidates.Count(c => c.IsSelected));
	public string SearchHint => Loc.T("pick_search");

	public bool HasCandidates => Candidates.Count > 0;
	public bool HasNoCandidates => Candidates.Count == 0;
	public string NameHint => Loc.T("newgroup_name_hint");
	public string MembersTitle => Loc.T("newgroup_members");
	public string NoContactsText => Loc.T("newgroup_no_contacts");
	public string CreateLabel => Loc.T("newgroup_create");

	[RelayCommand]
	private async Task CreateAsync()
	{
		Error = "";
		if (string.IsNullOrWhiteSpace(Name)) { Error = Loc.T("newgroup_err_name"); return; }

		Busy = true;
		try
		{
			var group = await _owner.Session.Client.CreateGroupAsync(Name.Trim(), Candidates.Where(c => c.IsSelected).Select(c => c.Nickname));
			_owner.Session.RaiseConversationsChanged();   // the list knows the group before it is opened
			_owner.Pop();
			_owner.OpenConversation("group:" + group.GroupId);
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); }
		finally { Busy = false; }
	}
}