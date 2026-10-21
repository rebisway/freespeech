/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>
/// Remote wipe for a stolen device (PROTOCOL section 1): the three phrases typed here order every other device of this identity to
/// erase itself. The same phrases, typed once on each device, make it obey.
/// </summary>
public sealed partial class WipeViewModel(MainViewModel owner) : ObservableObject
{
	[ObservableProperty] private string _phrase1 = "";
	[ObservableProperty] private string _phrase2 = "";
	[ObservableProperty] private string _phrase3 = "";
	[ObservableProperty] private string _result = "";
	[ObservableProperty] private bool _busy;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(CanActivate), nameof(StateIsGood))] private bool _ready = owner.Session.Client.RemoteWipeReady;

	private FreeSpeechClient Client => owner.Session.Client;

	public string StateText => Loc.T(Ready ? "wipe_state_on" : "wipe_state_off");
	public bool StateIsGood => Ready;
	public bool CanActivate => !Ready;
	public string HintText => Loc.T("wipe_hint");
	public string PhrasesText => Loc.T("wipe_phrases");
	public string ActivateLabel => Loc.T("wipe_activate");
	public string SendLabel => Loc.T("wipe_send");

	private void Clear() => Phrase1 = Phrase2 = Phrase3 = "";

	[RelayCommand]
	private async Task ActivateAsync()
	{
		if (Busy) return;
		Busy = true;
		Result = "";
		try
		{
			var (p1, p2, p3) = (Phrase1.Trim(), Phrase2.Trim(), Phrase3.Trim());
			await Task.Run(() => Client.EnableRemoteWipe(p1, p2, p3));   // the slow key derivation stays off the UI thread
			Clear();
			// The revocation key is now known: contacts and group peers pin it, which is what lets them check a later migration notice.
			_ = Client.NotifyContactsAsync();
			_ = Client.AnnounceRevocationKeyAsync();
			Result = Loc.T("wipe_activated");
			Ready = Client.RemoteWipeReady;
		}
		catch (ClientException ex) { Result = ErrorText.Of(ex); }
		finally { Busy = false; }
	}

	[RelayCommand]
	private async Task SendAsync()
	{
		if (Busy) return;
		if (!await Ui.ConfirmAsync(Loc.T("wipe_title"), Loc.T("wipe_confirm"), Loc.T("wipe_confirm_yes"), Loc.T("cancel"))) return;
		Busy = true;
		Result = "";
		try
		{
			var (p1, p2, p3) = (Phrase1.Trim(), Phrase2.Trim(), Phrase3.Trim());
			var reached = await Task.Run(() => Client.SendWipeAsync(p1, p2, p3));
			Clear();
			Result = Loc.T(reached ? "wipe_sent" : "wipe_sent_later");
			Ready = Client.RemoteWipeReady;
		}
		catch (ClientException ex) { Result = ErrorText.Of(ex); }
		finally { Busy = false; }
	}
}
