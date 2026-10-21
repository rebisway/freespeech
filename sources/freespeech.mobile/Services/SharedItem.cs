/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Mobile.Services;

/// <summary>Something another app asked FreeSpeech to send: a file (already copied to our cache) or a piece of text.</summary>
public sealed record SharedItem(string? Path, string? Text, string Name)
{
	public bool IsFile => Path != null;
	public string Label => IsFile ? Name : Text!.Length > 60 ? Text[..60] + "…" : Text;
}

/// <summary>The items waiting for the user to choose the conversations they go to.</summary>
public static class ShareInbox
{
	public static List<SharedItem> Items { get; set; } = [];
}