/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Controls;
using Avalonia.Input;
using FreeSpeech.Desktop.ViewModels;

namespace FreeSpeech.Desktop.Views;

public partial class LockView : UserControl
{
	public LockView()
	{
		InitializeComponent();
		AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => PinBox.Focus(), Avalonia.Threading.DispatcherPriority.Background);
	}

	/// <summary>Enter validates the code.</summary>
	private void OnPinKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || DataContext is not LockViewModel vm) return;
		e.Handled = true;
		if (vm.IsSet && sender == PinBox) return;   // the second box follows
		if (vm.OkCommand.CanExecute(null)) vm.OkCommand.Execute(null);
	}

	/// <summary>A PIN is digits only.</summary>
	private void OnDigitsOnly(object? sender, TextInputEventArgs e)
	{
		if (e.Text is { Length: > 0 } text && !text.All(char.IsAsciiDigit)) e.Handled = true;
	}
}
