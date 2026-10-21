/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

/// <summary>Step 2 of a new identity. No way back: leaving this screen is the end of the only chance to save the phrases.</summary>
public partial class PhrasesBackupPage : ContentPage
{
	private readonly PhrasesBackupViewModel _vm;

	public PhrasesBackupPage(PhrasesBackupViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
		Shell.SetNavBarIsVisible(this, false);
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Services.KeyboardHelper.Hide();
		// The phrases live in memory only: if the app was restarted on this screen they are gone, and so is the draft. Start again.
		if (!_vm.Load()) await Shell.Current.GoToAsync("//onboarding");
	}

	protected override bool OnBackButtonPressed() => true;
}