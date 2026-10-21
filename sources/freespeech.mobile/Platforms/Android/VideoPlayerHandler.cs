/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.Widget;
using FreeSpeech.Mobile.Controls;
using Microsoft.Maui.Handlers;

namespace FreeSpeech.Mobile;

public sealed class VideoPlayerHandler() : ViewHandler<VideoPlayerView, VideoView>(Mapper)
{
	public static readonly IPropertyMapper<VideoPlayerView, VideoPlayerHandler> Mapper = new PropertyMapper<VideoPlayerView, VideoPlayerHandler>(ViewMapper)
	{
		[nameof(VideoPlayerView.Source)] = (handler, view) =>
		{
			if (string.IsNullOrEmpty(view.Source)) return;
			handler.PlatformView.SetVideoPath(view.Source);
			handler.PlatformView.Start();
		},
	};

	protected override VideoView CreatePlatformView()
	{
		var video = new VideoView(Context);
		var controls = new MediaController(Context);
		controls.SetAnchorView(video);
		video.SetMediaController(controls);
		return video;
	}

	protected override void DisconnectHandler(VideoView platformView)
	{
		platformView.StopPlayback();
		base.DisconnectHandler(platformView);
	}
}