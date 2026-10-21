/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>
/// The safety number shared with a contact (protocol section 3). Two people compare it aloud or side by side: equal numbers mean no
/// one swapped the keys while the contact cards were exchanged. The user then marks the contact as verified.
/// </summary>
public sealed partial class SafetyViewModel : ObservableObject
{
	private readonly MainViewModel _owner;
	private readonly Contact? _contact;

	[ObservableProperty] private string _title = "";
	[ObservableProperty] private string _number = "";
	[ObservableProperty] private string? _photo;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ToggleLabel), nameof(StatusText))] private bool _isVerified;

	public SafetyViewModel(MainViewModel owner, string contactId)
	{
		_owner = owner;
		_contact = owner.Session.Client.FindContact(contactId);
		if (_contact == null) return;
		Title = _contact.DisplayName;
		Photo = _contact.Photo;
		Number = owner.Session.Client.SafetyNumberOf(_contact);
		IsVerified = _contact.VerifiedAt != null;
	}

	public string ToggleLabel => Loc.T(IsVerified ? "safety_unmark" : "safety_mark");
	public string StatusText => Loc.T(IsVerified ? "safety_verified" : "safety_not_verified");
	public string ExplainText => Loc.T("safety_explain");

	[RelayCommand]
	private void Toggle()
	{
		if (_contact == null) return;
		_owner.Session.Client.SetVerified(_contact, !IsVerified);
		IsVerified = _contact.VerifiedAt != null;
		_owner.Session.RaiseConversationsChanged();   // the contact list shows the mark
	}
}