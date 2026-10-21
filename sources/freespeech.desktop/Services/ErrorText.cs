/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Globalization;
using FreeSpeech.Client;

namespace FreeSpeech.Desktop.Services;

/// <summary>Turns a failure of the library into a sentence in the user's language: its code points to a text ("err_" + code), its arguments fill the blanks. Without a code, the library's English message is shown.</summary>
public static class ErrorText
{
	public static string Of(Exception ex)
	{
		if (ex is ICodedMessage { Code.Length: > 0 } coded && Loc.TryGet("err_" + coded.Code, out var text))
			return string.Format(CultureInfo.CurrentCulture, text, coded.Args);
		return ex.Message;
	}
}
