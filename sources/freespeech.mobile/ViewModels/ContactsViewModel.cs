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

public sealed record ContactRow(string Id, string Nickname, string? Photo, bool Verified, string Original = "", string? About = null)
{
	/// <summary>What they wrote about themselves, in small letters under the name (one line, cut short).</summary>
	public string Description => About is { Length: > 0 } a ? a.ReplaceLineEndings(" ").Trim() : "";
	public bool HasDescription => Description.Length > 0;

	/// <summary>Under the name I gave: what they call themselves, so I still know who it is.</summary>
	public string Subtitle => Original.Length > 0 && Original != Nickname ? Original : "";
	public bool HasSubtitle => Subtitle.Length > 0;

	/// <summary>A small check mark after the name once the safety number was compared.</summary>
	public string DisplayName => Verified ? Nickname + "  ✔" : Nickname;
}

/// <summary>My confirmed contacts, alphabetical; a tap opens the conversation.</summary>
public partial class ContactsViewModel : ListViewModelBase
{
	private readonly ClientService _service;

	public ObservableCollection<ContactRow> Contacts { get; } = [];

	public ContactsViewModel(ClientService service)
	{
		_service = service;
		service.ConversationsChanged += Refresh;
	}

	public void Refresh()
	{
		if (_service.State != AppState.Ready) return;
		// Synced in place: clearing the list made every picture blink out while the page was being left.
		var rows = _service.Client.Contacts.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
			.Select(c => new ContactRow(c.DiscoveryId, c.DisplayName, c.Photo, c.VerifiedAt != null, c.Nickname, c.Description)).ToList();
		for (var i = 0; i < rows.Count; i++)
		{
			if (i >= Contacts.Count) Contacts.Add(rows[i]);
			else if (Contacts[i] != rows[i]) Contacts[i] = rows[i];
		}
		while (Contacts.Count > rows.Count) Contacts.RemoveAt(Contacts.Count - 1);
	}

	/// <summary>Removing a contact archives the conversation (kept for reading, restored if the contact is added again, deleted for good from the archive).</summary>
	[RelayCommand]
	private async Task RemoveAsync(ContactRow row)
	{
		if (!await Dialogs.AlertAsync(Loc.T("contact_remove_title"), Loc.T("contact_remove_text", row.Nickname), Loc.T("contact_remove"), Loc.T("cancel"))) return;
		try
		{
			_service.Client.RemoveContact(row.Id);
			Status = Loc.T("contact_removed", row.Nickname);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_service.NotifyConversationsChanged();
	}

	/// <summary>Two Thomas in the list: I give one of them a name only I see. Empty goes back to their own. Nothing is sent to them.</summary>
	[RelayCommand]
	private async Task RenameAsync(ContactRow row)
	{
		var name = await Dialogs.PromptAsync(Loc.T("contact_rename_title"), Loc.T("contact_rename_text", row.Original), Loc.T("ok"), Loc.T("cancel"), initialValue: row.Nickname, maxLength: 64);
		if (name == null) return;   // cancelled
		try
		{
			_service.Client.RenameContact(_service.Client.FindContact(row.Id)!, name);
			Status = Loc.T("contact_renamed");
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_service.NotifyConversationsChanged();
	}

	[RelayCommand] private Task VerifyAsync(ContactRow row) => Shell.Current.GoToAsync($"safety?id={Uri.EscapeDataString(row.Id)}");

	[RelayCommand] private Task OpenAsync(ContactRow row) => Shell.Current.GoToAsync($"chat?key={Uri.EscapeDataString(row.Id)}");
}
