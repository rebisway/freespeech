/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FreeSpeech.Desktop.Controls;

public partial class ActionRow : UserControl
{
	public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<ActionRow, Geometry?>(nameof(Icon));
	public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<ActionRow, string>(nameof(Title), "");
	public static readonly StyledProperty<string> HintProperty = AvaloniaProperty.Register<ActionRow, string>(nameof(Hint), "");
	public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<ActionRow, ICommand?>(nameof(Command));

	public ActionRow() => InitializeComponent();

	public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
	public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public string Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }
	public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

	public bool HasHint => Hint.Length > 0;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == HintProperty) RaisePropertyChanged(HasHintProperty, !HasHint, HasHint);
	}

	public static readonly DirectProperty<ActionRow, bool> HasHintProperty = AvaloniaProperty.RegisterDirect<ActionRow, bool>(nameof(HasHint), r => r.HasHint);
}
