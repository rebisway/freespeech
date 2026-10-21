/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Client.Attachments;

/// <summary>What the user allows for the chunks of files (sent to others or received) on a metered connection.</summary>
public enum FileTransferMode
{
    /// <summary>Whatever the network.</summary>
    Always,

    /// <summary>Never on mobile data.</summary>
    WifiOnly,

    /// <summary>Not on mobile data, except files of <see cref="TransferPolicy.SmallFileBytes"/> or less.</summary>
    WifiExceptSmall,
}

/// <summary>The kind of network the device is on, as the app reports it (the library knows nothing of the operating system).</summary>
public enum NetworkKind
{
    /// <summary>Not known (a console client, or a platform that does not say): nothing is held back.</summary>
    Unknown,
    Wifi,
    Mobile,
}

/// <summary>
/// The "files only on Wi-Fi" setting, applied to the chunks only (protocol §6): messages, receipts and group states always go through. It holds back both
/// the chunks this device asks for and the chunks it serves to others, so a phone on mobile data is never made to carry the traffic of a group.
/// </summary>
public sealed class TransferPolicy
{
    /// <summary>Files up to this size are small enough to go over mobile data in <see cref="FileTransferMode.WifiExceptSmall"/> (a compressed photo).</summary>
    public const long SmallFileBytes = 1024 * 1024;

    public FileTransferMode Mode { get; set; } = FileTransferMode.Always;
    public NetworkKind Network { get; set; } = NetworkKind.Unknown;

    /// <summary>May the chunks of a file of this size travel on the current network?</summary>
    public bool Allows(long fileSize) =>
        Mode == FileTransferMode.Always || Network != NetworkKind.Mobile || (Mode == FileTransferMode.WifiExceptSmall && fileSize <= SmallFileBytes);
}
