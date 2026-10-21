/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

/// <summary>
/// Full-size view of a received or sent picture, inside the app. The picture is shown as large as the screen allows, in
/// proportion. Zoomed, it can be dragged, but never out of the frame: an edge of the picture stops at the edge of the screen.
/// </summary>
[QueryProperty(nameof(Path), "path")]
public partial class ImageViewerPage : ContentPage
{
	private const double MaxZoom = 6;

	private double _zoom = 1, _startZoom = 1, _x, _y, _startX, _startY, _dragX, _dragY;
	private float _pixelsWide, _pixelsHigh;

	public ImageViewerPage()
	{
		InitializeComponent();
		BindingContext = this;
		Stage.SizeChanged += (_, _) => Apply();
#if ANDROID
		// MAUI's pinch recognizer loses against the pan recognizer on the same view: Android's own detector is reliable.
		foreach (var pinch in Stage.GestureRecognizers.OfType<PinchGestureRecognizer>().ToList()) Stage.GestureRecognizers.Remove(pinch);
#endif
	}

#if ANDROID
	protected override void OnAppearing()
	{
		base.OnAppearing();
		var activity = Platform.CurrentActivity;
		if (activity != null)
		{
			MainActivity.PinchDetector = new Android.Views.ScaleGestureDetector(activity, new PinchListener(this));
			MainActivity.LongPressDetector = new Android.Views.GestureDetector(activity, new LongPressListener(this));
		}
	}

	protected override void OnDisappearing()
	{
		MainActivity.PinchDetector = null;
		MainActivity.LongPressDetector = null;
		base.OnDisappearing();
	}

	private sealed class LongPressListener(ImageViewerPage page) : Android.Views.GestureDetector.SimpleOnGestureListener
	{
		public override void OnLongPress(Android.Views.MotionEvent e) => MainThread.BeginInvokeOnMainThread(page.OpenMenu);
	}

	private sealed class PinchListener(ImageViewerPage page) : Android.Views.ScaleGestureDetector.SimpleOnScaleGestureListener
	{
		public override bool OnScale(Android.Views.ScaleGestureDetector detector)
		{
			page.ZoomBy(detector.ScaleFactor);
			return true;
		}
	}
#endif

	/// <summary>The pictures of the conversation, oldest first, set by the chat before opening the viewer.</summary>
	public static List<GalleryPhoto> Gallery { get; set; } = [];

	/// <summary>The actions of the message a picture belongs to, set by the chat: reply, react, forward, delete.</summary>
	public static Func<string, List<MenuOption>?>? MenuFor { get; set; }

	/// <summary>Puts an emoji on a message (message id, emoji), set by the chat.</summary>
	public static Func<string, string, Task>? ReactTo { get; set; }

	public ObservableCollection<MenuOption> MenuItems { get; } = [];
	public string[] ReactionEmojis => ChatViewModel.QuickReactions;

	private bool _isReactionBarOpen;
	public bool IsReactionBarOpen
	{
		get => _isReactionBarOpen;
		private set { _isReactionBarOpen = value; OnPropertyChanged(); }
	}

	private void OnCloseReactions(object? sender, EventArgs e) => IsReactionBarOpen = false;

	private async void OnPickEmoji(object? sender, TappedEventArgs e)
	{
		IsReactionBarOpen = false;
		if ((sender as BindableObject)?.BindingContext is string emoji && _index >= 0 && _index < Gallery.Count && ReactTo != null)
			await ReactTo(Gallery[_index].MessageId, emoji);
	}

	private bool _isMenuOpen;
	public bool IsMenuOpen
	{
		get => _isMenuOpen;
		private set { _isMenuOpen = value; OnPropertyChanged(); }
	}

	/// <summary>Long press, or the menu button: what can be done with the picture on screen (and its message).</summary>
	internal void OpenMenu()
	{
		if (IsMenuOpen || _index < 0 || _index >= Gallery.Count) return;
		var options = MenuFor?.Invoke(Gallery[_index].MessageId);
		if (options == null || options.Count == 0) return;
		MenuItems.Clear();
		foreach (var o in options) MenuItems.Add(o);
		IsMenuOpen = true;
	}

	private void OnMenu(object? sender, EventArgs e) => OpenMenu();
	private void OnCloseMenu(object? sender, EventArgs e) => IsMenuOpen = false;

	private async void OnPick(object? sender, TappedEventArgs e)
	{
		if (((sender as BindableObject)?.BindingContext) is not MenuOption option) return;
		IsMenuOpen = false;
		if (option.IsReact) { IsReactionBarOpen = true; return; }   // the chat's own bar is behind this page: the viewer shows its own
		try { await option.Run(); }
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { await Dialogs.AlertAsync("FreeSpeech", ErrorText.Of(ex), Loc.T("ok")); }

		if (option.LeavesViewer) { await Shell.Current.GoToAsync(".."); return; }

		// A delete took the picture away: show a neighbour, or leave when it was the last one.
		if (_index >= 0 && _index < Gallery.Count && !File.Exists(Gallery[_index].Path))
		{
			Gallery.RemoveAt(_index);
			if (Gallery.Count == 0) { await Shell.Current.GoToAsync(".."); return; }
			Show(Gallery[Math.Min(_index, Gallery.Count - 1)].Path);
		}
	}

	private int _index = -1;

	public string Path
	{
		set => Show(Uri.UnescapeDataString(value));
	}

	private void Show(string path)
	{
		_index = Gallery.FindIndex(g => g.Path == path);
		(_pixelsWide, _pixelsHigh) = ProfilePhoto.SizeOf(path);
		Picture.Source = ImageSource.FromFile(path);
		_zoom = 1;
		_x = _y = 0;
		Apply();
	}

	/// <summary>Not zoomed: a swipe left shows the next picture of the conversation, a swipe right the previous one, until the first or last.</summary>
	private void Step(int delta)
	{
		var i = _index + delta;
		if (_index < 0 || i < 0 || i >= Gallery.Count) return;
		Show(Gallery[i].Path);
	}

	/// <summary>Size of the picture on screen at zoom 1 (it fits the stage, in proportion).</summary>
	private (double W, double H) Fitted()
	{
		if (_pixelsWide <= 0 || _pixelsHigh <= 0 || Stage.Width <= 0) return (Stage.Width, Stage.Height);
		var scale = Math.Min(Stage.Width / _pixelsWide, Stage.Height / _pixelsHigh);
		return (_pixelsWide * scale, _pixelsHigh * scale);
	}

	private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
	{
		switch (e.Status)
		{
			case GestureStatus.Started: _startZoom = _zoom; break;
			case GestureStatus.Running: _zoom = Math.Clamp(_startZoom * e.Scale, 1, MaxZoom); Apply(); break;
		}
	}

	private void OnPan(object? sender, PanUpdatedEventArgs e)
	{
		switch (e.StatusType)
		{
			case GestureStatus.Started: _startX = _x; _startY = _y; _dragX = _dragY = 0; break;
			case GestureStatus.Running: _dragX = e.TotalX; _dragY = e.TotalY; _x = _startX + e.TotalX; _y = _startY + e.TotalY; Apply(); break;
			case GestureStatus.Completed:
				// The final event does not always carry the distance: the last running one does.
				if (_zoom <= 1.05 && Math.Abs(_dragX) > 70 && Math.Abs(_dragX) > 1.5 * Math.Abs(_dragY)) Step(_dragX < 0 ? 1 : -1);
				break;
		}
	}

	/// <summary>Double tap: zoom in, or back to the whole picture when already zoomed.</summary>
	private void OnDoubleTap(object? sender, TappedEventArgs e)
	{
		_zoom = _zoom > 1.05 ? 1 : 3;
		_x = _y = 0;
		Apply();
	}

	internal void ZoomBy(double factor)
	{
		_zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
		Apply();
	}

	/// <summary>Shows the picture at the current zoom, with the offset kept inside what the zoomed picture can move: nothing at all while it fits the screen.</summary>
	private void Apply()
	{
		var (w, h) = Fitted();
		var maxX = Math.Max(0, (w * _zoom - Stage.Width) / 2);
		var maxY = Math.Max(0, (h * _zoom - Stage.Height) / 2);
		_x = Math.Clamp(_x, -maxX, maxX);
		_y = Math.Clamp(_y, -maxY, maxY);

		Picture.Scale = _zoom;
		Picture.TranslationX = _x;
		Picture.TranslationY = _y;
	}

	private async void OnClose(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
