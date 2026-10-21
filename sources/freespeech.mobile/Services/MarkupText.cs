/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Markup;

namespace FreeSpeech.Mobile.Services;

/// <summary>A formatted message as the runs of a label (<see cref="MessageMarkup"/> reads the text; this only dresses the result).</summary>
public static class MarkupText
{
	/// <summary>Titles are larger than the 14 of the usual text.</summary>
	private static double SizeOf(int heading) => heading switch { 1 => 21, 2 => 18, 3 => 16, _ => -1 };

	public static FormattedString Build(string text, Color textColor, Color mutedColor, Color linkColor, Color codeBackground, Action<string> onLink)
	{
		var formatted = new FormattedString();
		foreach (var s in MessageMarkup.Parse(text))
		{
			var spacerSize = s.Spacer ? 6 : -1;   // a short empty line between a text line and a list line or a title
			var attributes = (s.Bold ? FontAttributes.Bold : FontAttributes.None) | (s.Italic ? FontAttributes.Italic : FontAttributes.None);
			var span = new Span
			{
				Text = s.Text,
				FontAttributes = attributes,
				TextColor = s.Link != null ? linkColor : s.Quote ? mutedColor : textColor,
				TextDecorations = s.Link != null ? TextDecorations.Underline : s.Strike ? TextDecorations.Strikethrough : TextDecorations.None,
			};
			if (s.Code) { span.FontFamily = "monospace"; span.BackgroundColor = codeBackground; }
			if (SizeOf(s.Heading) is var size and > 0) span.FontSize = size;
			if (s.Spacer) span.FontSize = spacerSize;
			if (s.Link is { } url) span.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => onLink(url)) });
			formatted.Spans.Add(span);
		}
		return formatted;
	}
}