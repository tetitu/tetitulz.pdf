// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Internal links — a link to somewhere else in the same document, as a table of contents or a
/// cross-reference makes one.
///
/// <para><b>Two halves were missing, and only one of them was recorded as missing.</b> The
/// exporter skipped any link whose target began with <c>#</c>, with a comment saying named
/// destinations were not emitted yet — that much was known. What was not recorded is that the
/// model had no way to declare a destination <i>at all</i>: <see cref="TextRun.LinkAnchor"/> (the
/// source half) has existed since it was written, and <c>DocxToBuilderMapper</c> has been setting
/// it from every DOCX internal hyperlink, with nothing anywhere able to receive it. So a converted
/// document's cross-references silently became plain text — and the register listed only the
/// half that had a comment.</para>
///
/// <para>These assert on the produced PDF's objects, not on the model: the claim is that a viewer
/// following the link arrives somewhere, and only the file can say that.</para>
/// </summary>
public class InternalAnchorTests
{
    /// <summary>
    /// The claim: a <c>#name</c> link becomes a <c>/GoTo</c> action naming a destination that the
    /// document's <c>/Names /Dests</c> tree actually declares. A <c>/GoTo</c> pointing at a name
    /// nothing declares is worse than no link, so both halves are asserted together.
    /// </summary>
    [Fact]
    public void AnInternalLink_BecomesAGoToActionWithADeclaredDestination()
    {
        string raw = PdfContentProbe.Raw(BuildPdf(anchor: "chapter1", linkTo: "chapter1"));

        Assert.Contains("/S /GoTo", raw);
        Assert.Contains("/D (chapter1)", raw);
        Assert.Contains("/Dests", raw);
        Assert.Contains("(chapter1)", raw);
    }

    /// <summary>
    /// An external URI still becomes a <c>/URI</c> action. The two share a code path now, and a
    /// destination name is not a URI — putting one in a <c>/URI</c> action opens a browser at a
    /// nonsense address, which is the failure mode this separation exists to prevent.
    /// </summary>
    [Fact]
    public void AnExternalLink_IsStillAUriAction()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Site", LinkUri = "https://example.invalid/x" });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string raw = PdfContentProbe.Raw(doc.ToPdf());

        Assert.Contains("/S /URI", raw);
        Assert.Contains("https://example.invalid/x", raw);
        Assert.DoesNotContain("/S /GoTo", raw);
    }

    /// <summary>
    /// A link naming a destination nothing declared is <b>dropped</b>, not written as a dangling
    /// <c>/GoTo</c>. A viewer's response to one is unspecified and in practice ranges from doing
    /// nothing to jumping to page 1 — worse than an inert rectangle, because it looks like it
    /// worked.
    /// </summary>
    [Fact]
    public void ALinkToANameNothingDeclares_IsDroppedRatherThanDangling()
    {
        string raw = PdfContentProbe.Raw(BuildPdf(anchor: null, linkTo: "nowhere"));

        Assert.DoesNotContain("/S /GoTo", raw);
        Assert.DoesNotContain("/Dests", raw);
    }

    /// <summary>
    /// <see cref="TextRun.LinkAnchor"/> reaches the output. It is the field the DOCX mapper sets,
    /// and until now nothing read it — so this is the case that actually closes the converted-DOCX
    /// defect, rather than the <c>"#name"</c> spelling a caller of the builder would use.
    /// </summary>
    [Fact]
    public void LinkAnchor_IsHonouredAndNotOnlyTheHashSpelling()
    {
        var doc = new Document();
        var section = new Section();

        var target = new Paragraph { AnchorName = "sec2" };
        target.Runs.Add(new TextRun { Text = "Section Two" });

        var source = new Paragraph();
        source.Runs.Add(new TextRun { Text = "see", LinkAnchor = "sec2" });

        section.Children.Add(source);
        section.Children.Add(target);
        doc.Sections.Add(section);

        string raw = PdfContentProbe.Raw(doc.ToPdf());

        Assert.Contains("/S /GoTo", raw);
        Assert.Contains("/D (sec2)", raw);
    }

    /// <summary>
    /// A forward link — the shape a table of contents is made of — resolves. Destinations are
    /// therefore gathered across every page before any action is written; resolving inside the
    /// page loop would work only for backward links, which is the subtler half of this feature and
    /// the one a single-page test would not notice.
    /// </summary>
    [Fact]
    public void AForwardLinkToALaterPage_Resolves()
    {
        var doc = new Document();
        var section = new Section();

        var source = new Paragraph();
        source.Runs.Add(new TextRun { Text = "jump", LinkAnchor = "later" });
        section.Children.Add(source);

        // Enough paragraphs to push the target onto a second page.
        for (int i = 0; i < 80; i++)
        {
            var filler = new Paragraph();
            filler.Runs.Add(new TextRun { Text = "filler line " + i });
            section.Children.Add(filler);
        }

        var target = new Paragraph { AnchorName = "later" };
        target.Runs.Add(new TextRun { Text = "Arrived" });
        section.Children.Add(target);
        doc.Sections.Add(section);

        byte[] pdf = doc.ToPdf();
        Assert.True(PdfContentProbe.CountPages(pdf) > 1,
            "the fixture stayed on one page, so it does not test a forward reference at all");
        Assert.Contains("/D (later)", PdfContentProbe.Raw(pdf));
    }

    /// <summary>
    /// A duplicate name keeps the <b>first</b> declaration. There is no correct answer, and the
    /// alternative — last wins — makes a table of contents whose entries all jump to the last
    /// section, which reads as a link bug rather than as the duplicate-name bug it is.
    /// </summary>
    [Fact]
    public void ADuplicateAnchorName_KeepsTheFirstDeclaration()
    {
        var doc = new Document();
        var section = new Section();

        var first = new Paragraph { AnchorName = "dup" };
        first.Runs.Add(new TextRun { Text = "First" });
        var second = new Paragraph { AnchorName = "dup" };
        second.Runs.Add(new TextRun { Text = "Second" });
        var source = new Paragraph();
        source.Runs.Add(new TextRun { Text = "go", LinkAnchor = "dup" });

        section.Children.Add(first);
        section.Children.Add(second);
        section.Children.Add(source);
        doc.Sections.Add(section);

        string raw = PdfContentProbe.Raw(doc.ToPdf());

        // Exactly one destination named "dup" in the /Names array, and the y it carries is the
        // first paragraph's — which is higher up the page, i.e. the larger y.
        int declarations = CountOccurrences(raw, "(dup)");
        Assert.Equal(2, declarations);   // one in the /Dests names array, one in the /GoTo action
    }

    /// <summary>
    /// The HTML exporter carries both halves too. This is the check GAPS asks for by name when it
    /// warns about a model field "silently dropped by three of them": a target that exists only in
    /// the PDF output is a field that looks implemented and is not.
    /// </summary>
    [Fact]
    public void TheHtmlExporter_EmitsBothTheIdAndTheFragmentLink()
    {
        var doc = new Document();
        var section = new Section();

        var source = new Paragraph();
        source.Runs.Add(new TextRun { Text = "go", LinkAnchor = "sec2" });
        var target = new Paragraph { AnchorName = "sec2" };
        target.Runs.Add(new TextRun { Text = "Section Two" });

        section.Children.Add(source);
        section.Children.Add(target);
        doc.Sections.Add(section);

        string html = System.Text.Encoding.UTF8.GetString(doc.ToHtml());

        Assert.Contains("id=\"sec2\"", html);
        Assert.Contains("href=\"#sec2\"", html);
    }

    // ----------------------------------------------------------------

    private static byte[] BuildPdf(string anchor, string linkTo)
    {
        var doc = new Document();
        var section = new Section();

        var source = new Paragraph();
        source.Runs.Add(new TextRun { Text = "go", LinkUri = "#" + linkTo });
        section.Children.Add(source);

        if (anchor != null)
        {
            var target = new Paragraph { AnchorName = anchor };
            target.Runs.Add(new TextRun { Text = "Target" });
            section.Children.Add(target);
        }

        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }
}
