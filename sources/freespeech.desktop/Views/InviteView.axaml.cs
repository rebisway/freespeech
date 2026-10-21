/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Controls;
using Avalonia.Input.Platform;
using FreeSpeech.Desktop.ViewModels;

namespace FreeSpeech.Desktop.Views;

public partial class InviteView : UserControl
{
	public InviteView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			if (DataContext is not InviteViewModel vm) return;
			vm.WriteClipboard = async text => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
			vm.ReadClipboard = async () => TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
		};
	}
}