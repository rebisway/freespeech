/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Update;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// The update check of the APK build (see <see cref="UpdateChecker"/>). The website and the key are chosen at build time (<c>FsUpdateUrl</c>, <c>FsUpdateKey</c>):
/// a build without them, such as the Google Play bundle that the store updates, has no check and no button. On request from Settings, or at start-up when the
/// user turned that on (off by default); a version is announced once by a dialog, then only in Settings.
/// </summary>
public static class UpdateService
{
	private static readonly string Url = BuildDefaults.Get("UpdateUrl");
	private static readonly string Key = BuildDefaults.Get("UpdateKey");

	public static bool Enabled => Url.Length > 0 && Key.Length > 0;

	/// <summary>Look for an update each time the app starts. Off by default: each check tells the publisher's website that someone is running the app.</summary>
	public static bool CheckAtStartup
	{
		get => Preferences.Default.Get("update_check_startup", false);
		set => Preferences.Default.Set("update_check_startup", value);
	}

	public static async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
	{
		if (!Enabled) return new UpdateResult(UpdateStatus.Failed);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		int? build = int.TryParse(AppInfo.Current.BuildString, out var b) ? b : null;
		return await new UpdateChecker(http, Url, Key).CheckAsync("android-apk", AppInfo.Current.VersionString, build, Loc.Instance.Language, ct);
	}

	/// <summary>The start-up check, when the user asked for it. Announces a version once.</summary>
	public static async Task RunStartupCheckAsync()
	{
		if (!Enabled || !CheckAtStartup) return;
		var result = await CheckAsync();
		if (result.Status != UpdateStatus.Available || Preferences.Default.Get("update_announced", "") == result.Version) return;
		Preferences.Default.Set("update_announced", result.Version ?? "");
		var open = await MainThread.InvokeOnMainThreadAsync(() =>
			Dialogs.AlertAsync(Loc.T("set_update_title"), Loc.T("set_update_available", result.Version ?? "", result.Date ?? ""), Loc.T("set_update_download"), Loc.T("cancel")));
		if (open && result.DownloadUrl is { } url) await Launcher.Default.OpenAsync(url);
	}
}
