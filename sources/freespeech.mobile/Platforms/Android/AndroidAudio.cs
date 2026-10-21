/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.Media;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile;

/// <summary>Voice messages: mono AAC at 32 kbit/s (about 240 KB a minute), in an .m4a.</summary>
public sealed class AndroidAudioRecorder : IAudioRecorder
{
	private MediaRecorder? _recorder;
	private string? _path;

	public async Task<bool> StartAsync(string path)
	{
		var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
		if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Microphone>();
		if (status != PermissionStatus.Granted) return false;

		Release();
		var ctx = Android.App.Application.Context;
		var recorder = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(ctx) : new MediaRecorder();
		try
		{
			recorder.SetAudioSource(AudioSource.Mic);
			recorder.SetOutputFormat(OutputFormat.Mpeg4);
			recorder.SetAudioEncoder(AudioEncoder.Aac);
			recorder.SetAudioChannels(1);
			recorder.SetAudioSamplingRate(16000);
			recorder.SetAudioEncodingBitRate(32000);
			recorder.SetOutputFile(path);
			recorder.Prepare();
			recorder.Start();
		}
		catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.IO.IOException or Java.Lang.RuntimeException)
		{
			recorder.Release();
			return false;
		}
		(_recorder, _path) = (recorder, path);
		return true;
	}

	public void Stop() => Release();

	public void Cancel()
	{
		var path = _path;
		Release();
		if (path != null) try { File.Delete(path); } catch (IOException) { }
	}

	private void Release()
	{
		var r = _recorder;
		_recorder = null;
		if (r == null) return;
		try { r.Stop(); }
		catch (Java.Lang.RuntimeException) { /* stopped at once: nothing was recorded */ }
		r.Release();
	}
}

public sealed class AndroidVideoThumbnailer : IVideoThumbnailer
{
	public string? ThumbnailOf(string videoPath)
	{
		try
		{
			var dir = Path.Combine(FileSystem.CacheDirectory, "thumbs");
			Directory.CreateDirectory(dir);
			var info = new FileInfo(videoPath);
			var target = Path.Combine(dir, $"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(videoPath + info.Length)))}.jpg");
			if (File.Exists(target)) return target;

			using var retriever = new MediaMetadataRetriever();
			retriever.SetDataSource(videoPath);
			using var frame = retriever.GetFrameAtTime(0, Option.ClosestSync);
			if (frame == null) return null;

			// A bubble only needs a small picture.
			var scale = Math.Min(1.0, 480.0 / Math.Max(frame.Width, frame.Height));
			using var small = scale < 1 ? Android.Graphics.Bitmap.CreateScaledBitmap(frame, (int)(frame.Width * scale), (int)(frame.Height * scale), true)! : frame;
			using var file = File.Create(target);
			small.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 80, file);
			return target;
		}
		catch (Exception ex) when (ex is Java.Lang.RuntimeException or Java.IO.IOException or IOException) { return null; }
	}
}

public sealed class AndroidAudioPlayer : IAudioPlayer
{
	private MediaPlayer? _player;
	private Action? _done;

	public void Play(string path, Action done)
	{
		Stop();
		var player = new MediaPlayer();
		try
		{
			player.SetDataSource(path);
			player.Prepare();
		}
		catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.IllegalStateException or Java.Lang.RuntimeException)
		{
			player.Release();
			done();
			return;
		}
		player.Completion += (_, _) => Stop();
		(_player, _done) = (player, done);
		player.Start();
	}

	public void Stop()
	{
		var (p, done) = (_player, _done);
		(_player, _done) = (null, null);
		if (p == null) return;
		try { p.Stop(); } catch (Java.Lang.IllegalStateException) { }
		p.Release();
		done?.Invoke();
	}

	public TimeSpan? DurationOf(string path)
	{
		try
		{
			using var r = new MediaMetadataRetriever();
			r.SetDataSource(path);
			return long.TryParse(r.ExtractMetadata(MetadataKey.Duration), out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
		}
		catch (Exception ex) when (ex is Java.Lang.RuntimeException or Java.IO.IOException) { return null; }
	}
}
