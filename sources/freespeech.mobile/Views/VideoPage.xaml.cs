/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Views;

[QueryProperty(nameof(Path), "path")]
public partial class VideoPage : ContentPage
{
	public VideoPage() => InitializeComponent();

	public string Path
	{
		set => Player.Source = Uri.UnescapeDataString(value);
	}

	private async void OnClose(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}