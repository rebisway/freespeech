/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Desktop.Services;

/// <summary>Loads the compressed public-domain book of the system language (embedded "Book.&lt;lang&gt;.txt.gz"); English when there is none.</summary>
public static class BookLoader
{
	private static readonly Dictionary<string, PhraseGenerator> Cache = [];

	public static async Task<PhraseGenerator> LoadAsync(string language)
	{
		var assembly = typeof(BookLoader).Assembly;
		foreach (var lang in new[] { language, "en" }.Distinct())
		{
			if (Cache.TryGetValue(lang, out var cached)) return cached;
			await using var file = assembly.GetManifestResourceStream($"Book.{lang}.txt.gz");
			if (file == null) continue;   // no book for this language: the next one
			var generator = await Task.Run(() => PhraseGenerator.FromGzip(file));
			Cache[lang] = generator;
			return generator;
		}
		throw new FileNotFoundException("No book is shipped with the app.");
	}
}
