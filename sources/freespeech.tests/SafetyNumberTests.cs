/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Identity;

namespace FreeSpeech.Tests;

public class SafetyNumberTests
{
    private static UserIdentity Id(string who) =>
        IdentityService.Derive((who + " one " + new string('a', 80))[..80], (who + " two " + new string('b', 80))[..80], (who + " three " + new string('c', 80))[..80], who);

    private static string Number(UserIdentity me, UserIdentity them) =>
        SafetyNumber.Compute(me.EncryptionPublicKey, me.SigningPublicKey, them.EncryptionPublicKey, them.SigningPublicKey);

    [Fact]
    public void Both_sides_compute_the_same_number()
    {
        var a = Id("alice");
        var b = Id("bob");
        Assert.Equal(Number(a, b), Number(b, a));
    }

    [Fact]
    public void The_number_has_twelve_groups_of_five_digits()
    {
        var n = Number(Id("alice"), Id("bob"));
        var groups = n.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(12, groups.Length);
        Assert.All(groups, g => { Assert.Equal(5, g.Length); Assert.True(g.All(char.IsDigit)); });
    }

    [Fact]
    public void A_man_in_the_middle_makes_the_two_numbers_differ()
    {
        var alice = Id("alice");
        var bob = Id("bob");
        var mallory = Id("mallory");

        // Alice thinks Mallory's keys are Bob's; Bob thinks Mallory's keys are Alice's (the keys were swapped at the exchange).
        var seenByAlice = Number(alice, mallory);
        var seenByBob = Number(bob, mallory);
        Assert.NotEqual(seenByAlice, seenByBob);
        Assert.NotEqual(seenByAlice, Number(alice, bob));
    }

    [Fact]
    public void Different_pairs_have_different_numbers()
    {
        Assert.NotEqual(Number(Id("alice"), Id("bob")), Number(Id("alice"), Id("carol")));
    }
}
