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
using Avalonia.Threading;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

public sealed record ServerRow(string Url, string Status, bool CanRequestAccess = false);
public sealed record ThemeOption(string Code, string Name);
public sealed record LockOption(int Value, string Name);

/// <summary>My profile (picture, name, about), the language, the options, and my servers: the same screen as the mobile app's.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
	private readonly MainViewModel _owner;
	private bool _loading;
	private string _savedNickname = "", _savedAbout = "";

	[ObservableProperty] private string _nickname = "";
	[ObservableProperty] private string _about = "";
	[ObservableProperty] private string _newServer = "";
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private string? _myPhoto;
	[ObservableProperty] private LanguageInfo? _selectedLanguage;
	[ObservableProperty] private ThemeOption? _selectedTheme;
	[ObservableProperty] private string _keyFilePassword = "";
	[ObservableProperty] private bool _keyFileBusy;

	public ObservableCollection<ServerRow> Servers { get; } = [];
	[ObservableProperty] private IReadOnlyList<LanguageInfo> _languages = Loc.Available;

	public SettingsViewModel(MainViewModel owner)
	{
		_owner = owner;
		Refresh();
	}

	private FreeSpeechClient Client => _owner.Session.Client;

	/// <summary>The name or the description was changed and not saved yet.</summary>
	public bool HasUnsavedChanges => Nickname.Trim() != _savedNickname || About.Trim() != _savedAbout;

	public bool CanRemoveLanguage => SelectedLanguage?.Imported == true;

	/// <summary>Size (MB) under which received files download by themselves; 0 = never.</summary>
	public int AutoDownloadMb
	{
		get => Prefs.AutoDownloadMb;
		set { Prefs.AutoDownloadMb = value; OnPropertyChanged(); }
	}

	/// <summary>Size (MB) above which a file is only sent after a confirmation; 0 = never asked.</summary>
	public int ConfirmSendMb
	{
		get => Prefs.ConfirmSendMb;
		set { Prefs.ConfirmSendMb = value; OnPropertyChanged(); }
	}

	/// <summary>Show "is typing…" to the other side, and see theirs (reciprocal).</summary>
	public bool TypingIndicator
	{
		get => Prefs.TypingIndicator;
		set { Prefs.TypingIndicator = value; OnPropertyChanged(); }
	}

	/// <summary>Tell contacts when I read their messages.</summary>
	public bool SendReadReceipts
	{
		get => Prefs.SendReadReceipts;
		set { Prefs.SendReadReceipts = value; OnPropertyChanged(); }
	}

	/// <summary>The popup when a message arrives while the window is not shown; muted conversations stay silent.</summary>
	public bool Notifications
	{
		get => Prefs.NotificationsEnabled;
		set { Prefs.NotificationsEnabled = value; OnPropertyChanged(); }
	}

	/// <summary>Shows "Outgoing" in the side menu (hidden by default).</summary>
	public bool ShowOutbox
	{
		get => Prefs.ShowOutbox;
		set { Prefs.ShowOutbox = value; OnPropertyChanged(); }
	}
	public string OutboxTitle => Loc.T("set_outbox");
	public string OutboxHint => Loc.T("set_outbox_hint");

	/// <summary>Start with the system, hidden in the tray (on by default).</summary>
	public bool LaunchAtStartup
	{
		get => Prefs.LaunchAtStartup;
		set { Prefs.LaunchAtStartup = value; Autostart.Set(value); OnPropertyChanged(); }
	}

	/// <summary>Record a diagnostic journal on this computer (for a bug report). Off by default; never content, keys or full identifiers.</summary>
	public bool DiagLogEnabled
	{
		get => DiagLog.Enabled;
		set { DiagLog.Enabled = value; OnPropertyChanged(); }
	}

	/// <summary>Follow a contact's verified migration without asking (never a contact I verified).</summary>
	public bool AutoAcceptMigration
	{
		get => Prefs.AutoAcceptMigration;
		set { Prefs.AutoAcceptMigration = value; Client.AutoAcceptMigration = value; OnPropertyChanged(); }
	}

	/// <summary>As a moderator, cut a migrated member's old identity off from my groups at once.</summary>
	public bool AutoExcludeMigrated
	{
		get => Prefs.AutoExcludeMigrated;
		set { Prefs.AutoExcludeMigrated = value; Client.AutoExcludeMigrated = value; OnPropertyChanged(); }
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
		var ok = await _owner.ShowLockAsync(on ? LockMode.Set : LockMode.Verify);
		if (ok && !on) AppLock.Disable();
		OnPropertyChanged(nameof(LockEnabled));   // a cancelled change puts the switch back
	}

	[RelayCommand] private void OpenWipe() => _owner.Push(new WipeViewModel(_owner), Loc.T("wipe_title"));

	[RelayCommand] private void OpenMigrate() => _owner.Push(new MigrateViewModel(_owner), Loc.T("mig_title"));

	public string WipeTitle => Loc.T("wipe_section");
	public string WipeHint => Loc.T("wipe_section_hint");
	public string WipeTip => Loc.T("wipe_title");
	public string MigrationHint => Loc.T("mig_section_hint");
	public string MigrationTip => Loc.T("mig_title");

	[RelayCommand]
	private async Task ChangePinAsync()
	{
		if (!await _owner.ShowLockAsync(LockMode.Verify)) return;
		if (await _owner.ShowLockAsync(LockMode.Set)) Status = Loc.T("lock_changed");
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
		_ = Dispatcher.UIThread.InvokeAsync(async () =>
		{
			if (await Ui.ConfirmAsync(Loc.T("lock_wipe"), Loc.T("lock_wipe_confirm", value.Value), Loc.T("lock_wipe_yes"), Loc.T("cancel"))) AppLock.WipeAfter = value.Value;
			else SelectedWipe = WipeOptions.FirstOrDefault(o => o.Value == AppLock.WipeAfter);
		});
	}

	public string LockTitle => Loc.T("lock_section");
	public string LockHint => Loc.T("lock_hint");
	public string LockDelayText => Loc.T("lock_delay");
	public string LockWipeText => Loc.T("lock_wipe");
	public string LockChangeTip => Loc.T("lock_change");

	public IReadOnlyList<ThemeOption> Themes => [new(ThemeService.Dark, Loc.T("theme_dark")), new(ThemeService.Light, Loc.T("theme_light")), new(ThemeService.System, Loc.T("theme_system"))];
	public string ThemeTitle => Loc.T("set_theme");

	public string VersionText => Loc.T("d_version", typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "", BuildDefaults.Get("VersionDate"));

	public string ProfileTitle => Loc.T("set_profile");
	public string NicknameHint => Loc.T("set_nickname_hint");
	public string AboutHint => Loc.T("set_about_hint");
	public string PhotoTip => Loc.T("a11y_photo");
	public string RemoveTip => Loc.T("a11y_remove");
	public string SaveTip => Loc.T("a11y_save");
	public string AddTip => Loc.T("a11y_add");
	public string LanguageTitle => Loc.T("set_language");
	public string LangImportTip => Loc.T("set_lang_import");
	public string LangTemplateTip => Loc.T("set_lang_template");
	public string LangRemoveTip => Loc.T("set_lang_remove");
	public string AutoDlTitle => Loc.T("set_autodl");
	public string AutoDlHint => Loc.T("set_autodl_hint");
	public string ConfirmSendTitle => Loc.T("set_confirmsend");
	public string ConfirmSendHint => Loc.T("set_confirmsend_hint");
	public string ReceiptsTitle => Loc.T("set_receipts");
	public string ReceiptsHint => Loc.T("set_receipts_hint");
	public string TypingTitle => Loc.T("set_typing");
	public string TypingHint => Loc.T("set_typing_hint");
	public string NotificationsTitle => Loc.T("set_notifications");
	public string NotificationsHint => Loc.T("d_notif_hint");
	public string StartupTitle => Loc.T("d_startup");
	public string StartupHint => Loc.T("d_startup_hint");
	public string MigrationTitle => Loc.T("mig_section");
	public string MigrateExcludeHint => Loc.T("set_migrate_exclude_hint");
	public string MigrateAutoHint => Loc.T("set_migrate_auto_hint");
	public string DiagTitle => Loc.T("set_diag_section");
	public string DiagHint => Loc.T("d_diag_hint");
	public string DiagOpenLabel => Loc.T("d_diag_open");
	public string KeyFileTitle => Loc.T("set_keyfile_section");
	public string KeyFileHint => Loc.T("set_keyfile_hint");
	public string KeyFilePasswordHint => Loc.T("set_keyfile_password_hint");
	public string KeyFilePickTip => Loc.T("set_keyfile_pick");
	public string ServersTitle => Loc.T("set_servers");
	public string ServerHint => Loc.T("set_server_hint");
	public string RequestAccessLabel => Loc.T("set_request_access");
	public string UseInvitationLabel => Loc.T("set_use_invitation");

	public void Refresh()
	{
		_loading = true;
		var me = Client.Identity;
		// What the user is typing survives a trip to the picture picker: the saved values only replace the fields when nothing was edited.
		if (!HasUnsavedChanges) { Nickname = me.Nickname ?? ""; About = me.Description ?? ""; }
		(_savedNickname, _savedAbout) = (me.Nickname ?? "", me.Description ?? "");
		MyPhoto = me.Photo;
		Languages = Loc.Available;   // a language may have been added or removed
		SelectedLanguage = Languages.FirstOrDefault(l => l.Code == Loc.Code);
		SelectedTheme = Themes.FirstOrDefault(t => t.Code == ThemeService.Mode);
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
		OnPropertyChanged(nameof(CanRemoveLanguage));
		_loading = false;
	}

	// The palette is read dynamically: the change shows at once.
	partial void OnSelectedThemeChanged(ThemeOption? value)
	{
		if (_loading || value == null || value.Code == ThemeService.Mode) return;
		ThemeService.Mode = value.Code;
	}

	partial void OnSelectedLanguageChanged(LanguageInfo? value)
	{
		OnPropertyChanged(nameof(CanRemoveLanguage));
		if (_loading || value == null || value.Code == Loc.Code) return;
		Loc.Select(value.Code);
		// The texts are read at start: a change asks to start the program again.
		_ = AskRestartAsync(value.Code);
	}

	private async Task AskRestartAsync(string code)
	{
		if (await Ui.ConfirmAsync(Loc.T("set_language"), Loc.T("d_restart_text"), Loc.T("theme_restart_now"), Loc.T("theme_later"))) AppRestart.Now();
	}

	[RelayCommand]
	private async Task ImportLanguageAsync()
	{
		try
		{
			if (await Ui.PickFileAsync(Loc.T("set_lang_import"), "*.json") is not { } path) return;
			await using var stream = File.OpenRead(path);
			var r = Loc.Import(stream, Path.GetFileName(path));
			Refresh();
			Status = Loc.T("lang_imported", r.Name, r.Missing);
			await AskRestartAsync(r.Code);
		}
		catch (InvalidDataException ex) { Status = Loc.T(ex.Message); }
		catch (IOException) { Status = Loc.T("lang_invalid"); }
	}

	[RelayCommand]
	private async Task SaveTemplateAsync()
	{
		try
		{
			if (await Ui.SaveFileAsync(Loc.T("set_lang_template"), "freespeech-language-template.json", "json") is not { } path) return;
			await File.WriteAllTextAsync(path, Loc.Template());
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = ex.Message; }
	}

	[RelayCommand]
	private void RemoveLanguage()
	{
		if (SelectedLanguage is not { Imported: true } l) return;
		Loc.RemoveImported(l.Code);
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

	/// <summary>The picture is cut to a centred square, reduced, saved and sent to the contacts.</summary>
	[RelayCommand]
	private async Task PickPhotoAsync()
	{
		string? prepared = null;
		try
		{
			if (await Ui.PickFileAsync(Loc.T("a11y_photo"), "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp") is not { } path) return;
			prepared = await Task.Run(() => ProfilePhoto.Prepare(path));
			if (prepared == null) { Status = Loc.T("onb_err_sheet"); return; }
			Client.SetPhoto(prepared);
			await Client.PublishProfileAsync();
			MyPhoto = Client.Identity.Photo;
			Status = Loc.T("set_photo_saved");
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ex is ClientException ? ErrorText.Of(ex) : ex.Message; }
		finally { if (prepared != null) try { File.Delete(prepared); } catch (IOException) { } }
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
	private void OpenDiagFolder()
	{
		if (!DiagLog.HasContent) { Status = Loc.T("set_diag_empty"); return; }
		try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(DiagLog.FilePath)!) { UseShellExecute = true }); }
		catch (System.ComponentModel.Win32Exception ex) { Status = ex.Message; }
	}

	/// <summary>
	/// Opens a key file with its password and says whether it is the key file of THIS identity: nothing is restored, stored or changed,
	/// the phrases only live in memory for the length of the check.
	/// </summary>
	[RelayCommand]
	private async Task VerifyKeyFileAsync()
	{
		if (KeyFileBusy) return;
		try
		{
			if (await Ui.PickFileAsync(Loc.T("set_keyfile_pick"), "*.fskey") is not { } path) return;
			var bytes = await File.ReadAllBytesAsync(path);
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
			var token = await Ui.PromptAsync(Loc.T("set_use_invitation"), Loc.T("set_invitation_prompt", row.Url), Loc.T("set_request_send"), Loc.T("cancel"), "", 200);
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

	/// <summary>Asks the administrator of a private server to allow me. My name leaves the computer, so the user is asked first.</summary>
	[RelayCommand]
	private async Task RequestAccessAsync(ServerRow row)
	{
		try
		{
			var name = Nickname.Trim().Length > 0 ? Nickname.Trim() : Loc.T("set_request_anonymous");
			if (!await Ui.ConfirmAsync(Loc.T("set_request_access"), Loc.T("set_request_consent", name, row.Url), Loc.T("set_request_send"), Loc.T("cancel"))) return;
			// A few optional words for the administrator (declared by me, not checked); cancelling here cancels the request.
			var note = await Ui.PromptAsync(Loc.T("set_request_access"), Loc.T("set_request_note_prompt"), Loc.T("set_request_send"), Loc.T("cancel"), "", 200);
			if (note == null) return;
			var s = await Client.RequestServerAccessAsync(row.Url, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
			Status = Loc.T(s.Connected ? "set_server_added" : s.Refusal == "access_pending" ? "set_server_pending" : s.Refusal == "access_refused" ? "set_server_refused" : "set_server_private");
			Refresh();
			// The way for the administrator to know the request is mine: this code, read out by another channel, is shown beside the request on the server.
			if (s.Refusal == "access_pending")
				await Ui.AlertAsync(Loc.T("set_request_sent_title"), Loc.T("set_request_sent", FreeSpeechClient.VerificationCode(Client.Identity.DiscoveryId)), Loc.T("ok"));
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