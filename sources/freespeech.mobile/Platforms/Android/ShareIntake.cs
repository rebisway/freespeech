/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.App;
using Android.Content;
using Android.Provider;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Reads what another app shares with FreeSpeech (the system "Share" sheet): copies each file to our cache, then opens the screen where conversations are chosen.</summary>
internal static class ShareIntake
{
	public static void Handle(Activity activity, Intent? intent)
	{
		if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple)) return;

		var uris = new List<Android.Net.Uri>();
#pragma warning disable CA1422   // the typed overloads need Android 13; the old ones still work everywhere
		if (intent.Action == Intent.ActionSend) { if (intent.GetParcelableExtra(Intent.ExtraStream) is Android.Net.Uri one) uris.Add(one); }
		else if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } many) uris.AddRange(many.OfType<Android.Net.Uri>());
#pragma warning restore CA1422
		var text = intent.GetStringExtra(Intent.ExtraText);

		_ = Task.Run(() =>
		{
			var items = new List<SharedItem>();
			foreach (var uri in uris)
			{
				try { items.Add(Copy(activity, uri)); }
				catch (Exception ex) when (ex is IOException or Java.IO.IOException or UnauthorizedAccessException or Java.Lang.SecurityException) { /* that one is skipped */ }
			}
			if (!string.IsNullOrWhiteSpace(text)) items.Add(new SharedItem(null, text.Trim(), ""));
			if (items.Count == 0) return;
			ShareInbox.Items = items;
			ClientService.RequestRoute("share");
		});
	}

	private static SharedItem Copy(Activity activity, Android.Net.Uri uri)
	{
		var resolver = activity.ContentResolver!;
		var name = "shared";
		using (var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null))
			if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } n) name = n;

		var dir = System.IO.Path.Combine(FileSystem.CacheDirectory, "share");
		Directory.CreateDirectory(dir);
		var target = System.IO.Path.Combine(dir, $"{DateTime.Now:HHmmssfff}_{System.IO.Path.GetFileName(name)}");
		using var input = resolver.OpenInputStream(uri) ?? throw new IOException("Cannot read the shared file.");
		using var output = File.Create(target);
		input.CopyTo(output);
		return new SharedItem(target, null, name);
	}
}