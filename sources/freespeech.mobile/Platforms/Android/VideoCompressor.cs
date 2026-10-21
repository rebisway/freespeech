/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.App;
using AndroidX.Media3.Common;
using AndroidX.Media3.Effect;
using AndroidX.Media3.Transformer;
using AndroidX.Media3.Common.Audio;

namespace FreeSpeech.Mobile.Services;

/// <summary>Android video compression: re-encodes to H.264 and shrinks to at most <see cref="MaxHeight"/> pixels high (Media3 Transformer, hardware codecs).</summary>
public static class VideoCompressor
{
	public const int MaxHeight = 720;

	/// <summary>Writes the compressed copy next to the source and returns its path. Throws <see cref="IOException"/> if the phone cannot do it.</summary>
	public static async Task<string> CompressAsync(string inputPath, CancellationToken cancel = default)
	{
		var output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(inputPath)!, System.IO.Path.GetFileNameWithoutExtension(inputPath) + "_small.mp4");
		if (File.Exists(output)) File.Delete(output);

		var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			var ctx = Platform.CurrentActivity ?? (Android.Content.Context)Android.App.Application.Context;
			var listener = new Listener(done);
			var transformer = new Transformer.Builder(ctx)
				.SetVideoMimeType(MimeTypes.VideoH264)
				.AddListener(listener)!
				.Build()!;

			var effects = new Effects(new List<IAudioProcessor>(), new List<IEffect> { AndroidX.Media3.Effect.Presentation.CreateForHeight(MaxHeight)! });
			var edited = new EditedMediaItem.Builder(MediaItem.FromUri(Android.Net.Uri.FromFile(new Java.IO.File(inputPath))!)!).SetEffects(effects)!.Build()!;
			transformer.Start(edited, output);
			cancel.Register(() => MainThread.BeginInvokeOnMainThread(() => transformer.Cancel()));
		});

		try { await done.Task; }
		catch (Exception ex) { throw new IOException("Video compression failed: " + ex.Message, ex); }
		return output;
	}

	private sealed class Listener(TaskCompletionSource<bool> done) : Java.Lang.Object, Transformer.IListener
	{
		public void OnCompleted(Composition? composition, ExportResult? exportResult) => done.TrySetResult(true);
		public void OnError(Composition? composition, ExportResult? exportResult, ExportException? exportException) => done.TrySetException(new IOException(exportException?.Message ?? "unknown"));
	}
}