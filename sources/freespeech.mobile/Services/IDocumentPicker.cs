/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Mobile.Services;

/// <summary>A file the person chose, wherever it lives (the phone, a memory card, a USB key, a cloud drive that offers its files to apps).</summary>
/// <param name="OpenRead">A new seekable stream to read it.</param>
/// <param name="OpenWrite">A new seekable stream to write it (it starts empty).</param>
public sealed record DocumentHandle(string Name, Func<Stream> OpenRead, Func<Stream> OpenWrite);

/// <summary>
/// Lets the person pick where a big file goes or comes from, and gives streams on it, so an archive of several GB is written straight to its
/// destination: no copy of it is made on a phone that is short of space. Set by the Android app.
/// </summary>
public interface IDocumentPicker
{
    /// <summary>Asks for a place to create a file named <paramref name="suggestedName"/>; null when the person cancels.</summary>
    Task<DocumentHandle?> CreateAsync(string suggestedName, string mime);

    /// <summary>Asks for an existing file; null when the person cancels.</summary>
    Task<DocumentHandle?> OpenAsync();
}
