/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpeech.Client;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One line of the conversation list.</summary>
public sealed partial class ConversationRow(ConversationSummary summary, int unread, string preview = "") : ObservableObject
{
	public Conversation Conversation { get; } = summary.Conversation;
	public string Key => summary.Conversation.HistoryKey;
	public string Title => summary.Title;
	public string? Photo => summary.Photo;
	public bool IsGroup => summary.IsGroup;
	public string Preview => (preview.Length > 0 ? preview : summary.Preview).ReplaceLineEndings(" ");
	public int Unread { get; } = unread;
	public bool HasUnread => Unread > 0;
	public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
	public string Time => summary.Last == null ? "" : Format(summary.Last.At.ToLocalTime());

	public static string Format(DateTime local)
	{
		var today = DateTime.Today;
		if (local.Date == today) return local.ToString("HH:mm");
		return local.Date == today.AddDays(-1) ? "−1 j" : local.Year == today.Year ? local.ToString("dd/MM") : local.ToString("dd/MM/yy");
	}
}