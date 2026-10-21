/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
namespace FreeSpeech.Client.Display;

/// <summary>Which template draws a message line: a light one with only what that kind of message shows, or the full one for every other combination.</summary>
public enum BubbleKind
{
    Full,
    Text,
    Image,
    Voice,
    Markdown,
}

/// <summary>What a line shows, as plain flags (the app's line object fills it in).</summary>
public readonly record struct BubbleFacts(
    bool IsSeparator, bool ShowPlainText, bool ShowMarkup, bool ShowAuthor, bool HasReply, bool HasFileStatus, bool HasReactions, bool IsFile,
    bool IsAlbum, bool HasImage, bool HasVideoThumb, bool IsVideo, bool IsAudio, bool IsArchived, bool Edited, bool IsDeleted);

public static class BubbleClassifier
{
    /// <summary>
    /// The lightest template that can show the line as is. Anything with a quoted reply, a sender header (group), reactions, a file status, an archive mark
    /// or a combination not listed goes to <see cref="BubbleKind.Full"/>.
    /// </summary>
    public static BubbleKind Of(in BubbleFacts f)
    {
        if (f.IsSeparator || f.ShowAuthor || f.HasReply || f.HasReactions || f.HasFileStatus || f.IsFile || f.IsAlbum || f.IsArchived || f.HasVideoThumb || f.IsVideo)
            return BubbleKind.Full;
        if (f.ShowPlainText && !f.HasImage && !f.IsAudio) return BubbleKind.Text;
        if (f.ShowMarkup && !f.HasImage && !f.IsAudio) return BubbleKind.Markdown;
        if (f.HasImage && !f.IsAudio && !f.Edited && !f.IsDeleted) return BubbleKind.Image;
        if (f.IsAudio && !f.HasImage && !f.Edited && !f.IsDeleted) return BubbleKind.Voice;
        return BubbleKind.Full;
    }
}
