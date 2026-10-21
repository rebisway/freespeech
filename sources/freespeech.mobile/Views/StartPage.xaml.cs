/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.Views;

/// <summary>Opens the profile and sends the user to the right screen: onboarding, or the conversations.</summary>
public partial class StartPage : ContentPage
{
	private readonly ClientService _service;

	public StartPage(ClientService service)
	{
		InitializeComponent();
		_service = service;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await StartAsync();
	}

	private async Task StartAsync()
	{
		Retry.IsVisible = false;
		Spinner.IsRunning = true;
		Message.Text = "";
		try
		{
			var state = await _service.StartAsync();
			await AppLock.WaitUnlockedAsync();   // a navigation would take the lock screen off: the app opens once the code is right
			await Shell.Current.GoToAsync(state == AppState.Ready ? _service.FirstScreen() : "//onboarding");
		}
		catch (Exception ex)
		{
			Spinner.IsRunning = false;
			Message.Text = Loc.T("start_open_error", ex.Message);
			Retry.IsVisible = true;
		}
	}

	private async void OnRetry(object? sender, EventArgs e) => await StartAsync();
}