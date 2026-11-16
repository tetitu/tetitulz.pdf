// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfAutoLinker</c> — turning URLs written as plain text into <c>/Link</c> annotations.
/// </summary>
/// <remarks>
/// <para>Three components shipped and never met: <c>PdfLinkExtractor</c> reads links,
/// <c>PdfAnnotationWriter</c> writes one, and <c>PdfStructuredTextExtractor</c> has per-character
/// geometry — nothing turned a URL in the text into an annotation over it.</para>
///
/// <para><b>Assertions read the produced annotation back through <c>ExtractLinks</c></b>, so they
/// cover the URI <i>and</i> the rectangle. A test that only counted annotations would pass for a
/// writer that put every link at the origin.</para>
/// </remarks>
public class AutoLinkTests
{
    [Fact]
    public void AnExplicitUrl_IsFoundWithItsUri()
    {
        var found = PdfOperations.FindLinkableUrls(new MemoryStream(PageWithText("Visit https://example.com today")));

        var link = Assert.Single(found);
        Assert.Equal("https://example.com", link.Uri);
        Assert.Equal(0, link.PageIndex);
    }

    /// <summary>A bare <c>www.</c> host gets a scheme; the text keeps its own spelling.</summary>
    [Fact]
    public void ABareWwwHost_GetsAnHttpsScheme()
    {
        var link = Assert.Single(PdfOperations.FindLinkableUrls(
            new MemoryStream(PageWithText("See www.example.com for more"))));

        Assert.Equal("https://www.example.com", link.Uri);
        Assert.Equal("www.example.com", link.Text);
    }

    [Fact]
    public void AnEmailAddress_BecomesAMailtoLink()
    {
        var link = Assert.Single(PdfOperations.FindLinkableUrls(
            new MemoryStream(PageWithText("Write to tamir@example.com now"))));

        Assert.Equal("mailto:tamir@example.com", link.Uri);
    }

    /// <summary>
    /// <b>Trailing punctuation is trimmed from the match.</b> Extraction does not retain space
    /// glyphs and rebuilds words from gaps, so a URL at the end of a sentence arrives with the full
    /// stop attached — and <c>https://example.com.</c> is a different host.
    /// </summary>
    [Fact]
    public void TrailingSentencePunctuation_IsNotPartOfTheUri()
    {
        var link = Assert.Single(PdfOperations.FindLinkableUrls(
            new MemoryStream(PageWithText("Go to https://example.com."))));

        Assert.Equal("https://example.com", link.Uri);
    }

    /// <summary>
    /// …but a closing bracket the URL itself opened is kept. Trimming it leaves a link to a
    /// different page, which is the failure a naive trim produces on exactly the URLs that need it.
    /// </summary>
    [Fact]
    public void ABalancedClosingBracket_IsKept()
    {
        var link = Assert.Single(PdfOperations.FindLinkableUrls(
            new MemoryStream(PageWithText("See https://e.com/a_(b) here"))));

        Assert.Equal("https://e.com/a_(b)", link.Uri);
    }

    /// <summary>Detection is conservative: ordinary prose is not linked.</summary>
    [Theory]
    [InlineData("This is e.g. a sentence")]
    [InlineData("See Fig.3 on page 12")]
    [InlineData("Version 1.2.3 was released")]
    public void OrdinaryProse_IsNotLinked(string text)
    {
        Assert.Empty(PdfOperations.FindLinkableUrls(new MemoryStream(PageWithText(text))));
    }

    /// <summary>Each detector can be turned off, and turning one off does not disable the others.</summary>
    [Fact]
    public void TheDetectorsCanBeTurnedOffIndependently()
    {
        byte[] pdf = PageWithText("mail tamir@example.com or www.example.com");

        var noEmail = PdfOperations.FindLinkableUrls(new MemoryStream(pdf),
            new AutoLinkOptions { DetectEmailAddresses = false });
        Assert.Single(noEmail);
        Assert.StartsWith("https://", noEmail[0].Uri);

        var noHosts = PdfOperations.FindLinkableUrls(new MemoryStream(pdf),
            new AutoLinkOptions { DetectBareHosts = false });
        Assert.Single(noHosts);
        Assert.StartsWith("mailto:", noHosts[0].Uri);
    }

    // ------------------------------------------------------------------
    //  Writing
    // ------------------------------------------------------------------

    /// <summary>
    /// The written annotation carries the right URI <b>and</b> sits over the text — read back
    /// through <c>ExtractLinks</c>, so both halves are asserted.
    /// </summary>
    [Fact]
    public void TheWrittenLink_HasTheUriAndCoversTheUrlText()
    {
        var candidate = PdfOperations.FindLinkableUrls(
            new MemoryStream(PageWithText("Visit https://example.com today")))[0];

        byte[] result = Link(PageWithText("Visit https://example.com today"));
        var written = Assert.Single(PdfOperations.ExtractLinks(new MemoryStream(result)));

        Assert.Equal("https://example.com", written.Uri);
        Assert.Equal(0, written.PageIndex);

        // The annotation is where the text is — not at the origin, which is what a writer that
        // dropped the geometry would produce and what a count-only assertion would accept.
        Assert.True(written.X > 1, $"link X was {written.X}; it should sit at the URL, not the origin");
        Assert.True(written.Width > 1, $"link width was {written.Width}");
        Assert.Equal(candidate.X, written.X, 1);
        Assert.Equal(candidate.Width, written.Width, 1);
    }

    /// <summary>
    /// <b>Every link goes in ONE revision.</b> The single-link writer appends a complete revision
    /// per annotation, so the obvious loop over N URLs leaves N stacked revisions and re-parses a
    /// growing file each time.
    /// </summary>
    [Fact]
    public void ManyLinksAreWrittenInOneRevision()
    {
        byte[] source = PageWithText("a https://one.com b https://two.com c https://three.com");
        byte[] result = Link(source);

        Assert.Equal(3, PdfOperations.ExtractLinks(new MemoryStream(result)).Count);
        Assert.Equal(CountEofMarkers(source) + 1, CountEofMarkers(result));
    }

    /// <summary>
    /// <b>Running it twice does not double the annotations.</b> A document produced by a word
    /// processor already links its URLs, so this is the common case rather than an edge one.
    /// </summary>
    [Fact]
    public void RunningItTwice_DoesNotDuplicateLinks()
    {
        byte[] once = Link(PageWithText("Visit https://example.com today"));
        byte[] twice = Link(once);

        Assert.Single(PdfOperations.ExtractLinks(new MemoryStream(twice)));
    }

    /// <summary>
    /// Over two pages, each URL is checked against its own page's links: run twice, two links, one a
    /// page. The URLs stand at different heights, so a link taken for the other page's overlaps
    /// nothing and a mismatch shows as a duplicate.
    /// </summary>
    [Fact]
    public void RunningItTwiceOverTwoPages_DoesNotDuplicateLinksOnEither()
    {
        byte[] once = Link(TwoPages("Visit https://one.example today", 700, "See https://two.example now", 400));
        byte[] twice = Link(once);

        var links = PdfOperations.ExtractLinks(new MemoryStream(twice));
        Assert.Equal(2, links.Count);
        Assert.Equal(new[] { 0, 1 }, links.Select(l => l.PageIndex).OrderBy(p => p).ToArray());
    }

    /// <summary>…and the control: with the guard off, the second pass does add another.</summary>
    [Fact]
    public void WithoutTheGuard_ASecondPassAddsAnother()
    {
        byte[] once = Link(PageWithText("Visit https://example.com today"));
        byte[] twice = Link(once, new AutoLinkOptions { SkipAlreadyLinked = false });

        Assert.Equal(2, PdfOperations.ExtractLinks(new MemoryStream(twice)).Count);
    }

    /// <summary>A document with no URLs is written unchanged in substance — no links appear.</summary>
    [Fact]
    public void ADocumentWithNoUrls_GetsNoLinks()
    {
        byte[] result = Link(PageWithText("Nothing to see here"));

        Assert.Empty(PdfOperations.ExtractLinks(new MemoryStream(result)));
    }

    /// <summary>The returned candidates are what was written, so a caller can log them.</summary>
    [Fact]
    public void AddLinksReturnsWhatItWrote()
    {
        using var input = new MemoryStream(PageWithText("a https://one.com b https://two.com"));
        using var output = new MemoryStream();
        var written = PdfOperations.AutoLinkUrls(input, output);

        Assert.Equal(2, written.Count);
        Assert.Contains(written, w => w.Uri == "https://one.com");
        Assert.Contains(written, w => w.Uri == "https://two.com");
    }

    // ------------------------------------------------------------------

    private static byte[] Link(byte[] pdf, AutoLinkOptions options = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AutoLinkUrls(input, output, options);
        return output.ToArray();
    }

    private static int CountEofMarkers(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int count = 0, at = 0;
        while ((at = text.IndexOf("%%EOF", at, System.StringComparison.Ordinal)) >= 0) { count++; at += 5; }
        return count;
    }

    /// <summary>One page showing <paramref name="text"/> in Helvetica at 12 pt.</summary>
    private static byte[] PageWithText(string text)
    {
        string content = "BT /F1 12 Tf 50 700 Td (" + Escape(text) + ") Tj ET";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObj(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    }

    /// <summary>Two pages, each showing its text in Helvetica at 12 pt at its own height.</summary>
    private static byte[] TwoPages(string first, int firstY, string second, int secondY)
    {
        string Content(string text, int y) =>
            "BT /F1 12 Tf 50 " + y.ToString(CultureInfo.InvariantCulture) + " Td (" + Escape(text) + ") Tj ET";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R"
                + " /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R"
                + " /Resources << /Font << /F1 7 0 R >> >> >>",
            StreamObj(Content(first, firstY)),
            StreamObj(Content(second, secondY)),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
