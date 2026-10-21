/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Globalization;

namespace FreeSpeech.Mobile;

public sealed class InvertedBoolConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public sealed class StringNotEmptyConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrEmpty(value as string);
	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Alice Martin" -> "AM": the avatar letters when there is no picture.</summary>
public sealed class InitialsConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		var words = (value as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
	}
	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}