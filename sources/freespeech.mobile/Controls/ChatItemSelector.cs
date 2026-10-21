/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Display;
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Controls;

/// <summary>Picks the light template of a plain text, formatted text, lone picture or voice bubble, and the full one for everything else (see <see cref="BubbleClassifier"/>).</summary>
public sealed class ChatItemSelector : DataTemplateSelector
{
	public DataTemplate? Text { get; set; }
	public DataTemplate? Markdown { get; set; }
	public DataTemplate? Image { get; set; }
	public DataTemplate? Voice { get; set; }
	public DataTemplate? Full { get; set; }

	protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
		(item is ChatItem line ? line.Kind : BubbleKind.Full) switch
		{
			BubbleKind.Text => Text!,
			BubbleKind.Markdown => Markdown!,
			BubbleKind.Image => Image!,
			BubbleKind.Voice => Voice!,
			_ => Full!,
		};
}
