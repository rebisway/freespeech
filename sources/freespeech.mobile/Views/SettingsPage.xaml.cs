/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

public partial class SettingsPage : ContentPage
{
	private readonly SettingsViewModel _vm;

	public SettingsPage(SettingsViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
	}

	/// <summary>False when the user was asked, with unsaved changes, and chose to stay.</summary>
	public async Task<bool> ConfirmLeaveAsync()
	{
		if (!_vm.HasUnsavedChanges) return true;
		var leave = await Dialogs.AlertAsync(Loc.T("set_unsaved_title"), Loc.T("set_unsaved_text"), Loc.T("set_unsaved_leave"), Loc.T("set_unsaved_stay"));
		if (leave) _vm.DiscardChanges();
		return leave;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_vm.Refresh();
	}
}
