/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Client.Identity;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;
using FreeSpeech.Mobile.Views;

namespace FreeSpeech.Mobile.ViewModels;

public sealed record ServerRow(string Url, string Status, bool CanRequestAccess = false);
public sealed record ThemeOption(string Code, string Name);
public sealed record LockOption(int Value, string Name);
public sealed record TransferOption(FreeSpeech.Client.Attachments.FileTransferMode Mode, string Name);

/// <summary>My profile (picture, name, about), the language, and my servers.</summary>
public partial class SettingsViewModel : ObservableObject
{
	private readonly ClientService _service;
	private bool _loading;
	private string _savedNickname = "", _savedAbout = "";

	/// <summary>The name or the description was changed and not saved yet.</summary>
	public bool HasUnsavedChanges => Nickname.Trim() != _savedNickname || About.Trim() != _savedAbout;

	/// <summary>Leaving without saving: the fields go back to what is saved.</summary>
	public void DiscardChanges() => (Nickname, About) = (_savedNickname, _savedAbout);

	[ObservableProperty] private string _nickname = "";
	[ObservableProperty] private string _about = "";
	[ObservableProperty] private string _newServer = "";
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private LanguageInfo? _selectedLanguage;
	[ObservableProperty] private string? _myPhoto;
	[ObservableProperty] private ThemeOption? _selectedTheme;
	[ObservableProperty] private TransferOption? _selectedTransfer;

	/// <summary>What file chunks may do on mobile data: anything, nothing, or only small files.</summary>
	public IReadOnlyList<TransferOption> TransferChoices => [new(FreeSpeech.Client.Attachments.FileTransferMode.Always, Loc.T("set_transfer_always")), new(FreeSpeech.Client.Attachments.FileTransferMode.WifiOnly, Loc.T("set_transfer_wifi")), new(FreeSpeech.Client.Attachments.FileTransferMode.WifiExceptSmall, Loc.T("set_transfer_wifi_small"))];

	public IReadOnlyList<ThemeOption> Themes => [new(ThemeService.Dark, Loc.T("theme_dark")), new(ThemeService.Light, Loc.T("theme_light")), new(ThemeService.System, Loc.T("theme_system"))];

	/// <summary>Size (MB) under which received files download by themselves; 0 = never.</summary>
	public int AutoDownloadMb
	{
		get => ClientService.AutoDownloadMb;
		set { ClientService.AutoDownloadMb = value; OnPropertyChanged(); }
	}

	public bool ShowOutbox
	{
		get => ClientService.ShowOutbox;
		set
		{
			try { ClientService.ShowOutbox = value; }
			catch (Exception ex)
			{
				// A crash seen on a Pixel (Release build): the exception is shown instead of closing the app, and kept in the journal.
				Diag.Log($"Outgoing menu switch failed: {ex}");
				var text = ex.ToString();
				_ = Dialogs.AlertAsync("FreeSpeech", text.Length > 700 ? text[..700] : text, Loc.T("ok"));
			}
			OnPropertyChanged();
		}
	}

	public bool DiscreetKeyboard
	{
		get => ClientService.DiscreetKeyboard;
		set { ClientService.DiscreetKeyboard = value; OnPropertyChanged(); }
	}

	public bool ProtectedScreen
	{
		get => ClientService.ProtectedScreen;
		set { ClientService.ProtectedScreen = value; OnPropertyChanged(); }
	}

	public bool HideLockScreenContent
	{
		get => ClientService.HideLockScreenContent;
		set { ClientService.HideLockScreenContent = value; OnPropertyChanged(); }
	}

	/// <summary>Size (MB) above which a file is only sent after a confirmation; 0 = never asked.</summary>
	public int ConfirmSendMb
	{
		get => ClientService.ConfirmSendMb;
		set { ClientService.ConfirmSendMb = value; OnPropertyChanged(); }
	}

	/// <summary>Show "is typing…" to the other side, and see theirs (reciprocal).</summary>
	public bool TypingIndicator
	{
		get => ClientService.TypingIndicator;
		set { ClientService.TypingIndicator = value; OnPropertyChanged(); }
	}

	/// <summary>Tell contacts when I read their messages.</summary>
	public bool SendReadReceipts
	{
		get => ClientService.SendReadReceipts;
		set { ClientService.SendReadReceipts = value; OnPropertyChanged(); }
	}

	/// <summary>System notifications while the app is in the background; muted conversations stay silent.</summary>
	public bool Notifications
	{
		get => ClientService.NotificationsEnabled;
		set
		{
			ClientService.NotificationsEnabled = value;
			OnPropertyChanged();
			if (value && ClientService.Notifier is { } n) _ = n.EnsurePermissionAsync().ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(NotificationsBlocked))));   // the phone asks once
			OnPropertyChanged(nameof(NotificationsBlocked));
		}
	}

	/// <summary>Receiving while the app is closed: the push wake-up and the periodic check. Off, messages only arrive when the app is opened.</summary>
	public bool BackgroundWake
	{
		get => ClientService.BackgroundWakeEnabled;
		set
		{
			ClientService.BackgroundWakeEnabled = value;
			OnPropertyChanged();
			_ = _service.ApplyBackgroundWakeAsync();
		}
	}

	/// <summary>The optional PIN (off by default). Switching it on asks for a code, switching it off asks for the current one.</summary>
	public bool LockEnabled
	{
		get => AppLock.Enabled;
		set => _ = SetLockAsync(value);
	}

	private async Task SetLockAsync(bool on)
	{
		if (on == AppLock.Enabled) return;
		var page = new LockPage(on ? LockMode.Set : LockMode.Verify);
		await Shell.Current.Navigation.PushModalAsync(page, false);
		if (await page.Result && !on) AppLock.Disable();
		OnPropertyChanged(nameof(LockEnabled));   // a cancelled change puts the switch back
	}

	[RelayCommand]
	private async Task ChangePinAsync()
	{
		var check = new LockPage(LockMode.Verify);
		await Shell.Current.Navigation.PushModalAsync(check, false);
		if (!await check.Result) return;
		var set = new LockPage(LockMode.Set);
		await Shell.Current.Navigation.PushModalAsync(set, false);
		if (await set.Result) Status = Loc.T("lock_changed");
	}

	[RelayCommand]
	private Task OpenWipeAsync() => Shell.Current.GoToAsync("wipe");

	[RelayCommand]
	private Task OpenMigrateAsync() => Shell.Current.GoToAsync("migrate");

	/// <summary>Record a diagnostic journal on this phone (for a bug report). Off by default; never content, keys or full identifiers.</summary>
	public bool DiagLogEnabled
	{
		get => DiagLog.Enabled;
		set { DiagLog.Enabled = value; OnPropertyChanged(); }
	}

	[RelayCommand]
	private async Task ShareDiagLogAsync()
	{
		if (!DiagLog.HasContent) { Status = Loc.T("set_diag_empty"); return; }
		await Share.Default.RequestAsync(new ShareFileRequest { Title = Loc.T("set_diag_title"), File = new ShareFile(DiagLog.BuildExport(), "text/plain") });
	}

	/// <summary>Follow a contact's verified migration without asking (never a contact I verified).</summary>
	public bool AutoAcceptMigration
	{
		get => ClientService.AutoAcceptMigration;
		set
		{
			ClientService.AutoAcceptMigration = value;
			if (_service.State == AppState.Ready) Client.AutoAcceptMigration = value;
			OnPropertyChanged();
		}
	}

	/// <summary>As a moderator, cut a migrated member's old identity off from my groups at once.</summary>
	public bool AutoExcludeMigrated
	{
		get => ClientService.AutoExcludeMigrated;
		set
		{
			ClientService.AutoExcludeMigrated = value;
			if (_service.State == AppState.Ready) Client.AutoExcludeMigrated = value;
			OnPropertyChanged();
		}
	}

	[ObservableProperty] private string _keyFilePassword = "";
	[ObservableProperty] private bool _keyFileBusy;

	/// <summary>
	/// Opens a key file with its password and says whether it is the key file of THIS identity: nothing is restored, stored or changed, the
	/// phrases only live in memory for the length of the check.
	/// </summary>
	[RelayCommand]
	private async Task VerifyKeyFileAsync()
	{
		if (KeyFileBusy) return;
		try
		{
			var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("set_keyfile_pick") });
			if (picked == null) return;
			byte[] bytes;
			await using (var stream = await picked.OpenReadAsync())
			{
				using var copy = new MemoryStream();
				await stream.CopyToAsync(copy);
				bytes = copy.ToArray();
			}
			var password = KeyFilePassword;
			KeyFileBusy = true;
			Status = Loc.T("set_keyfile_checking");
			var mine = Client.Identity.DiscoveryId;
			var found = await Task.Run(() =>
			{
				var p = PhraseVault.Open(bytes, password);   // the slow key derivation stays off the UI thread
				return IdentityService.Derive(p[0], p[1], p[2], null).DiscoveryId;
			});
			Status = Loc.T(found == mine ? "set_keyfile_ok" : "set_keyfile_other", found[..8]);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		catch (Exception ex) when (ex is ArgumentException or IOException) { Status = Loc.T("set_keyfile_bad"); }
		finally
		{
			KeyFileBusy = false;
			KeyFilePassword = "";
		}
	}

	[ObservableProperty] private IReadOnlyList<LockOption> _lockDelays = [];
	[ObservableProperty] private LockOption? _selectedLockDelay;
	[ObservableProperty] private IReadOnlyList<LockOption> _wipeOptions = [];
	[ObservableProperty] private LockOption? _selectedWipe;

	partial void OnSelectedLockDelayChanged(LockOption? value)
	{
		if (!_loading && value != null) AppLock.DelayMinutes = value.Value;
	}

	partial void OnSelectedWipeChanged(LockOption? value)
	{
		if (_loading || value == null || value.Value == AppLock.WipeAfter) return;
		if (value.Value == 0) { AppLock.WipeAfter = 0; return; }
		// Erasing is serious: say what it does before arming it.
		_ = MainThread.InvokeOnMainThreadAsync(async () =>
		{
			if (await Dialogs.AlertAsync(Loc.T("lock_wipe"), Loc.T("lock_wipe_confirm", value.Value), Loc.T("lock_wipe_yes"), Loc.T("cancel"))) AppLock.WipeAfter = value.Value;
			else SelectedWipe = WipeOptions.FirstOrDefault(o => o.Value == AppLock.WipeAfter);
		});
	}

	/// <summary>True when the phone itself blocks our notifications: the setting is on but nothing can show.</summary>
	public bool NotificationsBlocked => Notifications && ClientService.Notifier is { IsAllowed: false };

	[RelayCommand]
	private Task OpenBackgroundSetupAsync() => ClientService.OfferBackgroundSetupAsync(force: true);

	[RelayCommand]
	private void OpenNotificationSettings() => ClientService.Notifier?.OpenSystemSettings();

	/// <summary>"Version 0.1 (build 1)", from the app package.</summary>
	public string VersionText => Loc.T("set_version", AppInfo.Current.VersionString, AppInfo.Current.BuildString, BuildDefaults.Get("VersionDate"));

	public ObservableCollection<ServerRow> Servers { get; } = [];
	[ObservableProperty] private IReadOnlyList<LanguageInfo> _languages = Loc.Instance.Available;   // one list: the picker compares by reference

	public SettingsViewModel(ClientService service)
	{
		_service = service;
		Refresh();
		Loc.Instance.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Refresh);   // "connected" is a translated text
	}

	private FreeSpeechClient Client => _service.Client;

	public void Refresh()
	{
		_loading = true;
		var me = Client.Identity;
		// What the user is typing survives a trip to the picture picker: the saved values only replace the fields when nothing was edited.
		if (!HasUnsavedChanges) { Nickname = me.Nickname ?? ""; About = me.Description ?? ""; }
		(_savedNickname, _savedAbout) = (me.Nickname ?? "", me.Description ?? "");
		MyPhoto = me.Photo;
		Languages = Loc.Instance.Available;   // a language may have been added or removed
		SelectedLanguage = Languages.FirstOrDefault(l => l.Code == Loc.Instance.Language);
		OnPropertyChanged(nameof(Themes));
		OnPropertyChanged(nameof(VersionText));
		OnPropertyChanged(nameof(NotificationsBlocked));
		SelectedTheme = Themes.FirstOrDefault(t => t.Code == ThemeService.Mode);
		OnPropertyChanged(nameof(TransferChoices));
		SelectedTransfer = TransferChoices.FirstOrDefault(o => o.Mode == ClientService.TransferMode);
		LockDelays = [new(0, Loc.T("lock_delay_0")), new(5, Loc.T("lock_delay_5")), new(60, Loc.T("lock_delay_60")), new(1440, Loc.T("lock_delay_1440")), new(AppLock.DefaultDelayMinutes, Loc.T("lock_delay_10080"))];
		SelectedLockDelay = LockDelays.FirstOrDefault(o => o.Value == AppLock.DelayMinutes) ?? LockDelays[^1];
		WipeOptions = [new(0, Loc.T("lock_wipe_off")), new(5, Loc.T("lock_wipe_n", 5)), new(10, Loc.T("lock_wipe_n", 10))];
		SelectedWipe = WipeOptions.FirstOrDefault(o => o.Value == AppLock.WipeAfter) ?? WipeOptions[0];
		OnPropertyChanged(nameof(LockEnabled));

		Servers.Clear();
		foreach (var s in Client.Servers) Servers.Add(new ServerRow(s.Server.Url, Loc.T(s.Connected ? "set_online" : s.Refusal switch
		{
			"private_server" => "set_server_private",
			"access_pending" => "set_server_pending",
			"access_refused" => "set_server_refused",
			_ => "set_offline",
		}) + (!s.Connected && s.Refusal == "access_pending" ? "\n" + Loc.T("set_verification_code", FreeSpeechClient.VerificationCode(Client.Identity.DiscoveryId)) : ""),
			CanRequestAccess: !s.Connected && s.Refusal == "private_server"));
		_loading = false;
	}

	partial void OnSelectedTransferChanged(TransferOption? value)
	{
		if (_loading || value == null || value.Mode == ClientService.TransferMode) return;
		ClientService.TransferMode = value.Mode;
		ClientService.ApplyTransferSetting(_service.Client);   // applies now, and resumes what was waiting for Wi-Fi if it is allowed
	}

	// The palette is read when the app starts: a change asks to start over.
	partial void OnSelectedThemeChanged(ThemeOption? value)
	{
		if (_loading || value == null || value.Code == ThemeService.Mode) return;
		ThemeService.Mode = value.Code;
		_ = MainThread.InvokeOnMainThreadAsync(async () =>
		{
			if (await Dialogs.AlertAsync(Loc.T("set_theme"), Loc.T("theme_restart"), Loc.T("theme_restart_now"), Loc.T("theme_later"))) AppRestart.Now();
		});
	}

	partial void OnSelectedLanguageChanged(LanguageInfo? value)
	{
		OnPropertyChanged(nameof(CanRemoveLanguage));
		if (!_loading && value != null) Loc.Instance.Select(value.Code);
	}

	public bool CanRemoveLanguage => SelectedLanguage?.Imported == true;


	[RelayCommand]
	private async Task ImportLanguageAsync()
	{
		try
		{
			var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("set_lang_import") });
			if (picked == null) return;
			await using var stream = await picked.OpenReadAsync();
			var r = Loc.Instance.Import(stream, picked.FileName);
			Refresh();
			Status = Loc.T("lang_imported", r.Name, r.Missing);
		}
		catch (InvalidDataException ex) { Status = Loc.T(ex.Message); }
		catch (IOException) { Status = Loc.T("lang_invalid"); }
	}

	[RelayCommand]
	private async Task ShareTemplateAsync()
	{
		try { await Share.Default.RequestAsync(new ShareFileRequest(Loc.T("set_lang_template"), new ShareFile(Loc.Instance.WriteTemplate()))); }
		catch (IOException ex) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private void RemoveLanguage()
	{
		if (SelectedLanguage is not { Imported: true } l) return;
		Loc.Instance.RemoveImported(l.Code);
		Refresh();
		Status = Loc.T("lang_removed", l.Name);
	}

	[RelayCommand]
	private async Task SaveProfileAsync()
	{
		try
		{
			Client.SetNickname(Nickname);
			Client.SetDescription(string.IsNullOrWhiteSpace(About) ? null : About);
			await Client.PublishProfileAsync();
			(_savedNickname, _savedAbout) = (Nickname.Trim(), About.Trim());
			Status = Loc.T("set_saved");
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private async Task PickPhotoAsync()
	{
		try
		{
			var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("a11y_photo"), FileTypes = FilePickerFileType.Images });
			if (picked == null) return;
			ProfilePhoto.PendingPath = await ProfilePhoto.StageAsync(picked);
			ProfilePhoto.OnFramed = null;                 // my own picture: the crop screen saves it as the profile picture
			await Shell.Current.GoToAsync("photocrop");   // the user frames it, the screen saves it
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private async Task RemovePhotoAsync()
	{
		try
		{
			Client.SetPhoto(null);
			await Client.PublishProfileAsync();
			MyPhoto = null;
			Status = Loc.T("set_photo_removed");
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private async Task AddServerAsync()
	{
		try
		{
			var s = await Client.AddServerAsync(NewServer);   // the server says whether it is private; an invitation is asked for then (UseInvitation)
			await Client.NotifyContactsAsync();
			NewServer = "";
			Status = Loc.T(s.Connected ? "set_server_added" : s.Refusal switch
			{
				"private_server" => "set_server_private",
				"access_pending" => "set_server_pending",
				"access_refused" => "set_server_refused",
				_ => "set_server_unreachable",
			});
			Refresh();
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

	/// <summary>A private server turned me away and its administrator gave me an invitation: it is typed here, used once, and not kept.</summary>
	[RelayCommand]
	private async Task UseInvitationAsync(ServerRow row)
	{
		try
		{
			var token = await Dialogs.PromptAsync(Loc.T("set_use_invitation"), Loc.T("set_invitation_prompt", row.Url), Loc.T("set_request_send"), Loc.T("cancel"), Loc.T("set_invitation_hint"));
			if (string.IsNullOrWhiteSpace(token)) return;
			var s = await Client.UseInvitationAsync(row.Url, token);
			Status = Loc.T(s.Connected ? "set_server_added" : s.Refusal switch
			{
				"private_server" => "set_invitation_refused",
				"access_pending" => "set_server_pending",
				"access_refused" => "set_server_refused",
				_ => "set_server_unreachable",
			});
			Refresh();
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

	/// <summary>Asks the administrator of a private server to allow me. My name leaves the phone, so the user is asked first.</summary>
	[RelayCommand]
	private async Task RequestAccessAsync(ServerRow row)
	{
		try
		{
			var name = Nickname.Trim().Length > 0 ? Nickname.Trim() : Loc.T("set_request_anonymous");
			if (!await Dialogs.AlertAsync(Loc.T("set_request_access"), Loc.T("set_request_consent", name, row.Url), Loc.T("set_request_send"), Loc.T("cancel"))) return;
			// A few optional words for the administrator (declared by me, not checked); cancelling here cancels the request.
			var note = await Dialogs.PromptAsync(Loc.T("set_request_access"), Loc.T("set_request_note_prompt"), Loc.T("set_request_send"), Loc.T("cancel"), Loc.T("set_request_note_hint"), maxLength: 200);
			if (note == null) return;
			var s = await Client.RequestServerAccessAsync(row.Url, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
			Status = Loc.T(s.Connected ? "set_server_added" : s.Refusal == "access_pending" ? "set_server_pending" : s.Refusal == "access_refused" ? "set_server_refused" : "set_server_private");
			Refresh();
			// The way for the administrator to know the request is mine: this code, read out by another channel, is shown beside the request on the server.
			if (s.Refusal == "access_pending")
				await Dialogs.AlertAsync(Loc.T("set_request_sent_title"), Loc.T("set_request_sent", FreeSpeechClient.VerificationCode(Client.Identity.DiscoveryId)), Loc.T("ok"));
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private async Task RemoveServerAsync(ServerRow row)
	{
		try
		{
			await Client.RemoveServerAsync(row.Url);
			await Client.NotifyContactsAsync();
			Status = Loc.T("set_server_removed");
			Refresh();
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
	}

}
