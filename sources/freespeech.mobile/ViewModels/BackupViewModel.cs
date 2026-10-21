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
using FreeSpeech.Client.Security;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>Back up the account to an encrypted file (and hand it to the share sheet), or restore from one.</summary>
public partial class BackupViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	[ObservableProperty] private int _modeIndex = 1;
	[ObservableProperty] private bool _isBusy;

	// Data only (contacts, groups, conversations, attachments): the recovery phrases are never offered here, so a stranger holding the
	// unlocked phone cannot read them.
	public BackupViewModel(ClientService service) => _service = service;

	/// <summary>What to do with attachment files, in the order of <see cref="AttachmentBackupMode"/>.</summary>
	public IReadOnlyList<string> Modes => [Loc.T("backup_mode_none"), Loc.T("backup_mode_recent"), Loc.T("backup_mode_all")];

	private FreeSpeechClient Client => _service.Client;

	[RelayCommand]
	private async Task BackupAsync()
	{
		var pin = await PinConfirm.AskAsync();
		if (!pin.Ok) { Status = pin.Message; return; }
		IsBusy = true;
		try
		{
			var mode = (AttachmentBackupMode)ModeIndex;
			var path = Client.DefaultBackupPath;
			var summary = await Task.Run(() => Client.CreateBackup(path, mode));
			Status = Loc.T("backup_done", summary.Contacts, summary.Messages, summary.Bytes / 1024);
			// The cache folder is the one the share sheet can read; the file keeps its dated name. Earlier copies are removed so they do not pile up.
			var shared = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(path));
			foreach (var old in Directory.GetFiles(FileSystem.CacheDirectory, "freespeech-backup*.enc").Concat(Directory.GetFiles(Path.GetDirectoryName(path)!, "freespeech-backup*.enc")).Where(f => f != path))
				try { File.Delete(old); } catch (IOException) { }
			File.Copy(path, shared, overwrite: true);
			await Share.Default.RequestAsync(new ShareFileRequest { Title = Loc.T("backup_share_title"), File = new ShareFile(shared) });
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}

	/// <summary>Frees space by moving downloaded files into an archive file (its own screen).</summary>
	[RelayCommand]
	private Task OpenArchiveAsync() => Shell.Current.GoToAsync("attachmentarchive");

	[RelayCommand]
	private async Task RestoreAsync()
	{
		IsBusy = true;
		try
		{
			var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("restore_pick") });
			if (file == null) return;
			var summary = await Task.Run(() => Client.RestoreBackup(file.FullPath));
			Status = Loc.T("restore_done", summary.Contacts, summary.Messages);
			_service.NotifyConversationsChanged();
			_ = _service.OfferDeviceSyncAsync();   // another device of this identity, if one is connected, can complete what the file lacks
		}
		catch (Exception ex) when (ex is ClientException or IOException) { Status = ErrorText.Of(ex); }
		finally { IsBusy = false; }
	}
}
