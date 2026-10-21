/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.Graphics;
using Android.Media;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Shrinks a picture before it is sent: a phone photo is 3-8 MB, a 1600 px JPEG at quality 80 is a few hundred KB and looks
/// the same on a phone screen. The picture is turned upright (its EXIF flag is dropped), pictures with transparency stay PNG.
/// </summary>
public static class ImageCompressor
{
	public const int MaxSide = 1600;
	private const int JpegQuality = 80;

	/// <summary>Writes the reduced copy next to the source and returns its path, or null when the picture cannot be read.</summary>
	public static string? Shrink(string inputPath)
	{
		try
		{
			var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
			BitmapFactory.DecodeFile(inputPath, bounds);
			if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

			// Decode already reduced (a power of two) so a 12-megapixel photo never sits in memory at full size.
			var sample = 1;
			while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxSide) sample *= 2;
			using var decoded = BitmapFactory.DecodeFile(inputPath, new BitmapFactory.Options { InSampleSize = sample });
			if (decoded == null) return null;

			var matrix = new Matrix();
			switch (Orientation(inputPath))
			{
				case 2: matrix.PostScale(-1, 1); break;
				case 3: matrix.PostRotate(180); break;
				case 4: matrix.PostScale(1, -1); break;
				case 5: matrix.PostRotate(90); matrix.PostScale(-1, 1); break;
				case 6: matrix.PostRotate(90); break;
				case 7: matrix.PostRotate(270); matrix.PostScale(-1, 1); break;
				case 8: matrix.PostRotate(270); break;
			}
			var scale = Math.Min(1f, (float)MaxSide / Math.Max(decoded.Width, decoded.Height));
			matrix.PostScale(scale, scale);
			using var result = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true)!;

			var png = decoded.HasAlpha;
			var output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(inputPath)!, System.IO.Path.GetFileNameWithoutExtension(inputPath) + (png ? "_small.png" : "_small.jpg"));
			using (var file = File.Create(output))
				result.Compress(png ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!, JpegQuality, file);
			return output;
		}
		catch (Exception ex) when (ex is IOException or Java.Lang.Exception) { return null; }
	}

	/// <summary>The EXIF orientation of a picture file (1 = upright).</summary>
	internal static int Orientation(string path)
	{
		using var exif = new ExifInterface(path);
		return exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
	}
}
