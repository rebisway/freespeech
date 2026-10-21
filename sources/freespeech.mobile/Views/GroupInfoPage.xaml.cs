/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

public partial class GroupInfoPage : ContentPage
{
	private readonly GroupInfoViewModel _vm;

	public GroupInfoPage(GroupInfoViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
	}

	/// <summary>Coming back from the picture crop: the group's new picture shows.</summary>
	protected override void OnAppearing()
	{
		base.OnAppearing();
		_vm.Refresh();
	}
}
