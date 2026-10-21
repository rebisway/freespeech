/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Security;
using Xunit;

namespace FreeSpeech.Tests;

public class PinLockTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void APinIsFourToEightDigits(string? pin, bool valid) => Assert.Equal(valid, PinLock.IsValidPin(pin));

    [Fact]
    public void TheRightPinVerifiesAndAWrongOneDoesNot()
    {
        var stored = PinLock.Create("2468");
        Assert.True(PinLock.Verify("2468", stored));
        Assert.False(PinLock.Verify("2469", stored));
        Assert.False(PinLock.Verify("", stored));
    }

    [Fact]
    public void TheStoredValueHidesThePinAndIsSalted()
    {
        var a = PinLock.Create("1357");
        var b = PinLock.Create("1357");
        Assert.NotEqual(a, b);   // a fresh salt each time: two identical PINs do not look alike
        Assert.DoesNotContain("1357", a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void AGarbledStoredValueNeverVerifies(string? stored) => Assert.False(PinLock.Verify("1234", stored));

    [Fact]
    public void CreateRefusesABadPin() => Assert.Throws<ArgumentException>(() => PinLock.Create("12"));

    [Fact]
    public void TheLockIsDueAfterTheDelayOnly()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        var week = TimeSpan.FromDays(7);
        Assert.False(PinLock.IsDue(now.AddDays(-6), now, week));
        Assert.True(PinLock.IsDue(now.AddDays(-7), now, week));
        Assert.True(PinLock.IsDue(now.AddDays(-30), now, week));
    }

    [Fact]
    public void AClockSetBackCountsAsDue()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(PinLock.IsDue(now.AddDays(3), now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void AZeroDelayHasAShortGrace()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(PinLock.IsDue(now.AddSeconds(-5), now, TimeSpan.Zero));
        Assert.True(PinLock.IsDue(now.AddSeconds(-20), now, TimeSpan.Zero));
    }
}
