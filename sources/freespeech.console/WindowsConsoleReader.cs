/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FreeSpeech.Console;

/// <summary>
/// Reads a console line via the raw Win32 <c>ReadConsoleW</c> API instead of
/// <see cref="System.Console.ReadLine"/>. On Windows, <c>Console.ReadLine()</c>
/// combined with <c>Console.InputEncoding = Encoding.UTF8</c> has a known bug
/// (confirmed here by inspecting history.json) where accented characters are
/// replaced with NUL (e.g. typed "après" is stored as "apr\u0000s") — neither
/// removing InputEncoding, setting it, nor changing the console host's code
/// page (chcp 65001) fixed it, because the corruption happens inside .NET's
/// own buffering layer, not the code page translation. ReadConsoleW reads the
/// composed Unicode text directly from the console host (the same primitive
/// Windows Terminal/conhost use for line editing), bypassing that layer and
/// any code page entirely.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsConsoleReader
{
    private const int StdInputHandle = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleW(nint hConsoleInput, [Out] char[] lpBuffer, uint nNumberOfCharsToRead, out uint lpNumberOfCharsRead, nint lpReserved);

    /// <summary>Reads one line from stdin, or null on EOF (stdin closed/redirected/piped empty) — same contract as Console.ReadLine().</summary>
    public static string? ReadLine()
    {
        var handle = GetStdHandle(StdInputHandle);
        var buffer = new char[4096];

        if (!ReadConsoleW(handle, buffer, (uint)buffer.Length, out var charsRead, 0))
            return null;
        if (charsRead == 0)
            return null;

        return new string(buffer, 0, (int)charsRead).TrimEnd('\r', '\n');
    }
}

/// <summary>Cross-platform entry point: the Windows workaround above on Windows, Console.ReadLine() elsewhere (no known equivalent bug on Linux/macOS terminals).</summary>
internal static class ConsoleLine
{
    // ReadConsoleW needs a real console — piped stdin (scripted tests) goes through ReadLine.
    // Piped input can start with a UTF-8 BOM (Windows PowerShell 5.1 emits one on the first line it
    // writes): it would end up glued to the first word ("﻿send"), so it is dropped.
    public static string? Read() =>
        (OperatingSystem.IsWindows() && !System.Console.IsInputRedirected ? WindowsConsoleReader.ReadLine() : System.Console.ReadLine())
            ?.TrimStart('﻿');
}
