/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Foreground service started by a push wake-up while this device is sending a file: it only holds the process, the transfer itself runs in the client.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class TransferService : Service
{
	internal const string ChannelId = "transfer";
	internal const string TextExtra = "text";
	private const int NotificationId = 2;

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		var ctx = Android.App.Application.Context;
		if (ctx.GetSystemService(NotificationService) is NotificationManager nm && OperatingSystem.IsAndroidVersionAtLeast(26) && nm.GetNotificationChannel(ChannelId) == null)
			nm.CreateNotificationChannel(new NotificationChannel(ChannelId, Localization.Loc.T("notif_transfer_channel"), NotificationImportance.Low));

		var n = new Notification.Builder(ctx, ChannelId)
			.SetSmallIcon(Resource.Drawable.ic_stat)
			.SetContentTitle("FreeSpeech")
			.SetContentText(intent?.GetStringExtra(TextExtra) ?? "")
			.SetOngoing(true)
			.Build();
		if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, n, ForegroundService.TypeDataSync);
		else StartForeground(NotificationId, n);
		return StartCommandResult.NotSticky;
	}
}

public sealed class AndroidKeepAlive : IKeepAlive
{
	public void Start(string text)
	{
		var ctx = Android.App.Application.Context;
		var intent = new Intent(ctx, typeof(TransferService)).PutExtra(TransferService.TextExtra, text);
		try
		{
			if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(intent);
			else ctx.StartService(intent);
		}
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Keep-alive not started: {ex.Message}"); }
	}

	public void Stop()
	{
		var ctx = Android.App.Application.Context;
		ctx.StopService(new Intent(ctx, typeof(TransferService)));
	}
}