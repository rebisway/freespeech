/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using SkiaSharp;

namespace FreeSpeech.Desktop.Services;

/// <summary>A picture is reduced to 1600 px (JPEG) before it waits under the composer: never sent heavier than needed. Animated GIFs are left alone (re-encoding would keep one frame).</summary>
public static class ImageShrink
{
	private const int MaxSide = 1600;
	private static readonly string[] Photos = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

	/// <summary>The path of the reduced copy (a temporary file), or null when the file is not a picture to reduce or is already small.</summary>
	public static string? Shrink(string path)
	{
		if (!Photos.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
		try
		{
			using var source = SKBitmap.Decode(path);
			if (source == null || Math.Max(source.Width, source.Height) <= MaxSide) return null;
			var scale = MaxSide / (double)Math.Max(source.Width, source.Height);
			using var resized = source.Resize(new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
			if (resized == null) return null;
			using var image = SKImage.FromBitmap(resized);
			using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
			var target = Path.Combine(Path.GetTempPath(), $"freespeech-send-{Guid.NewGuid():N}.jpg");
			File.WriteAllBytes(target, data.ToArray());
			return target;
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
	}
}
