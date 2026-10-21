/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.Views;

/// <summary>
/// Remote wipe for a stolen phone (PROTOCOL §1): the three phrases typed here order every other device of this identity to erase
/// itself. The same phrases, typed once on each phone, make it obey (identities from before this existed have to be enabled).
/// </summary>
public partial class WipePage : ContentPage
{
	private readonly ClientService _service;
	private bool _busy;

	public WipePage(ClientService service)
	{
		InitializeComponent();
		_service = service;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Refresh();
	}

	private void Refresh()
	{
		var ready = _service.Client.RemoteWipeReady;
		State.Text = Loc.T(ready ? "wipe_state_on" : "wipe_state_off");
		State.TextColor = (Color)Application.Current!.Resources[ready ? "Secondary" : "Danger"];
		ActivateButton.IsVisible = !ready;
	}

	private (string, string, string) Typed() => (Phrase1.Text ?? "", Phrase2.Text ?? "", Phrase3.Text ?? "");

	private void Busy(bool on)
	{
		_busy = on;
		Spinner.IsVisible = Spinner.IsRunning = on;
	}

	private async void OnActivate(object? sender, EventArgs e)
	{
		if (_busy) return;
		Busy(true);
		Result.Text = "";
		try
		{
			var (p1, p2, p3) = Typed();
			await Task.Run(() => _service.Client.EnableRemoteWipe(p1, p2, p3));   // the slow key derivation stays off the UI thread
			ClearPhrases();
			// The revocation key is now known: contacts and group peers pin it, which is what lets them check a later migration notice.
			_ = _service.Client.NotifyContactsAsync();
			_ = _service.Client.AnnounceRevocationKeyAsync();
			Result.Text = Loc.T("wipe_activated");
			Refresh();
		}
		catch (ClientException ex) { Result.Text = ErrorText.Of(ex); }
		finally { Busy(false); }
	}

	private async void OnSend(object? sender, EventArgs e)
	{
		if (_busy) return;
		if (!await Dialogs.AlertAsync(Loc.T("wipe_title"), Loc.T("wipe_confirm"), Loc.T("wipe_confirm_yes"), Loc.T("cancel"))) return;
		Busy(true);
		Result.Text = "";
		try
		{
			var (p1, p2, p3) = Typed();
			var reached = await Task.Run(() => _service.Client.SendWipeAsync(p1, p2, p3));
			ClearPhrases();
			Result.Text = Loc.T(reached ? "wipe_sent" : "wipe_sent_later");
			Refresh();
		}
		catch (ClientException ex) { Result.Text = ErrorText.Of(ex); }
		finally { Busy(false); }
	}

	private void ClearPhrases() => Phrase1.Text = Phrase2.Text = Phrase3.Text = "";
}
