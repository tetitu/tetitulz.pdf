// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfAccessibility.ClassifyPageContent"/> — "is this page blank?" and "is this page a
/// scan?", which were computable but not askable.
/// </summary>
/// <remarks>
/// <para>The counts behind UA24 already answered both; a caller wanting them had to run a whole
/// audit and string-match an issue code, which yields <i>nothing at all</i> for a blank page. The
/// classifier reads the same <c>PageInfo</c> the audit builds, so the two cannot drift on what "an
/// image and no text" means.</para>
///
/// <para><b>The Form XObject case is the one that separates a real implementation from a plausible
/// one.</b> A page whose only text sits inside an invoked form is not a scan — counting page-level
/// <c>/Contents</c> alone accuses it of being one, which is exactly the defect UA24's own remarks
/// record.</para>
/// </remarks>
public class PageContentClassificationTests
{
    [Fact]
    public void APageThatOnlyShowsText_IsTextOnly()
    {
        var page = Classify(OnePage("BT /F1 12 Tf 10 10 Td (Hi) Tj ET"));

        Assert.Equal(PageContentKind.TextOnly, page.Kind);
        Assert.False(page.IsBlank);
        Assert.False(page.IsScanned);
        Assert.Equal(1, page.TextShowingOperators);
    }

    /// <summary>The scanned-page signature: a raster and no text at all.</summary>
    [Fact]
    public void APageThatOnlyDrawsAnImage_IsScanned()
    {
        var page = Classify(OnePageWithImage("q 100 0 0 100 0 0 cm /Im0 Do Q"));

        Assert.Equal(PageContentKind.ImageOnly, page.Kind);
        Assert.True(page.IsScanned);
        Assert.Equal(1, page.ImageXObjectInvocations);
    }

    /// <summary>
    /// An <b>inline</b> image is the other half of "this page draws a raster" (§8.9.7), and a scan
    /// written that way is still a scan.
    /// </summary>
    /// <remarks>
    /// Written because a mutation dropping <c>HasInlineImage</c> from the rule left all nine tests
    /// green: every fixture used an image XObject, so half the predicate was never exercised.
    /// </remarks>
    [Fact]
    public void APageWhoseOnlyRasterIsInline_IsStillScanned()
    {
        var page = Classify(OnePage("BI /W 1 /H 1 /BPC 8 /CS /G ID \x01 EI"));

        Assert.True(page.HasInlineImage);
        Assert.Equal(PageContentKind.ImageOnly, page.Kind);
        Assert.True(page.IsScanned);
    }

    [Fact]
    public void AnEmptyContentStream_IsBlank()
    {
        var page = Classify(OnePage(""));

        Assert.Equal(PageContentKind.Blank, page.Kind);
        Assert.True(page.IsBlank);
        Assert.False(page.IsScanned);
    }

    /// <summary>
    /// <b>Vector art is not blank.</b> Without a painting counter, a chart, a rule or a logo — a page
    /// covered in ink — reports as empty, which is the single most damaging wrong answer this API
    /// can give.
    /// </summary>
    [Fact]
    public void APageOfVectorArt_IsNotBlank()
    {
        var page = Classify(OnePage("0 0 1 rg 10 10 100 100 re f"));

        Assert.Equal(PageContentKind.VectorOnly, page.Kind);
        Assert.False(page.IsBlank);
        Assert.Equal(1, page.PaintingOperators);
    }

    /// <summary>
    /// <c>n</c> ends a path without painting it — it exists to set a clip. A page whose only content
    /// is a clipping path really is blank, and counting <c>n</c> as ink would say otherwise.
    /// </summary>
    [Fact]
    public void APageWhoseOnlyPathIsAClip_IsStillBlank()
    {
        var page = Classify(OnePage("10 10 100 100 re W n"));

        Assert.Equal(PageContentKind.Blank, page.Kind);
    }

    /// <summary>
    /// <b>The trap.</b> The page invokes an image and a Form XObject, and the form holds the only
    /// text. Counting page-level <c>/Contents</c> alone reports a scan; the shared walk descends.
    /// </summary>
    [Fact]
    public void TextInsideAFormXObject_CountsAsText()
    {
        var page = Classify(ImageAndFormWithText());

        Assert.Equal(PageContentKind.Mixed, page.Kind);
        Assert.False(page.IsScanned);
        Assert.Equal(1, page.TextShowingOperators);
        Assert.Equal(1, page.ImageXObjectInvocations);
    }

    /// <summary>Each page is classified on its own, and the numbering is one-based as the report model uses.</summary>
    [Fact]
    public void EachPageIsClassifiedSeparately()
    {
        var pages = PdfAccessibility.ClassifyPageContent(new MemoryStream(TwoDifferentPages()));

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].PageNumber);
        Assert.Equal(PageContentKind.TextOnly, pages[0].Kind);
        Assert.Equal(2, pages[1].PageNumber);
        Assert.Equal(PageContentKind.Blank, pages[1].Kind);
    }

    /// <summary>
    /// A page whose content will not decode is <see cref="PageContentKind.Undetermined"/>, and
    /// <b>not</b> blank — "we could not tell" must never read as "there is nothing there", which is
    /// the answer that would send a caller's pipeline past a page it should have flagged.
    /// </summary>
    [Fact]
    public void APageWhoseContentWillNotDecode_IsUndeterminedRatherThanBlank()
    {
        var page = Classify(UndecodableContent());

        Assert.Equal(PageContentKind.Undetermined, page.Kind);
        Assert.False(page.IsBlank);
        Assert.False(page.IsScanned);
        Assert.NotNull(page.UndeterminedReason);
    }

    /// <summary>
    /// The reason is non-null <b>exactly</b> when the kind is Undetermined — the contract the
    /// property documents, and the thing a caller branches on.
    /// </summary>
    [Fact]
    public void TheUndeterminedReasonIsPresentOnlyWhenUndetermined()
    {
        foreach (var page in PdfAccessibility.ClassifyPageContent(new MemoryStream(TwoDifferentPages())))
            Assert.Null(page.UndeterminedReason);
    }

    // ------------------------------------------------------------------

    private static PageContentClassification Classify(byte[] pdf)
    {
        var pages = PdfAccessibility.ClassifyPageContent(new MemoryStream(pdf));
        Assert.NotEmpty(pages);
        return pages[0];
    }

    private static byte[] OnePage(string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj(content),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static byte[] OnePageWithImage(string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /XObject << /Im0 5 0 R >> >> >>",
        StreamObj(content),
        StreamObj("\x01\x02\x03",
            "/Type /XObject /Subtype /Image /Width 1 /Height 1"
            + " /ColorSpace /DeviceRGB /BitsPerComponent 8"));

    /// <summary>A page drawing an image directly and its only text through a Form XObject.</summary>
    private static byte[] ImageAndFormWithText() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /XObject << /Im0 5 0 R /Fx0 6 0 R >> >> >>",
        StreamObj("q 100 0 0 100 0 0 cm /Im0 Do Q /Fx0 Do"),
        StreamObj("\x01\x02\x03",
            "/Type /XObject /Subtype /Image /Width 1 /Height 1"
            + " /ColorSpace /DeviceRGB /BitsPerComponent 8"),
        StreamObj("BT /F1 12 Tf 10 10 Td (Hi) Tj ET",
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"
            + " /Resources << /Font << /F1 7 0 R >> >>"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static byte[] TwoDifferentPages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj("BT /F1 12 Tf 10 10 Td (Hi) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << >> >>",
        StreamObj(""));

    /// <summary>
    /// A page whose <c>/Contents</c> claims <c>/FlateDecode</c> over bytes that are not a zlib
    /// stream, so the decode fails rather than producing an empty result.
    /// </summary>
    private static byte[] UndecodableContent() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj("not a flate stream at all", "/Filter /FlateDecode"));

    private static string StreamObj(string data, string extraKeys = null) =>
        "<< " + (extraKeys != null ? extraKeys + " " : "") +
        "/Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
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
