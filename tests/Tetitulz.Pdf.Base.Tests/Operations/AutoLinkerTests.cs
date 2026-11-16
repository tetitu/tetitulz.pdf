// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfAutoLinker"/> — a public entry point, reached through
/// <c>PdfOperations.FindLinks</c> / <c>AddLinks</c>, that <b>no test in the repository called</b>,
/// directly or through the facade.
/// </summary>
/// <remarks>
/// <para>It turns text into <c>/Link</c> annotations, so both directions of error are expensive: a
/// missed URL is a dead link in a delivered document, and an over-eager match writes a clickable
/// annotation over text that is not a URL — including, if the trailing-punctuation rule is wrong,
/// a link to a <i>different</i> address than the one printed on the page.</para>
///
/// <para>The rectangle it reports comes from <c>TextLineGlyphMap.TryUnionBox</c>, shared with
/// <c>PdfTextRedactor</c> — so that helper is already pinned by the redaction suite, and these
/// cover the decisions that are this class's own: which runs are URLs, what scheme they get, where
/// the match stops, and whether an existing link suppresses a duplicate.</para>
/// </remarks>
public class AutoLinkerTests
{
    // ── which runs become links, and with what scheme ────────────────────────────

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("www.example.com", "https://www.example.com")]   // bare host gets a scheme
    [InlineData("someone@example.com", "mailto:someone@example.com")]
    public void ARecognisedRunBecomesALinkWithTheRightScheme(string text, string expected)
    {
        var found = Assert.Single(Find(text));

        Assert.Equal(expected, found.Uri);
    }

    /// <summary>
    /// The control for the two option switches: with each turned off, the same text yields no
    /// candidate at all. Without this pair, a matcher that recognised everything would satisfy the
    /// theory above.
    /// </summary>
    [Theory]
    [InlineData("someone@example.com", false, true)]
    [InlineData("www.example.com", true, false)]
    public void TheOptionSwitchesSuppressTheirOwnKind(string text, bool emails, bool hosts)
    {
        var options = new AutoLinkOptions { DetectEmailAddresses = emails, DetectBareHosts = hosts };

        Assert.Empty(Find(text, options));
    }

    /// <summary>Ordinary prose is not a URL, so nothing is linked.</summary>
    [Fact]
    public void PlainTextYieldsNoCandidates()
    {
        Assert.Empty(Find("The quick brown fox."));
    }

    // ── where the match stops ────────────────────────────────────────────────────

    /// <summary>
    /// <b>A sentence's full stop is not part of the URL.</b> Trailing punctuation is trimmed, or
    /// every link at the end of a sentence points one character past the address printed.
    /// </summary>
    [Fact]
    public void TrailingSentencePunctuationIsNotPartOfTheLink()
    {
        var found = Assert.Single(Find("See https://example.com/page. Thanks"));

        Assert.Equal("https://example.com/page", found.Uri);
    }

    /// <summary>
    /// <b>...but a closing parenthesis the URL itself opened is kept.</b> Wikipedia-style addresses
    /// end in <c>(disambiguation)</c>, and trimming that links to a different page — which is worse
    /// than not linking, because it looks right.
    /// </summary>
    [Fact]
    public void ABalancedClosingParenthesisIsKeptAsPartOfTheLink()
    {
        var found = Assert.Single(Find("https://example.com/Mercury_(planet)"));

        Assert.Equal("https://example.com/Mercury_(planet)", found.Uri);
    }

    /// <summary>
    /// And an <i>unbalanced</i> one is punctuation after all: the parenthesis belongs to the
    /// sentence that wraps the URL, not to the URL.
    /// </summary>
    [Fact]
    public void AnUnbalancedClosingParenthesisIsTrimmed()
    {
        var found = Assert.Single(Find("(see https://example.com/page)"));

        Assert.Equal("https://example.com/page", found.Uri);
    }

    // ── AddLinks writes annotations, and does not duplicate ──────────────────────

    /// <summary>
    /// <c>AddLinks</c> writes a real <c>/Link</c> annotation, not just a report — asserted on the
    /// produced document's annotations rather than on the return value, which is the engine's own
    /// opinion of what it did.
    /// </summary>
    [Fact]
    public void AddLinksWritesALinkAnnotationIntoTheDocument()
    {
        byte[] output = AddLinks(PageWith("Visit https://example.com/a today"));

        using var ms = new MemoryStream(output);
        var links = PdfLinkExtractor.ExtractLinks(ms);

        Assert.Equal("https://example.com/a", Assert.Single(links).Uri);
    }

    /// <summary>
    /// <b>A page that already links the text is left alone</b> — running the linker twice must not
    /// stack a second annotation over the first, which is what <c>SkipAlreadyLinked</c> is for.
    /// </summary>
    [Fact]
    public void RunningTheLinkerTwiceDoesNotAddASecondAnnotation()
    {
        byte[] once = AddLinks(PageWith("Visit https://example.com/a today"));
        byte[] twice = AddLinks(once);

        using var ms = new MemoryStream(twice);
        Assert.Single(PdfLinkExtractor.ExtractLinks(ms));
    }

    /// <summary>
    /// The control that stops the test above from passing for the wrong reason: with the skip
    /// turned off, the second pass <i>does</i> add one — so the suppression is doing the work
    /// rather than the second pass finding nothing.
    /// </summary>
    [Fact]
    public void WithTheSkipDisabledTheSecondPassAddsAnother()
    {
        byte[] once = AddLinks(PageWith("Visit https://example.com/a today"));
        byte[] twice = AddLinks(once, new AutoLinkOptions { SkipAlreadyLinked = false });

        using var ms = new MemoryStream(twice);
        Assert.Equal(2, PdfLinkExtractor.ExtractLinks(ms).Count);
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<AutoLinkCandidate> Find(string text, AutoLinkOptions options = null)
    {
        using var ms = new MemoryStream(PageWith(text));
        return PdfAutoLinker.FindLinks(ms, options);
    }

    private static byte[] AddLinks(byte[] pdf, AutoLinkOptions options = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfAutoLinker.AddLinks(input, output, options);
        return output.ToArray();
    }

    private static byte[] PageWith(string text)
    {
        string content = "BT /F1 12 Tf 40 700 Td (" + text + ") Tj ET";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
