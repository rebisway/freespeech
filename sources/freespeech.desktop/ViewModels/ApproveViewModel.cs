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
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One thing waiting for an answer: a contact request, an invitation to a group, a member's proposal (for a moderator) or an identity change.</summary>
public enum ApprovalKind { Contact, GroupInvite, Suggestion, Migration }

public sealed record ApprovalItem(ApprovalKind Kind, string Id, string Title, string Subtitle, string? Photo = null)
{
	public bool IsGroup => Kind == ApprovalKind.GroupInvite;
	public bool HasSubtitle => Subtitle.Length > 0;
	public bool CanDecline => Kind != ApprovalKind.Contact;
}

/// <summary>What waits for my answer. Nothing is joined before I accept.</summary>
public sealed partial class ApproveViewModel : ObservableObject, IDisposable
{
	private readonly MainViewModel _owner;

	[ObservableProperty] private string _status = "";

	public ApproveViewModel(MainViewModel owner)
	{
		_owner = owner;
		owner.Session.ConversationsChanged += Refresh;
		Refresh();
	}

	public ObservableCollection<ApprovalItem> Items { get; } = [];

	public bool IsEmpty => Items.Count == 0;
	public string EmptyText => Loc.T("approve_empty");
	public string AcceptTip => Loc.T("ok");
	public string DeclineTip => Loc.T("cancel");

	public void Dispose() => _owner.Session.ConversationsChanged -= Refresh;

	/// <summary>Everything that waits for my answer, in the order of this screen (also shown at the top of the conversation list).</summary>
	public static List<ApprovalItem> Collect(FreeSpeechClient client)
	{
		var items = new List<ApprovalItem>();
		foreach (var r in client.PendingRequests) items.Add(new ApprovalItem(ApprovalKind.Contact, r.DiscoveryId, r.Nickname, "", r.Photo));
		foreach (var i in client.Groups.PendingGroupInvites)
			items.Add(new ApprovalItem(ApprovalKind.GroupInvite, i.GroupId, i.Name, i.SponsorName == null ? Loc.T("approve_group_by", i.InviterName) : Loc.T("approve_group_by_sponsor", i.InviterName, i.SponsorName)));
		foreach (var s in client.Groups.Suggestions)
			items.Add(new ApprovalItem(ApprovalKind.Suggestion, s.Key, s.Candidate.Nickname, Loc.T("approve_suggest_by", s.SponsorName, s.GroupName)));
		foreach (var m in client.PendingMigrations)
			items.Add(new ApprovalItem(ApprovalKind.Migration, m.OldId, Loc.T("approve_migration", m.OldName), Loc.T("approve_migration_sub", m.NewCard.Nickname, m.NewCard.DiscoveryId[..8])));
		return items;
	}

	private void Refresh()
	{
		if (_owner.Session.State != SessionState.Ready) return;
		Items.Clear();
		foreach (var item in Collect(_owner.Session.Client)) Items.Add(item);
		OnPropertyChanged(nameof(IsEmpty));
	}

	/// <summary>Asks for a picture and brings it to the profile size (a centred square); null when cancelled or unreadable.</summary>
	private static async Task<string?> PickGroupPhotoAsync()
	{
		if (await Ui.PickFileAsync(Loc.T("a11y_photo"), "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp") is not { } path) return null;
		return await Task.Run(() => ProfilePhoto.Prepare(path));
	}

	[RelayCommand]
	private async Task AcceptAsync(ApprovalItem item)
	{
		var client = _owner.Session.Client;
		try
		{
			switch (item.Kind)
			{
				case ApprovalKind.GroupInvite:
					// The group, who invites, and the name and picture I will show there (my usual ones by default, editable).
					var chosen = await Ui.JoinGroupAsync(item.Title, item.Subtitle, Loc.T("group_my_name"), client.Identity.Nickname ?? "", client.Identity.Photo, Loc.T("group_my_photo"), Loc.T("ok"), Loc.T("cancel"), PickGroupPhotoAsync);
					if (chosen is not { } mine) return;   // cancelled: the invitation keeps waiting
					FreeSpeech.Client.Groups.GroupRecord group;
					try { group = await client.Groups.AcceptInvitationAsync(item.Id, mine.Name, mine.PhotoFile == null ? null : FreeSpeechClient.ReadPicture(mine.PhotoFile)); }
					finally { if (mine.PhotoFile != null) try { File.Delete(mine.PhotoFile); } catch (IOException) { } }
					Status = Loc.T("approve_group_joined", group.Name);
					break;
				case ApprovalKind.Migration:
					await client.AcceptMigrationAsync(item.Id);
					Status = Loc.T("approve_migration_done", item.Title);
					break;
				case ApprovalKind.Suggestion:
					var ids = item.Id.Split('/');
					await client.Groups.InviteSuggestionAsync(ids[0], ids[1]);   // the person still answers the invitation
					Status = Loc.T("approve_suggest_done", item.Title);
					break;
				default:
					await client.ConfirmAsync(item.Id);
					Status = Loc.T("approve_done", item.Title);
					break;
			}
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Status = ErrorText.Of(ex); }
		_owner.Session.RaiseConversationsChanged();
	}

	[RelayCommand]
	private void Decline(ApprovalItem item)
	{
		var client = _owner.Session.Client;
		switch (item.Kind)
		{
			case ApprovalKind.GroupInvite: client.Groups.DeclineInvitation(item.Id); Status = Loc.T("approve_group_declined", item.Title); break;
			case ApprovalKind.Suggestion: var ids = item.Id.Split('/'); client.Groups.DismissSuggestion(ids[0], ids[1]); Status = Loc.T("approve_suggest_dismissed", item.Title); break;
			case ApprovalKind.Migration: client.DeclineMigration(item.Id); Status = Loc.T("approve_migration_declined", item.Title); break;
			default: return;
		}
		_owner.Session.RaiseConversationsChanged();
	}
}