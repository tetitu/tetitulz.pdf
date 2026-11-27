// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="RichText"/>: the text a rich text string shows, by the XFA 3.3 rich text reference ISO 32000-2
/// points to — a paragraph's or a line break's boundary a space, an inline element's none, whitespace collapsed.
/// </summary>
public sealed class RichTextTests
{
    /// <summary>What each construct shows: block boundaries are spaces, inline ones are not, and markup is not text.</summary>
    [Theory]
    [InlineData("<p>a</p><p>b</p>", "a b")]
    [InlineData("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" xfa:APIVersion=\"Acrobat:11.0.0\"><p>Hello <b>wor</b>ld</p></body>", "Hello world")]
    [InlineData("<p>one<br/>two</p>", "one two")]
    [InlineData("<p>H<sub>2</sub>O and <i>x</i><sup>2</sup> by <a href=\"u\">link</a> in <span style=\"font-weight:bold\">bold</span></p>", "H2O and x2 by link in bold")]
    [InlineData("<html><head><title>Hidden</title></head><body><p>Shown</p></body></html>", "Shown")]
    [InlineData("<p>a &amp; b &lt;c&gt; &quot;d&quot; &apos;e&apos;</p>", "a & b <c> \"d\" 'e'")]
    [InlineData("<p>it&#8217;s &#x2014; done</p>", "it’s — done")]
    [InlineData("<p>a&nbsp;&nbsp;b</p>", "a b")]
    [InlineData("<p><![CDATA[x < y]]></p>", "x < y")]
    [InlineData("<p>a<!-- a comment <p>with tags</p> -->b</p>", "ab")]
    [InlineData("<p style=\"x>y\">quoted</p>", "quoted")]
    [InlineData("<xhtml:p>prefixed</xhtml:p><xhtml:p>names</xhtml:p>", "prefixed names")]
    [InlineData("<P>upper</P><P>case</P>", "upper case")]
    [InlineData("<unknown>suppressed</unknown> to its text", "suppressed to its text")]
    [InlineData("a < b and no tag closes", "a < b and no tag closes")]
    [InlineData("&bogus; &#0; &#xD800; &#; &", "&bogus; &#0; &#xD800; &#; &")]
    [InlineData("  \r\n leading\tand   trailing \r ", "leading and trailing")]
    [InlineData("", "")]
    public void ToPlain_ReadsWhatTheRichTextShows(string rich, string expected)
        => Assert.Equal(expected, RichText.ToPlain(rich));

    /// <summary>A text string is read as text; a text stream decoded and read the same way, UTF-16 by its BOM too.</summary>
    [Fact]
    public void TextOf_ReadsAStringOrAStream()
    {
        Assert.Equal("plain", RichText.TextOf(new PdfString("plain"), out string unreadable));
        Assert.Null(unreadable);

        byte[] utf16 = Encoding.BigEndianUnicode.GetPreamble();
        utf16 = Concat(utf16, Encoding.BigEndianUnicode.GetBytes("<p>שלום</p>"));
        var stream = new PdfStream { RawData = utf16 };
        Assert.Equal("שלום", RichText.ToPlain(stream, out unreadable));
        Assert.Null(unreadable);

        Assert.Null(RichText.TextOf(new PdfNumber(1), out unreadable));
        Assert.Null(unreadable);
    }

    /// <summary>A stream past <see cref="RichText.MaxStreamBytes"/> is not read, and says why; one within it is.</summary>
    [Fact]
    public void TextOf_AStreamPastTheCeiling_IsNotRead()
    {
        var within = new PdfStream { RawData = Encoding.ASCII.GetBytes("ok") };
        Assert.Equal("ok", RichText.TextOf(within, out _));

        var past = new PdfStream { RawData = new byte[RichText.MaxStreamBytes + 1] };
        Assert.Null(RichText.TextOf(past, out string unreadable));
        Assert.Contains("was not decoded", unreadable);
    }

    /// <summary>A long run of markup is read in one pass: a quarter of a million unclosed comments end at the first scan.</summary>
    [Fact]
    public void ToPlain_ManyUnterminatedConstructs_IsLinear_HostileInput()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 250_000; i++) sb.Append("<p>&amp");
        string plain = RichText.ToPlain(sb.ToString());
        Assert.StartsWith("&amp", plain);
        Assert.Equal(250_000, CountOf(plain, "&amp"));
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
