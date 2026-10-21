/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

public partial class ConversationsPage : ContentPage
{
	private readonly ConversationsViewModel _vm;

	public ConversationsPage(ConversationsViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_vm.Refresh();
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(600), Services.ChatRoute.Prepare);   // the next conversation opens from a page built while the user looks at the list
	}

	private void OnSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is ConversationItem item)
		{
			_vm.OpenCommand.Execute(item);
			List.SelectedItem = null;   // the row is an action, not a state
		}
	}
}