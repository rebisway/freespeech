/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>Folds the on-screen keyboard away, for a screen change that would otherwise leave it hanging over a page with no field.</summary>
public static class KeyboardHelper
{
	public static void Hide()
	{
#if ANDROID
		// The token comes from the focused view, so it is read before the focus is dropped.
		if (Platform.CurrentActivity is { } activity && activity.CurrentFocus?.WindowToken is { } token
			&& activity.GetSystemService(Android.Content.Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager imm)
			imm.HideSoftInputFromWindow(token, 0);
#endif
	}
}