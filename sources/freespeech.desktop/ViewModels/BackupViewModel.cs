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
using FreeSpeech.Client.Backup;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>Back up the account to an encrypted file, or restore from one.</summary>
public sealed partial class BackupViewModel(MainViewModel owner) : ObservableObject
{
	[ObservableProperty] private int _modeIndex = 1;
	[ObservableProperty] private bool _busy;
	[ObservableProperty] private string _status = "";

	// Data only (contacts, groups, conversations, attachments): the recovery phrases are never offered here, so a stranger at the
	// unlocked computer cannot read them.

	/// <summary>What to do with attachment files, in the order of <see cref="AttachmentBackupMode"/>.</summary>
	public IReadOnlyList<string> Modes => [Loc.T("backup_mode_none"), Loc.T("backup_mode_recent"), Loc.T("backup_mode_all")];

	public string MakeTitle => Loc.T("backup_make_title");
	public string MakeText => Loc.T("backup_make_text");
	public string RestoreTitle => Loc.T("restore_title");
	public string RestoreText => Loc.T("restore_text");

	private FreeSpeechClient Client => owner.Session.Client;

	[RelayCommand]
	private async Task BackupAsync()
	{
		Busy = true;
		try
		{
			var name = Path.GetFileName(Client.DefaultBackupPath);
			if (await Ui.SaveFileAsync(Loc.T("backup_share_title"), name, "enc") is not { } path) return;
			var mode = (AttachmentBackupMode)ModeIndex;
			var summary = await Task.Run(() => Client.CreateBackup(path, mode));
			Status = Loc.T("backup_done", summary.Contacts, summary.Messages, summary.Bytes / 1024);
		}
		catch (Exception ex) when (ex is ClientException or IOException or UnauthorizedAccessException) { Status = ErrorText.Of(ex); }
		finally { Busy = false; }
	}

	[RelayCommand]
	private async Task RestoreAsync()
	{
		Busy = true;
		try
		{
			if (await Ui.PickFileAsync(Loc.T("restore_pick"), "*.enc") is not { } path) return;
			// A profile still blank (no description, no picture: an identity just recreated) takes the name, description and picture of the backup; one already filled in is kept.
				var me = Client.Identity;
				var blank = string.IsNullOrWhiteSpace(me.Description) && string.IsNullOrEmpty(me.Photo);
				var summary = await Task.Run(() => Client.RestoreBackup(path, restoreProfile: blank));
			Status = Loc.T("restore_done", summary.Contacts, summary.Messages);
			owner.Session.RaiseConversationsChanged();
			_ = owner.OfferDeviceSyncAsync();   // another device of this identity, if one is connected, completes what the file lacks
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ErrorText.Of(ex); }
		finally { Busy = false; }
	}
}