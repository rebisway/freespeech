/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Windows.Input;

namespace FreeSpeech.Mobile.Controls;

/// <summary>A large tappable line (icon, title, a short hint) for screens meant to be understood at a glance.</summary>
public partial class ActionRow : ContentView
{
	public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(ActionRow));
	public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(ActionRow), "", propertyChanged: (b, _, _) => ((ActionRow)b).Describe());
	public static readonly BindableProperty HintProperty = BindableProperty.Create(nameof(Hint), typeof(string), typeof(ActionRow), "", propertyChanged: (b, _, _) => ((ActionRow)b).Describe());
	public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ActionRow));

	public ActionRow() => InitializeComponent();

	public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
	public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
	public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

	public bool HasHint => !string.IsNullOrEmpty(Hint);

	/// <summary>Screen readers read the title and the hint as one sentence.</summary>
	private void Describe()
	{
		OnPropertyChanged(nameof(HasHint));
		SemanticProperties.SetDescription(this, HasHint ? $"{Title}. {Hint}" : Title);
	}
}
