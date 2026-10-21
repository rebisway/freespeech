/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FreeSpeech.Desktop.Controls;

/// <summary>
/// The picture slot of a list row, as on the mobile app. A contact: its picture (the base64 one of its contact card) or its initials,
/// in a circle. A group: its picture or the "people" glyph, in a circle too; with a picture it carries a small "people" mark in a corner.
/// </summary>
public sealed class Avatar : Border
{
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<Avatar, string>(nameof(Title), "");
	public static readonly StyledProperty<bool> IsGroupProperty = AvaloniaProperty.Register<Avatar, bool>(nameof(IsGroup));
	public static readonly StyledProperty<string?> PhotoProperty = AvaloniaProperty.Register<Avatar, string?>(nameof(Photo));
	public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Avatar, double>(nameof(Size), 44);

	private readonly TextBlock _initials = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
	private readonly PathIcon _glyph = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
	private readonly Image _picture = new() { Stretch = Stretch.UniformToFill };
	private readonly PathIcon _markGlyph = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
	private readonly Border _mark = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, BorderThickness = new Thickness(1) };

	public Avatar()
	{
		ClipToBounds = false;   // the group mark sticks out of the circle; the picture is clipped to the circle itself (see Apply)
		// The colours are the theme's: they follow a change of theme.
		_initials.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryBrush"));
		_glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("PrimaryBrush"));
		_mark.Child = _markGlyph;
		_markGlyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("PrimaryBrush"));
		_mark.Bind(BorderBrushProperty, this.GetResourceObservable("PrimaryBrush"));
		_mark.Bind(BackgroundProperty, this.GetResourceObservable("SurfaceBrush"));
		Child = new Grid { Children = { _initials, _glyph, _picture, _mark } };
		if (Application.Current is { } app && app.TryGetResource("Icon.people", null, out var people) && people is Geometry geometry) { _glyph.Data = geometry; _markGlyph.Data = geometry; }
		Apply();
	}

	public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public bool IsGroup { get => GetValue(IsGroupProperty); set => SetValue(IsGroupProperty, value); }
	/// <summary>Base64 picture, or null/empty for none.</summary>
	public string? Photo { get => GetValue(PhotoProperty); set => SetValue(PhotoProperty, value); }
	public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == TitleProperty || change.Property == IsGroupProperty || change.Property == PhotoProperty || change.Property == SizeProperty) Apply();
	}

	private void Apply()
	{
		Width = Height = Size;
		CornerRadius = new CornerRadius(Size / 2);
		this.Bind(BackgroundProperty, this.GetResourceObservable(IsGroup ? "GroupBgBrush" : "AvatarBgBrush"));
		if (IsGroup) this.Bind(BorderBrushProperty, this.GetResourceObservable("PrimaryBrush")); else BorderBrush = null;
		BorderThickness = new Thickness(IsGroup ? 1 : 0);

		var bitmap = Decode(Photo);
		(_picture.Source as IDisposable)?.Dispose();
		_picture.Source = bitmap;
		_picture.IsVisible = bitmap != null;
		_initials.Text = InitialsOf(Title);
		_initials.FontSize = Size * 0.36;
		_initials.IsVisible = !IsGroup && bitmap == null;
		_glyph.Width = _glyph.Height = Size * 0.55;
		_glyph.IsVisible = IsGroup && bitmap == null;
		var mark = Math.Max(14, Size * 0.3);
		_mark.IsVisible = IsGroup && bitmap != null;
		_mark.Width = _mark.Height = mark;
		_mark.CornerRadius = new CornerRadius(mark / 2);
		_mark.Margin = new Thickness(0, 0, -mark * 0.25, -mark * 0.25);
		var inner = Math.Max(1, Size - (IsGroup ? 2 : 0));
		_picture.Clip = new EllipseGeometry(new Rect(0, 0, inner, inner));
		_markGlyph.Width = _markGlyph.Height = mark * 0.62;
	}

	private static Bitmap? Decode(string? base64)
	{
		if (string.IsNullOrEmpty(base64)) return null;
		try { return new Bitmap(new MemoryStream(Convert.FromBase64String(base64))); }
		catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }   // a damaged picture falls back to the initials
	}

	private static string InitialsOf(string name)
	{
		var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
	}
}
