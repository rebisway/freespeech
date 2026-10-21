/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Microsoft.Maui.Controls.Shapes;

namespace FreeSpeech.Mobile.Controls;

/// <summary>
/// The picture slot of a list row. A contact: its picture (the base64 one of its contact card) or its initials, in a circle.
/// A group: its picture or the "people" glyph, in a circle too; with a picture it carries a small "people" mark in a corner.
/// </summary>
public partial class AvatarView : ContentView
{
	public static readonly BindableProperty TitleProperty = BindableProperty.Create(
		nameof(Title), typeof(string), typeof(AvatarView), "",
		propertyChanged: (b, _, v) => ((AvatarView)b).Initials.Text = InitialsOf((string)v));

	public static readonly BindableProperty IsGroupProperty = BindableProperty.Create(
		nameof(IsGroup), typeof(bool), typeof(AvatarView), false,
		propertyChanged: (b, _, _) => ((AvatarView)b).Apply());

	/// <summary>Base64 picture, or null/empty for none.</summary>
	public static readonly BindableProperty PhotoProperty = BindableProperty.Create(
		nameof(Photo), typeof(string), typeof(AvatarView), null,
		propertyChanged: (b, _, _) => ((AvatarView)b).Apply());

	public static readonly BindableProperty SizeProperty = BindableProperty.Create(
		nameof(Size), typeof(double), typeof(AvatarView), 44.0,
		propertyChanged: (b, _, v) => ((AvatarView)b).Resize((double)v));

	public AvatarView() => InitializeComponent();

	public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public bool IsGroup { get => (bool)GetValue(IsGroupProperty); set => SetValue(IsGroupProperty, value); }
	public string? Photo { get => (string?)GetValue(PhotoProperty); set => SetValue(PhotoProperty, value); }
	public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

	private void Apply()
	{
		GroupBadge.IsVisible = IsGroup;
		PersonBadge.IsVisible = !IsGroup;

		var source = PhotoSource(Photo);
		PersonPhoto.Source = IsGroup ? null : source;
		PersonPhoto.IsVisible = !IsGroup && source != null;
		Initials.IsVisible = source == null || IsGroup;
		GroupPhoto.Source = IsGroup ? source : null;
		GroupPhoto.IsVisible = IsGroup && source != null;
		GroupGlyph.IsVisible = source == null;
		GroupMark.IsVisible = IsGroup && source != null;
	}

	private void Resize(double size)
	{
		WidthRequest = HeightRequest = size;
		PersonBadge.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(size / 2) };
		GroupBadge.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(size / 2) };
		var mark = Math.Max(14, size * 0.3);
		GroupMark.WidthRequest = GroupMark.HeightRequest = mark;
		GroupMark.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(mark / 2) };
		GroupMarkGlyph.WidthRequest = GroupMarkGlyph.HeightRequest = mark * 0.62;
		GroupMark.TranslationX = GroupMark.TranslationY = mark * 0.25;   // sticks out of the circle a little
		GroupGlyph.WidthRequest = GroupGlyph.HeightRequest = size * 0.55;
		Initials.FontSize = size * 0.36;
	}

	private static ImageSource? PhotoSource(string? base64)
	{
		if (string.IsNullOrEmpty(base64)) return null;
		try
		{
			var bytes = Convert.FromBase64String(base64);
			return ImageSource.FromStream(() => new MemoryStream(bytes));
		}
		catch (FormatException) { return null; }   // a damaged picture falls back to the initials
	}

	private static string InitialsOf(string name)
	{
		var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
	}
}
