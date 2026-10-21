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
using FreeSpeech.Client.Groups;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>Accepting an invitation: the group, who invites, and the name and picture I will show in it (my global ones by default).</summary>
[QueryProperty(nameof(Id), "id")]
public partial class GroupJoinViewModel(ClientService service) : ObservableObject
{
	private string _id = "";
	private string? _photoFile;   // the picture framed for this group, if the user changed it

	[ObservableProperty] private string _groupName = "";
	[ObservableProperty] private string _invitedBy = "";
	[ObservableProperty] private string _myName = "";
	[ObservableProperty] private string? _myPhoto;
	[ObservableProperty] private string _status = "";

	public string Id
	{
		get => _id;
		set
		{
			_id = Uri.UnescapeDataString(value);
			var invite = service.Client.Groups.PendingGroupInvites.FirstOrDefault(i => i.GroupId == _id);
			if (invite == null) return;
			GroupName = invite.Name;
			InvitedBy = invite.SponsorName == null ? Loc.T("approve_group_by", invite.InviterName) : Loc.T("approve_group_by_sponsor", invite.InviterName, invite.SponsorName);
			MyName = service.Client.Identity.Nickname ?? "";
			MyPhoto = service.Client.Identity.Photo;
		}
	}

	[RelayCommand]
	private async Task ChoosePhotoAsync()
	{
		var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("a11y_photo"), FileTypes = FilePickerFileType.Images });
		if (picked == null) return;
		ProfilePhoto.PendingPath = await ProfilePhoto.StageAsync(picked);
		ProfilePhoto.OnFramed = path =>
		{
			_photoFile = path;
			MyPhoto = FreeSpeechClient.ReadPicture(path);   // shown here; sent when the invitation is accepted
			return Task.CompletedTask;
		};
		await Shell.Current.GoToAsync("photocrop");
	}

	[RelayCommand]
	private async Task AcceptAsync()
	{
		try
		{
			await service.Client.Groups.AcceptInvitationAsync(_id, MyName, _photoFile == null ? null : FreeSpeechClient.ReadPicture(_photoFile));
			service.NotifyConversationsChanged();
			await Shell.Current.GoToAsync("..");
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Status = ErrorText.Of(ex); }
	}

	[RelayCommand]
	private Task CancelAsync() => Shell.Current.GoToAsync("..");   // the invitation keeps waiting
}
