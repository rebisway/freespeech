/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace FreeSpeech.Mobile.Views;

/// <summary>
/// Framing a profile photo. The picture is shown at the size that just covers the square frame (zoom 1), then the user
/// drags it and zooms. The frame position in source pixels follows from three numbers: the zoom and the two offsets.
/// </summary>
public partial class PhotoCropPage : ContentPage
{
	private const double MaxZoom = 4;

	private readonly ClientService _service;
	private string _path = "";
	private float _width, _height;       // the picture, in pixels
	private double _frame;               // the square frame, in device-independent units
	private double _scale0;              // units per pixel at zoom 1
	private double _zoom = 1, _tx, _ty;
	private double _cropSize = 1;            // the frame's size as a fraction of the area (0.3 to 1)
	private double _panStartX, _panStartY, _pinchStartZoom;
	private bool _busy;

	public PhotoCropPage(ClientService service)
	{
		InitializeComponent();
		_service = service;
		SizeChanged += (_, _) => Layout();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_path = ProfilePhoto.PendingPath ?? "";
		if (_path.Length == 0 || !File.Exists(_path)) { ProfilePhoto.OnFramed = null; _ = Shell.Current.GoToAsync(".."); return; }
		(_width, _height) = ProfilePhoto.SizeOf(_path);
		_zoom = 1; _tx = _ty = 0; _cropSize = 1;
		ZoomSlider.Value = 1;
		CropSlider.Value = 1;
		Preview.Source = null;
		Preview.Source = ImageSource.FromFile(_path);
		Layout();
	}

	/// <summary>The frame can be made smaller than the picture's area: the part inside it is cut out and brought to the profile size afterwards.</summary>
	private void OnCropSizeChanged(object? sender, ValueChangedEventArgs e)
	{
		_cropSize = Math.Clamp(e.NewValue, 0.3, 1);
		ApplyGuide();
		Apply();   // a bigger circle may no longer fit where the picture was slid to
	}

	private void ApplyGuide()
	{
		var size = Math.Max(1, _frame * _cropSize);
		Guide.WidthRequest = Guide.HeightRequest = size;
		Guide.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(size / 2) };
	}

	/// <summary>Sizes the frame (a square as wide as the page allows) and the picture inside it.</summary>
	private void Layout()
	{
		if (Width <= 0 || _width <= 0) return;
		_frame = Math.Min(Width - 36, Height * 0.55);
		if (_frame <= 0) return;

		Area.WidthRequest = Area.HeightRequest = _frame;
		ApplyGuide();

		_scale0 = Math.Max(_frame / _width, _frame / _height);
		Preview.WidthRequest = _width * _scale0;
		Preview.HeightRequest = _height * _scale0;
		Apply();
	}

	/// <summary>Keeps the picture covering the frame, and shows it.</summary>
	private void Apply()
	{
		// The picture only has to cover the circle, not the whole area: with a smaller circle it can slide further, up and down as well.
		var circle = _frame * _cropSize;
		var maxX = Math.Max(0, (_width * _scale0 * _zoom - circle) / 2);
		var maxY = Math.Max(0, (_height * _scale0 * _zoom - circle) / 2);
		_tx = Math.Clamp(_tx, -maxX, maxX);
		_ty = Math.Clamp(_ty, -maxY, maxY);

		Preview.Scale = _zoom;
		Preview.TranslationX = _tx;
		Preview.TranslationY = _ty;
	}

	private void OnPan(object? sender, PanUpdatedEventArgs e)
	{
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				_panStartX = _tx;
				_panStartY = _ty;
				break;
			case GestureStatus.Running:
				_tx = _panStartX + e.TotalX;
				_ty = _panStartY + e.TotalY;
				Apply();
				break;
		}
	}

	private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
	{
		switch (e.Status)
		{
			case GestureStatus.Started:
				_pinchStartZoom = _zoom;
				break;
			case GestureStatus.Running:
				SetZoom(_pinchStartZoom * e.Scale);
				break;
		}
	}

	private void OnZoomChanged(object? sender, ValueChangedEventArgs e) => SetZoom(e.NewValue);

	private void SetZoom(double zoom)
	{
		_zoom = Math.Clamp(zoom, 1, MaxZoom);
		if (Math.Abs(ZoomSlider.Value - _zoom) > 0.01) ZoomSlider.Value = _zoom;
		Apply();
	}

	/// <summary>The frame, in pixels of the picture: centred on the point under the frame's centre, as wide as the frame shows.</summary>
	private (float X, float Y, float Side) FrameInPixels()
	{
		var pixelsPerUnit = 1 / (_scale0 * _zoom);
		var side = _frame * _cropSize * pixelsPerUnit;
		var centreX = _width / 2 - _tx * pixelsPerUnit;
		var centreY = _height / 2 - _ty * pixelsPerUnit;
		return ((float)(centreX - side / 2), (float)(centreY - side / 2), (float)side);
	}

	private async void OnConfirm(object? sender, EventArgs e)
	{
		if (_busy) return;
		_busy = true;
		try
		{
			var (x, y, side) = FrameInPixels();
			var cropped = await ProfilePhoto.CropAsync(_path, x, y, side);
			if (ProfilePhoto.OnFramed is { } framed) await framed(cropped);
			else
			{
				_service.Client.SetPhoto(cropped);
				await _service.Client.PublishProfileAsync();
			}
			ProfilePhoto.PendingPath = null;
			ProfilePhoto.OnFramed = null;
			await Shell.Current.GoToAsync("..");
		}
		catch (Exception ex) when (ex is ClientException or IOException or InvalidOperationException)
		{
			// A refusal of the library (a pause to respect, a picture too big) is told, not a crash; the framing stays open.
			await Dialogs.AlertAsync("FreeSpeech", ex is IOException ? ex.Message : ErrorText.Of(ex), Loc.T("ok"));
		}
		finally
		{
			_busy = false;
		}
	}
}
