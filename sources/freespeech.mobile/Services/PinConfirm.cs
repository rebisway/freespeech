/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Security;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// When the app lock is on, an action that takes much of the phone's data out in one go (a backup, an archive) is made only after the PIN is
/// typed again: a stranger holding the unlocked phone cannot take it.
/// </summary>
public static class PinConfirm
{
	/// <summary>True to go on. When it is false, <paramref name="message"/> says why (wrong PIN, attempts left), or is empty when the person cancelled.</summary>
	public static async Task<(bool Ok, string Message)> AskAsync()
	{
		if (!AppLock.Enabled) return (true, "");
		var pin = await Dialogs.PromptAsync(Loc.T("lock_title_verify"), "", Loc.T("ok"), Loc.T("cancel"), Loc.T("lock_pin_hint"), PinLock.MaxDigits, Keyboard.Numeric, secret: true);
		if (pin == null) return (false, "");
		var result = await AppLock.VerifyAsync(pin);
		if (result == PinResult.Ok) return (true, "");
		return (false, result == PinResult.Wrong ? AppLock.AttemptsLeft is { } left ? Loc.T("lock_wrong_left", left) : Loc.T("lock_wrong") : "");
	}
}
