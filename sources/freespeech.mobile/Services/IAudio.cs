/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Services;

/// <summary>Records a voice message to a file (small AAC in an .m4a). Implemented per platform.</summary>
public interface IAudioRecorder
{
	/// <summary>Starts recording into <paramref name="path"/>. False when the microphone is not allowed.</summary>
	Task<bool> StartAsync(string path);

	/// <summary>Stops and keeps the file.</summary>
	void Stop();

	/// <summary>Stops and drops what was recorded.</summary>
	void Cancel();
}

/// <summary>First frame of a video, as a small JPEG, for the bubble. Implemented per platform.</summary>
public interface IVideoThumbnailer
{
	/// <summary>Path of the thumbnail (made once, then reused), or null when the frame cannot be read.</summary>
	string? ThumbnailOf(string videoPath);
}

/// <summary>Plays one audio file at a time (a voice message received, or the one just recorded).</summary>
public interface IAudioPlayer
{
	/// <summary>Plays <paramref name="path"/>, stopping any other. <paramref name="done"/> is called when it ends or is stopped.</summary>
	void Play(string path, Action done);

	void Stop();

	/// <summary>Length of the audio file, or null when it cannot be read.</summary>
	TimeSpan? DurationOf(string path);
}
