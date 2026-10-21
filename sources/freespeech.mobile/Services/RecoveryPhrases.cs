/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>
/// The recovery phrases are never kept on the phone: whoever picks it up must not be able to read them. Earlier versions stored the ones
/// the app drew; this removes that copy.
/// </summary>
public static class RecoveryPhrases
{
	public static async Task PurgeAsync()
	{
		try { SecureStorage.Default.Remove("recovery_phrases"); }
		catch (Exception) { /* secure storage can be unavailable: nothing was stored then */ }
		await Task.CompletedTask;
	}
}

/// <summary>
/// A migration under way (PROTOCOL §12): the OLD phrases the user typed to sign the notice, held in memory until the notice is sent. The new
/// phrases travel in <see cref="NewIdentityDraft"/> through the save screen. Nothing here is ever stored.
/// </summary>
public static class MigrationDraft
{
	public static string[]? OldPhrases { get; private set; }

	public static bool Active => OldPhrases != null;

	public static void Set(string[] oldPhrases) => OldPhrases = oldPhrases;

	public static void Clear() => OldPhrases = null;
}

/// <summary>
/// A new identity that does not exist yet: the three phrases the app drew and the first name, held in memory only while the user saves the
/// phrases. The identity is created once that step is left, so quitting in the middle leaves nothing behind.
/// </summary>
public static class NewIdentityDraft
{
	public static string[]? Phrases { get; private set; }
	public static string Nickname { get; private set; } = "";
	public static string ServerUrl { get; private set; } = "";

	public static void Set(string[] phrases, string nickname, string serverUrl) => (Phrases, Nickname, ServerUrl) = (phrases, nickname, serverUrl);

	public static void Clear() => (Phrases, Nickname, ServerUrl) = (null, "", "");
}