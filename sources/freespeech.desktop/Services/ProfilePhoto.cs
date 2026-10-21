/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using SkiaSharp;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The profile picture must stay small (the library accepts 100 KiB at most): the picture chosen is cut to a centred square and
/// reduced to a 256 px JPEG, with a lower quality if it is still too heavy. A picture with transparency gets a white background.
/// </summary>
public static class ProfilePhoto
{
	private const int Side = 256;
	private const int MaxBytes = 90 * 1024;   // under the library's 100 KiB, with some room

	/// <summary>Writes the reduced picture to a temporary file and returns its path, or null when the file is not a picture.</summary>
	public static string? Prepare(string path)
	{
		using var source = SKBitmap.Decode(path);
		if (source == null) return null;

		var side = Math.Min(source.Width, source.Height);
		var crop = new SKRect((source.Width - side) / 2f, (source.Height - side) / 2f, (source.Width + side) / 2f, (source.Height + side) / 2f);
		using var surface = SKSurface.Create(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul));
		surface.Canvas.Clear(SKColors.White);
		using var image = SKImage.FromBitmap(source);
		surface.Canvas.DrawImage(image, crop, new SKRect(0, 0, Side, Side), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
		using var square = surface.Snapshot();

		byte[]? bytes = null;
		for (var quality = 92; quality >= 40; quality -= 12)
		{
			using var data = square.Encode(SKEncodedImageFormat.Jpeg, quality);
			bytes = data.ToArray();
			if (bytes.Length <= MaxBytes) break;
		}
		var temp = Path.Combine(Path.GetTempPath(), $"freespeech-photo-{Guid.NewGuid():N}.jpg");
		File.WriteAllBytes(temp, bytes!);
		return temp;
	}
}