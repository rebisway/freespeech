/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Client.Identity;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>
/// Step 2 of a new identity, the only moment the three phrases can be taken out of the app: shown on screen, printed (with a QR code the
/// app reads back), or saved in a file protected by a password. They are held in memory for this screen only and never stored, so a
/// stranger holding the phone cannot display them later. The identity itself is created when the user leaves this screen.
/// </summary>
public partial class PhrasesBackupViewModel(ClientService service) : ObservableObject
{
	private string[]? _phrases;
	private bool _done;   // something was done with them: printed, saved in a file, or ticked as noted

	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowLabel))] private bool _shown;
	[ObservableProperty] private string _phrasesText = "";
	[ObservableProperty] private bool _fileFormOpen;
	[ObservableProperty] private string _password = "";
	[ObservableProperty] private string _passwordAgain = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HidePassword))] private bool _showPassword;
	public bool HidePassword => !ShowPassword;
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private bool _isBusy;

	public string ShowLabel => Loc.T(Shown ? "ph_hide" : "ph_show");

	/// <summary>False when the draft is gone (the app was restarted on this screen): the caller goes back to the start.</summary>
	public bool Load()
	{
		_phrases = NewIdentityDraft.Phrases;
		return _phrases != null;
	}

	[RelayCommand]
	private void ToggleShow()
	{
		Shown = !Shown;   // shown without numbering: a selection pasted into "Restore my identity" must be the phrases and nothing else
		PhrasesText = Shown && _phrases != null ? string.Join("\n\n", _phrases) : "";
	}

	[RelayCommand]
	private async Task PrintAsync()
	{
		if (_phrases == null || ClientService.Printer is not { } printer) { Status = Loc.T("ph_print_failed"); return; }
		var qr = Convert.ToBase64String(ScanHub.Render(PhraseSheet.Encode(_phrases)));
		var items = string.Concat(_phrases.Select((p, i) => $"<p><b>{i + 1}.</b> {WebUtility.HtmlEncode(p)}</p>"));
		var html = $"<html><body style=\"font-family:sans-serif;margin:32px\"><h2>{WebUtility.HtmlEncode(Loc.T("ph_print_title"))}</h2>{items}"
			+ $"<p style=\"margin-top:28px\"><img src=\"data:image/png;base64,{qr}\" width=\"220\" height=\"220\"/></p>"
			+ $"<p style=\"color:#a33;margin-top:20px\"><b>{WebUtility.HtmlEncode(Loc.T("ph_print_warn"))}</b></p></body></html>";
		try
		{
			await printer.PrintAsync("FreeSpeech", html);
			Done(Loc.T("ph_printed"));
		}
		catch (Exception) { Status = Loc.T("ph_print_failed"); }
	}

	/// <summary>One phrase per line: the format the "Paste my three phrases" button of the restore screen reads back.</summary>
	[RelayCommand]
	private async Task CopyAsync()
	{
		if (_phrases == null) return;
		await Clipboard.Default.SetTextAsync(string.Join("\n", _phrases));
		Done(Loc.T("phrases_copied"));
	}

	[RelayCommand] private void ToggleFile() => FileFormOpen = !FileFormOpen;

	[RelayCommand]
	private async Task CreateFileAsync()
	{
		if (_phrases == null) return;
		if (Password != PasswordAgain) { Status = Loc.T("ph_pw_mismatch"); return; }
		IsBusy = true;
		try
		{
			var (phrases, password) = (_phrases, Password);
			var bytes = await Task.Run(() => PhraseVault.Seal(phrases, password));
			var path = Path.Combine(FileSystem.CacheDirectory, $"freespeech-key-{DateTime.Now:yyyyMMdd}.fskey");
			await File.WriteAllBytesAsync(path, bytes);
			Password = PasswordAgain = "";
			FileFormOpen = false;
			Done(Loc.T("ph_file_made"));
			await Share.Default.RequestAsync(new ShareFileRequest { Title = Loc.T("ph_file"), File = new ShareFile(path) });
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}

	[RelayCommand] private void Noted() => Done(Loc.T("phrases_noted"));

	private void Done(string message)
	{
		_done = true;
		Status = message;
	}

	/// <summary>Leaving: the identity is created now. Going on with nothing done is allowed, but only knowingly.</summary>
	[RelayCommand]
	private async Task ContinueAsync()
	{
		if (_phrases == null) return;
		if (!_done && !await Dialogs.AlertAsync(Loc.T("ph_skip_title"), Loc.T("ph_skip_text"), Loc.T("ph_skip_yes"), Loc.T("ph_skip_no"))) return;

		IsBusy = true;
		try
		{
			var p = _phrases;
			if (MigrationDraft.Active)
			{
				// A migration: the identity is not created here, the notice signed with the OLD phrases is sent; the app switches later.
				var old = MigrationDraft.OldPhrases!;
				var nick = NewIdentityDraft.Nickname;
				await Task.Run(() => service.Client.SendMovedAsync(old, p, nick));   // the slow key derivations stay off the UI thread
				MigrationDraft.Clear();
				NewIdentityDraft.Clear();
				_phrases = null;
				Password = PasswordAgain = PhrasesText = "";
				await Shell.Current.GoToAsync("//conversations");
				await Shell.Current.GoToAsync("migrate");
				return;
			}
			await service.CreateOrUnlockAsync(p[0], p[1], p[2], NewIdentityDraft.Nickname, NewIdentityDraft.ServerUrl);
			NewIdentityDraft.Clear();
			_phrases = null;
			Password = PasswordAgain = PhrasesText = "";
			await Shell.Current.GoToAsync(service.FirstScreen());
		}
		catch (Exception ex) when (ex is ArgumentException or ClientException) { Status = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}
}