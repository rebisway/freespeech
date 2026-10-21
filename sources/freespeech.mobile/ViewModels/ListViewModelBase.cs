/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>Base of the screens that only list things and show one status line.</summary>
public abstract partial class ListViewModelBase : ObservableObject
{
	[ObservableProperty] private string _status = "";
}
