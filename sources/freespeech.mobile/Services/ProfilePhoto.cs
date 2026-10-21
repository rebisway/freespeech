/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Turns a picture from the phone into a profile photo. The picture travels inside the contact card, so it must be small
/// (the library accepts 100 KiB at most): the part the user framed is cut out and reduced to a 256 px JPEG, with a lower
/// quality if it is still too heavy.
/// </summary>
public static class ProfilePhoto
{
	private const int Side = 256;
	private const int MaxBytes = 90 * 1024;   // under the library's 100 KiB, with some room

	/// <summary>The picked picture waiting to be framed (set by the settings screen, read by the crop screen).</summary>
	public static string? PendingPath { get; set; }

	/// <summary>
	/// What to do with the framed picture (a file): set by the screen that asked for it (a group's picture, my picture in a group).
	/// Null: it becomes my own profile picture.
	/// </summary>
	public static Func<string, Task>? OnFramed { get; set; }

	/// <summary>Copies a picked picture to the cache folder, where the crop screen reads it again and again.</summary>
	public static async Task<string> StageAsync(FileResult picked)
	{
		// A new name each time: the image control caches by path, and a reused name showed the first picture again.
		foreach (var old in Directory.GetFiles(FileSystem.CacheDirectory, "photo-to-crop*")) try { File.Delete(old); } catch (IOException) { }
		var path = Path.Combine(FileSystem.CacheDirectory, $"photo-to-crop-{Guid.NewGuid():N}" + Path.GetExtension(picked.FileName));
		await using var input = await picked.OpenReadAsync();
		await using var output = File.Create(path);
		await input.CopyToAsync(output);
		output.Close();
		NormalizeOrientation(path);
		return path;
	}

	/// <summary>
	/// A phone photo is often stored sideways with a flag saying "turn it". The picture is shown turned but its pixels are not,
	/// which stretched it in the frame and cut the wrong part: the staged copy is turned for real and the flag is dropped.
	/// </summary>
	private static void NormalizeOrientation(string path)
	{
#if ANDROID
		try
		{
			var orientation = ExifOrientation(path);
			if (orientation <= 1) return;

			var m = new Android.Graphics.Matrix();
			switch (orientation)
			{
				case 2: m.PostScale(-1, 1); break;
				case 3: m.PostRotate(180); break;
				case 4: m.PostScale(1, -1); break;
				case 5: m.PostRotate(90); m.PostScale(-1, 1); break;
				case 6: m.PostRotate(90); break;
				case 7: m.PostRotate(270); m.PostScale(-1, 1); break;
				case 8: m.PostRotate(270); break;
				default: return;
			}
			using var source = Android.Graphics.BitmapFactory.DecodeFile(path);
			if (source == null) return;
			using var turned = Android.Graphics.Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, m, true);
			using var file = File.Create(path);
			turned.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 92, file);
		}
		catch (Exception ex) when (ex is IOException or Java.Lang.Exception) { /* keep the picture as it is */ }
#endif
	}

#if ANDROID
	private static int ExifOrientation(string path)
	{
		using var exif = new Android.Media.ExifInterface(path);
		return exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1);
	}
#endif

	/// <summary>Pixel size of a picture as it is shown (turned upright when its file says so).</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (float Width, float Height)> _sizes = new();

	/// <summary>The size of a picture on disk (read once: a line rebuilt while the user scrolls does not open the file again).</summary>
	public static (float Width, float Height) SizeOf(string path) => _sizes.GetOrAdd(path, ReadSize);

	private static (float Width, float Height) ReadSize(string path)
	{
#if ANDROID
		var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
		Android.Graphics.BitmapFactory.DecodeFile(path, bounds);
		if (bounds.OutWidth > 0)
		{
			var sideways = ExifOrientation(path) is >= 5 and <= 8;
			return sideways ? (bounds.OutHeight, bounds.OutWidth) : (bounds.OutWidth, bounds.OutHeight);
		}
#endif
		using var stream = File.OpenRead(path);
		using var image = PlatformImage.FromStream(stream);
		return (image.Width, image.Height);
	}

#if !WINDOWS
	/// <summary>Whether the picture has an alpha channel (a logo, a drawing): a JPEG would put white behind it.</summary>
	private static bool HasTransparency(string path)
	{
#if ANDROID
		var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = 8 };   // a small copy is enough to know
		using var bitmap = Android.Graphics.BitmapFactory.DecodeFile(path, options);
		return bitmap?.HasAlpha ?? false;
#else
		return false;
#endif
	}

	private static byte[] Render(Microsoft.Maui.Graphics.IImage source, float x, float y, float side, int size, bool white, ImageFormat format, float quality)
	{
		using var context = new PlatformBitmapExportContext(size, size);
		var canvas = context.Canvas;
		if (white)
		{
			canvas.FillColor = Colors.White;
			canvas.FillRectangle(0, 0, size, size);
		}
		canvas.SaveState();
		canvas.Scale(size / side, size / side);
		canvas.Translate(-x, -y);
		canvas.DrawImage(source, 0, 0, source.Width, source.Height);
		canvas.RestoreState();
		return context.Image.AsBytes(format, quality);
	}
#endif

	/// <summary>Cuts the square (<paramref name="x"/>, <paramref name="y"/>, <paramref name="side"/>, in source pixels) out of the picture; returns the path of the resulting PNG (small, keeps transparency) or JPEG (photos).</summary>
	public static async Task<string> CropAsync(string path, float x, float y, float side)
	{
#if WINDOWS
		await Task.CompletedTask;
		throw new PlatformNotSupportedException("Cropping a profile photo is implemented for Android and iOS (the Windows target only serves to iterate on the screens).");
#else
		return await Task.Run(async () =>
		{
			using var stream = File.OpenRead(path);
			using var source = PlatformImage.FromStream(stream);

			// A logo or drawing with transparent areas must stay transparent, so it stays a PNG: if it is too heavy at 256 px it is
			// made smaller (192, 128, 96 px) until it fits. A photo has no transparency and is a JPEG (lighter at this size).
			if (HasTransparency(path))
			{
				foreach (var size in new[] { Side, 192, 128, 96 })
				{
					var png = Render(source, x, y, side, size, white: false, format: ImageFormat.Png, quality: 1f);
					if (png.Length > MaxBytes) continue;
					var pngPath = Path.Combine(FileSystem.CacheDirectory, "profile-photo.png");
					await File.WriteAllBytesAsync(pngPath, png);
					return pngPath;
				}
			}

			byte[]? best = null;
			foreach (var quality in new[] { 0.85f, 0.7f, 0.5f, 0.3f })
			{
				best = Render(source, x, y, side, Side, white: true, format: ImageFormat.Jpeg, quality);
				if (best.Length <= MaxBytes) break;
			}

			var result = Path.Combine(FileSystem.CacheDirectory, "profile-photo.jpg");
			await File.WriteAllBytesAsync(result, best!);
			return result;
		});
#endif
	}
}
