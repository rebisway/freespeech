/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
#if FCM
using Android.App;
using AndroidX.Work;
using Firebase.Messaging;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>
/// Receives the push wake-up (PROTOCOL §9). The message carries no content: it only says "connect now". The same short check
/// as the 15-minute fallback then runs at once, and the system notification comes from what the servers hand over.
/// </summary>
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class PushService : FirebaseMessagingService
{
	public override void OnMessageReceived(RemoteMessage message)
	{
		// Run the check right here, not through WorkManager (which may wait minutes under battery saving): a high-priority push
		// gives this process a short window. The sender resends as soon as it sees us online, so a few seconds are enough; 25 s leaves room for a sender that was killed and needs about 10 s to start and connect.
		try
		{
			Diag.Log("Push: wake-up received");
			var live = IPlatformApplication.Current?.Services.GetService<ClientService>();
			Diag.Log($"Push: client service {(live == null ? "missing" : live.State.ToString())}");
			if (live != null) BackgroundPoll.RunOnceAsync(live, TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
		}
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Push check failed: {ex.Message}"); }
	}

	/// <summary>Firebase issued a new token: remembered, and sent to the servers the next time the app is connected.</summary>
	public override void OnNewToken(string token)
	{
		Preferences.Default.Set("push_token", token);
		Preferences.Default.Set("push_token_sent", "");
	}
}

/// <summary>Reads this device's FCM token.</summary>
public static class FcmToken
{
	public static Task<string?> GetAsync()
	{
		var source = new TaskCompletionSource<string?>();
		FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new Listener(source));
		return source.Task;
	}

	private sealed class Listener(TaskCompletionSource<string?> source) : Java.Lang.Object, Android.Gms.Tasks.IOnCompleteListener
	{
		public void OnComplete(Android.Gms.Tasks.Task task) => source.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
	}
}
#endif