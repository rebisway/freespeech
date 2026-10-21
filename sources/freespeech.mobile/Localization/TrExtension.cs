/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Localization;

/// <summary>XAML: <c>Text="{loc:Tr nav_contacts}"</c>. A binding to the language table, so the text follows a language change.</summary>
[ContentProperty(nameof(Key))]
public sealed class TrExtension : IMarkupExtension<BindingBase>
{
	public string Key { get; set; } = "";

	public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
		new Binding($"[{Key}]", BindingMode.OneWay, source: Loc.Instance);

	object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
