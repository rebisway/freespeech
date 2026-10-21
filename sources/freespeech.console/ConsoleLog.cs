/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client;

namespace FreeSpeech.Console;

/// <summary>
/// Where the client library's diagnostic trace (<see cref="ClientLog"/>) goes in the console client: a file (<c>--log-file</c>) or,
/// by default, the error stream in grey so it stays apart from the conversation. Off unless <c>--log &lt;level&gt;</c> is given;
/// the level can be changed while running with the <c>log</c> command.
/// </summary>
public static class ConsoleLog
{
    private static readonly object Gate = new();
    private static string? _file;

    /// <summary>Takes <c>--log &lt;level&gt;</c> and <c>--log-file &lt;path&gt;</c> out of the arguments; returns the rest (profile, server URL).</summary>
    public static string[] Setup(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log" && i + 1 < args.Length)
            {
                if (ClientLog.TryParse(args[++i], out var level)) ClientLog.Level = level;
                else System.Console.Error.WriteLine($"Unknown log level '{args[i]}' (off, error, warning, info, debug, trace).");
            }
            else if (args[i] == "--log-file" && i + 1 < args.Length) _file = Path.GetFullPath(args[++i]);
            else rest.Add(args[i]);
        }
        if (_file != null && ClientLog.Level == ClientLogLevel.Off) ClientLog.Level = ClientLogLevel.Info;   // a file was asked for: log something
        ClientLog.Written += Write;
        return [.. rest];
    }

    private static void Write(DateTime time, ClientLogLevel level, string category, string text)
    {
        var line = $"{time:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[..3]}] {category}: {text}";
        lock (Gate)
        {
            if (_file != null) File.AppendAllText(_file, line + Environment.NewLine);
            else
            {
                var color = System.Console.ForegroundColor;
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.Error.WriteLine(line);
                System.Console.ForegroundColor = color;
            }
        }
    }

    /// <summary>The "log" command: no argument shows the level, a level changes it.</summary>
    public static string Command(string[] parts)
    {
        if (parts.Length < 2) return $"Log level: {ClientLog.Level.ToString().ToLowerInvariant()}" + (_file != null ? $" (file {_file})" : " (error stream)");
        if (!ClientLog.TryParse(parts[1], out var level)) return "Levels: off, error, warning, info, debug, trace.";
        ClientLog.Level = level;
        return $"Log level set to {level.ToString().ToLowerInvariant()}.";
    }
}
