/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile;

/// <summary>
/// The questions the app asks, in the app's own look: a card with the gold edge over the dimmed screen, the answers as round icon buttons (a tick, a cross)
/// whose words are what a screen reader says. A tap outside the card, or the back button, is a "no". Same shapes as the system dialogs they stand for.
/// </summary>
public static class Dialogs
{
	/// <summary>A message (one tick) or a question (a tick and a cross when <paramref name="cancel"/> is given). True for the tick.</summary>
	public static async Task<bool> AlertAsync(string title, string message, string accept, string? cancel = null)
	{
		var page = new DialogPage(false, title, message, accept, cancel, null, 0, null, "", null);
		return await page.ShowAsync() is true;
	}

	/// <summary>Asks for a line of text (a free text of several lines when it may be long; hidden when <paramref name="secret"/>). Null when cancelled.</summary>
	public static async Task<string?> PromptAsync(string title, string message, string accept = "OK", string cancel = "Cancel", string? placeholder = null, int maxLength = -1, Keyboard? keyboard = null, string initialValue = "", bool secret = false)
	{
		var page = new DialogPage(true, title, message, accept, cancel, null, maxLength, keyboard, initialValue, placeholder) { Secret = secret };
		return await page.ShowAsync() as string;
	}

	/// <summary>Asks to tick any number of lines. The ticked indexes (maybe none), or null when cancelled.</summary>
	public static async Task<List<int>?> ChooseManyAsync(string title, string accept, string cancel, int max, params string[] items)
	{
		var page = new DialogPage(false, title, "", accept, cancel, items, 0, null, "", null, multi: true, max: max);
		return await page.ShowAsync() as List<int>;
	}

	/// <summary>Asks to select one line among radio buttons, <paramref name="current"/> first selected, and confirm with the tick. The index chosen, or null when cancelled.</summary>
	public static async Task<int?> ChooseRadioAsync(string title, string accept, string cancel, int current, params string[] items)
	{
		var page = new DialogPage(false, title, "", accept, cancel, items, 0, null, "", null, radio: current);
		return await page.ShowAsync() as int?;
	}

	/// <summary>Asks to pick one of several lines. The line chosen, or <paramref name="cancel"/> when nothing was.</summary>
	public static async Task<string?> ChooseAsync(string title, string? cancel, string? destruction, params string[] buttons)
	{
		var items = destruction == null ? buttons : [destruction, .. buttons];
		var page = new DialogPage(false, title, "", "", cancel, items, 0, null, "", null);
		return await page.ShowAsync() as string ?? cancel;
	}
}

internal sealed class DialogPage : ContentPage
{
	private readonly TaskCompletionSource<object?> _done = new();
	private readonly Entry? _entry;
	private readonly Editor? _editor;
	private bool _closed;

	/// <summary>The text typed is hidden (a PIN).</summary>
	public bool Secret { init { if (value && _entry != null) _entry.IsPassword = true; } }

	public DialogPage(bool asksText, string title, string message, string accept, string? cancel, string[]? choices, int maxLength, Keyboard? keyboard, string initial, string? placeholder, bool multi = false, int max = 0, int radio = -1)
	{
		var radioIndex = radio;
		var picked = new SortedSet<int>();
		BackgroundColor = Color.FromArgb("#99000000");
		Shell.SetNavBarIsVisible(this, false);

		var body = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(18, 16) };
		body.Add(new Label { Text = title, FontFamily = "OpenSansSemibold", FontSize = 17, TextColor = Res<Color>("Primary") });
		if (message.Length > 0) body.Add(new Label { Text = message, FontSize = 14 });

		if (asksText && maxLength is < 0 or >= 200)
		{
			_editor = new Editor { Text = initial, Placeholder = placeholder, Keyboard = keyboard ?? Keyboard.Chat, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80, MaximumHeightRequest = 200 };
			if (maxLength > 0) _editor.MaxLength = maxLength;
			body.Add(_editor);
		}
		else if (asksText)
		{
			_entry = new Entry { Text = initial, Placeholder = placeholder, Keyboard = keyboard ?? Keyboard.Default };
			if (maxLength > 0) _entry.MaxLength = maxLength;
			_entry.Completed += (_, _) => Close(_entry.Text ?? "");
			body.Add(_entry);
		}

		if (choices != null)
		{
			var list = new VerticalStackLayout { Spacing = 0 };
			var rows = new List<(View Row, string Text)>();
			Label? count = null;
			if (multi)
			{
				count = new Label { FontSize = 13, TextColor = Res<Color>("Gray200") };
				void Recount(bool full = false)
				{
					count.Text = full ? FreeSpeech.Mobile.Localization.Loc.T("pick_max", max) : FreeSpeech.Mobile.Localization.Loc.T("pick_count", picked.Count);
					count.TextColor = Res<Color>(full ? "Danger" : "Gray200");
				}
				Recount();
				for (var n = 0; n < choices.Length; n++)
				{
					var index = n;
					var box = new CheckBox { VerticalOptions = LayoutOptions.Center };
					box.CheckedChanged += (_, e) =>
					{
						if (e.Value && max > 0 && picked.Count >= max && !picked.Contains(index)) { box.IsChecked = false; Recount(true); return; }   // the limit: the box is released
						if (e.Value) picked.Add(index); else picked.Remove(index);
						Recount();
					};
					var line = new HorizontalStackLayout { Spacing = 6, Children = { box, new Label { Text = choices[n], FontSize = 15, VerticalOptions = LayoutOptions.Center } } };
					var toggle = new TapGestureRecognizer();
					toggle.Tapped += (_, _) => box.IsChecked = !box.IsChecked;
					line.GestureRecognizers.Add(toggle);
					list.Add(line);
					rows.Add((line, choices[n]));
				}
			}
			else if (radio >= 0)
			{
				var group = Guid.NewGuid().ToString();
				for (var n = 0; n < choices.Length; n++)
				{
					var index = n;
					var button = new RadioButton { Content = choices[n], GroupName = group, IsChecked = n == radio, FontSize = 15, Padding = new Thickness(4, 6) };
					button.CheckedChanged += (_, e) => { if (e.Value) radioIndex = index; };
					list.Add(button);
					rows.Add((button, choices[n]));
				}
			}
			else
			{
				foreach (var choice in choices)
				{
					var row = new Label { Text = choice, FontSize = 15, Padding = new Thickness(4, 12) };
					var tap = new TapGestureRecognizer();
					tap.Tapped += (_, _) => Close(choice);
					row.GestureRecognizers.Add(tap);
					list.Add(row);
					rows.Add((row, choice));
				}
			}
			if (choices.Length > 10)
			{
				// A long list (hundreds of contacts or conversations): a search box hides the lines that do not match; the ticks stay.
				var search = new Entry { Placeholder = FreeSpeech.Mobile.Localization.Loc.T("pick_search"), ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
				search.TextChanged += (_, e) =>
				{
					var q = (e.NewTextValue ?? "").Trim();
					foreach (var (row, text) in rows) row.IsVisible = q.Length == 0 || text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
				};
				body.Add(search);
			}
			if (count != null) body.Add(count);
			body.Add(new ScrollView { Content = list, MaximumHeightRequest = 360 });
		}

		var buttons = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.End };
		if (cancel != null) buttons.Add(Round("ic_close.png", cancel, false, () => Close(null)));
		if (multi) buttons.Add(Round("ic_check.png", accept, true, () => Close(picked.ToList())));
		else if (radio >= 0) buttons.Add(Round("ic_check.png", accept, true, () => Close(radioIndex)));
		else if (choices == null) buttons.Add(Round("ic_check.png", accept, true, () => Close(_entry != null ? _entry.Text ?? "" : _editor != null ? _editor.Text ?? "" : true)));
		body.Add(buttons);

		var card = new Border
		{
			Content = body,
			BackgroundColor = Res<Color>("Gray900"),
			Stroke = Res<Color>("Primary"),
			StrokeThickness = 1,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(14) },
			VerticalOptions = LayoutOptions.Center,
			Margin = new Thickness(24, 0),
		};
		card.GestureRecognizers.Add(new TapGestureRecognizer());   // a tap on the card is not a tap outside

		var overlay = new Grid { Children = { card } };
		var outside = new TapGestureRecognizer();
		outside.Tapped += (_, _) => Close(null);
		overlay.GestureRecognizers.Add(outside);
		Content = overlay;
		Appearing += (_, _) => { (_entry as View ?? _editor)?.Focus(); };
	}

	private static T Res<T>(string key) => Application.Current!.Resources.TryGetValue(key, out var value) && value is T typed ? typed : default!;

	private ImageButton Round(string icon, string words, bool primary, Action click)
	{
		var button = new ImageButton
		{
			Source = icon, WidthRequest = 44, HeightRequest = 44, Padding = 10, CornerRadius = 22,
			BackgroundColor = Res<Color>("Gray600"),   // the icon is gold: on a gold button it would not be seen (primary only tells the tick from the cross by its glyph)
		};
		SemanticProperties.SetDescription(button, words);
		button.Clicked += (_, _) => click();
		return button;
	}

	/// <summary>The tick answers true (or the text), a cross or a tap outside answers null.</summary>
	private void Close(object? answer)
	{
		if (_closed) return;
		_closed = true;
		// The answer is given once the card is gone: a caller that asks the next question at once (a menu, then a text) finds a clean stack, and
		// the late pop of the first card can no longer take the second one away.
		if (Application.Current?.Dispatcher is not { } dispatcher) { _done.TrySetResult(answer); return; }
		_ = dispatcher.DispatchAsync(async () =>
		{
			try { await Navigation.PopModalAsync(false); }
			catch (InvalidOperationException) { /* already gone */ }
			finally { _done.TrySetResult(answer); }
		});
	}

	protected override bool OnBackButtonPressed()
	{
		Close(null);
		return true;
	}

	public async Task<object?> ShowAsync()
	{
		await Application.Current!.Windows[0].Page!.Navigation.PushModalAsync(this, false);
		return await _done.Task;
	}
}
