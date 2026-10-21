/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.History;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

/// <summary>The one-line description of a message: used under a conversation's name and in the notification banner.</summary>
public static class PreviewText
{
	private const int MaxLength = 80;

	/// <summary>The text itself, or "Photo" / "File: name" for an attachment, or a short label for a deleted message.</summary>
	public static string Of(FreeSpeechClient client, HistoryEntry entry)
	{
		switch (entry.State)
		{
			case MessageState.Retracted: return Loc.T("preview_deleted");
			case MessageState.FileRetracted: return Loc.T("preview_file_removed");
		}

		if (client.Attachments.Store.Find(entry.Id) is { } file)
			return file.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? Loc.T("preview_photo") : Loc.T("preview_file", file.Name);

		return OneLine(FreeSpeech.Client.Markup.MessageMarkup.PlainText(entry.Text));
	}

	/// <summary>The newest message of a conversation, as it is described under its name; in a group, with its author in front.</summary>
	public static string Latest(FreeSpeechClient client, Conversation conversation, HistoryEntry? last)
	{
		if (last == null) return "";
		var text = Of(client, last);
		if (conversation.Group == null) return text;
		var author = last.Direction == MessageDirection.Sent ? Loc.T("chat_me") : client.DisplayNameOf(conversation, last.From ?? "");
		return $"{author} : {text}";
	}

	private static string OneLine(string text)
	{
		var end = text.IndexOfAny(['\r', '\n']);
		var line = end < 0 ? text : text[..end];
		return line.Length > MaxLength ? line[..MaxLength] + "…" : line;
	}
}
