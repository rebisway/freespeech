/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Client;

public enum ClientLogLevel { Off = 0, Error = 1, Warning = 2, Info = 3, Debug = 4, Trace = 5 }

/// <summary>
/// Diagnostic trace of the client library, off by default. An application (the console client, the mobile app) picks a level and
/// subscribes to <see cref="Written"/>; nothing is stored or sent by the library itself. Lines describe what the client does —
/// connections, registrations, refusals, send results, outbox runs — and never carry message content, keys, phrases or full
/// identifiers (identifiers are shortened to 8 characters with <see cref="Short"/>).
/// </summary>
public static class ClientLog
{
    private static volatile int _level;

    /// <summary>Lines at this level or more severe are written. <see cref="ClientLogLevel.Off"/> (the default) writes nothing.</summary>
    public static ClientLogLevel Level
    {
        get => (ClientLogLevel)_level;
        set => _level = (int)value;
    }

    /// <summary>Raised for every line written: (time, level, category, text). Handlers must not throw and should be quick.</summary>
    public static event Action<DateTime, ClientLogLevel, string, string>? Written;

    public static bool IsEnabled(ClientLogLevel level) => level != ClientLogLevel.Off && (int)level <= _level;

    public static void Write(ClientLogLevel level, string category, string text)
    {
        if (!IsEnabled(level)) return;
        try { Written?.Invoke(DateTime.Now, level, category, text); }
        catch { /* a broken log sink must never break the client */ }
    }

    public static void Info(string category, string text) => Write(ClientLogLevel.Info, category, text);
    public static void Debug(string category, string text) => Write(ClientLogLevel.Debug, category, text);
    public static void Warn(string category, string text) => Write(ClientLogLevel.Warning, category, text);

    /// <summary>First 8 characters of an identifier: enough to tell two apart, useless to anyone else.</summary>
    public static string Short(string? id) => string.IsNullOrEmpty(id) ? "-" : id.Length <= 8 ? id : id[..8];

    /// <summary>Server address reduced to its host, for the same reason.</summary>
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    public static bool TryParse(string? text, out ClientLogLevel level)
    {
        level = ClientLogLevel.Off;
        if (text == null) return false;
        if (text.Equals("warn", StringComparison.OrdinalIgnoreCase)) { level = ClientLogLevel.Warning; return true; }
        if (text.Equals("information", StringComparison.OrdinalIgnoreCase)) { level = ClientLogLevel.Info; return true; }
        return Enum.TryParse(text, ignoreCase: true, out level) && !int.TryParse(text, out _);
    }
}
