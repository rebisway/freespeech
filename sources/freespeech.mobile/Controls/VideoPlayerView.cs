/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Controls;

/// <summary>Plays the video file at <see cref="Source"/> with the platform's own player (Android: VideoView, with play/pause and a seek bar). The handler is in Platforms.</summary>
public class VideoPlayerView : View
{
	public static readonly BindableProperty SourceProperty = BindableProperty.Create(nameof(Source), typeof(string), typeof(VideoPlayerView));

	public string? Source
	{
		get => (string?)GetValue(SourceProperty);
		set => SetValue(SourceProperty, value);
	}
}