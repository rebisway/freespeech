/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using System.Text;
using Android.Graphics;
using FreeSpeech.Mobile.Services;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using Rect = Android.Graphics.Rect;

namespace FreeSpeech.Mobile;

/// <summary>Draws an album mosaic with Android's canvas: each picture cropped to fill its cell, "+N" on the last one.</summary>
public sealed class AndroidCollage : ICollage
{
	public string? Make(IReadOnlyList<string> paths, string overlay, double widthDp, double heightDp, IReadOnlyList<(double X, double Y, double W, double H)> cells, uint background)
	{
		try
		{
			var density = Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 2.5f;
			var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join('|', paths) + "#" + overlay + "#" + widthDp + "x" + heightDp + "#" + background)));
			var dir = System.IO.Path.Combine(FileSystem.CacheDirectory, "albums");
			Directory.CreateDirectory(dir);
			var file = System.IO.Path.Combine(dir, key + ".jpg");
			if (File.Exists(file)) return file;

			using var mosaic = Bitmap.CreateBitmap((int)(widthDp * density), (int)(heightDp * density), Bitmap.Config.Argb8888!)!;
			using var canvas = new Canvas(mosaic);
			canvas.DrawColor(new Color((int)background));

			for (var i = 0; i < cells.Count && i < paths.Count; i++)
			{
				var (cx, cy, cw, ch) = cells[i];
				var dst = new Rect((int)(cx * density), (int)(cy * density), (int)((cx + cw) * density), (int)((cy + ch) * density));
				using var photo = Decode(paths[i], dst.Width(), dst.Height());
				if (photo == null) continue;

				// Centre-crop: the part of the picture with the cell's proportions.
				var cellRatio = (double)dst.Width() / dst.Height();
				var photoRatio = (double)photo.Width / photo.Height;
				Rect src = photoRatio > cellRatio
					? new Rect((int)((photo.Width - photo.Height * cellRatio) / 2), 0, (int)((photo.Width + photo.Height * cellRatio) / 2), photo.Height)
					: new Rect(0, (int)((photo.Height - photo.Width / cellRatio) / 2), photo.Width, (int)((photo.Height + photo.Width / cellRatio) / 2));
				canvas.DrawBitmap(photo, src, dst, new Paint(PaintFlags.FilterBitmap));

				if (overlay.Length > 0 && i == cells.Count - 1)
				{
					canvas.DrawRect(dst, new Paint { Color = new Color(0x99, 0, 0, 0) });
					var text = new Paint(PaintFlags.AntiAlias) { Color = Color.White, TextSize = 30 * density, TextAlign = Paint.Align.Center };
					text.SetTypeface(Typeface.DefaultBold);
					canvas.DrawText(overlay, dst.CenterX(), dst.CenterY() + text.TextSize / 3, text);
				}
			}

			using var output = File.Create(file);
			mosaic.Compress(Bitmap.CompressFormat.Jpeg!, 88, output);
			return file;
		}
		catch (Exception ex) when (ex is IOException or Java.Lang.Exception or ArgumentException or UnauthorizedAccessException)
		{
			System.Diagnostics.Debug.WriteLine($"Album mosaic failed: {ex.Message}");
			return null;
		}
	}

	/// <summary>Reads a picture no bigger than needed for the cell (power-of-two sub-sampling), so a few large photos do not fill the memory.</summary>
	private static Bitmap? Decode(string path, int wantedW, int wantedH)
	{
		var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeFile(path, bounds);
		var sample = 1;
		while (bounds.OutWidth / (sample * 2) >= wantedW && bounds.OutHeight / (sample * 2) >= wantedH) sample *= 2;
		return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
	}
}