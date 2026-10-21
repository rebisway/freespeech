/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>Shows a system notification (the phone's notification shade) while the app is not on screen. Implemented per platform.</summary>
public interface INotifier
{
	/// <summary>A tap on the notification opens <paramref name="route"/> (a Shell route).</summary>
	void Show(string title, string text, string route);

	/// <summary>Removes the notification shown for <paramref name="route"/> (the user has seen what it was about). One notification per route: a newer one replaces it.</summary>
	void Cancel(string route);

	/// <summary>False when the system blocks this app's notifications (permission refused, or switched off in the phone's settings).</summary>
	bool IsAllowed { get; }

	/// <summary>Starts (or stops) the periodic background check for new messages while the app is not running.</summary>
	void ApplyBackgroundPolling(bool enabled);

	/// <summary>Opens the phone's own notification settings for this app.</summary>
	void OpenSystemSettings();

	/// <summary>Asks the user's permission to notify, if the system needs it. True when allowed.</summary>
	Task<bool> EnsurePermissionAsync();

	/// <summary>True when the phone may still stop the app from waking up for a push: battery optimisation applies, or the maker adds an auto-start switch.</summary>
	bool NeedsBackgroundSetup { get; }

	/// <summary>Asks the system to leave the app out of battery optimisation (one Yes/No box), then opens the maker's auto-start page when there is one.</summary>
	void OpenBackgroundSetup();
}