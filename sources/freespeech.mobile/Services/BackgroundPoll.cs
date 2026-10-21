/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// The polling fallback for notifications (PROTOCOL §9): the system wakes the app now and then (about every 15 minutes at
/// best), it connects for a few seconds, takes what the servers kept for it, tells the user, and goes back to sleep.
/// It never runs while the app itself is connected (the app notifies live), and the app waits for it before it opens the profile.
/// </summary>
public static class BackgroundPoll
{
	/// <summary>Held while a check runs: the profile database must not be opened twice.</summary>
	public static readonly SemaphoreSlim Gate = new(1, 1);

	private static readonly TimeSpan MaxTransfer = TimeSpan.FromMinutes(10);   // a transfer that goes on longer is dropped; the receiver asks again

	private static CancellationTokenSource? _stop;

	/// <summary>The app was opened: a running background check ends at once, so the app does not wait up to 45 s (10 min with a transfer) for the profile.</summary>
	public static void RequestStop() { try { _stop?.Cancel(); } catch (ObjectDisposedException) { } }

	private static readonly TimeSpan Listen = TimeSpan.FromSeconds(45);   // a sender's outbox is retried on a 30 s tick (the servers keep no message): stay connected longer than one tick

	/// <param name="listen">How long to stay connected; the push wake-up uses a shorter time (the system lets that process run only briefly).</param>
	public static async Task RunOnceAsync(ClientService live, TimeSpan? listen = null)
	{
		if (!ClientService.BackgroundWakeEnabled || ClientService.Notifier is not { } notifier) { Diag.Log("BG poll: wake-up off"); return; }
		var canShow = ClientService.NotificationsEnabled && notifier.IsAllowed;   // with notifications off the check still brings the messages in, silently
		if (live.State == AppState.Ready) { Diag.Log("BG poll: app is connected itself"); return; }   // the app is connected itself: it tells the user live
		if (!await Gate.WaitAsync(0))
		{
			// The periodic check (WorkManager) often starts with the process and wins the gate. A push wake-up must still hold its
			// process until that check is over: returning at once lets the system freeze it right after it connected.
			Diag.Log("BG poll: another check is running");
			if (listen != null)
			{
				var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
				while (Gate.CurrentCount == 0 && DateTime.UtcNow < until) await Task.Delay(500);
			}
			return;
		}
		Diag.Log("BG poll: start");
		using var stop = new CancellationTokenSource();
		_stop = stop;
		try
		{
			await using var client = ClientService.NewClient();
			if (!await Task.Run(client.TryLoadIdentity)) return;   // no profile yet, or the key is lost: nothing to check
			await Task.Run(client.Initialize);

			client.MessageReceived += m =>
			{
				System.Diagnostics.Debug.WriteLine($"BG poll: message from {m.FromName} mine={m.Mine} otherDevice={m.FromOtherDevice} muted={ClientService.IsMuted(m.Conversation.HistoryKey)}");
				if (!canShow || m.Mine || m.FromOtherDevice || ClientService.IsMuted(m.Conversation.HistoryKey)) return;
				var last = client.GetLatestMessages(m.Conversation, 1).Entries.FirstOrDefault();
				var title = m.Conversation.Group is { } g ? $"{m.FromName} · {g.Name}" : m.FromName;
				notifier.Show(title, last == null ? m.Text : PreviewText.Of(client, last), $"chat?key={Uri.EscapeDataString(m.Conversation.HistoryKey)}");
			};
			client.ContactRequested += c => { if (canShow) notifier.Show(c.Nickname, Loc.T("banner_request"), "//approve"); };
			client.MigrationRequested += n => { if (canShow) notifier.Show(n.OldName, Loc.T("banner_migration"), "//approve"); };
			client.Groups.InvitationReceived += i => { if (canShow) notifier.Show(i.InviterName, Loc.T("banner_group_invite", i.Name), "//approve"); };

			await client.ConnectAsync(); Diag.Log("BG poll: connected");
			// A phone that has just started (or just left airplane mode) often has no network yet: the first connection times out and the
			// periodic retry is 30 s away. Try again every few seconds, and count the listening time from the moment a server is connected.
			var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(60);
			while (!client.AnyServerConnected && DateTime.UtcNow < giveUp)
			{
				await Task.Delay(3000, stop.Token);
				try { await client.ReconnectServersAsync(); } catch (Exception ex) when (ex is ClientException or HttpRequestException or IOException or InvalidOperationException) { }
			}
			Diag.Log($"BG poll: servers connected={client.AnyServerConnected}");
			await Task.Delay(listen ?? Listen, stop.Token);   // the senders' own clients re-send what they hold for us when they see us online
				// A file is being served (a video, a document: more than the wake-up window): hold the process until it is done.
				Diag.Log($"BG poll: window over, serving={client.Attachments.IsServing}");
				if (client.Attachments.IsServing && ClientService.KeepAlive is { } keep)
				{
					keep.Start(Loc.T("notif_transfer"));
					try
					{
						var end = DateTime.UtcNow + MaxTransfer;
						while (client.Attachments.IsServing && DateTime.UtcNow < end) await Task.Delay(1000, stop.Token);
					}
					finally { keep.Stop(); }
				}
		}
		catch (Exception ex) when (ex is ClientException or HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
		{
			Diag.Log($"BG poll failed: {ex.GetType().Name} {ex.Message}");
		}
		finally { _stop = null; Gate.Release(); Diag.Log("BG poll: end"); }
	}
}