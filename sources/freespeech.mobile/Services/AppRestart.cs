/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
#endif
namespace FreeSpeech.Mobile.Services;

/// <summary>Starts the app over (a new theme is read once, at start).</summary>
public static class AppRestart
{
	public static void Now()
	{
#if ANDROID
		var ctx = Android.App.Application.Context;
		var intent = new Intent(ctx, typeof(RestartActivity));
		intent.PutExtra(RestartActivity.ExtraPid, Process.MyPid());
		intent.AddFlags(ActivityFlags.NewTask);
		ctx.StartActivity(intent);
#endif
	}
}