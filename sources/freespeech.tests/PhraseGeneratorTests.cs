/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

/// <summary>The recovery phrases drawn from the books shipped in the app (the real compressed files).</summary>
public class PhraseGeneratorTests
{
    private static string BooksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "freespeech.mobile"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("folder holding freespeech.mobile"), "freespeech.mobile", "Resources", "Raw", "books");
    }

    private static PhraseGenerator For(string lang)
    {
        using var file = File.OpenRead(Path.Combine(BooksDir(), lang + ".txt.gz"));
        return PhraseGenerator.FromGzip(file);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public void Phrases_areLongEnough_distinct_andAcceptedByTheIdentity(string lang)
    {
        var g = For(lang);
        for (var round = 0; round < 200; round++)
        {
            var p = g.Generate();
            Assert.Equal(3, p.Distinct().Count());
            // Only characters one can retype from paper: letters (with the language's accents), digits, spaces, plain punctuation.
            Assert.All(p, x => Assert.All(x, c => Assert.True(char.IsLetterOrDigit(c) || " .,;:'-!?".Contains(c), $"'{c}' in: {x}")));
            Assert.All(p, x => { Assert.True(x.Length >= PhraseGenerator.MinPhraseLength, x); Assert.True(IdentityService.IsPhraseValid(x)); });

            // The three phrases really make an identity.
            var id = IdentityService.Derive(p[0], p[1], p[2], "Tom");
            Assert.Equal(64, id.DiscoveryId.Length);
        }
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public void TheWordListHasExactly2048Words_andTheDrawHasEnoughEntropy(string lang)
    {
        var g = For(lang);
        Assert.Equal(PhraseGenerator.DictionarySize, g.Dictionary.Count);
        Assert.Equal(g.Dictionary.Count, g.Dictionary.Distinct().Count());
        Assert.True(g.BitsPerPhrase * 3 > 128, $"{g.BitsPerPhrase * 3:F0} bits");   // the three phrases together
    }

    [Fact]
    public void ARandomSource_makesTheDrawReproducible_andTwoDrawsDiffer()
    {
        var g = For("fr");
        var a = g.Generate(new Random(7).Next);
        var b = g.Generate(new Random(7).Next);
        Assert.Equal(a, b);
        Assert.NotEqual(a, g.Generate());                 // the real source is never the same twice
        Assert.NotEqual(g.Generate(), g.Generate());
    }

    [Fact]
    public void TheRandomWordsAreDrawnFromTheBookItself()
    {
        var g = For("en");
        var p = g.Generate()[0];
        var lastWords = p.Split(' ')[^PhraseGenerator.RandomWords..];
        Assert.All(lastWords, w => Assert.Contains(w, g.Dictionary));
    }
}
