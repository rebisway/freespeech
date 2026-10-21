/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>One thing waiting for an answer: a contact request, an invitation to a group, or (for a moderator) a member's proposal to invite one of their contacts. The last two can also be declined.</summary>
public enum ApprovalKind { Contact, GroupInvite, Suggestion, Migration }

public sealed record ApprovalItem(ApprovalKind Kind, string Id, string Title, string Subtitle, string? Photo)
{
	public bool IsGroup => Kind == ApprovalKind.GroupInvite;
	public bool HasSubtitle => Subtitle.Length > 0;
	public bool CanDecline => Kind != ApprovalKind.Contact;
}

/// <summary>What waits for my answer: contact requests (someone imported my card) and invitations to groups. Nothing is joined before I accept.</summary>
public partial class ApproveViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	public ObservableCollection<ApprovalItem> Items { get; } = [];

	public ApproveViewModel(ClientService service)
	{
		_service = service;
		service.ConversationsChanged += Refresh;
		service.Notice += _ => Refresh();
	}

	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		var client = _service.Client;
		Items.Clear();
		foreach (var r in client.PendingRequests) Items.Add(new ApprovalItem(ApprovalKind.Contact, r.DiscoveryId, r.Nickname, "", r.Photo));
		foreach (var i in client.Groups.PendingGroupInvites)
			Items.Add(new ApprovalItem(ApprovalKind.GroupInvite, i.GroupId, i.Name, i.SponsorName == null ? Loc.T("approve_group_by", i.InviterName) : Loc.T("approve_group_by_sponsor", i.InviterName, i.SponsorName), null));
		foreach (var s in client.Groups.Suggestions)
			Items.Add(new ApprovalItem(ApprovalKind.Suggestion, s.Key, s.Candidate.Nickname, Loc.T("approve_suggest_by", s.SponsorName, s.GroupName), null));
		// A contact (or a member of a group I moderate) moved to a new identity; the notice is verified, following it is my decision.
		foreach (var m in client.PendingMigrations)
			Items.Add(new ApprovalItem(ApprovalKind.Migration, m.OldId, Loc.T("approve_migration", m.OldName), Loc.T("approve_migration_sub", m.NewCard.Nickname, m.NewCard.DiscoveryId[..8]), m.NewCard.Photo));
		_service.UpdateBadges();
	}

	[RelayCommand]
	private async Task AcceptAsync(ApprovalItem item)
	{
		try
		{
			if (item.Kind == ApprovalKind.GroupInvite)
			{
				// The name and picture I will show in the group are chosen on a page of their own; it joins the group.
				await Shell.Current.GoToAsync($"groupjoin?id={Uri.EscapeDataString(item.Id)}");
				return;
			}
			else if (item.Kind == ApprovalKind.Migration)
				{
					await _service.Client.AcceptMigrationAsync(item.Id);
					Status = Loc.T("approve_migration_done", item.Title);
					_service.NotifyConversationsChanged();
				}
			else if (item.Kind == ApprovalKind.Suggestion)
			{
				var ids = item.Id.Split('/');
				await _service.Client.Groups.InviteSuggestionAsync(ids[0], ids[1]);   // the person still answers the invitation
				Status = Loc.T("approve_suggest_done", item.Title);
			}
			else
			{
				await _service.Client.ConfirmAsync(item.Id);
				Status = Loc.T("approve_done", item.Title);
			}
			Refresh();
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Status = ErrorText.Of(ex); Refresh(); }
	}

	[RelayCommand]
	private void Decline(ApprovalItem item)
	{
		switch (item.Kind)
		{
			case ApprovalKind.GroupInvite:
				_service.Client.Groups.DeclineInvitation(item.Id);
				Status = Loc.T("approve_group_declined", item.Title);
				break;
			case ApprovalKind.Suggestion:
				var ids = item.Id.Split('/');
				_service.Client.Groups.DismissSuggestion(ids[0], ids[1]);
				Status = Loc.T("approve_suggest_dismissed", item.Title);
				break;
			case ApprovalKind.Migration:
				_service.Client.DeclineMigration(item.Id);
				Status = Loc.T("approve_migration_declined", item.Title);
				break;
			default: return;
		}
		Refresh();
	}
}
