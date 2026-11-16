// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The emitted <see cref="PdfTextChar"/> box must live in the same space as the emitted
/// origin — PDF page space. The origin goes through <c>Compose(Tm, CTM)</c>, so the box has
/// to carry that same scale.
///
/// <para>
/// The case that breaks is a producer writing <c>1 Tf</c> and carrying the point size in the
/// matrix instead (<c>12 0 0 12 x y Tm</c>) — which is exactly what Cairo and matplotlib emit.
/// With the box left in unscaled text space, every inter-glyph gap dwarfed the word-split
/// threshold in <c>AssembleLines</c> and each glyph became its own word, which
/// <c>PdfTableExtractor</c>, <c>PdfLayoutAnalyzer</c> and <c>PdfSpatialQuery</c> all inherit.
/// </para>
/// </summary>
public class PdfStructuredTextExtractorMatrixScaleTests
{
    // ------------------------------------------------------------------
    //  In-memory PDF builder (single Helvetica font, single page)
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfPageTextLayout Layout(string contentStream)
    {
        using var ms = new MemoryStream(BuildPdf(contentStream));
        return PdfStructuredTextExtractor.Extract(ms)[0];
    }

    private static IReadOnlyList<PdfTextChar> Chars(string contentStream) =>
        Layout(contentStream).Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).ToList();

    // ==================================================================
    //  The reported failure: word assembly
    // ==================================================================

    [Fact]
    public void TmScaledFontSize_AssemblesOneWord_NotOneWordPerGlyph()
    {
        // "1 Tf" with the size carried by the matrix — the Cairo / matplotlib shape.
        // Glyph origins advance by charW * 12 while the box stayed charW * 1, so the
        // gap between adjacent glyphs read as ~11x the preceding advance width.
        var lines = Layout("BT /F1 1 Tf 12 0 0 12 100 700 Tm (Hello) Tj ET\n").Lines;

        var word = Assert.Single(Assert.Single(lines).Words);
        Assert.Equal("Hello", word.Text);
    }

    // ==================================================================
    //  The arithmetic: the two ways of asking for 12pt must agree
    // ==================================================================

    [Fact]
    public void TmScaledFontSize_ReportsSameCharBoxAsDirectFontSize()
    {
        // "1 Tf" + a 12x matrix and "12 Tf" + an identity matrix describe the same
        // rendered glyph, so they must report the same page-space box.
        var viaMatrix = Chars("BT /F1 1 Tf 12 0 0 12 100 700 Tm (H) Tj ET\n");
        var viaTf     = Chars("BT /F1 12 Tf 1 0 0 1 100 700 Tm (H) Tj ET\n");

        Assert.Equal("H", Assert.Single(viaMatrix).Char);
        Assert.Equal("H", Assert.Single(viaTf).Char);

        Assert.Equal(viaTf[0].Width,  viaMatrix[0].Width,  precision: 3);
        Assert.Equal(viaTf[0].Height, viaMatrix[0].Height, precision: 3);

        // And the box is the 12pt one, not the 1pt one — pins the direction of the fix.
        Assert.Equal(12f, viaMatrix[0].Height, precision: 3);
    }

    [Fact]
    public void CtmScale_IsCarriedIntoTheCharBox()
    {
        // The CTM half of the same rule: `cm` before BT scales the glyph on the page,
        // so it must scale the reported box too.
        var plain  = Chars("BT /F1 12 Tf 1 0 0 1 50 350 Tm (H) Tj ET\n");
        var scaled = Chars("2 0 0 2 0 0 cm BT /F1 12 Tf 1 0 0 1 50 350 Tm (H) Tj ET\n");

        Assert.Equal(2f * Assert.Single(plain).Width,  Assert.Single(scaled).Width,  precision: 3);
        Assert.Equal(24f, scaled[0].Height, precision: 3);
    }

    // ==================================================================
    //  Rotation carries no scale — guards the column-norm form of the fix
    // ==================================================================

    [Fact]
    public void PureRotation_LeavesTheCharBoxUnscaled()
    {
        // A 90 degree matrix has [a b c d] = [0 1 -1 0]: both column norms are 1, so the box
        // is unchanged. Reading the scale off fullTm[0] / fullTm[3] instead would collapse
        // this box to zero.
        var upright = Chars("BT /F1 12 Tf 1 0 0 1 100 700 Tm (H) Tj ET\n");
        var rotated = Chars("BT /F1 12 Tf 0 1 -1 0 100 700 Tm (H) Tj ET\n");

        Assert.Equal(Assert.Single(upright).Width, Assert.Single(rotated).Width, precision: 3);
        Assert.Equal(12f, rotated[0].Height, precision: 3);
    }
}
