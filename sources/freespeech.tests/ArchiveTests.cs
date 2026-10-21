/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Security.Cryptography;
using FreeSpeech.Client.Archive;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Tests;

/// <summary>Archiving downloaded files into an encrypted archive file, and loading it back (protocol §2 "Archive").</summary>
public class ArchiveTests : IDisposable
{
    private const string Me = "aaaaaaaa11111111", Alice = "bbbbbbbb22222222", Carol = "cccccccc33333333";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-archive-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeMaker : IPictureThumbnailer
    {
        public PictureThumbnail? Make(string path) => new PictureThumbnail([1, 2, 3], 480, 320);
        public PictureThumbnail? MakeVideo(string path) => new PictureThumbnail([4, 5, 6], 480, 270);
    }

    private static long Ticks(int daysAgo) => DateTime.UtcNow.AddDays(-daysAgo).Ticks;

    private (AttachmentService Svc, ArchiveService Archive) NewDevice(string name = "me", byte[]? key = null)
    {
        var svc = new AttachmentService(new AttachmentStore(Path.Combine(_root, name)), (_, _, _) => Task.FromResult(true), _ => { }) { Pictures = new FakeMaker() };
        return (svc, new ArchiveService(key ?? _key, svc));
    }

    private string MakeFile(string name, int size, byte seed, bool jpeg = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + seed);
        if (jpeg) { bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0; }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string ArchivePath(string name = "out.fsarchive") => Path.Combine(_root, name);

    [Fact]
    public void AnOldFileIsArchived_itsRecordStays_itsThumbnailStays_andLoadingTheArchiveBringsItBack()
    {
        var (svc, archive) = NewDevice();
        var photoPath = MakeFile("old.jpg", 120_000, 1, jpeg: true);
        var original = File.ReadAllBytes(photoPath);
        var (old, _) = svc.Prepare(Me, Alice, photoPath, Ticks(400));
        var (recent, _) = svc.Prepare(Me, Alice, MakeFile("new.bin", 50_000, 9), Ticks(10));
        var oldBlob = old.Path!;

        var preview = archive.Preview(ArchiveSelection.OlderThanMonths(12));
        Assert.Equal(new ArchivePreview(1, 1, 120_000), preview);

        var summary = archive.Create(ArchivePath(), ArchiveSelection.OlderThanMonths(12));
        Assert.Equal((1, 1, 120_000L), (summary.Files, summary.Records, summary.FreedBytes));
        Assert.True(File.Exists(ArchivePath()) && summary.ArchiveBytes > 120_000);

        Assert.Equal(AttachmentState.Archived, old.State);
        Assert.Null(old.Path);
        Assert.False(File.Exists(oldBlob));                                    // the space is freed
        Assert.Equal(AttachmentState.Complete, recent.State);                    // the recent one is untouched
        Assert.NotNull(svc.Store.Thumbnails.Get(old.Root));                      // the picture stays recognisable
        Assert.Equal(new ArchivePreview(0, 0, 0), archive.Preview(ArchiveSelection.OlderThanMonths(12)));   // nothing left to archive

        var restored = archive.Restore(ArchivePath())!;
        Assert.Equal((1, 1, 0), (restored.Files, restored.Records, restored.Skipped));
        Assert.Equal(AttachmentState.Complete, old.State);
        Assert.Equal(original, File.ReadAllBytes(old.Path!));                    // the very same bytes
    }

    [Fact]
    public void AContentKeptByARecentRecordToo_isNotArchived_becauseNoSpaceWouldBeFreed()
    {
        var (svc, archive) = NewDevice();
        var file = MakeFile("shared.bin", 40_000, 3);
        svc.Prepare(Me, Alice, file, Ticks(500));      // an old message...
        svc.Prepare(Me, Carol, file, Ticks(5));        // ...and a recent one, same content

        Assert.Equal(0, archive.Preview(ArchiveSelection.OlderThanMonths(12)).Files);
        Assert.Equal(1, archive.Preview(new ArchiveSelection()).Files);   // everything: the content counts once
        Assert.Equal(2, archive.Preview(new ArchiveSelection()).Records);
    }

    [Fact]
    public void TheOtherCriteria_narrowTheSelection()
    {
        var (svc, archive) = NewDevice();
        svc.Prepare(Me, Alice, MakeFile("a.jpg", 30_000, 1, jpeg: true), Ticks(800));
        svc.Prepare(Me, Carol, MakeFile("b.bin", 90_000, 2), Ticks(300));
        svc.Prepare(Me, Alice, MakeFile("c.bin", 10_000, 4), Ticks(100));

        Assert.Equal(3, archive.Preview(new ArchiveSelection()).Files);
        Assert.Equal(1, archive.Preview(new ArchiveSelection { Conversation = Carol }).Files);
        Assert.Equal(2, archive.Preview(new ArchiveSelection { Conversation = Alice }).Files);
        Assert.Equal(1, archive.Preview(new ArchiveSelection { MimePrefix = "image/" }).Files);
        Assert.Equal(2, archive.Preview(new ArchiveSelection { OtherKindsOnly = true }).Files);   // everything but the picture
        Assert.Equal(1, archive.Preview(new ArchiveSelection { MinSize = 50_000 }).Files);
        Assert.Equal(1, archive.Preview(new ArchiveSelection { From = DateTime.UtcNow.AddDays(-400), To = DateTime.UtcNow.AddDays(-200) }).Files);   // the 300-day-old one
        Assert.Equal(2, archive.Preview(new ArchiveSelection { Before = DateTime.UtcNow.AddDays(-200) }).Files);
    }

    [Fact]
    public void AnArchiveOpensOnlyWithTheIdentityThatMadeIt_andAnArchiveIsNotABackup()
    {
        var (svc, archive) = NewDevice();
        svc.Prepare(Me, Alice, MakeFile("x.bin", 20_000, 5), Ticks(500));
        archive.Create(ArchivePath(), new ArchiveSelection());

        var (_, stranger) = NewDevice("other", RandomNumberGenerator.GetBytes(32));
        Assert.Null(stranger.Restore(ArchivePath()));                          // another identity cannot open it

        Assert.Null(archive.Restore(Path.Combine(_root, "nothing.fsarchive")));   // no such file
        var notAnArchive = Path.Combine(_root, "plain.txt");
        File.WriteAllText(notAnArchive, "hello");
        Assert.Null(archive.Restore(notAnArchive));
    }

    [Fact]
    public void ADamagedArchiveIsRefused_andNothingLeavesTheDevice()
    {
        var (svc, archive) = NewDevice();
        var (rec, _) = svc.Prepare(Me, Alice, MakeFile("y.bin", 20_000, 6), Ticks(500));
        archive.Create(ArchivePath(), new ArchiveSelection());

        var bytes = File.ReadAllBytes(ArchivePath());
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(ArchivePath(), bytes);

        Assert.Null(archive.Restore(ArchivePath()));
        Assert.Equal(AttachmentState.Archived, rec.State);   // the damaged archive brought nothing back
    }

    [Fact]
    public void LoadingAnArchiveOnAnotherDevice_addsTheRecords_andTwiceChangesNothing()
    {
        var (svc, archive) = NewDevice();
        var (rec, _) = svc.Prepare(Me, Alice, MakeFile("z.bin", 25_000, 7), Ticks(600));
        archive.Create(ArchivePath(), new ArchiveSelection());

        var (second, secondArchive) = NewDevice("second");
        var first = secondArchive.Restore(ArchivePath())!;
        Assert.Equal((1, 1), (first.Files, first.Records));
        var added = second.Store.Find(rec.Ref)!;
        Assert.Equal(AttachmentState.Complete, added.State);
        Assert.Equal(25_000, new FileInfo(added.Path!).Length);

        var again = secondArchive.Restore(ArchivePath())!;
        Assert.Equal(1, again.Files);
        Assert.Single(second.Store.All);                                       // no duplicate record
    }

    [Fact]
    public void TheArchiveCanBeWrittenToAndReadFromAnyStream_asTheFilesAPersonChoosesOnTheirPhone()
    {
        var (svc, archive) = NewDevice();
        var (rec, _) = svc.Prepare(Me, Alice, MakeFile("s.bin", 70_000, 8), Ticks(450));
        var target = ArchivePath("chosen.bin");

        ArchiveSummary summary;
        using (var destination = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            summary = archive.Create(destination, new ArchiveSelection(), () => new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Equal(1, summary.Files);
        Assert.Equal(AttachmentState.Archived, rec.State);

        var restored = archive.Restore(() => new FileStream(target, FileMode.Open, FileAccess.Read))!;
        Assert.Equal(1, restored.Files);
        Assert.Equal(AttachmentState.Complete, rec.State);
    }

    [Fact]
    public void AVideoKeepsItsThumbnailAfterArchiving()
    {
        var (svc, archive) = NewDevice();
        var path = Path.Combine(_root, "clip.mp4");
        Directory.CreateDirectory(_root);
        var data = new byte[60_000];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
        "\0\0\0\u0018ftypmp42"u8.ToArray().CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        var (rec, _) = svc.Prepare(Me, Alice, path, Ticks(500));
        Assert.StartsWith("video/", rec.Mime);

        archive.Create(ArchivePath(), new ArchiveSelection());
        Assert.Equal(AttachmentState.Archived, rec.State);
        Assert.Equal(270, svc.ThumbnailOf(rec)!.Height);                          // the first frame, from the database
    }
}
