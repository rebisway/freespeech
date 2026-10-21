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

/// <summary>My confirmed contacts, alphabetical; a click opens the conversation.</summary>
public sealed partial class ContactsPageViewModel : ObservableObject, IDisposable
{
	private readonly MainViewModel _owner;

	[ObservableProperty] private string _status = "";

	public ContactsPageViewModel(MainViewModel owner)
	{
		_owner = owner;
		owner.Session.ConversationsChanged += Refresh;
		Refresh();
	}

	public ObservableCollection<ContactRow> Contacts { get; } = [];

	public bool IsEmpty => Contacts.Count == 0;
	public string EmptyText => Loc.T("contacts_empty");
	public string RenameTip => Loc.T("contact_rename_title");
	public string VerifyTip => Loc.T("safety_title");
	public string DeleteTip => Loc.T("a11y_delete");

	public void Dispose() => _owner.Session.ConversationsChanged -= Refresh;

	private void Refresh()
	{
		if (_owner.Session.State != SessionState.Ready) return;
		var rows = _owner.Session.Client.Contacts.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
			.Select(c => new ContactRow(c.DiscoveryId, c.DisplayName, c.Photo, c.VerifiedAt != null, c.Nickname, c.Description)).ToList();
		// Synced in place: clearing the list made the rows blink.
		for (var i = 0; i < rows.Count; i++)
		{
			if (i >= Contacts.Count) Contacts.Add(rows[i]);
			else if (Contacts[i] != rows[i]) Contacts[i] = rows[i];
		}
		while (Contacts.Count > rows.Count) Contacts.RemoveAt(Contacts.Count - 1);
		OnPropertyChanged(nameof(IsEmpty));
	}

	[RelayCommand] private void Open(ContactRow row) => _owner.OpenConversation(row.Id);

	/// <summary>Removing a contact archives the conversation (kept for reading, restored if the contact is added again).</summary>
	[RelayCommand]
	private async Task RemoveAsync(ContactRow row)
	{
		if (!await Ui.ConfirmAsync(Loc.T("contact_remove_title"), Loc.T("contact_remove_text", row.Nickname), Loc.T("contact_remove"), Loc.T("cancel"))) return;
		try
		{
			_owner.Session.Client.RemoveContact(row.Id);
			Status = Loc.T("contact_removed", row.Nickname);
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_owner.Session.RaiseConversationsChanged();
	}

	/// <summary>Two Thomas in the list: I give one of them a name only I see. Empty goes back to their own. Nothing is sent to them.</summary>
	[RelayCommand]
	private async Task RenameAsync(ContactRow row)
	{
		var name = await Ui.PromptAsync(Loc.T("contact_rename_title"), Loc.T("contact_rename_text", row.Original), Loc.T("ok"), Loc.T("cancel"), row.Nickname);
		if (name == null) return;   // cancelled
		try
		{
			_owner.Session.Client.RenameContact(_owner.Session.Client.FindContact(row.Id)!, name);
			Status = Loc.T("contact_renamed");
		}
		catch (ClientException ex) { Status = ErrorText.Of(ex); }
		_owner.Session.RaiseConversationsChanged();
	}

	/// <summary>The safety number to compare with the contact.</summary>
	[RelayCommand] private void Verify(ContactRow row) => _owner.Push(new SafetyViewModel(_owner, row.Id), Loc.T("safety_title"));
}