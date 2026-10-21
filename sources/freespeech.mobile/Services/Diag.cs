/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>Diagnostic line that reaches `adb logcat` (tag "FreeSpeech") even in a Release build, unlike Debug.WriteLine. Never carries message content.</summary>
public static class Diag
{
	public static void Log(string text)
	{
#if ANDROID
		Android.Util.Log.Info("FreeSpeech", text);
#else
		System.Diagnostics.Debug.WriteLine(text);
#endif
	}
}