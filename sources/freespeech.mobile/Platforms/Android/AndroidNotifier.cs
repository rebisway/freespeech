/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using Android.App;
using Android.Content;
using Android.OS;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Android's POST_NOTIFICATIONS permission (runtime-requested from Android 13).</summary>
public sealed class NotificationPermission : Permissions.BasePlatformPermission
{
	public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
		OperatingSystem.IsAndroidVersionAtLeast(33) ? [("android.permission.POST_NOTIFICATIONS", true)] : [];
}

/// <summary>Notifications for messages, contact requests and invitations that arrive while the app is in the background.</summary>
public sealed class AndroidNotifier : INotifier
{
	private const string ChannelId = "messages";
	private const int NotificationId = 1;   // the route is the tag: one notification per conversation, replaced by a newer one

	public void Show(string title, string text, string route)
	{
		var ctx = Android.App.Application.Context;
		if (ctx.GetSystemService(Context.NotificationService) is not NotificationManager nm || !nm.AreNotificationsEnabled()) return;
		if (OperatingSystem.IsAndroidVersionAtLeast(26) && nm.GetNotificationChannel(ChannelId) == null)
			nm.CreateNotificationChannel(new NotificationChannel(ChannelId, Loc("notif_channel"), NotificationImportance.High));

		var open = new Intent(ctx, typeof(MainActivity));
		open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
		open.PutExtra("route", route);
		var pending = PendingIntent.GetActivity(ctx, route.GetHashCode() & 0x7fffffff, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

		var builder = new Notification.Builder(ctx, ChannelId)
			.SetSmallIcon(Resource.Drawable.ic_stat)
			.SetColor(Android.Graphics.Color.ParseColor("#C89A4B"))   // the app's gold: tints the small icon and the app name in the header
			.SetContentTitle(title)
			.SetContentText(text)
			.SetAutoCancel(true)
			.SetContentIntent(pending);
		if (FreeSpeech.Mobile.Services.ClientService.HideLockScreenContent)
		{
			// On a locked phone the system shows this plain version; the real one appears once it is unlocked.
			var plain = new Notification.Builder(ctx, ChannelId)
				.SetSmallIcon(Resource.Drawable.ic_stat)
				.SetContentTitle(Loc("notif_hidden_title"))
				.SetContentText(Loc("notif_hidden_text"))
				.Build();
			builder.SetVisibility(NotificationVisibility.Private).SetPublicVersion(plain);
		}
		nm.Notify(route, NotificationId, builder.Build());
	}

	public void Cancel(string route)
	{
		if (Android.App.Application.Context.GetSystemService(Context.NotificationService) is NotificationManager nm) nm.Cancel(route, NotificationId);
	}

	public bool IsAllowed => (Android.App.Application.Context.GetSystemService(Context.NotificationService) as NotificationManager)?.AreNotificationsEnabled() ?? false;

	public void ApplyBackgroundPolling(bool enabled) => PollWorker.Apply(enabled);

	public void OpenSystemSettings()
	{
		var ctx = Android.App.Application.Context;
		var intent = new Intent(Android.Provider.Settings.ActionAppNotificationSettings).PutExtra(Android.Provider.Settings.ExtraAppPackage, ctx.PackageName);
		intent.AddFlags(ActivityFlags.NewTask);
		ctx.StartActivity(intent);
	}

	public async Task<bool> EnsurePermissionAsync()
	{
		var status = await Permissions.CheckStatusAsync<NotificationPermission>();
		if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<NotificationPermission>();
		return status == PermissionStatus.Granted;
	}

	private static bool BatteryOptimised()
	{
		var ctx = Android.App.Application.Context;
		return OperatingSystem.IsAndroidVersionAtLeast(23) && ctx.GetSystemService(Context.PowerService) is PowerManager pm && !pm.IsIgnoringBatteryOptimizations(ctx.PackageName);
	}

	public bool NeedsBackgroundSetup => BatteryOptimised();

	public void OpenBackgroundSetup()
	{
		var ctx = Android.App.Application.Context;
		if (BatteryOptimised())
		{
			try
			{
				var ask = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations, Android.Net.Uri.Parse("package:" + ctx.PackageName));
				ask.AddFlags(ActivityFlags.NewTask);
				ctx.StartActivity(ask);
			}
			catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException) { Diag.Log($"Battery request failed: {ex.GetType().Name}"); }
		}
	}

	private static string Loc(string key) => Localization.Loc.T(key);
}