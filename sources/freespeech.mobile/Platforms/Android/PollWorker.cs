/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.App;
using Android.Content;
using AndroidX.Work;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Android's WorkManager job behind <see cref="BackgroundPoll"/>: runs about every 15 minutes while there is a network.</summary>
public sealed class PollWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
	public const string UniqueName = "freespeech-poll";

	public override Result DoWork()
	{
		try
		{
			// The system starts this job with the process, at the very moment the screen is being created: the screen builds ClientService
			// and opens the profile itself, and the app is connected live, so the check stays out of its way until it has finished.
			if (StartupTrace.ActivityCreated && !StartupTrace.UiReady) { StartupTrace.Mark("PollWorker: the screen is starting, no check"); return Result.InvokeSuccess()!; }
			StartupTrace.Mark("PollWorker: start");
			var live = IPlatformApplication.Current?.Services.GetService<ClientService>();
			if (live != null) BackgroundPoll.RunOnceAsync(live).GetAwaiter().GetResult();
			StartupTrace.Mark("PollWorker: end");
		}
		catch (Exception ex) { StartupTrace.Crash("PollWorker", ex); }
		return Result.InvokeSuccess()!;   // the next period will try again whatever happened
	}

	/// <summary>Schedules the check, or cancels it.</summary>
	public static void Apply(bool enabled)
	{
		var work = WorkManager.GetInstance(Android.App.Application.Context);
		if (!enabled) { work.CancelUniqueWork(UniqueName); return; }

		var constraints = new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!)!.Build();
		var request = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(PollWorker)), 15, Java.Util.Concurrent.TimeUnit.Minutes!)
			.SetConstraints(constraints)!.Build();
		work.EnqueueUniquePeriodicWork(UniqueName, ExistingPeriodicWorkPolicy.Keep!, (PeriodicWorkRequest)request);
	}
}
#if DEBUG
/// <summary>Development only: <c>adb shell am broadcast -a org.freespeech.mobile.POLL_NOW -p org.freespeech.mobile</c> runs the check at once (WorkManager would wait for its 15-minute period).</summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(["org.freespeech.mobile.POLL_NOW"])]
public sealed class PollNowReceiver : BroadcastReceiver
{
	public override void OnReceive(Context? context, Intent? intent)
	{
		if (context == null) return;
		WorkManager.GetInstance(context).Enqueue((OneTimeWorkRequest)new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(PollWorker))).Build()!);
	}
}
#endif