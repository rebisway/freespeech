/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using Avalonia.Threading;
using FreeSpeech.Client.Update;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The update check of the desktop client (see <see cref="UpdateChecker"/>). The website and the key are chosen at build time (<c>FsUpdateUrl</c>,
/// <c>FsUpdateKey</c>): a build without them has no check and no button. On request from Settings, or at start-up when the user turned that on
/// (off by default); a version is announced once by a dialog, then only in Settings.
/// </summary>
public static class UpdateService
{
	private static readonly string Url = BuildDefaults.Get("UpdateUrl");
	private static readonly string Key = BuildDefaults.Get("UpdateKey");

	public static bool Enabled => Url.Length > 0 && Key.Length > 0;

	/// <summary>Look for an update each time the program starts. Off by default: each check tells the publisher's website that someone is running the program.</summary>
	public static bool CheckAtStartup
	{
		get => Prefs.Get("update_check_startup", false);
		set => Prefs.Set("update_check_startup", value);
	}

	public static async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
	{
		if (!Enabled) return new UpdateResult(UpdateStatus.Failed);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		var version = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
		return await new UpdateChecker(http, Url, Key).CheckAsync(OperatingSystem.IsWindows() ? "windows" : "linux", version, null, Loc.Language, ct);
	}

	public static void Open(string url)
	{
		try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Diag.Log("Cannot open the download page: " + ex.Message); }
	}

	/// <summary>The start-up check, when the user asked for it. Announces a version once.</summary>
	public static async Task RunStartupCheckAsync()
	{
		if (!Enabled || !CheckAtStartup) return;
		var result = await CheckAsync();
		if (result.Status != UpdateStatus.Available || Prefs.Get("update_announced", "") == result.Version) return;
		Prefs.Set("update_announced", result.Version ?? "");
		var open = await Dispatcher.UIThread.InvokeAsync(() =>
			Ui.ConfirmAsync(Loc.T("set_update_title"), Loc.T("set_update_available", result.Version ?? "", result.Date ?? ""), Loc.T("set_update_download"), Loc.T("cancel")));
		if (open && result.DownloadUrl is { } url) Open(url);
	}
}
