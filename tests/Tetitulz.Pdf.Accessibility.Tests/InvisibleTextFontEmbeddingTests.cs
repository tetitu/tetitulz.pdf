// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// ISO 14289-1 7.21.4.1: a font must be embedded when it is <i>used</i> — a glyph of it referenced
/// from a content stream — and NOTE 2 exempts one used only in render mode 3, which paints nothing.
/// The two PDF/UA checkers, the deep checker's UA21 and the plugin's UA25, ask the same question,
/// and the audit runs both; they must give one answer.
/// </summary>
/// <remarks>
/// <b>They did not.</b> UA21 tracked the text state and granted the exemption; UA25 counted every
/// font in a page's resources, used or not, painted or not. The audit of this library's own
/// searchable PDF — an OCR layer written, on purpose, in a font that is not embedded — reported it
/// non-conforming.
/// </remarks>
public class InvisibleTextFontEmbeddingTests
{
    private static readonly byte[] Font = MinimalTrueType.Build(
        new string(Enumerable.Range(0x20, 0x5F).Select(c => (char)c).ToArray()));

    /// <summary>
    /// A searchable scan: a conforming tagged page with this library's OCR text layer on it. The
    /// Hebrew word routes the layer through its composite font, which <c>OcrTextLayerFont</c> does
    /// not embed because nothing ever draws it.
    /// </summary>
    [Fact]
    public void AnInvisibleOcrLayersFontIsReportedByNeitherChecker()
    {
        var tagged = new MemoryStream();
        new PdfTaggedDocumentBuilder()
            .SetFontProvider(_ => Font)
            .SetTitle("Scan")
            .SetLanguage("he-IL")
            .AddParagraph("Page body.")
            .Build(tagged);

        using var output = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(new MemoryStream(tagged.ToArray()), 0,
            new List<PdfOcrWord> { new PdfOcrWord("שלום", 72, 600, 60, 12) }, output);

        var codes = PdfAccessibility.Audit(new MemoryStream(output.ToArray())).Issues.Select(i => i.Code).ToList();

        Assert.DoesNotContain("UA21", codes);
        Assert.DoesNotContain("UA25", codes);
    }

    /// <summary>
    /// The pair that isolates the rule: one non-embedded Helvetica, drawn in mode 3 and then in mode
    /// 0. Invisible, neither checker reports it; painted, both do — so the answer turns on the render
    /// mode, and both checkers see the same one.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void BothCheckersReportANonEmbeddedFontExactlyWhenItPaints(int renderMode, bool reported)
    {
        var codes = PdfAccessibility.Audit(new MemoryStream(HelveticaDrawnIn(renderMode))).Issues
                                    .Select(i => i.Code).ToList();

        Assert.Equal(reported, codes.Contains("UA21"));
        Assert.Equal(reported, codes.Contains("UA25"));
    }

    /// <summary>
    /// A font in the page's resources that no content draws with is not used, and 7.21.4.1 asks
    /// nothing of it. The page does draw text — in the other font, invisibly — so the checkers have
    /// content to read and a font it names; they find nothing because there is nothing to find.
    /// </summary>
    [Fact]
    public void AFontNothingDrawsWithIsNotReportedByTheCheckerThatReadsResources()
    {
        var codes = PdfAccessibility.Audit(new MemoryStream(UnusedHelvetica())).Issues.Select(i => i.Code).ToList();

        Assert.DoesNotContain("UA25", codes);
        Assert.DoesNotContain("UA21", codes);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>One page drawing "Hello" in a non-embedded Helvetica, in the given render mode.</summary>
    private static byte[] HelveticaDrawnIn(int renderMode) => Page(
        "BT /F1 12 Tf " + renderMode.ToString(CultureInfo.InvariantCulture) + " Tr 72 700 Td (Hello) Tj ET",
        "/F1 5 0 R",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>
    /// One page whose resources name a non-embedded Helvetica as /F2 that nothing draws with; its text
    /// is drawn in /F1 invisibly, so the page still references a font — just not that one.
    /// </summary>
    private static byte[] UnusedHelvetica() => Page(
        "BT /F1 12 Tf 3 Tr 72 700 Td (Hello) Tj ET",
        "/F1 5 0 R /F2 6 0 R",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>Objects: 1 catalog, 2 pages, 3 page, 4 content, then the fonts from 5.</summary>
    private static byte[] Page(string content, string fontResources, params string[] fonts)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << " + fontResources + " >> >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" +
            content + "\nendstream",
        };
        bodies.AddRange(fonts);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
