/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;
#if ANDROID
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
#endif

namespace FreeSpeech.Mobile.Views;

/// <summary>Camera page that reads a contact QR code and hands the text to the invite page. Built in code: the camera view exists on Android only.</summary>
public class ScanPage : ContentPage
{
	private bool _done;

	public ScanPage()
	{
		Title = Loc.T("scan_title");
		Shell.SetFlyoutBehavior(this, FlyoutBehavior.Disabled);
#if ANDROID
		var reader = new CameraBarcodeReaderView
		{
			Options = new BarcodeReaderOptions { Formats = BarcodeFormats.TwoDimensional, AutoRotate = true, Multiple = false },
			IsDetecting = true,
		};
		reader.BarcodesDetected += OnDetected;
		Content = reader;
#else
		Content = new Label { Text = Loc.T("scan_unavailable"), Margin = 24, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
#endif
	}

#if ANDROID
	private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
	{
		var text = e.Results?.FirstOrDefault()?.Value;
		if (_done || string.IsNullOrWhiteSpace(text)) return;
		_done = true;
		ScanHub.Result = text;
		MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync(".."));
	}
#endif
}
