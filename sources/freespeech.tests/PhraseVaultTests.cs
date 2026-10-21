/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.Identity;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>The key file (phrases under a password) and the printed sheet's QR text.</summary>
public class PhraseVaultTests
{
    private static readonly IdentityService.KdfCost Cheap = new(64, 1);
    private static readonly string[] Phrases = ["premiere phrase assez longue", "deuxieme phrase avec des accents é à ç", "troisieme phrase"];

    [Fact]
    public void SealThenOpenGivesTheSamePhrases()
    {
        var file = PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap);
        Assert.True(PhraseVault.LooksLikeVault(file));
        Assert.Equal(Phrases, PhraseVault.Open(file, "un mot de passe solide"));
    }

    [Fact]
    public void AWrongPasswordIsRefusedAndTheFileNeverShowsThePhrases()
    {
        var file = PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap);
        var ex = Assert.Throws<ClientException>(() => PhraseVault.Open(file, "un autre mot de passe"));
        Assert.Equal("vault_wrong_password", ex.Code);
        Assert.DoesNotContain("premiere", System.Text.Encoding.UTF8.GetString(file));
    }

    [Fact]
    public void ATamperedFileIsRefused()
    {
        var file = PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap);
        file[^3] ^= 0x01;
        Assert.Throws<ClientException>(() => PhraseVault.Open(file, "un mot de passe solide"));

        var costChanged = PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap);
        costChanged[12] ^= 0x01;   // the cost is authenticated too
        Assert.Throws<ClientException>(() => PhraseVault.Open(costChanged, "un mot de passe solide"));
    }

    [Fact]
    public void ShortPasswordsAndForeignFilesAreRefused()
    {
        Assert.Equal("vault_password_short", Assert.Throws<ClientException>(() => PhraseVault.Seal(Phrases, "court", Cheap)).Code);
        Assert.False(PhraseVault.LooksLikeVault(new byte[200]));
        Assert.Equal("vault_bad_file", Assert.Throws<ClientException>(() => PhraseVault.Open(new byte[200], "un mot de passe solide")).Code);
    }

    [Fact]
    public void AHostileCostInTheFileIsRefusedBeforeAnythingIsAllocated()
    {
        var file = PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap);
        file[6] = 0x7F;   // memory = ~2 TiB
        Assert.Equal("vault_bad_file", Assert.Throws<ClientException>(() => PhraseVault.Open(file, "un mot de passe solide")).Code);
    }

    [Fact]
    public void TwoSealsOfTheSamePhrasesDiffer()
    {
        Assert.NotEqual(PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap), PhraseVault.Seal(Phrases, "un mot de passe solide", Cheap));
    }

    [Fact]
    public void ThePrintedSheetRoundTripsAndOtherQrCodesAreNotMistakenForIt()
    {
        Assert.Equal(Phrases, PhraseSheet.TryDecode(PhraseSheet.Encode(Phrases)));
        Assert.Null(PhraseSheet.TryDecode("eyJjYXJkIjoiYSJ9"));
        Assert.Null(PhraseSheet.TryDecode("freespeech-phrases-v1\none\ntwo"));
    }
}
