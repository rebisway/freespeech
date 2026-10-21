/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Keeps the database key in the platform's secure store (Android Keystore; Keychain on iOS), so the recovery phrases are
/// typed once. The client library calls this synchronously, from a worker thread (see <see cref="ClientService"/>):
/// never call it from the UI thread.
/// </summary>
public sealed class SecureStorageKeyCache : IKeyCache
{
	private const string Key = "freespeech.dbkey";

	public byte[]? Load()
	{
		try
		{
			var text = SecureStorage.Default.GetAsync(Key).GetAwaiter().GetResult();
			return text == null ? null : Convert.FromBase64String(text);
		}
		catch
		{
			// The secure store can be wiped (new lock screen, restored device): the phrases then regenerate the key.
			return null;
		}
	}

	public void Store(byte[] key) => SecureStorage.Default.SetAsync(Key, Convert.ToBase64String(key)).GetAwaiter().GetResult();
}