/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.ComponentModel;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// What the side menu shows besides its fixed labels: "Approve" carries the number of things waiting for an answer (contact
/// requests, group invitations, proposals), so none goes unnoticed, and "Archives" the number of archived conversations.
/// Follows language changes too.
/// </summary>
public sealed class AppBadges : INotifyPropertyChanged
{
	public static AppBadges Instance { get; } = new();

	private int _pendingRequests;
	private int _archived;

	public event PropertyChangedEventHandler? PropertyChanged;

	private AppBadges() => Loc.Instance.PropertyChanged += (_, _) =>
	{
		Raise(nameof(ApproveTitle));
		Raise(nameof(ArchiveTitle));
		Raise(nameof(OutboxTitle));
	};

	private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	public int PendingRequests
	{
		get => _pendingRequests;
		set
		{
			if (_pendingRequests == value) return;
			_pendingRequests = value;
			Raise(nameof(PendingRequests));
			Raise(nameof(ApproveTitle));
		}
	}

	/// <summary>Archived conversations (groups I left, contacts I removed).</summary>
	public int Archived
	{
		get => _archived;
		set
		{
			if (_archived == value) return;
			_archived = value;
			Raise(nameof(ArchiveTitle));
		}
	}

	private bool _showOutbox = Preferences.Default.Get("show_outbox", false);

	/// <summary>The "Outgoing" entry of the side menu is hidden unless the setting turns it on.</summary>
	public bool ShowOutbox
	{
		get => _showOutbox;
		set
		{
			if (_showOutbox == value) return;
			_showOutbox = value;
			Raise(nameof(ShowOutbox));
		}
	}

	public string OutboxTitle => Loc.Instance["outbox_title"];

	public string ApproveTitle => _pendingRequests > 0 ? $"{Loc.Instance["nav_approve"]} ({_pendingRequests})" : Loc.Instance["nav_approve"];

	public string ArchiveTitle => _archived > 0 ? $"{Loc.Instance["archive_title"]} ({_archived})" : Loc.Instance["archive_title"];
}
