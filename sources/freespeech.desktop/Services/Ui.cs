/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace FreeSpeech.Desktop.Services;

/// <summary>What the ViewModels ask of the window: a question, a message, a file to open or to save.</summary>
public static class Ui
{
	/// <summary>The main window (the owner of every dialog).</summary>
	public static Window? Main { get; set; }

	/// <summary>A question with two answers (a tick and a cross). True for the first, which is also Enter; Escape answers the second.</summary>
	public static async Task<bool> ConfirmAsync(string title, string text, string yes, string no)
	{
		var result = false;
		Window? window = null;
		var body = new StackPanel { Spacing = 16, Children = { Heading(title), Body(text), Buttons(() => window!, (yes, "Icon.check", true, () => result = true), (no, "Icon.close", false, () => result = false)) } };
		window = Frame(title, 400, body, cancel: () => result = false, accept: null);   // Enter does not confirm a question
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
		return result;
	}

	/// <param name="created">Gets the dialog, so that the caller can close it when what it shows is over.</param>
	public static async Task AlertAsync(string title, string text, string ok, Action<Window>? created = null)
	{
		Window? window = null;
		var body = new StackPanel { Spacing = 16, Children = { Heading(title), Body(text), Buttons(() => window!, (ok, "Icon.check", true, () => { })) } };
		window = Frame(title, 400, body, cancel: () => { }, accept: () => { });
		created?.Invoke(window);
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
	}

	/// <summary>Asks to pick one of several lines. The index, or -1 when cancelled.</summary>
	public static async Task<int> ChooseAsync(string title, IReadOnlyList<string> items, string cancel)
	{
		var chosen = -1;
		Window? window = null;
		var shown = Enumerable.Range(0, items.Count).ToList();   // the original index of each line the search lets through
		var list = new ListBox { ItemsSource = items.ToList(), Height = 280, Background = Avalonia.Media.Brushes.Transparent };
		int Current() => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : -1;
		list.DoubleTapped += (_, _) => { chosen = Current(); window!.Close(); };
		var search = new TextBox { PlaceholderText = Loc.T("pick_search"), IsVisible = items.Count > 10 };
		search.TextChanged += (_, _) =>
		{
			var q = (search.Text ?? "").Trim();
			shown = Enumerable.Range(0, items.Count).Where(i => q.Length == 0 || items[i].Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
			list.ItemsSource = shown.Select(i => items[i]).ToList();
		};
		var body = new StackPanel { Spacing = 14, Children = { Heading(title), search, list, Buttons(() => window!, (title, "Icon.check", true, () => chosen = Current()), (cancel, "Icon.close", false, () => chosen = -1)) } };
		window = Frame(title, 380, body, cancel: () => chosen = -1, accept: () => chosen = Current());
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
		return chosen;
	}
	/// <summary>Asks to tick any number of lines (at most <paramref name="max"/> when it is above 0; a search box hides the lines that do not match, the ticks stay). The ticked indexes, or null when cancelled.</summary>
	public static async Task<List<int>?> ChooseManyAsync(string title, IReadOnlyList<string> items, string ok, string cancel, int max = 0)
	{
		List<int>? chosen = null;
		Window? window = null;
		var picked = new SortedSet<int>();
		var count = new TextBlock { FontSize = 12, Opacity = 0.7 };
		void Recount(bool full = false) => count.Text = full ? Loc.T("pick_max", max) : Loc.T("pick_count", picked.Count);
		Recount();
		var rows = new List<(Control Row, string Text)>();
		var stack = new StackPanel();
		for (var n = 0; n < items.Count; n++)
		{
			var index = n;
			var box = new CheckBox { Content = items[n], Margin = new Thickness(4, 2) };
			box.IsCheckedChanged += (_, _) =>
			{
				if (box.IsChecked == true)
				{
					if (max > 0 && picked.Count >= max && !picked.Contains(index)) { box.IsChecked = false; Recount(true); return; }   // the limit: the box is released
					picked.Add(index);
				}
				else picked.Remove(index);
				Recount();
			};
			stack.Children.Add(box);
			rows.Add((box, items[n]));
		}
		var search = new TextBox { PlaceholderText = Loc.T("pick_search"), IsVisible = items.Count > 10 };
		search.TextChanged += (_, _) =>
		{
			var q = (search.Text ?? "").Trim();
			foreach (var (row, text) in rows) row.IsVisible = q.Length == 0 || text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
		};
		var body = new StackPanel { Spacing = 10, Children = { Heading(title), search, count, new ScrollViewer { Height = 280, Content = stack }, Buttons(() => window!, (ok, "Icon.check", true, () => chosen = picked.ToList()), (cancel, "Icon.close", false, () => chosen = null)) } };
		window = Frame(title, 380, body, cancel: () => chosen = null, accept: () => chosen = picked.ToList());
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
		return chosen;
	}
	/// <summary>Asks for a line of text. Null when cancelled.</summary>
	public static async Task<string?> PromptAsync(string title, string text, string ok, string cancel, string initial = "", int maxLength = 64)
	{
		string? result = null;
		Window? window = null;
		var input = new TextBox { Text = initial, MaxLength = maxLength };
		var body = new StackPanel { Spacing = 14, Children = { Heading(title), Body(text), input, Buttons(() => window!, (ok, "Icon.check", true, () => result = input.Text ?? ""), (cancel, "Icon.close", false, () => result = null)) } };
		window = Frame(title, 400, body, cancel: () => result = null, accept: () => result = input.Text ?? "");
		window.Opened += (_, _) => { window.Activate(); input.Focus(); input.SelectAll(); };
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
		return result;
	}

	/// <summary>
	/// Joining a group: the group and who invites, the picture (click it to change) and the name I will show there. Null when cancelled;
	/// otherwise the name and the picture file chosen (null when the picture was left as it is).
	/// </summary>
	/// <param name="pickPhoto">Asks for a picture and returns the prepared file, or null.</param>
	public static async Task<(string Name, string? PhotoFile)?> JoinGroupAsync(string title, string text, string nameLabel, string name, string? photo, string photoTip, string ok, string cancel, Func<Task<string?>> pickPhoto)
	{
		(string, string?)? result = null;
		string? photoFile = null;
		Window? window = null;
		var avatar = new FreeSpeech.Desktop.Controls.Avatar { Title = name, Photo = photo, Size = 56 };
		var photoButton = new Button { Content = avatar, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
		photoButton.Classes.Add("flat");
		ToolTip.SetTip(photoButton, photoTip);
		var input = new TextBox { Text = name, MaxLength = 64, VerticalAlignment = VerticalAlignment.Center };
		photoButton.Click += async (_, _) =>
		{
			if (await pickPhoto() is not { } file) return;
			photoFile = file;
			avatar.Photo = FreeSpeech.Client.FreeSpeechClient.ReadPicture(file);
		};
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
		row.Children.Add(photoButton);
		Grid.SetColumn(input, 1);
		row.Children.Add(input);
		var body = new StackPanel
		{
			Spacing = 14,
			Children = { Heading(title), Body(text), new TextBlock { Text = nameLabel, FontSize = 12, Opacity = 0.7 }, row,
				Buttons(() => window!, (ok, "Icon.check", true, () => result = (input.Text ?? "", photoFile)), (cancel, "Icon.close", false, () => result = null)) },
		};
		window = Frame(title, 420, body, cancel: () => result = null, accept: () => result = (input.Text ?? "", photoFile));
		window.Opened += (_, _) => { window.Activate(); input.Focus(); input.SelectAll(); };
		if (Main is { } owner) await window.ShowDialog(owner); else window.Show();
		return result;
	}

	/// <summary>Opens a file. Null when cancelled.</summary>
	public static async Task<string?> PickFileAsync(string title, params string[] patterns)
	{
		if (Main?.StorageProvider is not { CanOpen: true } storage) return null;
		var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
		if (patterns.Length > 0) options.FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All];
		var files = await storage.OpenFilePickerAsync(options);
		return files.Count == 0 ? null : files[0].TryGetLocalPath();
	}

	/// <summary>Opens several files at once. Empty when cancelled.</summary>
	public static async Task<IReadOnlyList<string>> PickFilesAsync(string title)
	{
		if (Main?.StorageProvider is not { CanOpen: true } storage) return [];
		var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
		return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
	}

	/// <summary>Asks where to save a file. Null when cancelled.</summary>
	public static async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
	{
		if (Main?.StorageProvider is not { CanSave: true } storage) return null;
		var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension, ShowOverwritePrompt = true });
		return file?.TryGetLocalPath();
	}

	// ------------------------------------------------------------------
	// The look of the dialogs: no system title bar, a rounded card with the gold edge, the question as a gold heading, round icon buttons
	// ------------------------------------------------------------------

	private static object? Res(string key) => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;

	private static TextBlock Heading(string title)
	{
		var block = new TextBlock { Text = title, FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
		if (Res("PrimaryBrush") is Avalonia.Media.IBrush brush) block.Foreground = brush;
		return block;
	}

	private static TextBlock Body(string text) => new() { Text = text, FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

	/// <summary>Round icon buttons on the right: the first with <paramref name="primary"/> is filled gold. Each runs its action, then closes the dialog.</summary>
	private static StackPanel Buttons(Func<Window> window, params (string Tip, string Icon, bool Primary, Action Run)[] buttons)
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
		foreach (var (tip, icon, primary, run) in buttons)
		{
			var glyph = new Avalonia.Controls.PathIcon { Data = Res(icon) as Avalonia.Media.Geometry };
			var button = new Button { Content = glyph, Width = 36, Height = 36 };
			button.Classes.Add("icon");
			if (primary)
			{
				if (Res("PrimaryBrush") is Avalonia.Media.IBrush fill) button.Background = fill;
				if (Res("OnPrimaryBrush") is Avalonia.Media.IBrush ink) glyph.Foreground = ink;
			}
			ToolTip.SetTip(button, tip);
			button.Click += (_, _) => { run(); window().Close(); };
			row.Children.Add(button);
		}
		return row;
	}

	/// <summary>A window without system decorations: the card can be dragged, Enter accepts, Escape cancels.</summary>
	private static Window Frame(string title, double width, Control content, Action cancel, Action? accept)
	{
		var card = new Border { Padding = new Thickness(20, 16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Margin = new Thickness(6), Child = content };
		if (Res("SurfaceBrush") is Avalonia.Media.IBrush surface) card.Background = surface;
		if (Res("PrimaryBrush") is Avalonia.Media.IBrush edge) card.BorderBrush = edge;
		var window = new Window
		{
			Title = title,
			Width = width,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowDecorations = WindowDecorations.None,
			Background = Avalonia.Media.Brushes.Transparent,
			TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false,
			Content = card,
		};
		card.PointerPressed += (_, e) =>
		{
			if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
			// Only the bare card drags the window: a press on a line of a list, on a button's icon or in a field (the source is then a child of it) is theirs.
			if (e.Source is Avalonia.Visual source && source.GetSelfAndVisualAncestors().TakeWhile(v => v != card).Any(v => v is TextBox or Button or ListBox or ListBoxItem)) return;
			window.BeginMoveDrag(e);
		};
		window.KeyDown += (_, e) =>
		{
			if (e.Key == Avalonia.Input.Key.Escape) { cancel(); window.Close(); e.Handled = true; }
			else if (accept != null && e.Key == Avalonia.Input.Key.Enter && e.Source is not ListBox) { accept(); window.Close(); e.Handled = true; }
		};
		return window;
	}
}