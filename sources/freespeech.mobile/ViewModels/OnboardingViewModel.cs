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
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>
/// First start. Two plain choices — "First connection" (the app draws the recovery phrases; the user only gives a name) and
/// "Restore my identity" (the three phrases, plus optionally a backup file) — or, when the stored key was lost, just the phrases.
/// </summary>
public partial class OnboardingViewModel(ClientService service) : ObservableObject
{
	public enum Step { Choose, First, Restore, Unlock }

	[ObservableProperty] private string _phrase1 = "";
	[ObservableProperty] private string _phrase2 = "";
	[ObservableProperty] private string _phrase3 = "";
	[ObservableProperty] private string _nickname = "";
	[ObservableProperty] private string _serverUrl = service.ServerUrl;
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _isBusy;

	/// <summary>"Advanced" only shows the server field.</summary>
	[ObservableProperty] private bool _showAdvanced;

	[ObservableProperty] private Step _current = Step.Choose;

	/// <summary>A backup file picked while restoring: merged once the identity (and so the backup's key) exists.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(RestoreLabel), nameof(HasRestoreFile))] private string? _restorePath;

	public bool IsChoose => Current == Step.Choose;
	public bool IsFirst => Current == Step.First;
	public bool IsRestore => Current == Step.Restore;
	public bool IsUnlock => Current == Step.Unlock;
	public bool ShowPhraseFields => IsRestore || IsUnlock;
	public bool ShowAdvancedToggle => IsFirst || IsRestore;
	public bool ShowServer => ShowAdvancedToggle && ShowAdvanced;
	public bool HasRestoreFile => RestorePath != null;
	public string RestoreLabel => RestorePath == null ? "" : Loc.T("onb_restore_chosen", Path.GetFileName(RestorePath));
	public string AdvancedText => Loc.T(ShowAdvanced ? "onb_simple" : "onb_advanced");

	public string Title => Loc.T(Current switch { Step.Choose => "onb_choose_title", Step.First => "onb_create_title", Step.Restore => "onb_restore_title", _ => "onb_unlock_title" });
	public string Explanation => Loc.T(Current switch { Step.Choose => "onb_choose_text", Step.First => "onb_create_simple_text", Step.Restore => "onb_restore_text", _ => "onb_unlock_text" });
	public string SubmitText => Loc.T(Current switch { Step.Restore => "onb_submit_restore", Step.Unlock => "onb_submit_unlock", _ => "onb_submit_create" });

	partial void OnCurrentChanged(Step value) => RefreshTexts();
	partial void OnShowAdvancedChanged(bool value) => RefreshTexts();

	private void RefreshTexts()
	{
		foreach (var name in new[] { nameof(Title), nameof(Explanation), nameof(SubmitText), nameof(IsChoose), nameof(IsFirst), nameof(IsRestore), nameof(IsUnlock),
			nameof(ShowPhraseFields), nameof(ShowAdvancedToggle), nameof(ShowServer), nameof(AdvancedText) })
			OnPropertyChanged(name);
	}

	/// <summary>A protected key file picked while restoring: asks for its password, then fills the three phrases.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasKeyFile), nameof(KeyFileLabel))] private string? _keyFilePath;
	[ObservableProperty] private string _keyPassword = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HideKeyPassword))] private bool _showKeyPassword;
	public bool HideKeyPassword => !ShowKeyPassword;
	public bool HasKeyFile => KeyFilePath != null;
	public string KeyFileLabel => KeyFilePath == null ? "" : Loc.T("onb_key_chosen", Path.GetFileName(KeyFilePath));

	[RelayCommand]
	private async Task PickKeyFileAsync()
	{
		try
		{
			var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("onb_row_keyfile") });
			if (file == null) return;
			if (!PhraseVault.LooksLikeVault(await File.ReadAllBytesAsync(file.FullPath))) { Error = Loc.T("err_vault_bad_file"); return; }
			KeyFilePath = file.FullPath;
			KeyPassword = "";
			Error = "";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
	}

	[RelayCommand]
	private async Task OpenKeyFileAsync()
	{
		if (KeyFilePath is not { } path) return;
		IsBusy = true;
		try
		{
			var (bytes, password) = (await File.ReadAllBytesAsync(path), KeyPassword);
			var p = await Task.Run(() => PhraseVault.Open(bytes, password));
			(Phrase1, Phrase2, Phrase3) = (p[0], p[1], p[2]);
			KeyFilePath = null;
			KeyPassword = "";
			Error = "";
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Error = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}

	[RelayCommand] private Task ScanSheetAsync() => Shell.Current.GoToAsync("scan");

	public void Prepare()
	{
		if (ScanHub.Result is { } scanned && (IsRestore || IsUnlock))   // back from the scanner: the printed sheet fills the three phrases
		{
			ScanHub.Result = null;
			if (PhraseSheet.TryDecode(scanned) is { } sheet) { (Phrase1, Phrase2, Phrase3) = (sheet[0], sheet[1], sheet[2]); Error = ""; }
			else Error = Loc.T("onb_err_sheet");
		}

		if (service.State == AppState.NeedsPhrases) Current = Step.Unlock;
		else if (Current == Step.Unlock) Current = Step.Choose;
	}

	[RelayCommand] private void StartFirst() { Error = ""; Current = Step.First; }
	[RelayCommand] private void StartRestore() { Error = ""; Current = Step.Restore; }
	[RelayCommand] private void Back() { Error = ""; ShowAdvanced = false; RestorePath = null; KeyFilePath = null; Current = Step.Choose; }
	[RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

	[RelayCommand]
	private async Task PickBackupAsync()
	{
		try
		{
			var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("restore_pick") });
			if (file != null) { RestorePath = file.FullPath; Error = ""; }
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
	}

	/// <summary>Three phrases copied from a note or a password manager, one per line, fill the three fields at once.</summary>
	[RelayCommand]
	private async Task PastePhrasesAsync()
	{
		var lines = (await Clipboard.Default.GetTextAsync() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length != 3) { Error = Loc.T("onb_err_paste"); return; }
		(Phrase1, Phrase2, Phrase3) = (lines[0], lines[1], lines[2]);
		Error = "";
	}

	[RelayCommand]
	private async Task SubmitAsync()
	{
		Error = "";
		if (IsFirst)
		{
			try
			{
				var generator = await BookLoader.LoadAsync(Loc.Instance.Language);
				var p = generator.Generate();
				(Phrase1, Phrase2, Phrase3) = (p[0], p[1], p[2]);
			}
			catch (Exception ex) when (ex is IOException or ArgumentException) { Error = Loc.T("onb_err_book"); return; }
		}

		foreach (var p in new[] { Phrase1, Phrase2, Phrase3 })
		{
			if (!FreeSpeechClient.IsPhraseValid(p)) { Error = Loc.T("onb_err_phrase"); return; }
		}
		if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{ Error = Loc.T("onb_err_server"); return; }

		// A new identity: the phrases were just drawn. They go to step 2 (saving them) in memory only, and the identity is created
		// when that step is left, so quitting in the middle creates nothing.
		if (IsFirst)
		{
			NewIdentityDraft.Set([Phrase1, Phrase2, Phrase3], string.IsNullOrWhiteSpace(Nickname) ? Loc.T("onb_default_nick") : Nickname.Trim(), ServerUrl.Trim());
			Phrase1 = Phrase2 = Phrase3 = "";
			KeyboardHelper.Hide();
			await Shell.Current.GoToAsync("phrases");
			return;
		}

		IsBusy = true;
		try
		{
			// A backup that these phrases cannot open means a typo in a phrase (or the wrong file): say so BEFORE creating
			// anything, otherwise a different, empty identity would be created.
			if (IsRestore && RestorePath is { } file)
			{
				var (a, b, c) = (Phrase1, Phrase2, Phrase3);
				if (!await Task.Run(() => FreeSpeechClient.BackupMatchesPhrases(file, a, b, c))) { Error = Loc.T("onb_err_backup_mismatch"); return; }
			}

			var nick = string.IsNullOrWhiteSpace(Nickname) ? Loc.T("onb_default_nick") : Nickname.Trim();
			await service.CreateOrUnlockAsync(Phrase1, Phrase2, Phrase3, nick, ServerUrl);
			Phrase1 = Phrase2 = Phrase3 = "";   // the phrases leave memory as soon as the key is derived

			string? restoreProblem = null;
			var restored = false;
			if (IsRestore && RestorePath is { } backup)
			{
				try
				{
					await Task.Run(() => service.Client.RestoreBackup(backup, restoreProfile: true));
					restored = true;
					service.NotifyConversationsChanged();
				}
				catch (Exception ex) when (ex is ClientException or IOException) { restoreProblem = Loc.T("onb_restore_failed", ErrorText.Of(ex)); }
				RestorePath = null;
			}

			await Shell.Current.GoToAsync(service.FirstScreen());
			if (restoreProblem != null) await Dialogs.AlertAsync(Loc.T("onb_restore_title"), restoreProblem, Loc.T("ok"));
			else if (restored) _ = service.OfferDeviceSyncAsync();   // the identity is restored from a file: another device of it, if one is connected, can complete it
		}
		catch (Exception ex) when (ex is ArgumentException or ClientException)
		{
			Error = ErrorText.Of(ex);
		}
		finally
		{
			IsBusy = false;
		}
	}
}