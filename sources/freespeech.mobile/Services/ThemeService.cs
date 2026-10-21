/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Services;

/// <summary>The colour theme: dark (the Rebis look), light, or the phone's own. Read at start (see Palette); a change restarts the app.</summary>
public static class ThemeService
{
	public const string Dark = "dark", Light = "light", System = "system";

	public static string Mode
	{
		get => Preferences.Default.Get("theme", Dark);
		set => Preferences.Default.Set("theme", value);
	}

	/// <summary>True when the light palette is in use: chosen, or "system" on a phone set to light.</summary>
	/// <summary>The logo files for the current palette (black ones on the light theme), for XAML: <c>{x:Static svc:ThemeService.Logo}</c>.</summary>
	public static string Logo => IsLight ? "logo_light.png" : "logo.png";
	public static string FlagLogo => IsLight ? "logo_flag_light.png" : "logo_flag.png";

	public static bool IsLight => Mode == Light || (Mode == System && AppInfo.Current.RequestedTheme == AppTheme.Light);
}