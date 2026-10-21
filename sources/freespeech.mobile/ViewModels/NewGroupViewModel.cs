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

public sealed partial class Candidate(string nickname) : ObservableObject
{
	public string Nickname { get; } = nickname;
	[ObservableProperty] private bool _isSelected;
}

/// <summary>Creating a group: a name and some confirmed contacts. The others can be added later by a moderator.</summary>
public partial class NewGroupViewModel : ObservableObject
{
	private readonly ClientService _service;

	[ObservableProperty] private string _name = "";
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _isBusy;

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

	public NewGroupViewModel(ClientService service)
	{
		_service = service;
		foreach (var c in service.Client.Contacts)
		{
			var candidate = new Candidate(c.DisplayName);
			candidate.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Candidate.IsSelected)) Recount(); };
			Candidates.Add(candidate);
		}
		Reshow();
		Recount();
	}

	[RelayCommand]
	private async Task CreateAsync()
	{
		Error = "";
		if (string.IsNullOrWhiteSpace(Name)) { Error = Loc.T("newgroup_err_name"); return; }

		IsBusy = true;
		try
		{
			var group = await _service.Client.CreateGroupAsync(Name.Trim(), Candidates.Where(c => c.IsSelected).Select(c => c.Nickname));
			await Shell.Current.GoToAsync("..");
			await Shell.Current.GoToAsync($"chat?key={Uri.EscapeDataString("group:" + group.GroupId)}");
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}
}