/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Services;

/// <summary>One picture of an album mosaic: where it sits (in dp, from the top left of the mosaic) and the message it belongs to.</summary>
public sealed record AlbumCell(double X, double Y, double W, double H, ChatItem Item);

/// <summary>An album as the chat shows it: one pre-drawn image (the mosaic), its size on screen, and where each picture sits in it.</summary>
public sealed record AlbumInfo(string Key, string Image, double Width, double Height, IReadOnlyList<AlbumCell> Cells);

/// <summary>Draws an album mosaic into a single image file (cached): lighter to show than several pictures, and it cannot overflow the bubble.</summary>
public interface ICollage
{
	/// <param name="overlay">Text laid over the last cell ("+3"), or empty.</param>
	/// <param name="background">ARGB of the bubble, visible in the gaps between pictures.</param>
	string? Make(IReadOnlyList<string> paths, string overlay, double widthDp, double heightDp, IReadOnlyList<(double X, double Y, double W, double H)> cells, uint background);
}

/// <summary>The mosaic layout: three pictures = one large on the left and two stacked on the right; four = two by two.</summary>
public static class AlbumLayout
{
	public const double Gap = 2;

	/// <summary>An album is never taller than a third of the screen, nor wider than the bubble.</summary>
	public static (double W, double H) Size()
	{
		var info = DeviceDisplay.Current.MainDisplayInfo;
		var screenHeightDp = info.Density > 0 ? info.Height / info.Density : 800;
		return (288, Math.Min(216, screenHeightDp / 3));
	}

	public static List<(double X, double Y, double W, double H)> Cells(int shown, double w, double h)
	{
		if (shown <= 3)
		{
			var left = w * 0.62;
			var right = w - left - Gap;
			var half = (h - Gap) / 2;
			return [(0, 0, left, h), (left + Gap, 0, right, half), (left + Gap, half + Gap, right, half)];
		}
		var cw = (w - Gap) / 2;
		var ch = (h - Gap) / 2;
		return [(0, 0, cw, ch), (cw + Gap, 0, cw, ch), (0, ch + Gap, cw, ch), (cw + Gap, ch + Gap, cw, ch)];
	}
}