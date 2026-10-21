/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Views;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Route of the conversation screen. Its page is built (XAML read, view model created) ahead of time, while the chat list is on screen, and handed over
/// when a conversation is opened: building it then is a large part of the delay before a conversation shows. One spare at a time.
/// </summary>
public sealed class ChatRoute : RouteFactory
{
	private static ChatPage? _spare;

	/// <summary>Builds the spare page unless there is one. Only called while the chat list shows (no conversation is open, so no other view model is live).</summary>
	public static void Prepare()
	{
		if (_spare != null || IPlatformApplication.Current?.Services is not { } services) return;
		_spare = services.GetRequiredService<ChatPage>();
	}

	public override Element GetOrCreate()
	{
		var page = _spare;
		_spare = null;
		return page ?? IPlatformApplication.Current!.Services.GetRequiredService<ChatPage>();
	}

	public override Element GetOrCreate(IServiceProvider services)
	{
		var page = _spare;
		_spare = null;
		return page ?? services.GetRequiredService<ChatPage>();
	}
}
