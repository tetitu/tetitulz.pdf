// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The editor-level test the composite-font CID row owed. <c>PdfRedactionEngine</c> computes each
/// glyph's advance to decide where it sits, and feeds that width to the intersection test against
/// the redaction rectangles — so a wrong advance does not merely misplace text, it decides
/// <b>whether content the caller asked to destroy is found at all</b>.
///
/// <para>A composite font's <c>/W</c> array is indexed by <b>CID</b> (§9.7.4.3). CID equals the
/// character code only under <c>Identity-H</c>, which every fixture in this suite used, so keying
/// on the code looked correct for years.</para>
///
/// <para><b>Why the primitives were not enough.</b> The first tests written for this fix asserted
/// <c>SplitCodes</c> / <c>GetWidth</c> / <c>IsWordSpaceCode</c> — and reverting both editors to the
/// code-keyed read left every one of them green, because they never invoked an editor. This file
/// drives the real engine.</para>
/// </summary>
public class RedactionCompositeFontAdvanceTests
{
    // Codes 'A'..'E' map to CIDs 5..9 through the embedded CMap. /W gives those CIDs a full em;
    // /DW is deliberately narrow, so a code-keyed lookup (codes 65..69 are absent from /W) yields
    // 100 units instead of 1000 and the run collapses to a tenth of its true length.
    private const double EmWidth = 1000;
    private const double NarrowDefaultWidth = 100;
    private const double FontSize = 12;

    /// <summary>Correct advance per glyph, in points: 1000/1000 * 12.</summary>
    private const double CorrectAdvancePt = EmWidth / 1000.0 * FontSize;      // 12 pt

    private static byte[] FiveGlyphPdf()
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin\nbegincmap\n" +
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 begincidrange\n<0041> <0045> 5\nendcidrange\n" +
            "endcmap\n";

        // /ToUnicode so extraction can read the glyphs back as "ABCDE".
        const string toUnicode =
            "/CIDInit /ProcSet findresource begin\nbegincmap\n" +
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "5 beginbfchar\n<0041> <0041>\n<0042> <0042>\n<0043> <0043>\n<0044> <0044>\n<0045> <0045>\nendbfchar\n" +
            "endcmap\n";

        // Five two-byte codes: 0041 0042 0043 0044 0045 -> "ABCDE", drawn from x = 100, y = 700.
        const string content =
            "BT /F0 12 Tf 1 0 0 1 100 700 Tm <00410042004300440045> Tj ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 6 0 R "
                + "/DescendantFonts [7 0 R] /ToUnicode 8 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test "
                + $"/DW {NarrowDefaultWidth} /W [5 [{EmWidth}] 6 [{EmWidth}] 7 [{EmWidth}] "
                + $"8 [{EmWidth}] 9 [{EmWidth}]] >>",
            $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string RedactAndExtract(double x, double width)
    {
        using var input = new MemoryStream(FiveGlyphPdf());
        using var output = new MemoryStream();

        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea
            {
                PageIndex = 0,
                X = (float)x,
                Y = 695,
                Width = (float)width,
                Height = 20,
            },
        });

        output.Position = 0;
        return PdfOperations.ExtractText(output);
    }

    /// <summary>
    /// The whole point. Under the correct CID-keyed advance the five glyphs sit at
    /// x = 100, 112, 124, 136, <b>148</b>. A rectangle over the last one destroys only 'E'.
    ///
    /// <para>Under the old code-keyed read every advance was <c>/DW</c> = 100 units = 1.2 pt, so
    /// the run occupied x = 100..104.8 and this rectangle covered <b>nothing at all</b> — the
    /// redaction silently succeeded and left every glyph in the file.</para>
    /// </summary>
    [Fact]
    public void ARectangleOverTheLastGlyphDestroysItWhenAdvancesComeFromTheCid()
    {
        // Glyph 5 spans x = 148..160. The rectangle sits strictly INSIDE it, because the engine
        // inflates every glyph box by IntersectMargin = 1 pt before testing — deliberately, so
        // advance drift can never leak — and a rectangle that merely touches the boundary takes
        // the neighbour too. 152..158 clears glyph 4's inflated box (135..149).
        double lastGlyphX = 100 + 4 * CorrectAdvancePt;

        string text = RedactAndExtract(lastGlyphX + 4, 6);

        Assert.DoesNotContain("E", text);
        Assert.Contains("A", text);
        Assert.Contains("D", text);
    }

    /// <summary>
    /// The control that makes the test above a statement about <i>position</i> rather than about
    /// redaction working at all: the same-sized rectangle over the FIRST glyph destroys 'A' and
    /// leaves 'E'. Both glyphs are at x = 100 under the broken advance, so a code-keyed engine
    /// cannot satisfy both of these tests at once.
    /// </summary>
    [Fact]
    public void ARectangleOverTheFirstGlyphDestroysItAndLeavesTheRest()
    {
        // Glyph 1 spans x = 100..112; 103..109 is inside it and clear of glyph 2's inflated box.
        string text = RedactAndExtract(103, 6);

        Assert.DoesNotContain("A", text);
        Assert.Contains("E", text);
    }

    /// <summary>
    /// And the negative control: a rectangle well past the end of the run destroys nothing. Without
    /// it, an engine that deleted the whole text object would pass both tests above.
    /// </summary>
    [Fact]
    public void ARectangleBeyondTheRunLeavesEveryGlyph()
    {
        string text = RedactAndExtract(300, 40);

        foreach (string glyph in new[] { "A", "B", "C", "D", "E" })
            Assert.Contains(glyph, text);
    }
}
