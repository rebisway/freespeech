/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Desktop.Services;

/// <summary>Whether the window is on screen and in use: a message shown in a chat only counts as read while it is (and never behind the PIN screen).</summary>
public static class WindowFocus
{
	private static bool _active;

	public static bool IsActive
	{
		get => _active && !AppLock.Showing;
		set
		{
			if (_active == value) return;
			_active = value;
			Changed?.Invoke();
		}
	}

	/// <summary>Raised when the window becomes active or inactive.</summary>
	public static event Action? Changed;
}
