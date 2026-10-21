/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Controls;

/// <summary>
/// Holds a panel that is built the first time <see cref="IsOpen"/> becomes true, not when the page is created: MAUI builds the native views of a
/// hidden element too, which made opening a conversation slow. The built panel keeps its own visibility binding and is never torn down.
/// </summary>
public sealed class LazyHost : ContentView
{
	public static readonly BindableProperty IsOpenProperty = BindableProperty.Create(nameof(IsOpen), typeof(bool), typeof(LazyHost), false, propertyChanged: (b, _, _) => ((LazyHost)b).Build());

	public bool IsOpen
	{
		get => (bool)GetValue(IsOpenProperty);
		set => SetValue(IsOpenProperty, value);
	}

	private DataTemplate? _template;

	public DataTemplate? Template
	{
		get => _template;
		set { _template = value; Build(); }
	}

	private void Build()
	{
		if (Content != null || !IsOpen || _template == null) return;
		Content = (View)_template.CreateContent();
	}
}
