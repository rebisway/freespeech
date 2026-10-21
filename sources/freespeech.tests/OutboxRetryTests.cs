/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;

namespace FreeSpeech.Tests;

/// <summary>The periodic run spaces out, then stops, retrying a letter nobody has taken (a contact who no longer exists must not keep the app busy).</summary>
public class OutboxRetryTests
{
    [Theory]
    [InlineData(0, 0)]                 // fresh: every tick
    [InlineData(9, 0)]
    [InlineData(11, 15)]               // then every 15 minutes
    [InlineData(23 * 60, 15)]
    [InlineData(25 * 60, 6 * 60)]      // then every 6 hours
    [InlineData(71 * 60, 6 * 60)]
    public void RetryInterval_growsWithTheAgeOfTheLetter(int ageMinutes, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), FreeSpeechClient.RetryInterval(TimeSpan.FromMinutes(ageMinutes)));

    [Fact]
    public void RetryInterval_stopsAfterTheGiveUpAge()
    {
        Assert.Null(FreeSpeechClient.RetryInterval(FreeSpeechClient.OutboxGiveUpAfter));
        Assert.Null(FreeSpeechClient.RetryInterval(TimeSpan.FromDays(30)));
    }
}

/// <summary>"Stop waiting" on a letter: it stays queued, but only an undelivered letter can be parked.</summary>
public class OutboxParkTests
{
    [Fact]
    public void SetParked_keepsTheLetterAndOnlyForUndeliveredOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-park-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FreeSpeech.Client.Outbox.PendingMessageStore(dir);
            store.Add(new() { Id = "a:1", To = "b", Text = "hi", Ts = 1, CreatedAt = DateTime.UtcNow });
            store.Add(new() { Id = "a:2", To = "b", Text = "yo", Ts = 2, CreatedAt = DateTime.UtcNow, SentAt = DateTime.UtcNow });

            Assert.True(store.SetParked("a:1", true));
            Assert.True(store.All.Single(p => p.Id == "a:1").Parked);
            Assert.False(store.SetParked("a:2", true));   // delivered: nothing to stop waiting for
            Assert.False(store.SetParked("nope", true));
            Assert.Equal(2, store.All.Count);              // nothing deleted

            Assert.True(store.SetParked("a:1", false));
            Assert.False(store.All.Single(p => p.Id == "a:1").Parked);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
