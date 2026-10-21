/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>
/// The safety number shared with a contact (protocol §3). Two people compare it aloud or side by side: equal numbers mean no one
/// swapped the keys while the contact cards were exchanged. The user then marks the contact as verified.
/// </summary>
[QueryProperty(nameof(Id), "id")]
public partial class SafetyViewModel : ObservableObject
{
	private readonly ClientService _service;
	private FreeSpeech.Client.Contacts.Contact? _contact;

	[ObservableProperty] private string _title = "";
	[ObservableProperty] private string _number = "";
	[ObservableProperty] private string? _photo;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ToggleLabel)), NotifyPropertyChangedFor(nameof(StatusText))] private bool _isVerified;

	public string ToggleLabel => Loc.T(IsVerified ? "safety_unmark" : "safety_mark");
	public string StatusText => Loc.T(IsVerified ? "safety_verified" : "safety_not_verified");

	public SafetyViewModel(ClientService service) => _service = service;

	public string Id
	{
		set
		{
			_contact = _service.Client.FindContact(Uri.UnescapeDataString(value));
			if (_contact == null) return;
			Title = _contact.DisplayName;
			Photo = _contact.Photo;
			Number = _service.Client.SafetyNumberOf(_contact);
			IsVerified = _contact.VerifiedAt != null;
		}
	}

	[RelayCommand]
	private void Toggle()
	{
		if (_contact == null) return;
		_service.Client.SetVerified(_contact, !IsVerified);
		IsVerified = _contact.VerifiedAt != null;
		_service.NotifyConversationsChanged();   // the contact list shows the mark
	}
}