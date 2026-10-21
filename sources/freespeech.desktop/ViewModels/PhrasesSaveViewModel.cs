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
using FreeSpeech.Client.Identity;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>
/// Step 2 of a new identity, the only moment the three phrases can be taken out of the app: shown on screen, printed (with a QR code the
/// app reads back), or saved in a file protected by a password. They are held in memory for this screen only and never stored, so a
/// stranger at the computer cannot display them later. The identity itself is created when the user leaves this screen.
/// </summary>
public sealed partial class PhrasesSaveViewModel(string[] phrases) : ObservableObject
{
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowLabel))] private bool _shown;
	[ObservableProperty] private string _phrasesText = "";
	[ObservableProperty] private bool _fileFormOpen;
	[ObservableProperty] private string _password = "";
	[ObservableProperty] private string _passwordAgain = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(PasswordChar))] private bool _showPassword;
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private bool _busy;

	/// <summary>Something was done with them: printed, saved in a file, copied, or ticked as noted.</summary>
	public bool Done { get; private set; }

	/// <summary>Set by the view: puts text on the clipboard.</summary>
	public Func<string, Task>? WriteClipboard { get; set; }

	/// <summary>What leaving this screen does: creates the identity (first connection) or sends the migration notice. Given the screen, to know whether anything was done.</summary>
	public Func<PhrasesSaveViewModel, Task>? ContinueAction { get; set; }

	[RelayCommand]
	private async Task ContinueAsync()
	{
		if (ContinueAction != null) await ContinueAction(this);
	}

	/// <summary>The status line, for the action to report a failure.</summary>
	public void Report(string text) => Status = text;

	public char PasswordChar => ShowPassword ? '\0' : '●';
	public string ShowLabel => Loc.T(Shown ? "ph_hide" : "ph_show");
	public string WarningText => Loc.T("ph_warning");
	public string CopyLabel => Loc.T("phrases_copy");
	public string PrintTitle => Loc.T("ph_print");
	public string PrintHint => Loc.T("ph_print_hint");
	public string FileTitle => Loc.T("ph_file");
	public string FileHint => Loc.T("ph_file_hint");
	public string PasswordLabel => Loc.T("ph_password");
	public string PasswordAgainLabel => Loc.T("ph_password2");
	public string ShowPasswordLabel => Loc.T("ph_show_password");
	public string FileWarning => Loc.T("ph_file_warn");
	public string MakeFileLabel => Loc.T("ph_file_make");
	public string NotedLabel => Loc.T("phrases_saved");
	public string ContinueLabel => Loc.T("ph_continue");

	/// <summary>Shown without numbering: a selection pasted into "Restore my identity" must be the phrases and nothing else.</summary>
	[RelayCommand]
	private void ToggleShow()
	{
		Shown = !Shown;
		PhrasesText = Shown ? string.Join("\n\n", phrases) : "";
	}

	[RelayCommand]
	private async Task PrintAsync()
	{
		Status = "";
		if (await PhraseSheetPaper.PrintAsync(phrases)) MarkDone(Loc.T("ph_printed"));
		else Status = Loc.T("ph_print_failed");
	}

	/// <summary>One phrase per line: the format the "Paste my three phrases" button of the restore screen reads back.</summary>
	[RelayCommand]
	private async Task CopyAsync()
	{
		if (WriteClipboard == null) return;
		await WriteClipboard(string.Join("\n", phrases));
		MarkDone(Loc.T("phrases_copied"));
	}

	[RelayCommand] private void ToggleFile() => FileFormOpen = !FileFormOpen;

	[RelayCommand]
	private async Task CreateFileAsync()
	{
		if (Password != PasswordAgain) { Status = Loc.T("ph_pw_mismatch"); return; }
		Busy = true;
		try
		{
			var password = Password;
			var bytes = await Task.Run(() => PhraseVault.Seal(phrases, password));
			if (await Ui.SaveFileAsync(Loc.T("ph_file"), $"freespeech-key-{DateTime.Now:yyyyMMdd}.fskey", "fskey") is not { } path) return;
			await File.WriteAllBytesAsync(path, bytes);
			Password = PasswordAgain = "";
			FileFormOpen = false;
			MarkDone(Loc.T("ph_file_made"));
		}
		catch (Exception ex) when (ex is ClientException or IOException or UnauthorizedAccessException) { Status = ErrorText.Of(ex); }
		finally { Busy = false; }
	}

	[RelayCommand] private void Noted() => MarkDone(Loc.T("phrases_noted"));

	private void MarkDone(string message)
	{
		Done = true;
		Status = message;
	}
}
