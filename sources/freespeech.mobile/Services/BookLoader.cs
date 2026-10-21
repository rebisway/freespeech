/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/using FreeSpeech.Client.Identity;

namespace FreeSpeech.Mobile.Services;

/// <summary>Loads the compressed public-domain book of the app's language (Resources/Raw/books/&lt;lang&gt;.txt.gz); English when there is none.</summary>
public static class BookLoader
{
	private static readonly Dictionary<string, PhraseGenerator> Cache = [];

	public static async Task<PhraseGenerator> LoadAsync(string language)
	{
		foreach (var lang in new[] { language, "en" }.Distinct())
		{
			if (Cache.TryGetValue(lang, out var cached)) return cached;
			try
			{
				await using var file = await FileSystem.OpenAppPackageFileAsync($"books/{lang}.txt.gz");
				var generator = await Task.Run(() => PhraseGenerator.FromGzip(file));
				Cache[lang] = generator;
				return generator;
			}
			catch (FileNotFoundException) { /* no book for this language: the next one */ }
		}
		throw new FileNotFoundException("No book is shipped with the app.");
	}
}