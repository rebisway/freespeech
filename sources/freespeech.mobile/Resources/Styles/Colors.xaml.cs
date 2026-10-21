/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Resources.Styles;

/// <summary>
/// The colours. Colors.xaml is the dark palette; when the user chose the light theme, the same keys are given their
/// light values here, right after loading and before the styles (which read them) are parsed. That is why a change of
/// theme takes effect at the next start.
/// </summary>
public partial class Palette : ResourceDictionary
{
	public Palette()
	{
		InitializeComponent();
		if (Services.ThemeService.IsLight) ApplyLight();
	}

	private static readonly Dictionary<string, string> Light = new()
	{
		["Primary"] = "#9A6B1F", ["PrimaryDark"] = "#C89A4B", ["PrimaryDarkText"] = "#16130F",
		["Secondary"] = "#7A6A44", ["SecondaryDarkText"] = "#6B645B", ["Tertiary"] = "#D9D0C0",
		["White"] = "#2A251F", ["Black"] = "#F7F3EB", ["Magenta"] = "#9A6B1F", ["MidnightBlue"] = "#16130F", ["OffBlack"] = "#F7F3EB",
		["Gray100"] = "#2A251F", ["Gray200"] = "#6B645B", ["Gray300"] = "#8F877B", ["Gray400"] = "#CFC6B6", ["Gray500"] = "#D9D0C0",
		["Gray600"] = "#E8E1D3", ["Gray900"] = "#EFE9DD", ["Gray950"] = "#F7F3EB",
		["IconSurface"] = "#2A251F", ["IconFlatSurface"] = "#2A251F",
		["BubbleMine"] = "#EBD9AE", ["BubbleTheirs"] = "#E8E1D3", ["Danger"] = "#B23B2E",
	};

	private void ApplyLight()
	{
		foreach (var (key, hex) in Light) this[key] = Color.FromArgb(hex);
		foreach (var key in Light.Keys.Where(k => ContainsKey(k + "Brush")).ToList()) this[key + "Brush"] = new SolidColorBrush((Color)this[key]);
	}
}