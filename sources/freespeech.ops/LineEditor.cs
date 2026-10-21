/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text;

namespace FreeSpeech.Ops;

/// <summary>
/// A small line editor for the admin prompt: Up/Down walk through the history, Left/Right/Home/End move, Backspace/Delete edit,
/// Ctrl+U clears the line. The history is kept in a file in the user's profile (last 200 lines). Without a terminal (piped input) it
/// falls back to <see cref="Console.ReadLine"/>.
/// </summary>
public sealed class LineEditor
{
    private const int MaxHistory = 200;
    private readonly List<string> _history = [];
    private readonly string? _file;

    public LineEditor(string historyFileName)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) _file = Path.Combine(home, historyFileName);
            if (_file != null && File.Exists(_file)) _history.AddRange(File.ReadAllLines(_file).TakeLast(MaxHistory));
        }
        catch (Exception) { _file = null; }   // no history file: the session still works
    }

    /// <summary>Reads one line after showing the prompt; null at end of input (Ctrl+D on an empty line).</summary>
    public string? Read(string prompt)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) { Console.Write(prompt); return Console.ReadLine(); }

        Console.Write(prompt);
        var start = Console.CursorLeft;
        var buffer = new StringBuilder();
        var cursor = 0;
        var index = _history.Count;      // == Count: the line being typed
        var typed = "";

        void Redraw(int previousLength)
        {
            Console.CursorLeft = start;
            Console.Write(buffer.ToString());
            if (previousLength > buffer.Length) Console.Write(new string(' ', previousLength - buffer.Length));
            Console.CursorLeft = start + cursor;
        }

        void Set(string text)
        {
            var before = buffer.Length;
            buffer.Clear().Append(text);
            cursor = buffer.Length;
            Redraw(before);
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var line = buffer.ToString();
                    Remember(line);
                    return line;
                case ConsoleKey.UpArrow:
                    if (index == _history.Count) typed = buffer.ToString();
                    if (index > 0) { index--; Set(_history[index]); }
                    break;
                case ConsoleKey.DownArrow:
                    if (index < _history.Count) { index++; Set(index == _history.Count ? typed : _history[index]); }
                    break;
                case ConsoleKey.LeftArrow: if (cursor > 0) { cursor--; Console.CursorLeft = start + cursor; } break;
                case ConsoleKey.RightArrow: if (cursor < buffer.Length) { cursor++; Console.CursorLeft = start + cursor; } break;
                case ConsoleKey.Home: cursor = 0; Console.CursorLeft = start; break;
                case ConsoleKey.End: cursor = buffer.Length; Console.CursorLeft = start + cursor; break;
                case ConsoleKey.Backspace:
                    if (cursor > 0) { var n = buffer.Length; buffer.Remove(cursor - 1, 1); cursor--; Redraw(n); }
                    break;
                case ConsoleKey.Delete:
                    if (cursor < buffer.Length) { var n = buffer.Length; buffer.Remove(cursor, 1); Redraw(n); }
                    break;
                default:
                    if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0) { Console.WriteLine(); return null; }
                    if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control)) { Set(""); break; }
                    if (!char.IsControl(key.KeyChar))
                    {
                        var n = buffer.Length;
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        Redraw(n);
                    }
                    break;
            }
        }
    }

    private void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || (_history.Count > 0 && _history[^1] == line)) return;
        _history.Add(line);
        if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
        try { if (_file != null) File.WriteAllLines(_file, _history); } catch (Exception) { /* history is a convenience */ }
    }
}
