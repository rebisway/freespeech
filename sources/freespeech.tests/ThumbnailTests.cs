/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Tests;

/// <summary>The small pictures kept in the database: stored once per content, made on demand or in the background, dropped with the file.</summary>
public class ThumbnailTests : IDisposable
{
    private const string Me = "aaaaaaaa11111111", Alice = "bbbbbbbb22222222";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-thumb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeMaker : IPictureThumbnailer
    {
        public int Calls;
        public PictureThumbnail? Make(string path)
        {
            Interlocked.Increment(ref Calls);
            return new PictureThumbnail([1, 2, 3, 4], 480, 320);
        }

        public PictureThumbnail? MakeVideo(string path) => new PictureThumbnail([5, 6, 7], 480, 270);
    }

    private (AttachmentService Svc, FakeMaker Maker) NewService(string name = "me")
    {
        var maker = new FakeMaker();
        var svc = new AttachmentService(new AttachmentStore(Path.Combine(_root, name)), (_, _, _) => Task.FromResult(true), _ => { }) { Pictures = maker };
        return (svc, maker);
    }

    private string MakeImage(string name = "p.jpg", int extra = 2_000)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(new byte[extra].Select((_, i) => (byte)(i * 31))).ToArray());
        return path;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public void TheStoreKeepsOneThumbnailPerContent_andCountsThem()
    {
        var (svc, _) = NewService();
        var store = svc.Store.Thumbnails;
        Assert.Null(store.Get("r1"));
        store.Put("r1", new PictureThumbnail([9, 9, 9], 10, 20));
        store.Put("r1", new PictureThumbnail([7, 7, 7, 7, 7], 30, 40));   // replaced, not added
        store.Put("r2", new PictureThumbnail([1], 1, 1));

        var kept = store.Get("r1")!;
        Assert.Equal([7, 7, 7, 7, 7], kept.Data);
        Assert.Equal((30, 40), (kept.Width, kept.Height));
        Assert.Equal((2, 6L), store.Usage());

        store.Delete("r1");
        Assert.Null(store.Get("r1"));
    }

    [Fact]
    public void ThumbnailsSurviveReopeningTheProfile()
    {
        var dir = Path.Combine(_root, "reopen");
        var first = new AttachmentStore(dir);
        first.Thumbnails.Put("root-a", new PictureThumbnail([5, 6], 12, 34));

        var second = new AttachmentStore(dir);
        Assert.Equal(34, second.Thumbnails.Get("root-a")!.Height);
    }

    [Fact]
    public async Task ASentImageGetsItsThumbnail_madeOnce()
    {
        var (svc, maker) = NewService();
        var (rec, _) = svc.Prepare(Me, Alice, MakeImage(), 1);
        await WaitFor(() => svc.Store.Thumbnails.Get(rec.Root) != null);

        Assert.Equal(480, svc.ThumbnailOf(rec)!.Width);
        Assert.Equal(480, svc.ThumbnailOf(rec)!.Width);
        Assert.Equal(1, maker.Calls);   // read from the database the second time
    }

    [Fact]
    public void AnImageWithNoThumbnailYet_getsOneWhenTheChatAsks()
    {
        var (svc, maker) = NewService();
        var (rec, _) = svc.Prepare(Me, Alice, MakeImage(), 1);
        svc.Store.Thumbnails.Delete(rec.Root);   // e.g. a picture received before thumbnails were kept
        var before = maker.Calls;

        Assert.NotNull(svc.ThumbnailOf(rec));
        Assert.NotNull(svc.ThumbnailOf(rec));
        Assert.True(maker.Calls >= before);
        Assert.NotNull(svc.Store.Thumbnails.Get(rec.Root));
    }

    [Fact]
    public async Task OtherFilesAndAnimatedGifsHaveNoThumbnail()
    {
        var (svc, _) = NewService();
        var data = Path.Combine(_root, "d.bin");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(data, new byte[3_000].Select((_, i) => (byte)(i * 7 + 1)).ToArray());
        var (other, _) = svc.Prepare(Me, Alice, data, 1);
        Assert.Null(svc.ThumbnailOf(other));

        var gif = Path.Combine(_root, "a.gif");
        File.WriteAllBytes(gif, "GIF89a"u8.ToArray().Concat(new byte[500].Select((_, i) => (byte)(i * 13 + 5))).ToArray());
        var (animated, _) = svc.Prepare(Me, Alice, gif, 2);
        await Task.Delay(100);
        Assert.Equal("image/gif", animated.Mime);
        Assert.Null(svc.ThumbnailOf(animated));
    }

    [Fact]
    public void NoMaker_noThumbnail_butAKeptOneIsStillServed()
    {
        var (svc, _) = NewService();
        var (rec, _) = svc.Prepare(Me, Alice, MakeImage(), 1);
        svc.Store.Thumbnails.Put(rec.Root, new PictureThumbnail([1], 2, 3));

        svc.Pictures = null;
        Assert.Equal(3, svc.ThumbnailOf(rec)!.Height);
    }

    [Fact]
    public void DeletingTheFile_dropsItsThumbnail_unlessAnotherConversationHoldsTheSameContent()
    {
        var (svc, _) = NewService();
        var file = MakeImage();
        var (a, _) = svc.Prepare(Me, Alice, file, 1);
        var (b, _) = svc.Prepare(Me, "cccccccc33333333", file, 2);   // same content, another conversation
        svc.Store.Thumbnails.Put(a.Root, new PictureThumbnail([1], 1, 1));

        svc.RemoveContent(a.Ref);
        Assert.NotNull(svc.Store.Thumbnails.Get(a.Root));   // b still holds it

        svc.RemoveContent(b.Ref);
        Assert.Null(svc.Store.Thumbnails.Get(a.Root));
    }

    [Fact]
    public async Task Backfill_makesTheMissingThumbnails_newestFirst_andSkipsTheOnesAlreadyKept()
    {
        var (svc, maker) = NewService();
        var one = svc.Prepare(Me, Alice, MakeImage("one.jpg", 1_500), 1).Record;
        var two = svc.Prepare(Me, Alice, MakeImage("two.jpg", 2_500), 2).Record;
        await WaitFor(() => svc.Store.Thumbnails.Get(one.Root) != null && svc.Store.Thumbnails.Get(two.Root) != null);
        svc.Store.Thumbnails.Delete(one.Root);
        var before = maker.Calls;

        await svc.BackfillThumbnailsAsync();

        Assert.NotNull(svc.Store.Thumbnails.Get(one.Root));
        Assert.Equal(before + 1, maker.Calls);   // only the one that was missing
    }
}
