/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Threading;
using FreeSpeech.Desktop.ViewModels;
using FreeSpeech.Desktop.Views;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The application's own notifications: a popup that rises from the tray corner, with a reply box. The same on every system, and
/// nothing to register with the operating system.
/// </summary>
public sealed class PopupNotifier : INotifier
{
	private readonly PopupViewModel _vm = new();
	private PopupWindow? _window;

	public event Action<string>? Opened;
	public event Action<string, string>? Replied;

	public PopupNotifier()
	{
		_vm.Opened += key => Opened?.Invoke(key);
		_vm.Replied += (key, text) => Replied?.Invoke(key, text);
		_vm.Emptied += () => _window?.Hide();
	}

	public void FileReady(string messageId, string path) => Dispatcher.UIThread.Post(() => _vm.FileReady(messageId, path));

	public void Show(string title, string text, string conversationKey, string? messageId = null) => Dispatcher.UIThread.Post(() =>
	{
		_vm.Show(title, text, conversationKey, messageId);
		_window ??= new PopupWindow(_vm);
		if (!_window.IsVisible) _window.Show();
		Dispatcher.UIThread.Post(_window.Place, DispatcherPriority.Background);
	});

	public void Cancel(string conversationKey) => Dispatcher.UIThread.Post(() => _vm.Cancel(conversationKey));
}