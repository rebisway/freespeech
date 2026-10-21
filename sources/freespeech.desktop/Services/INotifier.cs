/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Desktop.Services;

/// <summary>System notifications. Implemented per operating system; a system without an implementation gets <see cref="NoNotifier"/>.</summary>
public interface INotifier
{
	/// <summary>A notification for a conversation (its history key). One per conversation: a newer one replaces it. A click opens the conversation.</summary>
	void Show(string title, string text, string conversationKey, string? messageId = null);

	/// <summary>The file of this message arrived: a picture appears in the notification that shows it.</summary>
	void FileReady(string messageId, string path);

	/// <summary>The conversation was read: its notification goes away.</summary>
	void Cancel(string conversationKey);

	/// <summary>Raised on a click on a notification: open this conversation.</summary>
	event Action<string>? Opened;

	/// <summary>Raised when the user answers from the notification: conversation key and text.</summary>
	event Action<string, string>? Replied;
}

/// <summary>No system notifications (yet) on this platform.</summary>
public sealed class NoNotifier : INotifier
{
	public void Show(string title, string text, string conversationKey, string? messageId = null) { }
	public void FileReady(string messageId, string path) { }
	public void Cancel(string conversationKey) { }
	public event Action<string>? Opened { add { } remove { } }
	public event Action<string, string>? Replied { add { } remove { } }
}
