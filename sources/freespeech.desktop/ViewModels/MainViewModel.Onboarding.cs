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
/// The first-start steps, the same as the mobile app: two plain choices (First connection: the app draws the recovery phrases and the
/// user only gives a name; Restore my identity: the three phrases typed, pasted, read from a protected key file or from a picture of the
/// printed sheet, plus optionally a backup file), a screen to save the drawn phrases, and, when the stored key was lost, just the phrases.
/// </summary>
public enum OnboardingStep { Choose, First, Restore, Unlock, Save }

public sealed partial class MainViewModel
{
	[ObservableProperty, NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsFirst), nameof(IsRestore), nameof(IsUnlock), nameof(IsSave), nameof(ShowPhraseFields), nameof(ShowAdvancedToggle),
		nameof(ShowServer), nameof(ShowSubmit), nameof(OnboardingTitle), nameof(OnboardingText), nameof(StartLabel), nameof(AdvancedText))]
	private OnboardingStep _step = OnboardingStep.Choose;

	/// <summary>"Advanced" only shows the server field.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowServer), nameof(AdvancedText))] private bool _showAdvanced;

	/// <summary>A backup file picked while restoring: merged once the identity (and so the backup's key) exists.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasRestoreFile), nameof(RestoreLabel))] private string? _restorePath;

	/// <summary>A protected key file picked while restoring: asks for its password, then fills the three phrases.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasKeyFile), nameof(KeyFileLabel))] private string? _keyFilePath;
	[ObservableProperty] private string _keyPassword = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(KeyPasswordChar))] private bool _showKeyPassword;

	/// <summary>Set by the view: puts text on the clipboard, reads the clipboard.</summary>
	public Func<string, Task>? WriteClipboard { get; set; }
	public Func<Task<string?>>? ReadClipboard { get; set; }

	public bool IsChoose => Step == OnboardingStep.Choose;
	public bool IsFirst => Step == OnboardingStep.First;
	public bool IsRestore => Step == OnboardingStep.Restore;
	public bool IsUnlock => Step == OnboardingStep.Unlock;
	public bool IsSave => Step == OnboardingStep.Save;
	public bool ShowPhraseFields => IsRestore || IsUnlock;
	public bool ShowAdvancedToggle => IsFirst || IsRestore;
	public bool ShowServer => ShowAdvancedToggle && ShowAdvanced;
	public bool ShowSubmit => Step is OnboardingStep.First or OnboardingStep.Restore or OnboardingStep.Unlock;
	public bool HasRestoreFile => RestorePath != null;
	public bool HasKeyFile => KeyFilePath != null;
	public char KeyPasswordChar => ShowKeyPassword ? '\0' : '●';
	public string RestoreLabel => RestorePath == null ? "" : Loc.T("onb_restore_chosen", Path.GetFileName(RestorePath));
	public string KeyFileLabel => KeyFilePath == null ? "" : Loc.T("onb_key_chosen", Path.GetFileName(KeyFilePath));
	public string AdvancedText => Loc.T(ShowAdvanced ? "onb_simple" : "onb_advanced");

	public string OnboardingTitle => Loc.T(Step switch { OnboardingStep.Choose => "onb_choose_title", OnboardingStep.First => "onb_create_title", OnboardingStep.Restore => "onb_restore_title", OnboardingStep.Save => "ph_title", _ => "onb_unlock_title" });
	public string OnboardingText => Loc.T(Step switch { OnboardingStep.Choose => "onb_choose_text", OnboardingStep.First => "onb_create_simple_text", OnboardingStep.Restore => "onb_restore_text", OnboardingStep.Save => "ph_text", _ => "onb_unlock_text" });
	public string StartLabel => Loc.T(Step switch { OnboardingStep.Restore => "onb_submit_restore", OnboardingStep.Unlock => "onb_submit_unlock", _ => "onb_submit_create" });

	public string FirstRowTitle => Loc.T("onb_row_first");
	public string FirstRowHint => Loc.T("onb_row_first_hint");
	public string RestoreRowTitle => Loc.T("onb_row_restore");
	public string RestoreRowHint => Loc.T("onb_row_restore_hint");
	public string KeyFileRowTitle => Loc.T("onb_row_keyfile");
	public string KeyFileRowHint => Loc.T("onb_row_keyfile_hint");
	public string SheetRowTitle => Loc.T("onb_row_sheet");
	public string SheetRowHint => Loc.T("onb_row_sheet_hint");
	public string BackupRowTitle => Loc.T("onb_row_backup");
	public string BackupRowHint => Loc.T("onb_row_backup_hint");
	public string BackLabel => Loc.T("onb_back");
	public string PasteThreeLabel => Loc.T("onb_paste");
	public string Phrase1Label => Loc.T("onb_phrase1");
	public string Phrase2Label => Loc.T("onb_phrase2");
	public string Phrase3Label => Loc.T("onb_phrase3");
	public string NicknameLabel => Loc.T("onb_nickname");
	public string ServerLabel => Loc.T("onb_server");
	public string KeyPasswordLabel => Loc.T("onb_key_password");
	public string ShowPasswordLabel => Loc.T("ph_show_password");
	public string KeyOpenLabel => Loc.T("onb_key_open");

	partial void OnStepChanged(OnboardingStep value)
	{
		if (value == OnboardingStep.Save) PhrasesSave = new PhrasesSaveViewModel(NewIdentityDraft.Phrases ?? ["", "", ""]) { ContinueAction = ContinueFromSaveAsync };
	}

	[RelayCommand] private void StartFirst() { Error = ""; Step = OnboardingStep.First; }

	[RelayCommand] private void StartRestore() { Error = ""; Step = OnboardingStep.Restore; }

	[RelayCommand]
	private void BackToChoice()
	{
		Error = "";
		ShowAdvanced = false;
		RestorePath = null;
		KeyFilePath = null;
		Step = OnboardingStep.Choose;
	}

	[RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

	[RelayCommand]
	private async Task PickBackupAsync()
	{
		if (await Ui.PickFileAsync(Loc.T("restore_pick")) is { } path) { RestorePath = path; Error = ""; }
	}

	[RelayCommand]
	private async Task PickKeyFileAsync()
	{
		try
		{
			if (await Ui.PickFileAsync(Loc.T("onb_row_keyfile"), "*.fskey") is not { } path) return;
			if (!PhraseVault.LooksLikeVault(await File.ReadAllBytesAsync(path))) { Error = Loc.T("err_vault_bad_file"); return; }
			KeyFilePath = path;
			KeyPassword = "";
			Error = "";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
	}

	[RelayCommand]
	private async Task OpenKeyFileAsync()
	{
		if (KeyFilePath is not { } path) return;
		Busy = true;
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
		finally { Busy = false; }
	}

	/// <summary>The printed sheet: on a computer, its picture (a photo or a scan) stands for the camera.</summary>
	[RelayCommand]
	private async Task ScanSheetAsync()
	{
		if (await Ui.PickFileAsync(Loc.T("onb_row_sheet"), "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif") is not { } path) return;
		if (await Task.Run(() => PhraseSheetPaper.ReadPicture(path)) is { } sheet) { (Phrase1, Phrase2, Phrase3) = (sheet[0], sheet[1], sheet[2]); Error = ""; }
		else Error = Loc.T("onb_err_sheet");
	}

	/// <summary>Three phrases copied from a note or a password manager, one per line, fill the three fields at once.</summary>
	[RelayCommand]
	private async Task PastePhrasesAsync()
	{
		var lines = ((ReadClipboard != null ? await ReadClipboard() : null) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length != 3) { Error = Loc.T("onb_err_paste"); return; }
		(Phrase1, Phrase2, Phrase3) = (lines[0], lines[1], lines[2]);
		Error = "";
	}

	/// <summary>The main button: draw the phrases (first connection), or open an identity from the typed phrases (restore, unlock).</summary>
	[RelayCommand]
	private async Task SubmitAsync()
	{
		Error = "";
		if (IsFirst)
		{
			try
			{
				var generator = await BookLoader.LoadAsync(Loc.Language);
				var p = generator.Generate();
				(Phrase1, Phrase2, Phrase3) = (p[0], p[1], p[2]);
			}
			catch (Exception ex) when (ex is IOException or ArgumentException) { Error = Loc.T("onb_err_book"); return; }
		}

		foreach (var p in new[] { Phrase1, Phrase2, Phrase3 })
			if (!FreeSpeechClient.IsPhraseValid(p.Trim())) { Error = Loc.T("onb_err_phrase"); return; }
		if (!Uri.TryCreate(Server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { Error = Loc.T("onb_err_server"); return; }

		var nick = string.IsNullOrWhiteSpace(Nickname) ? Loc.T("onb_default_nick") : Nickname.Trim();
		// A new identity: the phrases were just drawn. They go to the screen that saves them, in memory only, and the identity is created
		// when that screen is left, so quitting in the middle creates nothing.
		if (IsFirst)
		{
			NewIdentityDraft.Set([Phrase1, Phrase2, Phrase3], nick, Server.Trim());
			Phrase1 = Phrase2 = Phrase3 = "";
			Step = OnboardingStep.Save;
			return;
		}

		Busy = true;
		try
		{
			// A backup that these phrases cannot open means a typo in a phrase (or the wrong file): say so BEFORE creating anything,
			// otherwise a different, empty identity would be created.
			if (IsRestore && RestorePath is { } file)
			{
				var (a, b, c) = (Phrase1.Trim(), Phrase2.Trim(), Phrase3.Trim());
				if (!await Task.Run(() => FreeSpeechClient.BackupMatchesPhrases(file, a, b, c))) { Error = Loc.T("onb_err_backup_mismatch"); return; }
			}

			await _session.CreateOrUnlockAsync(Phrase1.Trim(), Phrase2.Trim(), Phrase3.Trim(), nick, Server.Trim());
			Phrase1 = Phrase2 = Phrase3 = "";   // the phrases leave memory as soon as the key is derived

			string? restoreProblem = null;
			var restoreWithBackup = IsRestore && RestorePath != null;
			if (IsRestore && RestorePath is { } backup)
			{
				try { await Task.Run(() => _session.Client.RestoreBackup(backup, restoreProfile: true)); }
				catch (Exception ex) when (ex is ClientException or IOException) { restoreProblem = Loc.T("onb_restore_failed", ErrorText.Of(ex)); }
				RestorePath = null;
			}

			State = _session.State;
			Refresh();
			if (restoreProblem != null) await Ui.AlertAsync(Loc.T("onb_restore_title"), restoreProblem, Loc.T("ok"));
			else if (restoreWithBackup) _ = OfferDeviceSyncAsync();   // another device of this identity, if one is connected, completes what the file lacks
		}
		catch (Exception ex) when (ex is ArgumentException or ClientException) { Error = ErrorText.Of(ex); }
		finally { Busy = false; }
	}

	/// <summary>Leaving the save screen: the identity is created now. Going on with nothing done is allowed, but only knowingly.</summary>
	public async Task ContinueFromSaveAsync(PhrasesSaveViewModel save)
	{
		if (NewIdentityDraft.Phrases is not { } p) return;
		if (!save.Done && !await Ui.ConfirmAsync(Loc.T("ph_skip_title"), Loc.T("ph_skip_text"), Loc.T("ph_skip_yes"), Loc.T("ph_skip_no"))) return;

		Busy = true;
		try
		{
			await _session.CreateOrUnlockAsync(p[0], p[1], p[2], NewIdentityDraft.Nickname, NewIdentityDraft.ServerUrl);
			NewIdentityDraft.Clear();
			PhrasesSave = null;
			State = _session.State;
			Refresh();
		}
		catch (Exception ex) when (ex is ArgumentException or ClientException) { save.Status = ErrorText.Of(ex); }
		finally { Busy = false; }
	}
}

/// <summary>
/// A migration under way (PROTOCOL section 12): the OLD phrases the user typed to sign the notice, held in memory until the notice is sent.
/// The new phrases travel in <see cref="NewIdentityDraft"/> through the save screen. Nothing here is ever stored.
/// </summary>
public static class MigrationDraft
{
	public static string[]? OldPhrases { get; private set; }

	public static bool Active => OldPhrases != null;

	public static void Set(string[] oldPhrases) => OldPhrases = oldPhrases;

	public static void Clear() => OldPhrases = null;
}

/// <summary>A new identity that does not exist yet: the three phrases drawn and the first name, held in memory only while the user saves the phrases.</summary>
public static class NewIdentityDraft
{
	public static string[]? Phrases { get; private set; }
	public static string Nickname { get; private set; } = "";
	public static string ServerUrl { get; private set; } = "";

	public static void Set(string[] phrases, string nickname, string serverUrl) => (Phrases, Nickname, ServerUrl) = (phrases, nickname, serverUrl);

	public static void Clear() => (Phrases, Nickname, ServerUrl) = (null, "", "");
}
