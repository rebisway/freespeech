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
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeSpeech.Desktop.ViewModels;

namespace FreeSpeech.Desktop.Views;

public partial class ChatView : UserControl
{
	private ChatViewModel? _vm;

	public ChatView()
	{
		InitializeComponent();
		// Shift+Enter sends, Enter makes a new line.
		Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
		Scroller.ScrollChanged += OnScrolled;
		// Files dropped on the chat wait under the composer.
		DragDrop.SetAllowDrop(this, true);
		AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
		AddHandler(DragDrop.DropEvent, OnDrop);
		DataContextChanged += (_, _) =>
		{
			if (_vm != null) { _vm.ScrollToEndRequested -= ScrollToEnd; _vm.ScrollToItemRequested -= ScrollToItem; _vm.PropertyChanged -= OnVmChanged; }
			_vm = DataContext as ChatViewModel;
			if (_vm != null)
			{
				_vm.ScrollToEndRequested += ScrollToEnd;
				_vm.ScrollToItemRequested += ScrollToItem;
				_vm.PropertyChanged += OnVmChanged; ScrollToEnd(_vm.OpenAt);
				_vm.CopyText = async text => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
			}
			Composer.Focus();
		};
	}

	private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ChatViewModel.IsSearchOpen) && _vm is { IsSearchOpen: true }) Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
	}

	private void OnComposerKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
		e.Handled = true;
		if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
	}

	// ------------------------------------------------------------------
	// Scrolling
	// ------------------------------------------------------------------

	private void ScrollToEnd(int? firstUnread)
	{
		Dispatcher.UIThread.Post(() =>
		{
			Scroller.UpdateLayout();
			if (firstUnread is { } i && Scroller.Extent.Height > Scroller.Viewport.Height * 1.2) ScrollToItem(i, atTop: true);
			else
			{
				Scroller.ScrollToEnd();
				// Images and wrapped text settle after the first pass: stick to the end once more.
				DispatcherTimer.RunOnce(() => { if (firstUnread == null) Scroller.ScrollToEnd(); }, TimeSpan.FromMilliseconds(150));
			}
		}, DispatcherPriority.Background);
	}

	private void ScrollToItem(int index) => ScrollToItem(index, atTop: false);

	private void ScrollToItem(int index, bool atTop)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (Scroller.Content is not ItemsControl list || list.ContainerFromIndex(index) is not Control container) return;
			// Opening on the unread line: it goes to the top (the offset is clamped, so a short remainder shows down to the end).
			if (atTop && container.TranslatePoint(default, list) is { } p) Scroller.Offset = new Vector(0, Math.Max(0, p.Y - 8));
			else container.BringIntoView();
		}, DispatcherPriority.Background);
	}

	/// <summary>Scrolled to the top: the next older page is added, and the view stays on the line it was on.</summary>
	private void OnScrolled(object? sender, ScrollChangedEventArgs e)
	{
		if (_vm is not { HasMoreBefore: true } || Scroller.Offset.Y > 40 || Scroller.Extent.Height <= Scroller.Viewport.Height) return;
		var (before, offset) = (Scroller.Extent.Height, Scroller.Offset.Y);
		if (_vm.LoadOlder() == 0) return;
		Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(0, offset + Scroller.Extent.Height - before), DispatcherPriority.Background);
	}

	// ------------------------------------------------------------------
	// Messages: menu and files
	// ------------------------------------------------------------------

	/// <summary>Right click: the menu of the message. Left click on a message that holds a file: the file opens (or is asked for).</summary>
	private void OnBubbleReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (_vm == null || sender is not Border { DataContext: ChatItem item } bubble) return;
		var point = e.GetCurrentPoint(bubble);
		if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
		{
			ShowMenu(bubble, item);
			e.Handled = true;
		}
		else if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased && item.IsFile && !item.IsDeleted && _vm.OpenFileOfCommand.CanExecute(item))
		{
			_vm.OpenFileOfCommand.Execute(item);
		}
	}

	private void ShowMenu(Control anchor, ChatItem item)
	{
		if (_vm == null) return;
		var options = _vm.OptionsFor(item);
		if (options.Count == 0) return;
		var menu = new ContextMenu();
		foreach (var option in options)
		{
			var entry = new Avalonia.Controls.MenuItem { Header = option.Label };
			if (Application.Current is { } app && app.TryGetResource(option.Icon, null, out var geometry) && geometry is Avalonia.Media.Geometry g)
				entry.Icon = new PathIcon { Data = g, Width = 16, Height = 16 };
			entry.Click += (_, _) => { _vm.PickMenuCommand.Execute(option); DispatcherTimer.RunOnce(() => { if (TopLevel.GetTopLevel(this) is Window { IsActive: true }) Composer.Focus(); }, TimeSpan.FromMilliseconds(250)); };
			menu.Items.Add(entry);
		}
		menu.Open(anchor);
	}

	private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (sender is not ListBox { SelectedItem: SearchResult result } list || _vm == null) return;
		list.SelectedItem = null;
		_vm.OpenResultCommand.Execute(result);
	}

	private void OnDrop(object? sender, DragEventArgs e)
	{
		if (_vm == null) return;
		foreach (var file in e.DataTransfer.TryGetFiles() ?? [])
			if (file.TryGetLocalPath() is { } path && File.Exists(path)) _vm.Stage(path);
	}
}
