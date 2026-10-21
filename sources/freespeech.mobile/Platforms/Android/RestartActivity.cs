/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.App;
using Android.Content;
using Android.OS;

namespace FreeSpeech.Mobile;

/// <summary>
/// Lives in its own process (":restart"), so that it survives the app's process being killed: it kills it, then starts the
/// app again, which reads the new theme. Android refuses to start an activity from a background alarm, hence this detour.
/// </summary>
[Activity(Process = ":restart", Exported = false, NoHistory = true, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public sealed class RestartActivity : Activity
{
	public const string ExtraPid = "pid";

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		var pid = Intent?.GetIntExtra(ExtraPid, -1) ?? -1;
		var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
		if (pid > 0) Process.KillProcess(pid);
		if (launch != null)
		{
			launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
			StartActivity(launch);
		}
		Finish();
	}
}