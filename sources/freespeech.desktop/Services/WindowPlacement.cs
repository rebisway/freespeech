/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// Remembers where the main window was (position, size, maximized) and puts it back at the next start. A saved place that is on no
/// screen any more (a second monitor that was unplugged) is not used: the window comes back on the primary screen.
/// </summary>
public static class WindowPlacement
{
	private const int MinVisibleWidth = 100;   // pixels of the title strip that must show on a screen
	private const int TitleStrip = 40;

	private sealed record Saved(int X, int Y, double Width, double Height, bool Maximized);

	private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", Options.Profile, "window.json");

	/// <summary>Puts the window back where it was, or leaves its default place when nothing was saved.</summary>
	public static void Restore(Window window)
	{
		var saved = Load();
		if (saved == null) return;
		window.WindowStartupLocation = WindowStartupLocation.Manual;
		window.Width = Math.Max(window.MinWidth, saved.Width);
		window.Height = Math.Max(window.MinHeight, saved.Height);
		window.Position = new PixelPoint(saved.X, saved.Y);
		EnsureVisible(window);
		if (saved.Maximized) window.WindowState = WindowState.Maximized;
	}

	/// <summary>Saves the place whenever it changes (a moment after the last change), and at close.</summary>
	public static void Attach(Window window)
	{
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		timer.Tick += (_, _) => { timer.Stop(); Save(window); };
		void Changed() { timer.Stop(); timer.Start(); }
		window.PositionChanged += (_, _) => Changed();
		window.SizeChanged += (_, _) => Changed();
		window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Changed(); };
		window.Closing += (_, _) => Save(window);
	}

	/// <summary>If the window is on no screen (or only a sliver of it), moves it to the middle of the primary screen, smaller if it does not fit.</summary>
	public static void EnsureVisible(Window window)
	{
		var screens = window.Screens;
		if (screens == null || screens.All.Count == 0) return;
		var scale = window.RenderScaling;
		var size = new PixelSize((int)(window.Width * scale), (int)(window.Height * scale));
		var strip = new PixelRect(window.Position.X, window.Position.Y, size.Width, Math.Min(TitleStrip, size.Height));
		foreach (var screen in screens.All)
		{
			var shown = screen.WorkingArea.Intersect(strip);
			if (shown.Width >= Math.Min(MinVisibleWidth, size.Width) && shown.Height > 0) return;
		}

		var area = (screens.Primary ?? screens.All[0]).WorkingArea;
		var width = Math.Min(size.Width, area.Width);
		var height = Math.Min(size.Height, area.Height);
		window.Width = width / scale;
		window.Height = height / scale;
		window.Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
	}

	private static Saved? Load()
	{
		try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) : null; }
		catch (Exception ex) when (ex is IOException or JsonException) { return null; }
	}

	private static void Save(Window window)
	{
		// A minimized or hidden window has no meaningful place; a maximized one keeps the normal place it had before.
		if (window.WindowState == WindowState.Minimized || !window.IsVisible) return;
		try
		{
			var maximized = window.WindowState == WindowState.Maximized;
			var previous = Load();
			var normal = maximized && previous != null
				? previous with { Maximized = true }
				: new Saved(window.Position.X, window.Position.Y, window.Width, window.Height, maximized);
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(normal));
		}
		catch (IOException) { /* the place is a convenience: not saved this time */ }
	}
}
