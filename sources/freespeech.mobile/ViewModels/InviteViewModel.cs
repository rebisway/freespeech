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
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>
/// Exchanging contact details: mine to give out (a code to scan, a message, a copy) and someone else's to receive (scan, paste).
/// The details travel as a card. Each one is valid a month and works once, and one is made fresh every time I share or copy, so
/// several people can be given theirs before the first answers and nobody has to think about it.
/// </summary>
public partial class InviteViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasQr))] private ImageSource? _qrImage;

	public bool HasQr => QrImage != null;

	public InviteViewModel(ClientService service) => _service = service;

	private FreeSpeechClient Client => _service.Client;

	public void Refresh()
	{
		if (ScanHub.Result is { } scanned)   // back from the scanner: add the contact straight away
		{
			ScanHub.Result = null;
			_ = ImportAsync(scanned);
		}
	}

	// Face to face: the QR card is valid 10 minutes and carries no picture (it would not fit), the picture follows with the next profile update.
	[RelayCommand] private void ToggleQr()
	{
		if (QrImage != null) { QrImage = null; return; }
		var png = ScanHub.Render(Client.ExportCard("qr", includePhoto: false));
		QrImage = ImageSource.FromStream(() => new MemoryStream(png));
	}

	[RelayCommand] private Task ScanAsync() => Shell.Current.GoToAsync("scan");

	[RelayCommand] private async Task CopyAsync()
	{
		await Clipboard.Default.SetTextAsync(Client.ExportCard("manual"));
		Status = Loc.T("invite_copied");
	}

	[RelayCommand] private Task ShareAsync() =>
		Share.Default.RequestAsync(new ShareTextRequest { Text = Client.ExportCard("manual"), Title = Loc.T("invite_share_title") });

	/// <summary>The details someone sent me are on the clipboard: one tap adds them, no box to fill in.</summary>
	[RelayCommand]
	private async Task PasteAsync()
	{
		var text = (await Clipboard.Default.GetTextAsync() ?? "").Trim();
		if (text.Length == 0) { Status = Loc.T("invite_paste_empty"); return; }
		await ImportAsync(text);
	}

	private async Task ImportAsync(string card)
	{
		try
		{
			var nick = await Client.ImportCardAsync(card.Trim());
			Status = "";
			// Done: back to the chat list, where a banner says the request left.
			await Shell.Current.GoToAsync("//conversations");
			_service.Announce(nick, Loc.T("invite_sent_short"));
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}
}
