/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.ViewModels;

namespace FreeSpeech.Mobile.Views;

/// <summary>Accepting an invitation to a group: the name and the picture I will show there.</summary>
public partial class GroupJoinPage : ContentPage
{
	public GroupJoinPage(GroupJoinViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
