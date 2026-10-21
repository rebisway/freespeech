/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

public partial class ChatPage : ContentPage
{
	private readonly ChatViewModel _vm;
	private bool _loadingPage;

	/// <summary>The edit field lives in a panel built on first use (see <see cref="Controls.LazyHost"/>): null until then.</summary>
	private Editor? EditField => (EditHost.Content as Element)?.FindByName<Editor>("EditField");

	public ChatPage(ChatViewModel vm)
	{
		FreeSpeech.Mobile.Services.Perf.Mark("ChatPage: constructor starts");
		InitializeComponent();
		FreeSpeech.Mobile.Services.Perf.Mark("ChatPage: XAML loaded");
		BindingContext = _vm = vm;
		vm.ScrollToEndRequested += ScrollToEnd;
		vm.ScrollToItemRequested += ScrollToItem;
		vm.EditClosed += () => { HideKeyboard(); Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), HideKeyboard); };
		vm.EditRequested += () => Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), () => { if (EditField is { } field) { field.Focus(); field.CursorPosition = field.Text?.Length ?? 0; } });
		Unloaded += (_, _) => vm.Detach();
	}

#if ANDROID
	// A long press on a message, a picture or a file. MAUI has no such gesture and the rows swallow Android's own long click, so the
	// activity hands us every touch (see MainActivity): we find the row under the finger and, once it fires, turn the release into
	// a cancel so it does not also count as a tap.
	protected override void OnAppearing()
	{
		base.OnAppearing();
		FreeSpeech.Mobile.Services.Perf.Mark("ChatPage: appearing");
		_vm.RefreshTrust();   // coming back from the safety screen: the check may have changed
		_vm.RefreshIdentity();   // or from the group info: my name there may have changed
		if (Platform.CurrentActivity is { } activity) MainActivity.ChatLongPressDetector = new Android.Views.GestureDetector(activity, new RowLongPress(this));
	}

	protected override void OnDisappearing()
	{
		MainActivity.ChatLongPressDetector = null;
		base.OnDisappearing();
	}

	private sealed class RowLongPress(ChatPage page) : Android.Views.GestureDetector.SimpleOnGestureListener
	{
		public override void OnLongPress(Android.Views.MotionEvent e) => page.LongPressAt(e.RawX, e.RawY);
	}

	private void LongPressAt(float rawX, float rawY)
	{
		if (_vm.IsReactionBarOpen || _vm.IsMenuOpen || _vm.IsEditOpen || _vm.IsHeaderMenuOpen || _vm.IsSearchOpen || MessagesView.Handler?.PlatformView is not AndroidX.RecyclerView.Widget.RecyclerView list) return;
		var where = new int[2];
		list.GetLocationOnScreen(where);
		var child = list.FindChildViewUnder(rawX - where[0], rawY - where[1]);
		if (child == null) return;
		var position = list.GetChildAdapterPosition(child);
		if (position < 0 || position >= _vm.Items.Count) return;

		MainActivity.ChatLongPressFired = true;
		list.PerformHapticFeedback(Android.Views.FeedbackConstants.LongPress);
		var item = _vm.Items[position];
		MainThread.BeginInvokeOnMainThread(() => _vm.LongPressCommand.Execute(item));
	}
#endif

	/// <summary>A tap on the album mosaic: the viewer opens on the picture that was touched.</summary>
	private void OnAlbumTapped(object? sender, TappedEventArgs e)
	{
		if (sender is not View view || view.BindingContext is not ChatItem item || item.AlbumCells is not { Count: > 0 } cells) return;
		var p = e.GetPosition(view);
		var cell = (p == null ? null : cells.FirstOrDefault(c => p.Value.X >= c.X && p.Value.X <= c.X + c.W && p.Value.Y >= c.Y && p.Value.Y <= c.Y + c.H)) ?? cells[0];
		_vm.OpenImageCommand.Execute(cell.Item);
	}

	/// <summary>The edit field closed: the keyboard folds away. The window's own token is used, because the field that had the focus is already hidden.</summary>
	private void HideKeyboard()
	{
#if ANDROID
		if (Platform.CurrentActivity is { } activity && activity.Window?.DecorView?.WindowToken is { } token
			&& activity.GetSystemService(Android.Content.Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager imm)
			imm.HideSoftInputFromWindow(token, Android.Views.InputMethods.HideSoftInputFlags.None);
#endif
		EditField?.Unfocus();
	}
	/// <summary>The message is on its way: the keyboard folds away, so the conversation is visible again.</summary>
	private void OnSendClicked(object? sender, EventArgs e)
	{
#if ANDROID
		// The token comes from the focused view, so it is read before the focus is dropped.
		if (Platform.CurrentActivity is { } activity && activity.CurrentFocus?.WindowToken is { } token
			&& activity.GetSystemService(Android.Content.Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager imm)
			imm.HideSoftInputFromWindow(token, 0);
#endif
		Composer.Unfocus();
	}

	/// <summary>
	/// Jumps to the newest message. Asked right after the list changed, the native list has not measured its rows yet, so the
	/// jump is made twice: at once, and again once the layout has settled.
	/// </summary>
	private void ScrollToEnd(int? firstUnread)
	{
		void Jump()
		{
			FreeSpeech.Mobile.Services.Perf.Mark("ChatPage: scroll to the last line");
			if (_vm.Items.Count == 0) return;
			// Few new messages: all of them are visible with the last one at the bottom. Too many: the first new one goes to the top.
			if (firstUnread is { } index && MessagesView.Height > 0 && _vm.EstimatedHeightFrom(index) > MessagesView.Height)
				MessagesView.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
			else
				MessagesView.ScrollTo(_vm.Items.Count - 1, position: ScrollToPosition.End, animate: false);
		}
		if (firstUnread == null)
		{
			Dispatcher.Dispatch(Jump);
			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), Jump);
			return;
		}

		// Opened on new messages: whether the first one goes to the top or the end is shown needs the size of the list, which is not known
		// yet when the app starts from a notification. Wait for it (a few seconds at most) instead of falling back to the end.
		var tries = 0;
		void Attempt()
		{
			if (MessagesView.Height <= 0 && ++tries < 25) { Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), Attempt); return; }
			Jump();
			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), Jump);
		}
		Dispatcher.Dispatch(Attempt);
	}

	/// <summary>Centres a line (the message a search led to); done twice, like <see cref="ScrollToEnd"/>, because the rows are not measured yet.</summary>
	private void ScrollToItem(int index)
	{
		void Jump()
		{
			if (index >= 0 && index < _vm.Items.Count) MessagesView.ScrollTo(index, position: ScrollToPosition.Center, animate: false);
		}
		Dispatcher.Dispatch(Jump);
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), Jump);
	}

	/// <summary>How many lines from an end the next page is read: a little before the user gets there, so a fling does not run into the end.</summary>
	private const int Prefetch = 10;

	/// <summary>
	/// Scrolled near the top: load the next older page; near the bottom after reading far back: the next newer one. What is on screen stays
	/// exactly where it is, to the pixel, whatever was added or let go (the screen is not snapped to the top of a line, which jolted a fling).
	/// </summary>
	private void OnScrolled(object? sender, ItemsViewScrolledEventArgs e)
	{
		_vm.UpdateBottomButton(e.LastVisibleItemIndex);
		if (_loadingPage) return;
		var older = e.VerticalDelta < 0 && e.FirstVisibleItemIndex <= Prefetch && _vm.HasMoreBefore;
		var newer = e.VerticalDelta > 0 && e.LastVisibleItemIndex >= _vm.Items.Count - Prefetch && _vm.HasMoreAfter;
		if (!older && !newer) return;

		_loadingPage = true;
		try
		{
			var anchor = _vm.Items.Count == 0 ? null : _vm.Items[Math.Clamp(e.FirstVisibleItemIndex, 0, _vm.Items.Count - 1)].Id;
			var offset = TopOffsetOf(e.FirstVisibleItemIndex);
			var changed = older ? _vm.LoadOlder() > 0 : _vm.LoadNewer();
			if (changed && anchor != null) Dispatcher.Dispatch(() => KeepPlace(anchor, offset));
		}
		finally
		{
			_loadingPage = false;
		}
	}

	/// <summary>How far the first visible line starts from the top edge of the list, in pixels (negative when it is partly above).</summary>
	private int TopOffsetOf(int firstVisible)
	{
#if ANDROID
		if (MessagesView.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView list && list.GetLayoutManager() is AndroidX.RecyclerView.Widget.LinearLayoutManager layout)
			return layout.FindViewByPosition(firstVisible)?.Top - list.PaddingTop ?? 0;
#endif
		return 0;
	}

	/// <summary>Puts the line that was at the top back at the same distance from the edge.</summary>
	private void KeepPlace(string anchor, int offset)
	{
		var at = _vm.IndexOfLine(anchor);
		if (at < 0) return;
#if ANDROID
		if (MessagesView.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView list && list.GetLayoutManager() is AndroidX.RecyclerView.Widget.LinearLayoutManager layout)
		{
			layout.ScrollToPositionWithOffset(at, offset + list.PaddingTop);
			return;
		}
#endif
		MessagesView.ScrollTo(at, position: ScrollToPosition.Start, animate: false);
	}
}