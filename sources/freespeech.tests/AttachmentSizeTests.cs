/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Globalization;
using System.Text.Json;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Tests;

/// <summary>File sizes: the writing of a size, the protocol ceiling, and the free-space check before a download.</summary>
public class AttachmentSizeTests : IDisposable
{
    private const string Alice = "aa", Bob = "bb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-size-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        AttachmentStore.FreeSpaceProbe = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AttachmentService NewService() =>
        new(new AttachmentStore(Path.Combine(_root, Guid.NewGuid().ToString("N"))), (_, _, _) => Task.FromResult(true), _ => { });

    private static string Offer(long size) =>
        JsonSerializer.Serialize(new AttachmentOffer { Name = "big.bin", Mime = "application/octet-stream", Size = size, Root = new string('a', 64) });

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(12 * 1024 * 1024 + 400 * 1024, "12.4 MB")]
    [InlineData(180L * 1024 * 1024, "180 MB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GB")]
    public void ByteSize_writesWhatAPersonReads(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, ByteSize.Format(bytes)); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ByteSize_usesTheUnitsOfTheLanguage()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal("3.5 Mo", ByteSize.Format(3_670_016, "o|Ko|Mo|Go")); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void AnOfferBeyond25MiB_isAccepted_upToTheProtocolCeiling()
    {
        var svc = NewService();
        Assert.NotNull(svc.OnOffer(Alice, 1, Offer(300L * 1024 * 1024)));
        Assert.NotNull(svc.OnOffer(Alice, 2, Offer(AttachmentService.MaxSize)));
        Assert.Null(svc.OnOffer(Alice, 3, Offer(AttachmentService.MaxSize + 1)));
    }

    [Fact]
    public void EnsureRoom_refusesWhenTheDriveCannotTakeTheFileAndItsAssembly()
    {
        var svc = NewService();
        var rec = svc.OnOffer(Bob, 1, Offer(400L * 1024 * 1024))!;

        AttachmentStore.FreeSpaceProbe = _ => 500L * 1024 * 1024;   // 400 MB of chunks + 400 MB assembled do not fit
        var ex = Assert.Throws<CodedInvalidOperationException>(() => svc.EnsureRoom(rec));
        Assert.Equal("not_enough_space", ex.Code);
        Assert.Equal(500L, (long)ex.Args[1]);   // MB free

        AttachmentStore.FreeSpaceProbe = _ => 2000L * 1024 * 1024;
        svc.EnsureRoom(rec);   // fits
    }

    [Fact]
    public void EnsureRoom_refusesNothingWhenTheSpaceIsUnknown()
    {
        var svc = NewService();
        var rec = svc.OnOffer(Bob, 1, Offer(AttachmentService.MaxSize))!;
        AttachmentStore.FreeSpaceProbe = _ => null;
        svc.EnsureRoom(rec);
    }
}
