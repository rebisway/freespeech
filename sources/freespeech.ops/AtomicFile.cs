/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Ops;

/// <summary>Crash-safe file writes for the small JSON state files (siblings, client access, relay keys).</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Same for bytes; the file is readable by its owner only where the system has such rights.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the folder rights still apply */ }
        File.Move(temp, path, overwrite: true);
    }
}
