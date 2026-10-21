/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Markup;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>A message is formatted only when its first line is exactly "md"; the formatting is a small subset of Markdown.</summary>
public class MessageMarkupTests
{
    private static IReadOnlyList<MarkupSpan> P(string body) => MessageMarkup.Parse("md\n" + body);

    [Theory]
    [InlineData("md\nhello", true)]
    [InlineData("MD\nhello", true)]
    [InlineData("Md\nhello", true)]   // the keyboard capitalises the first letter
    [InlineData("mD\nhello", true)]
    [InlineData("md  \nhello", true)]
    [InlineData("md\r\nhello", true)]
    [InlineData("md", false)]                 // nothing follows
    [InlineData("md\r\n", false)]
    [InlineData("md\r\n\r\n  \r\n", false)]   // only blanks and line breaks after it: the message is just "md"
    [InlineData("  MD  \r\n \r\n", false)]
    [InlineData("md\n", false)]
    [InlineData("md hello", false)]           // not alone on the first line
    [InlineData("hello\nmd\nworld", false)]   // not the first line
    [InlineData("mdr\nhello", false)]
    public void OnlyAFirstLineOfMd_turnsTheFormattingOn(string text, bool expected) => Assert.Equal(expected, MessageMarkup.IsMarkdown(text));

    [Fact]
    public void AMessageThatIsNotMarkdown_isShownAsTyped()
    {
        var spans = MessageMarkup.Parse("**not bold** and `not code`");
        Assert.Equal(new MarkupSpan("**not bold** and `not code`"), Assert.Single(spans));
        Assert.Equal("**not bold** and `not code`", MessageMarkup.PlainText("**not bold** and `not code`"));
    }

    [Fact]
    public void TheMdLine_isNotShown()
    {
        Assert.Equal("hello", MessageMarkup.Body("md\nhello"));
        Assert.Equal("hello", MessageMarkup.PlainText("md\nhello"));
    }

    [Fact]
    public void BoldItalicStrikeAndCode()
    {
        var spans = P("a **bold** b *it* c _it2_ d ~~gone~~ e `x*y*`");
        Assert.Contains(new MarkupSpan("bold", Bold: true), spans);
        Assert.Contains(new MarkupSpan("it", Italic: true), spans);
        Assert.Contains(new MarkupSpan("it2", Italic: true), spans);
        Assert.Contains(new MarkupSpan("gone", Strike: true), spans);
        Assert.Contains(new MarkupSpan("x*y*", Code: true), spans);   // nothing is read inside code
    }

    [Fact]
    public void BoldAndItalicCanBeNested()
    {
        var spans = P("**bold and *italic* inside** and ***both***");
        Assert.Contains(new MarkupSpan("bold and ", Bold: true), spans);
        Assert.Contains(new MarkupSpan("italic", Bold: true, Italic: true), spans);
        Assert.Contains(new MarkupSpan("both", Bold: true, Italic: true), spans);
    }

    [Theory]
    [InlineData("snake_case_name and 2*3*4 and a * b * c")]
    [InlineData("an unclosed **marker and a lone ~~ and a lone `tick")]
    public void SignsThatAreNotMarkers_stayAsTyped(string body) => Assert.Equal(body, MessageMarkup.PlainText("md\n" + body));

    [Fact]
    public void ABackslash_showsTheNextSignAsItIs() =>
        Assert.Equal("*not italic*", MessageMarkup.PlainText("md\n\\*not italic\\*"));

    [Fact]
    public void Titles_haveALevelAndAreBold()
    {
        var spans = P("# One\n## Two\n### Three\n#### Four");
        Assert.Contains(new MarkupSpan("One", Bold: true, Heading: 1), spans);
        Assert.Contains(new MarkupSpan("Two", Bold: true, Heading: 2), spans);
        Assert.Contains(new MarkupSpan("Three", Bold: true, Heading: 3), spans);
        Assert.Contains(new MarkupSpan("Four", Bold: true, Heading: 3), spans);   // deeper titles stop at the smallest
        Assert.Equal("One\nTwo\nThree\nFour", MessageMarkup.PlainText("md\n# One\n## Two\n### Three\n#### Four"));
    }

    [Fact]
    public void Lists_areFlushLeftAndNestedItemsKeepTheirIndentation()
    {
        Assert.Equal("• one\n• two\n  • nested", MessageMarkup.PlainText("md\n- one\n* two\n  + nested"));
        Assert.Equal("1. one\n2. two", MessageMarkup.PlainText("md\n1. one\n2) two"));
    }

    [Fact]
    public void ALittleAir_separatesATextLineFromAListLineOrATitle_butNotTwoItems()
    {
        var count = (string body) => P(body).Count(s => s.Spacer);
        Assert.Equal(1, count("text\n- item"));          // text then list
        Assert.Equal(1, count("- item\ntext"));          // list then text
        Assert.Equal(2, count("a\n- one\n- two\nb"));    // both edges, none between the items
        Assert.Equal(0, count("- one\n- two"));
        Assert.Equal(1, count("# Title\ntext"));        // title then text
        Assert.Equal(1, count("text\n## Title"));        // text then title
        Assert.Equal(2, count("# Title\n- item\ntext")); // title then list, list then text
        Assert.Equal(0, count("# One\n## Two"));         // two titles
        Assert.Equal(0, count("text\n\n- item"));        // a blank line already is some air
        Assert.Equal("text\n• item", MessageMarkup.PlainText("md\ntext\n- item"));   // never in a preview
    }

    [Fact]
    public void AListItem_canCarryInlineFormatting() =>
        Assert.Contains(new MarkupSpan("bold", Bold: true), P("- a **bold** item"));

    [Fact]
    public void AQuote_isMarkedAndKeepsItsFormatting()
    {
        var spans = P("> quoted *text*");
        Assert.Contains(new MarkupSpan("▎ quoted ", Quote: true), spans);
        Assert.Contains(new MarkupSpan("text", Italic: true, Quote: true), spans);
    }

    [Fact]
    public void Links_areFoundWithOrWithoutText()
    {
        var spans = P("see [the site](https://example.org/a?b=1) or https://example.com/x, then http://plain.test.");
        Assert.Contains(new MarkupSpan("the site", Link: "https://example.org/a?b=1"), spans);
        Assert.Contains(new MarkupSpan("https://example.com/x", Link: "https://example.com/x"), spans);   // the comma is not part of it
        Assert.Contains(new MarkupSpan("http://plain.test", Link: "http://plain.test"), spans);
        Assert.Equal(["https://example.org/a?b=1", "https://example.com/x", "http://plain.test"], MessageMarkup.Links("md\nsee [the site](https://example.org/a?b=1) or https://example.com/x, then http://plain.test."));
    }

    [Fact]
    public void OnlyHttpAndHttpsAreLinks() =>
        Assert.DoesNotContain(P("[x](javascript:alert(1)) and ftp://host/file"), s => s.Link != null);

    [Fact]
    public void ALineOfDashes_isARule() =>
        Assert.Equal("a\n────────────\nb", MessageMarkup.PlainText("md\na\n---\nb"));

    [Fact]
    public void ATooLongMessage_isShownAsTyped() =>
        Assert.False(MessageMarkup.IsMarkdown("md\n" + new string('x', MessageMarkup.MaxLength)));
}
