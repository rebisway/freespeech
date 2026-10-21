/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Attachments;

/// <summary>A small JPEG of a picture, with its size in pixels.</summary>
public sealed record PictureThumbnail(byte[] Data, int Width, int Height);

/// <summary>Makes the small picture of a file. The decoding is the platform's job (Android, Windows…): the library only stores the result.</summary>
public interface IPictureThumbnailer
{
    /// <summary>A reduced JPEG of the picture at <paramref name="path"/> (upright, longest side <see cref="ThumbnailStore.MaxSide"/> at most), or null when it cannot be read.</summary>
    PictureThumbnail? Make(string path);

    /// <summary>A reduced JPEG of the first frame of the video at <paramref name="path"/>, or null when it cannot be read.</summary>
    PictureThumbnail? MakeVideo(string path);
}

/// <summary>
/// The small pictures shown in the chat, kept in the encrypted database beside the history: a bubble is drawn from a few tens of KB
/// instead of decoding the full 1,600 px file each time it scrolls into view, and the picture stays recognisable if the file itself goes.
/// One thumbnail per content (the Merkle root), shared by every conversation that holds the same file.
/// </summary>
public sealed class ThumbnailStore(ClientDatabase db)
{
    /// <summary>The longest side of a thumbnail, in pixels: twice the width of a bubble on a common phone screen.</summary>
    public const int MaxSide = 480;

    public PictureThumbnail? Get(string root)
    {
        var rows = db.Query("SELECT data, width, height FROM thumbnails WHERE root = $root",
            r => new PictureThumbnail(db.OpenBytes(r, 0, $"thumb|{root}"), r.GetInt32(1), r.GetInt32(2)), ("$root", root));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>True when a thumbnail is kept for this content (the picture itself is not read).</summary>
    public bool Has(string root) => db.Scalar("SELECT COUNT(*) FROM thumbnails WHERE root = $root", ("$root", root)) > 0;

    public void Put(string root, PictureThumbnail thumbnail) =>
        db.Execute("INSERT OR REPLACE INTO thumbnails(root, width, height, data) VALUES($root, $w, $h, $data)",
            ("$root", root), ("$w", thumbnail.Width), ("$h", thumbnail.Height), ("$data", db.SealBytes(thumbnail.Data, $"thumb|{root}")));

    public void Delete(string root) => db.Execute("DELETE FROM thumbnails WHERE root = $root", ("$root", root));

    /// <summary>How many thumbnails are kept and how many bytes they take.</summary>
    public (int Count, long Bytes) Usage()
    {
        var rows = db.Query("SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM thumbnails", r => (r.GetInt32(0), r.GetInt64(1)));
        return rows[0];
    }
}
