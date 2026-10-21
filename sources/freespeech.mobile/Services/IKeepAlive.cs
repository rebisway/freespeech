/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Keeps the process alive while a long transfer runs after a push wake-up (a few seconds are not enough for a video or a document).
/// Android: a foreground service with a visible notification; other platforms: nothing to do.
/// </summary>
public interface IKeepAlive
{
	void Start(string text);
	void Stop();
}