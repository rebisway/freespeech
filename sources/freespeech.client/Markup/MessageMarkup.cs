/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text;
using System.Text.RegularExpressions;

namespace FreeSpeech.Client.Markup;

/// <summary>One run of a formatted message: its text and how it is shown. A line break is a run of its own ("\n").</summary>
/// <param name="Heading">0 for body text, 1 to 3 for a title (1 the largest).</param>
/// <param name="Link">The address a link points to (http or https), or null.</param>
/// <param name="Spacer">A line break that makes a short empty line (a little air between a text line and a list line or a title); shown small, and left out of <see cref="MessageMarkup.PlainText"/>.</param>
public sealed record MarkupSpan(string Text, bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, int Heading = 0, bool Quote = false, string? Link = null, bool Spacer = false);

/// <summary>
/// The formatting of a message. A message is formatted only when its first line is exactly <c>md</c>: what follows is read as a small
/// subset of Markdown (titles, lists, quotes, bold, italic, strike, inline code, links). Any other message is shown as typed. The text
/// that travels is always the raw one, so a client that does not know this convention shows it readably.
/// <list type="bullet">
/// <item>Titles: <c>#</c>, <c>##</c>, <c>###</c> then a space.</item>
/// <item>Lists: <c>-</c>, <c>*</c> or <c>+</c> then a space, or a number then <c>.</c> or <c>)</c> and a space; shown flush left with a bullet (<c>•</c>) or the number, a nested item keeping the indentation typed.</item>
/// <item>Quote: <c>&gt;</c> then an optional space. A line of <c>---</c> is a rule. A little air (a short empty line, <see cref="MarkupSpan.Spacer"/>) separates a text line from a list line or a title, and a list from a title.</item>
/// <item>Inline: <c>**bold**</c>, <c>*italic*</c> or <c>_italic_</c> (a single underscore or star never splits a word, so snake_case and 2*3*4 are safe), <c>~~strike~~</c>, <c>`code`</c>, <c>[text](https://…)</c> and bare http(s) addresses; a backslash shows the next sign as it is.</item>
/// </list>
/// </summary>
public static partial class MessageMarkup
{
    /// <summary>The first line that turns the formatting on.</summary>
    public const string Marker = "md";

    /// <summary>The longest text that is formatted (above it the message is shown as typed).</summary>
    public const int MaxLength = 8000;

    /// <summary>Whether the first line of this text is exactly <see cref="Marker"/> (any case, trailing spaces allowed) and something follows it.</summary>
    public static bool IsMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;
        var end = text.IndexOf('\n');
        return end > 0 && text[..end].Trim().Equals(Marker, StringComparison.OrdinalIgnoreCase) && text[(end + 1)..].Trim().Length > 0;
    }

    /// <summary>The text without the <see cref="Marker"/> line (the text itself when it is not formatted).</summary>
    public static string Body(string text) => IsMarkdown(text) ? text[(text.IndexOf('\n') + 1)..].TrimEnd('\r', '\n') : text;

    /// <summary>The runs of a message: one run for a message that is not formatted, as typed.</summary>
    public static IReadOnlyList<MarkupSpan> Parse(string text)
    {
        if (!IsMarkdown(text)) return [new MarkupSpan(text)];

        var spans = new List<MarkupSpan>();
        var lines = Body(text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var previous = LineKind.Text;
        for (var i = 0; i < lines.Length; i++)
        {
            var kind = KindOf(lines[i]);
            if (i > 0)
            {
                spans.Add(new MarkupSpan("\n"));
                // A little air where a text line meets a list line or a title, either way (not between two items or two titles, not around a blank line).
                if (kind != previous && lines[i].Trim().Length > 0 && lines[i - 1].Trim().Length > 0) spans.Add(new MarkupSpan("\n", Spacer: true));
            }
            ParseLine(lines[i], spans);
            previous = kind;
        }
        return Merge(spans);
    }

    /// <summary>The text with no sign of formatting left, for a preview (list of conversations, notification, quoted reply). A message that is not formatted comes back as it is.</summary>
    public static string PlainText(string text)
    {
        if (!IsMarkdown(text)) return text;
        var sb = new StringBuilder();
        foreach (var s in Parse(text).Where(s => !s.Spacer)) sb.Append(s.Text);
        return sb.ToString();
    }

    /// <summary>The addresses a message contains (formatted messages only), in order.</summary>
    public static IReadOnlyList<string> Links(string text) =>
        IsMarkdown(text) ? Parse(text).Where(s => s.Link != null).Select(s => s.Link!).Distinct().ToList() : [];

    // ------------------------------------------------------------------
    // Lines
    // ------------------------------------------------------------------

    private enum LineKind { Text, List, Title }

    private static LineKind KindOf(string line) =>
        BulletLine().IsMatch(line) || NumberedLine().IsMatch(line) ? LineKind.List : HeadingLine().IsMatch(line) ? LineKind.Title : LineKind.Text;

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingLine();
    [GeneratedRegex(@"^>\s?(.*)$")] private static partial Regex QuoteLine();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")] private static partial Regex BulletLine();
    [GeneratedRegex(@"^(\s*)(\d{1,9})[.)]\s+(.*)$")] private static partial Regex NumberedLine();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex RuleLine();

    private static void ParseLine(string line, List<MarkupSpan> spans)
    {
        if (RuleLine().IsMatch(line)) { spans.Add(new MarkupSpan("────────────", Quote: true)); return; }

        if (HeadingLine().Match(line) is { Success: true } h)
        {
            Inline(h.Groups[2].Value, new Style { Heading = Math.Min(3, h.Groups[1].Length), Bold = true }, spans);
            return;
        }
        if (QuoteLine().Match(line) is { Success: true } q)
        {
            spans.Add(new MarkupSpan("▎ ", Quote: true));
            Inline(q.Groups[1].Value, new Style { Quote = true }, spans);
            return;
        }
        if (BulletLine().Match(line) is { Success: true } b)
        {
            spans.Add(new MarkupSpan(b.Groups[1].Value + "• "));   // flush left at the first level; a nested item keeps the indentation typed
            Inline(b.Groups[2].Value, new Style(), spans);
            return;
        }
        if (NumberedLine().Match(line) is { Success: true } n)
        {
            spans.Add(new MarkupSpan(n.Groups[1].Value + n.Groups[2].Value + ". "));
            Inline(n.Groups[3].Value, new Style(), spans);
            return;
        }
        Inline(line, new Style(), spans);
    }

    // ------------------------------------------------------------------
    // Inline
    // ------------------------------------------------------------------

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strike = false, int Heading = 0, bool Quote = false);

    private const string Escapable = "\\`*_~[]()#>-+.!";

    [GeneratedRegex(@"\G\[([^\]\n]+)\]\((https?://[^)\s]+)\)")] private static partial Regex LinkWithText();
    [GeneratedRegex(@"\Ghttps?://[^\s<>]+")] private static partial Regex BareLink();

    private static MarkupSpan Run(string text, Style st, bool code = false, string? link = null) =>
        new(text, st.Bold, st.Italic, st.Strike, code, st.Heading, st.Quote, link);

    private static void Inline(string s, Style st, List<MarkupSpan> spans)
    {
        var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { spans.Add(Run(text.ToString(), st)); text.Clear(); } }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];

            if (c == '\\' && i + 1 < s.Length && Escapable.Contains(s[i + 1])) { text.Append(s[i + 1]); i += 2; continue; }

            if (c == '`')
            {
                var close = s.IndexOf('`', i + 1);
                if (close > i + 1) { Flush(); spans.Add(Run(s[(i + 1)..close], st, code: true)); i = close + 1; continue; }
            }

            if (c == '[' && LinkWithText().Match(s, i) is { Success: true } lm)
            {
                Flush(); spans.Add(Run(lm.Groups[1].Value, st, link: lm.Groups[2].Value)); i += lm.Length; continue;
            }

            if (c == 'h' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && BareLink().Match(s, i) is { Success: true } bm)
            {
                var url = bm.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
                if (url.Length > "https://".Length - 1) { Flush(); spans.Add(Run(url, st, link: url)); i += url.Length; continue; }
            }

            // Markers: longest first. A marker opens when a sign that is not a space follows it and a matching one closes it later in the line.
            foreach (var (marker, apply) in Markers)
            {
                if (string.CompareOrdinal(s, i, marker, 0, marker.Length) != 0) continue;
                if (!CanOpen(s, i, marker)) continue;
                var close = FindClose(s, i + marker.Length, marker);
                if (close < 0) continue;

                Flush();
                Inline(s[(i + marker.Length)..close], apply(st), spans);
                i = close + marker.Length;
                goto next;
            }

            text.Append(c);
            i++;
        next:;
        }
        Flush();
    }

    private static readonly (string Marker, Func<Style, Style> Apply)[] Markers =
    [
        ("***", st => st with { Bold = true, Italic = true }),
        ("**", st => st with { Bold = true }),
        ("__", st => st with { Bold = true }),
        ("~~", st => st with { Strike = true }),
        ("*", st => st with { Italic = true }),
        ("_", st => st with { Italic = true }),
    ];

    private static bool CanOpen(string s, int at, string marker)
    {
        var after = at + marker.Length;
        if (after >= s.Length || char.IsWhiteSpace(s[after])) return false;
        // A single underscore or star inside a word (snake_case, 2*3*4) is not a marker.
        if (marker.Length == 1 && marker[0] is '_' or '*' && at > 0 && char.IsLetterOrDigit(s[at - 1])) return false;
        return true;
    }

    private static int FindClose(string s, int from, string marker)
    {
        for (var j = s.IndexOf(marker, from, StringComparison.Ordinal); j >= 0; j = s.IndexOf(marker, j + 1, StringComparison.Ordinal))
        {
            if (j == from || char.IsWhiteSpace(s[j - 1]) || s[j - 1] == '\\') continue;
            var after = j + marker.Length;
            if (marker.Length == 1 && marker[0] is '_' or '*' && after < s.Length && char.IsLetterOrDigit(s[after])) continue;
            // A single marker is not the start of a longer one (an italic * is not the first star of **).
            if (marker.Length == 1 && after < s.Length && s[after] == marker[0]) continue;
            return j;
        }
        return -1;
    }

    private static IReadOnlyList<MarkupSpan> Merge(List<MarkupSpan> spans)
    {
        var merged = new List<MarkupSpan>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && merged[^1] is { } last && last.Text != "\n" && s.Text != "\n" && last with { Text = "" } == s with { Text = "" })
                merged[^1] = last with { Text = last.Text + s.Text };
            else merged.Add(s);
        }
        return merged;
    }
}
