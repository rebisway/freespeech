/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// Draws the three recovery phrases for the user (PROTOCOL §1), so nobody has to invent them. Each phrase is a passage of a
/// public-domain book — readable, and long enough for the 75-character rule — followed by a few words drawn at random from the
/// 2,048 most frequent words of that book.
///
/// Why the random words: the passage alone is guessable. The book is public, so an attacker can try every starting point
/// (about 200,000 in a novel, ~17.6 bits); the phrases are hashed with nothing slow in between. Each random word adds 11 bits, so a
/// phrase carries about 17.6 + 3 × 11 = 50 bits and the three together about 150 bits: out of reach whatever the hardware.
/// </summary>
public sealed class PhraseGenerator
{
    /// <summary>Same as the identity rule: a phrase must have at least this many characters.</summary>
    public const int MinPhraseLength = 75;

    /// <summary>Random words appended to each passage.</summary>
    public const int RandomWords = 3;

    /// <summary>Size of the word list the random words come from (11 bits each).</summary>
    public const int DictionarySize = 2048;

    private readonly string[] _tokens;       // the book, word by word (with its punctuation)
    private readonly string[] _dictionary;   // the most frequent plain words of the book

    public PhraseGenerator(string corpus)
    {
        // Each word keeps letters, digits and a little plain punctuation: curly quotes, guillemets and dashes would be a pain to retype
        // from a sheet of paper on another keyboard.
        _tokens = corpus.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(t => t.Length > 0).ToArray();
        if (_tokens.Length < 5000) throw new ArgumentException("The text is too short to draw phrases from.", nameof(corpus));

        _dictionary = Regex.Matches(corpus.ToLower(CultureInfo.InvariantCulture), @"\p{L}{4,9}")
            .Select(m => m.Value)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(DictionarySize)
            .Select(g => g.Key)
            .ToArray();
        if (_dictionary.Length < DictionarySize) throw new ArgumentException("The text has too few different words.", nameof(corpus));
    }

    private static string Plain(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or ',' or ';' or ':' or '\'' or '-' or '!' or '?') sb.Append(c);
            else if (c == '’') sb.Append('\'');   // the typographic apostrophe becomes the plain one
        }
        return sb.ToString();
    }

    /// <summary>Reads a gzip-compressed UTF-8 text (how the app ships its books).</summary>
    public static PhraseGenerator FromGzip(Stream gzipped)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, new UTF8Encoding(false));
        return new PhraseGenerator(reader.ReadToEnd());
    }

    /// <summary>The word list the random words come from (for tests and documentation).</summary>
    public IReadOnlyList<string> Dictionary => _dictionary;

    /// <summary>Roughly how many bits one phrase carries: the passage's starting point plus the random words.</summary>
    public double BitsPerPhrase => Math.Log2(_tokens.Length) + RandomWords * Math.Log2(DictionarySize);

    /// <summary>Three different phrases, each at least <see cref="MinPhraseLength"/> characters long.</summary>
    /// <param name="nextInt">Random integer in [0, max): the system's secure generator by default; tests pass a seeded one.</param>
    public string[] Generate(Func<int, int>? nextInt = null)
    {
        nextInt ??= max => RandomNumberGenerator.GetInt32(max);

        var phrases = new List<string>();
        while (phrases.Count < 3)
        {
            var phrase = One(nextInt);
            if (!phrases.Contains(phrase)) phrases.Add(phrase);
        }
        return [.. phrases];
    }

    private string One(Func<int, int> nextInt)
    {
        var extra = string.Join(' ', Enumerable.Range(0, RandomWords).Select(_ => _dictionary[nextInt(_dictionary.Length)]));

        // Passage: consecutive words from a random place until the whole phrase (passage, space, random words) is long enough.
        var start = nextInt(_tokens.Length - 40);
        var sb = new StringBuilder();
        for (var i = start; i < _tokens.Length; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(_tokens[i]);
            if (sb.Length + 1 + extra.Length >= MinPhraseLength + 5) break;   // a margin: the user may retype it
        }
        return sb + " " + extra;
    }
}
