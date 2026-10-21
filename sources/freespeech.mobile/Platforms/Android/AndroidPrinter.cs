/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using Android.Content;
using Android.Print;
using AndroidWebView = Android.Webkit.WebView;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Prints through Android's print framework: the page is drawn by an off-screen WebView and handed to the print dialog. Nothing is written to a file by the app.</summary>
public sealed class AndroidPrinter : IPrinter
{
	private static readonly List<AndroidWebView> Alive = [];   // a WebView that is garbage collected before the dialog is done prints nothing

	public Task PrintAsync(string jobName, string html)
	{
		var done = new TaskCompletionSource();
		MainThread.BeginInvokeOnMainThread(() =>
		{
			var activity = Platform.CurrentActivity;
			if (activity == null) { done.TrySetException(new InvalidOperationException("No activity to print from.")); return; }
			var web = new AndroidWebView(activity);
			Alive.Add(web);
			web.SetWebViewClient(new PrintClient(jobName, done, web));
			web.LoadDataWithBaseURL(null, html, "text/html", "UTF-8", null);
		});
		return done.Task;
	}

	private sealed class PrintClient(string jobName, TaskCompletionSource done, AndroidWebView web) : Android.Webkit.WebViewClient
	{
		public override void OnPageFinished(AndroidWebView? view, string? url)
		{
			try
			{
				if (view?.Context?.GetSystemService(Context.PrintService) is PrintManager manager)
					manager.Print(jobName, view.CreatePrintDocumentAdapter(jobName), null);
				done.TrySetResult();
			}
			catch (Exception ex) { done.TrySetException(ex); }
			// The print dialog holds the adapter for a while: the WebView is let go a minute later.
			_ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => Alive.Remove(web));
		}
	}
}