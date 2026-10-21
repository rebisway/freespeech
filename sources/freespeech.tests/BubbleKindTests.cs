/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Display;

namespace FreeSpeech.Tests;

/// <summary>The template a message line gets: light for the common kinds, full whenever anything else is shown around it.</summary>
public class BubbleKindTests
{
    private static BubbleFacts Text => new() { ShowPlainText = true };
    private static BubbleFacts Markdown => new() { ShowMarkup = true };
    private static BubbleFacts Picture => new() { HasImage = true };
    private static BubbleFacts Voice => new() { IsAudio = true };

    [Fact]
    public void PlainTextImageAndVoice_getTheirLightTemplate()
    {
        Assert.Equal(BubbleKind.Text, BubbleClassifier.Of(Text));
        Assert.Equal(BubbleKind.Markdown, BubbleClassifier.Of(Markdown));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Markdown with { HasReply = true }));
        Assert.Equal(BubbleKind.Image, BubbleClassifier.Of(Picture));
        Assert.Equal(BubbleKind.Voice, BubbleClassifier.Of(Voice));
    }

    [Fact]
    public void AnEditedOrDeletedText_staysLight_theEditHappensInADialog() =>
        Assert.Equal(BubbleKind.Text, BubbleClassifier.Of(Text with { Edited = true, IsDeleted = true }));

    [Fact]
    public void AnythingAroundTheBubble_goesToTheFullTemplate()
    {
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { ShowAuthor = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { HasReply = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { HasReactions = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { HasFileStatus = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { IsFile = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Picture with { HasReply = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Picture with { HasReactions = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Voice with { ShowAuthor = true }));
    }

    [Fact]
    public void SeparatorsAlbumsVideosAndArchivedFiles_areFull()
    {
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Text with { IsSeparator = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Picture with { IsAlbum = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(new BubbleFacts { HasVideoThumb = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(new BubbleFacts { IsVideo = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Picture with { IsArchived = true }));
    }

    [Fact]
    public void ImageOrVoiceEditedOrDeleted_isFull_andNothingShownIsFull()
    {
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Picture with { Edited = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(Voice with { IsDeleted = true }));
        Assert.Equal(BubbleKind.Full, BubbleClassifier.Of(new BubbleFacts()));
    }
}
