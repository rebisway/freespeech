/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FreeSpeech.Desktop.ViewModels;

namespace FreeSpeech.Desktop.Views;

/// <summary>The tray popup: cards with a reply box, in the bottom right corner of the primary screen, above the taskbar.</summary>
public partial class PopupWindow : Window
{
	private const int ScreenMargin = 12;

	/// <summary>For the XAML loader; the tray creates the window with its view model.</summary>
	public PopupWindow() => InitializeComponent();

	public PopupWindow(PopupViewModel vm) : this()
	{
		DataContext = vm;
		SizeChanged += (_, _) => Place();
	}

	/// <summary>Bottom right of the work area (the taskbar is outside it), whatever the height of the cards.</summary>
	public void Place()
	{
		var screen = Screens?.Primary;
		if (screen == null) return;
		var scale = screen.Scaling;
		var area = screen.WorkingArea;
		var (width, height) = ((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
		Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin);
	}

	private PopupViewModel Vm => (PopupViewModel)DataContext!;

	private void OnCardEntered(object? sender, PointerEventArgs e) { if ((sender as Control)?.DataContext is PopupCard card) Vm.Hold(card); }

	private void OnCardExited(object? sender, PointerEventArgs e) { if ((sender as Control)?.DataContext is PopupCard card) Vm.Restart(card); }

	private void OnReplyFocused(object? sender, RoutedEventArgs e) { if ((sender as Control)?.DataContext is PopupCard card) Vm.Hold(card); }

	/// <summary>Enter sends the answer.</summary>
	private void OnReplyKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || (sender as Control)?.DataContext is not PopupCard card) return;
		e.Handled = true;
		if (Vm.ReplyCommand.CanExecute(card)) Vm.ReplyCommand.Execute(card);
	}
}