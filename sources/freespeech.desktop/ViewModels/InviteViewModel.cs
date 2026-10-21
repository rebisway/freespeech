/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>
/// Exchanging contact details: mine to give out (a code to scan, a message, a copy) and someone else's to receive (a picture of the
/// code, paste). The details travel as a card. Each one is valid a month and works once, and one is made fresh every time I share or
/// copy, so several people can be given theirs before the first answers and nobody has to think about it.
/// </summary>
public sealed partial class InviteViewModel(MainViewModel owner) : ObservableObject, IDisposable
{
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasQr))] private Bitmap? _qrImage;
	[ObservableProperty] private string _status = "";

	/// <summary>Set by the view: puts text on the clipboard, reads the clipboard.</summary>
	public Func<string, Task>? WriteClipboard { get; set; }
	public Func<Task<string?>>? ReadClipboard { get; set; }

	public bool HasQr => QrImage != null;

	public string SendTitle => Loc.T("invite_send_title");
	public string SendHint => Loc.T("invite_send_hint");
	public string QrTitle => Loc.T("invite_row_qr");
	public string Together => Loc.T("invite_together");
	public string ShareTitle => Loc.T("invite_row_share");
	public string Apart => Loc.T("invite_apart");
	public string CopyTitle => Loc.T("invite_row_copy");
	public string CopyHint => Loc.T("invite_row_copy_hint");
	public string ReceiveTitle => Loc.T("invite_recv_title");
	public string ReceiveHint => Loc.T("invite_recv_hint");
	public string ScanTitle => Loc.T("invite_row_scan");
	public string PasteTitle => Loc.T("invite_row_paste");
	public string PasteHint => Loc.T("invite_row_paste_hint");

	private FreeSpeechClient Client => owner.Session.Client;

	public void Dispose() => QrImage?.Dispose();

	// Face to face: the QR card is valid 10 minutes and carries no picture (it would not fit), the picture follows with the next profile update.
	[RelayCommand]
	private void ToggleQr()
	{
		if (QrImage != null) { QrImage = null; return; }
		QrImage = new Bitmap(new MemoryStream(PhraseSheetPaper.RenderQr(Client.ExportCard("qr", includePhoto: false))));
	}

	/// <summary>At a distance: the default mail program opens with the card in a new message.</summary>
	[RelayCommand]
	private void Share()
	{
		var uri = $"mailto:?subject={Uri.EscapeDataString(Loc.T("invite_share_title"))}&body={Uri.EscapeDataString(Client.ExportCard("manual"))}";
		try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
		catch (System.ComponentModel.Win32Exception) { Status = Loc.T("ph_print_failed"); }
	}

	[RelayCommand]
	private async Task CopyAsync()
	{
		if (WriteClipboard == null) return;
		await WriteClipboard(Client.ExportCard("manual"));
		Status = Loc.T("invite_copied");
	}

	/// <summary>The code of someone near me: on a computer, its picture (a photo or a screenshot) stands for the camera.</summary>
	[RelayCommand]
	private async Task ScanAsync()
	{
		if (await Ui.PickFileAsync(Loc.T("invite_row_scan"), "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif") is not { } path) return;
		if (await Task.Run(() => PhraseSheetPaper.ReadQr(path)) is { } card) await ImportAsync(card);
		else Status = Loc.T("onb_err_sheet");
	}

	/// <summary>The details someone sent me are on the clipboard: one click adds them, no box to fill in.</summary>
	[RelayCommand]
	private async Task PasteAsync()
	{
		var text = ((ReadClipboard != null ? await ReadClipboard() : null) ?? "").Trim();
		if (text.Length == 0) { Status = Loc.T("invite_paste_empty"); return; }
		await ImportAsync(text);
	}

	private async Task ImportAsync(string card)
	{
		try
		{
			var nick = await Client.ImportCardAsync(card.Trim());
			Status = Loc.T("invite_sent", nick);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}
}