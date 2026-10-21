/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

public partial class OutboxPage : ContentPage
{
	private readonly OutboxViewModel _vm;

	public OutboxPage(OutboxViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_vm.Refresh();
	}
}
