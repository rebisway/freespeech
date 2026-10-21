/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.Graphics;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Makes the small picture shown in a chat bubble: the file is decoded already reduced (a power of two, so a 1,600 px photo is never held at
/// full size), turned upright, scaled to <see cref="ThumbnailStore.MaxSide"/> and written as a JPEG of a few tens of KB. A picture with
/// transparency is put on white first, as a JPEG has none.
/// </summary>
public sealed class AndroidPictureThumbnailer : IPictureThumbnailer
{
	private const int JpegQuality = 75;

	/// <summary>The first frame of a video, reduced the same way.</summary>
	public PictureThumbnail? MakeVideo(string path)
	{
		try
		{
			using var retriever = new Android.Media.MediaMetadataRetriever();
			retriever.SetDataSource(path);
			using var frame = retriever.GetFrameAtTime(0, Android.Media.Option.ClosestSync);
			if (frame == null) return null;

			var scale = Math.Min(1.0, (double)ThumbnailStore.MaxSide / Math.Max(frame.Width, frame.Height));
			using var small = scale < 1 ? Bitmap.CreateScaledBitmap(frame, (int)(frame.Width * scale), (int)(frame.Height * scale), true)! : frame;
			using var stream = new MemoryStream();
			small.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, stream);
			return new PictureThumbnail(stream.ToArray(), small.Width, small.Height);
		}
		catch (Exception ex) when (ex is IOException or Java.Lang.Exception) { return null; }
	}

	public PictureThumbnail? Make(string path)
	{
		try
		{
			var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
			BitmapFactory.DecodeFile(path, bounds);
			if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

			var sample = 1;
			while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= ThumbnailStore.MaxSide) sample *= 2;
			using var decoded = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
			if (decoded == null) return null;

			var matrix = new Matrix();
			switch (ImageCompressor.Orientation(path))
			{
				case 2: matrix.PostScale(-1, 1); break;
				case 3: matrix.PostRotate(180); break;
				case 4: matrix.PostScale(1, -1); break;
				case 5: matrix.PostRotate(90); matrix.PostScale(-1, 1); break;
				case 6: matrix.PostRotate(90); break;
				case 7: matrix.PostRotate(270); matrix.PostScale(-1, 1); break;
				case 8: matrix.PostRotate(270); break;
			}
			var scale = Math.Min(1f, (float)ThumbnailStore.MaxSide / Math.Max(decoded.Width, decoded.Height));
			matrix.PostScale(scale, scale);
			using var turned = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true)!;

			using var flat = Bitmap.CreateBitmap(turned.Width, turned.Height, Bitmap.Config.Argb8888!)!;
			using (var canvas = new Canvas(flat))
			{
				canvas.DrawColor(Android.Graphics.Color.White);
				canvas.DrawBitmap(turned, 0, 0, null);
			}

			using var stream = new MemoryStream();
			flat.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, stream);
			return new PictureThumbnail(stream.ToArray(), flat.Width, flat.Height);
		}
		catch (Exception ex) when (ex is IOException or Java.Lang.Exception) { return null; }
	}
}
