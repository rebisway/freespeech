/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using FreeSpeech.Client.Markup;

namespace FreeSpeech.Desktop.Controls;

/// <summary>
/// The text of a message: shown as typed, or, when its first line is "md", formatted (titles, lists, quotes, bold, italic, strike, code,
/// links; <see cref="MessageMarkup"/> reads it, this only dresses the result). Still selectable. A click on a link (nothing selected) raises
/// <see cref="LinkClicked"/>.
/// </summary>
public sealed class MarkupText : SelectableTextBlock
{
	public static readonly StyledProperty<string> RawProperty = AvaloniaProperty.Register<MarkupText, string>(nameof(Raw), "");

	/// <summary>What a click on a link does (set by the open chat): the address is shown and confirmed before anything opens.</summary>
	public static Action<string>? LinkClicked { get; set; }

	private readonly List<(int Start, int End, string Url)> _links = [];

	public string Raw { get => GetValue(RawProperty); set => SetValue(RawProperty, value); }

	protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == RawProperty) Apply();
	}

	private static IBrush? Brush(string key) => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;

	private void Apply()
	{
		_links.Clear();
		if (!MessageMarkup.IsMarkdown(Raw)) { Inlines?.Clear(); Text = Raw; return; }

		Text = null;
		Inlines ??= [];
		Inlines.Clear();
		var position = 0;
		foreach (var s in MessageMarkup.Parse(Raw))
		{
			if (s.Text == "\n") { Inlines.Add(s.Spacer ? new LineBreak { FontSize = FontSize * 0.45 } : new LineBreak()); position++; continue; }

			var run = new Run(s.Text)
			{
				FontWeight = s.Bold ? FontWeight.Bold : FontWeight.Normal,
				FontStyle = s.Italic ? FontStyle.Italic : FontStyle.Normal,
			};
			if (s.Heading > 0) run.FontSize = FontSize * (s.Heading == 1 ? 1.6 : s.Heading == 2 ? 1.35 : 1.15);
			if (s.Code) { run.FontFamily = new FontFamily("Consolas, Menlo, monospace"); run.Background = Brush("RaisedBrush"); }
			if (s.Link != null) { run.TextDecorations = Avalonia.Media.TextDecorations.Underline; if (Brush("PrimaryBrush") is { } link) run.Foreground = link; _links.Add((position, position + s.Text.Length, s.Link)); }
			else if (s.Strike) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
			if (s.Quote && s.Link == null && Brush("MutedBrush") is { } muted) run.Foreground = muted;
			Inlines.Add(run);
			position += s.Text.Length;
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (_links.Count == 0 || e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
		var at = TextLayout.HitTestPoint(e.GetPosition(this)).TextPosition;
		foreach (var (start, end, url) in _links)
			if (at >= start && at < end) { LinkClicked?.Invoke(url); return; }
	}
}