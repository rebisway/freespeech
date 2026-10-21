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
using FreeSpeech.Client.Groups;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

public sealed record MemberRow(string Id, string Nickname, string? Photo, string Role, bool IsMe, bool IsPending = false, int Trust = 0)
{
	/// <summary>A confirmed contact (grey check) / a contact whose safety number I compared (gold check).</summary>
	public bool IsContact => Trust == 1;
	public bool IsVerified => Trust == 2;
	public bool HasRole => Role.Length > 0;
	public double RowOpacity => IsPending ? 0.55 : 1;
}

/// <summary>
/// A group seen by its members: who is in it and with which role, and, according to my own role, what I can change.
/// Creator: name, picture, promote/demote, hand the role over, lift a ban. Moderator: add, remove, ban. Anyone: leave.
/// The rules themselves (who may do what, keys, rotation) are in the library; this screen only offers them.
/// </summary>
[QueryProperty(nameof(Key), "key")]
public partial class GroupInfoViewModel : ObservableObject
{
	private readonly ClientService _service;
	private GroupRecord? _group;
	private string _key = "";

	[ObservableProperty] private string _name = "";
	[ObservableProperty] private string? _photo;
	[ObservableProperty] private string _summary = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasDescription))] private string _description = "";
	[ObservableProperty] private string _status = "";
	[ObservableProperty] private bool _canModerate;
	[ObservableProperty] private bool _isCreator;
	[ObservableProperty] private bool _canSuggest;
	[ObservableProperty] private bool _isActive = true;
	[ObservableProperty] private bool _hasBans;
	[ObservableProperty] private string _bansLabel = "";

	/// <summary>Some member is one of my contacts: the legend of the checks is shown.</summary>
	[ObservableProperty] private bool _hasTrust;

	/// <summary>The name and the picture I show in this group.</summary>
	[ObservableProperty] private string _myName = "";
	[ObservableProperty] private string? _myPhoto;

	public ObservableCollection<MemberRow> Members { get; } = [];

	public bool HasDescription => Description.Length > 0;

	public GroupInfoViewModel(ClientService service) => _service = service;

	private FreeSpeechClient Client => _service.Client;
	private GroupService Groups => Client.Groups;

	public string Key
	{
		get => _key;
		set
		{
			_key = Uri.UnescapeDataString(value);
			Refresh();
		}
	}

	public void Refresh()
	{
		_group = Client.FindConversation(_key)?.Group is { } g ? Client.ResolveGroup(g.GroupId) : null;
		if (_group == null) return;

		var me = Client.Identity.DiscoveryId;
		var state = Groups.CurrentState(_group);
		Name = _group.Name;
		Photo = Groups.GroupPhoto(_group);
		Description = Groups.GroupDescription(_group) ?? "";
		IsActive = _group.Active;
		IsCreator = _group.Active && state.CreatedBy == me;
		CanModerate = _group.Active && Groups.IsModerator(_group);
		CanSuggest = _group.Active && !CanModerate;   // a member proposes, the moderators decide
		MyName = Groups.MyNameIn(_group);
		MyPhoto = Groups.MyPhotoIn(_group);

		var pending = Groups.PendingMembers(_group);
		Members.Clear();
		foreach (var m in Groups.Members(_group))
		{
			var role = m.DiscoveryId == state.CreatedBy ? Loc.T("group_role_creator") : state.Moderators.Contains(m.DiscoveryId) ? Loc.T("group_role_moderator") : pending.Contains(m.DiscoveryId) ? Loc.T("group_pending") : "";
			Members.Add(new MemberRow(m.DiscoveryId, Client.MemberNameIn(_group, m.DiscoveryId), Client.MemberPhotoIn(_group, m.DiscoveryId), role, m.DiscoveryId == me, pending.Contains(m.DiscoveryId), m.DiscoveryId == me ? 0 : Client.TrustOf(m.DiscoveryId)));
		}
		Summary = Loc.T("group_members_count", Members.Count);
		HasTrust = Members.Any(m => m.Trust > 0);
		var bans = CanModerate ? Groups.Bans(_group).Count : 0;
		BansLabel = Loc.T("group_bans_title", bans);
		HasBans = bans > 0;
	}

	// ------------------------------------------------------------------
	// Changing the group
	// ------------------------------------------------------------------

	/// <summary>The check after a member's name: opens the safety number of that contact, to compare it.</summary>
	[RelayCommand]
	private Task OpenSafetyAsync(MemberRow row) => row.Trust == 0 ? Task.CompletedTask : Shell.Current.GoToAsync($"safety?id={Uri.EscapeDataString(row.Id)}");

	/// <summary>Asks for the name I show in this group; empty = my global name again.</summary>
	[RelayCommand]
	private async Task EditMyNameAsync()
	{
		if (_group == null || !_group.Active) return;
		var group = _group;
		var name = await Dialogs.PromptAsync(Loc.T("group_my_name"), Loc.T("group_my_name_hint"), Loc.T("ok"), Loc.T("cancel"), initialValue: Groups.MyNameIn(group), maxLength: GroupService.MaxMemberNameLength);
		if (name == null || name.Trim() == Groups.MyNameIn(group)) return;   // cancelled, or unchanged
		await RunAsync(async () =>
		{
			await Groups.SetMyNameAsync(group, name);
			Status = Loc.T("group_my_name_saved");
		});
		Refresh();
	}

	[RelayCommand]
	private async Task AddMemberAsync()
	{
		if (_group == null) return;
		var inGroup = Groups.Members(_group).Select(m => m.DiscoveryId).ToHashSet();
		var candidates = Client.Contacts.Where(c => !inGroup.Contains(c.DiscoveryId)).ToList();
		if (candidates.Count == 0) { Status = Loc.T("group_nobody_to_add"); return; }

		var picked = await Dialogs.ChooseAsync(Loc.T("group_add_title"), Loc.T("cancel"), null, candidates.Select(Label).ToArray());
		var chosen = candidates.FirstOrDefault(c => Label(c) == picked);
		if (chosen == null) return;
		await RunAsync(async () =>
		{
			await Client.AddGroupMemberAsync(_group, chosen.Nickname);
			Status = Loc.T("group_invited", chosen.Nickname);   // the invitation waits for their answer
		});
	}

	[RelayCommand]
	private async Task SuggestMemberAsync()
	{
		if (_group == null) return;
		var inGroup = Groups.Members(_group).Select(m => m.DiscoveryId).ToHashSet();
		var candidates = Client.Contacts.Where(c => !inGroup.Contains(c.DiscoveryId)).ToList();
		if (candidates.Count == 0) { Status = Loc.T("group_nobody_to_add"); return; }

		var picked = await Dialogs.ChooseAsync(Loc.T("group_suggest_title"), Loc.T("cancel"), null, candidates.Select(Label).ToArray());
		var chosen = candidates.FirstOrDefault(c => Label(c) == picked);
		if (chosen == null) return;
		await RunAsync(async () =>
		{
			await Groups.SuggestAsync(_group, FreeSpeechClient.ToGroupMember(chosen));
			Status = Loc.T("group_suggested", chosen.Nickname);
		});
	}

	// The creator changes the name or the description (what the group is about).
	[RelayCommand]
	private async Task EditAsync()
	{
		if (_group == null || !IsCreator) return;
		var choice = await Dialogs.ChooseAsync(Loc.T("group_edit_title"), Loc.T("cancel"), null, Loc.T("group_rename_title"), Loc.T("group_description_title"));
		if (choice == Loc.T("group_rename_title"))
		{
			var text = await Dialogs.PromptAsync(Loc.T("group_rename_title"), "", Loc.T("ok"), Loc.T("cancel"), initialValue: Name);
			if (string.IsNullOrWhiteSpace(text) || text.Trim() == Name) return;
			await RunAsync(() => Groups.SetProfileAsync(_group, text.Trim(), null));
		}
		else if (choice == Loc.T("group_description_title"))
		{
			var text = await Dialogs.PromptAsync(Loc.T("group_description_title"), Loc.T("group_description_hint"), Loc.T("ok"), Loc.T("cancel"), initialValue: Description, maxLength: 300);
			if (text == null || text.Trim() == Description) return;   // cancelled, or unchanged
			var clear = text.Trim().Length == 0;
			await RunAsync(() => Groups.SetProfileAsync(_group, null, null, description: clear ? null : text.Trim(), clearDescription: clear));
		}
	}

	[RelayCommand]
	private async Task ChangePhotoAsync()
	{
		if (_group == null || !IsCreator) return;
		var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("a11y_photo"), FileTypes = FilePickerFileType.Images });
		if (picked == null) return;
		ProfilePhoto.PendingPath = await ProfilePhoto.StageAsync(picked);
		var group = _group;
		ProfilePhoto.OnFramed = path => Client.SetGroupPhotoAsync(group, path);   // the crop screen saves it as the group's picture
		await Shell.Current.GoToAsync("photocrop");
	}

	/// <summary>My picture in this group: framed like my profile picture, or back to my global one.</summary>
	[RelayCommand]
	private async Task ChangeMyPhotoAsync()
	{
		if (_group == null || !_group.Active) return;
		var group = _group;
		var choice = await Dialogs.ChooseAsync(Loc.T("group_my_photo"), Loc.T("cancel"), null, Loc.T("group_my_photo_pick"), Loc.T("group_my_photo_global"));
		if (choice == Loc.T("group_my_photo_global")) { await RunAsync(() => Client.SetMyGroupPhotoAsync(group, null)); return; }
		if (choice != Loc.T("group_my_photo_pick")) return;
		var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Loc.T("a11y_photo"), FileTypes = FilePickerFileType.Images });
		if (picked == null) return;
		ProfilePhoto.PendingPath = await ProfilePhoto.StageAsync(picked);
		ProfilePhoto.OnFramed = path => Client.SetMyGroupPhotoAsync(group, path);
		await Shell.Current.GoToAsync("photocrop");
	}

	[RelayCommand]
	private async Task MemberActionsAsync(MemberRow row)
	{
		if (_group == null || row.IsMe || !_group.Active) return;
		var state = Groups.CurrentState(_group);
		var isMod = state.Moderators.Contains(row.Id);
		var isGroupCreator = row.Id == state.CreatedBy;

		var options = new List<(string Label, Func<Task> Run)>();
		if (row.IsPending)
		{
			// Invited, no answer yet: the only things to do are asking again or withdrawing the invitation.
			if (!CanModerate) return;
			options.Add((Loc.T("group_act_resend"), async () => { await Groups.ResendInvitationAsync(_group, row.Id); Status = Loc.T("group_invite_resent", row.Nickname); }));
			options.Add((Loc.T("group_act_cancel_invite"), () => Groups.RemoveMemberAsync(_group, row.Id)));
		}
		if (!row.IsPending && CanModerate && !isMod && !isGroupCreator) options.Add((Loc.T("group_act_promote"), () => Groups.PromoteAsync(_group, row.Id)));
		if (!row.IsPending && IsCreator && isMod && !isGroupCreator) options.Add((Loc.T("group_act_demote"), () => Groups.DemoteAsync(_group, row.Id)));
		if (!row.IsPending && IsCreator && !isGroupCreator) options.Add((Loc.T("group_act_transfer"), async () =>
		{
			if (await Dialogs.AlertAsync(Loc.T("group_act_transfer"), Loc.T("group_transfer_text", row.Nickname), Loc.T("ok"), Loc.T("cancel")))
				await Groups.TransferCreatorAsync(_group, row.Id);
		}));
		if (!row.IsPending && CanModerate && !isGroupCreator) options.Add((Loc.T("group_act_remove"), () => Groups.RemoveMemberAsync(_group, row.Id)));
		if (!row.IsPending && CanModerate && !isGroupCreator && !isMod) options.Add((Loc.T("group_act_ban"), async () =>
		{
			await Groups.BanAsync(_group, row.Id);
			await Groups.RemoveMemberAsync(_group, row.Id);   // a ban keeps someone out; the member leaves now too
		}));
		if (options.Count == 0) return;

		var choice = await Dialogs.ChooseAsync(row.Nickname, Loc.T("cancel"), null, options.Select(o => o.Label).ToArray());
		var picked = options.FindIndex(o => o.Label == choice);
		if (picked >= 0) await RunAsync(options[picked].Run);
	}

	// A banned contact stays in the list, flagged, so the refusal is not a surprise.
	private string Label(FreeSpeech.Client.Contacts.Contact c) => _group != null && Groups.Bans(_group).Contains(c.DiscoveryId) ? "⛔ " + c.DisplayName + " (" + Loc.T("group_banned_tag") + ")" : c.DisplayName;

	// The blacklist: moderators see it, only the creator can lift a ban.
	[RelayCommand]
	private async Task ShowBansAsync()
	{
		if (_group == null) return;
		var names = Groups.Bans(_group).ToDictionary(id => id, id => Client.Contacts.FirstOrDefault(c => c.DiscoveryId == id)?.DisplayName ?? (id.Length > 10 ? id[..10] + "…" : id));
		if (names.Count == 0) return;
		var picked = await Dialogs.ChooseAsync(Loc.T("group_bans_title", names.Count), Loc.T("cancel"), null, names.Values.ToArray());
		var id = names.FirstOrDefault(kv => kv.Value == picked).Key;
		if (id == null) return;
		if (!IsCreator) { Status = Loc.T("group_bans_creator_only"); return; }
		if (!await Dialogs.AlertAsync(Loc.T("group_unban"), Loc.T("group_unban_text", picked), Loc.T("group_unban"), Loc.T("cancel"))) return;
		await RunAsync(() => Groups.UnbanAsync(_group, id));
	}

	[RelayCommand]
	private async Task LeaveAsync()
	{
		if (_group == null || !_group.Active) return;
		if (!await Dialogs.AlertAsync(Loc.T("group_leave_title"), Loc.T("group_leave_text", Name), Loc.T("group_leave"), Loc.T("cancel"))) return;
		await RunAsync(async () =>
		{
			await Groups.LeaveAsync(_group);
			_service.NotifyConversationsChanged();
			await Shell.Current.GoToAsync("//conversations");
		});
	}

	/// <summary>Runs a change, then shows the group as it is now; a refusal of the library (not allowed, not a member any more) is told in one line.</summary>
	private async Task RunAsync(Func<Task> change)
	{
		Status = "";
		try { await change(); }
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Status = ErrorText.Of(ex); }
		Refresh();
		_service.NotifyConversationsChanged();
	}
}
