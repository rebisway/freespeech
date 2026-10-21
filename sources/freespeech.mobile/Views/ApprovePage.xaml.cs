/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;
public partial class ApprovePage : ContentPage
{
	private readonly ApproveViewModel _vm;

	public ApprovePage(ApproveViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Services.ClientService.Notifier?.Cancel("//approve");   // the requests are on screen: the phone notification is no use any more
		_vm.Refresh();
	}
}