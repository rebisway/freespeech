/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The colour theme: dark (the Rebis look), light, or the system's. The colours are resources of the application that every control
/// reads dynamically, so a change shows at once. The palettes are the mobile app's.
/// </summary>
public static class ThemeService
{
	public const string Dark = "dark", Light = "light", System = "system";

	private static readonly Dictionary<string, string> DarkColors = new()
	{
		["Primary"] = "#C89A4B", ["PrimaryHover"] = "#D9AC5E", ["OnPrimary"] = "#16130F", ["Bg"] = "#16130F", ["Surface"] = "#1C1812", ["Raised"] = "#2B2620",
		["MineBubble"] = "#2A2318", ["MineEdge"] = "#73C89A4B", ["Text"] = "#EDE7DD", ["Muted"] = "#9C9187", ["Line"] = "#3A342E", ["Danger"] = "#C4584B",
		["CardEdge"] = "#8A6D3A", ["Hover"] = "#241F18", ["AvatarBg"] = "#3A342E", ["GroupBg"] = "#2A2318",
	};

	private static readonly Dictionary<string, string> LightColors = new()
	{
		["Primary"] = "#9A6B1F", ["PrimaryHover"] = "#B07C2A", ["OnPrimary"] = "#FFFFFF", ["Bg"] = "#F7F3EB", ["Surface"] = "#EFE9DD", ["Raised"] = "#E8E1D3",
		["MineBubble"] = "#EBD9AE", ["MineEdge"] = "#739A6B1F", ["Text"] = "#2A251F", ["Muted"] = "#6B645B", ["Line"] = "#D9D0C0", ["Danger"] = "#B23B2E",
		["CardEdge"] = "#C9A867", ["Hover"] = "#E4DDCD", ["AvatarBg"] = "#D9D0C0", ["GroupBg"] = "#EBD9AE",
	};

	private static bool _watching;

	public static string Mode
	{
		get => Prefs.Get("theme", Dark);
		set { Prefs.Set("theme", value); Apply(); }
	}

	/// <summary>True when the light palette is in use: chosen, or "system" on a system set to light.</summary>
	public static bool IsLight => Mode == Light || (Mode == System && Application.Current?.ActualThemeVariant == ThemeVariant.Light);

	/// <summary>The flag of the title bar and of the side menu (the black one on the light theme).</summary>
	public static Bitmap FlagImage => new(AssetLoader.Open(new Uri($"avares://FreeSpeech/Assets/{(IsLight ? "logo_flag_light" : "logo_flag")}.png")));

	/// <summary>Raised after the palette changed (the pictures that depend on it are read again).</summary>
	public static event Action? Changed;

	/// <summary>Sets the palette: call at start and after each change.</summary>
	public static void Apply()
	{
		if (Application.Current is not { } app) return;
		app.RequestedThemeVariant = Mode switch { Light => ThemeVariant.Light, Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
		if (!_watching) { app.ActualThemeVariantChanged += (_, _) => { if (Mode == System) Apply(); }; _watching = true; }

		foreach (var (key, hex) in IsLight ? LightColors : DarkColors)
		{
			var color = Color.Parse(hex);
			app.Resources[key] = color;
			app.Resources[key + "Brush"] = new SolidColorBrush(color);
		}
		app.Resources["SystemAccentColor"] = Color.Parse(IsLight ? LightColors["Primary"] : DarkColors["Primary"]);
		Changed?.Invoke();
	}
}