// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// UA25/UA26 reach fonts wherever a page can reach them.
///
/// <para><b>Why this exists.</b> <c>PdfUaChecker.CollectFonts</c> descended into Form XObjects and
/// then <c>return</c>ed, so a font reachable only through a tiling pattern's <c>/Resources</c> or a
/// Type 3 font's glyph procedures was never examined. <c>PdfDeepAccessibilityChecker</c>, which
/// answers the same question on the other side of the assembly boundary, descends into both — so
/// the same non-embedded font was reported by one checker and silently passed by the other.</para>
///
/// <para>The font pair being numbered oppositely on the two sides (deep: UA20 Unicode / UA21
/// embedding; plugin: UA25 embedding / UA26 Unicode) is deliberate and is NOT what this tests.</para>
/// </summary>
public class UaFontWalkReachTests
{
    /// <summary>A non-embedded font reachable only through a tiling pattern is reported.</summary>
    [Fact]
    public void NonEmbeddedFont_InsideATilingPattern_IsReported()
    {
        byte[] pdf = PatternFontPdf();

        var codes = Validate(pdf);

        Assert.Contains("UA25", codes);
    }

    /// <summary>A non-embedded font reachable only through a Type 3 glyph procedure is reported.</summary>
    [Fact]
    public void NonEmbeddedFont_InsideAType3GlyphProcedure_IsReported()
    {
        byte[] pdf = Type3FontPdf();

        var codes = Validate(pdf);

        Assert.Contains("UA25", codes);
    }

    /// <summary>
    /// A page whose only font is embedded reports no embedding violation — the control, so the two
    /// tests above cannot pass by the checker having started reporting UA25 unconditionally.
    /// </summary>
    [Fact]
    public void AnEmbeddedFont_InsideATilingPattern_IsNotReportedAsUnembedded()
    {
        byte[] pdf = PatternFontPdf(embedded: true);

        var codes = Validate(pdf);

        Assert.DoesNotContain("UA25", codes);
    }

    /// <summary>
    /// ISO 14289-1 7.21.4.1 covers every font used for rendering, and an annotation's appearance
    /// stream renders: a font it alone names is reported when it has no program, and not otherwise.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AFontInsideAnAnnotationAppearance_IsJudgedForEmbedding(bool embedded, bool reported)
    {
        var codes = Validate(AppearanceFontPdf(embedded));

        Assert.Equal(reported, codes.Contains("UA25"));
    }

    /// <summary>Page resources name no font; the FONT lives in a FreeText's appearance stream.</summary>
    private static byte[] AppearanceFontPdf(bool embedded)
    {
        const string appearance = "BT /AF 12 Tf 1 1 Td (x) Tj ET";
        string fontObj = embedded
            ? "8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Emb /FontDescriptor 9 0 R >>\nendobj\n"
            : "8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /NotEmbedded >>\nendobj\n";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R /MarkInfo << /Marked true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> /Annots [5 0 R] >>\nendobj\n",
            "4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Annot /Subtype /FreeText /Rect [0 0 100 20] /F 4 /Contents (x) /DA (/AF 12 Tf 0 g) /AP << /N 6 0 R >> >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /AF 8 0 R >> >> /Length {appearance.Length} >>"
                + $"\nstream\n{appearance}\nendstream\nendobj\n",
            "7 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n",
            fontObj,
            "9 0 obj\n<< /Type /FontDescriptor /FontName /Emb /Flags 4 /FontFile 7 0 R >>\nendobj\n",
            "10 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n",
        };
        return Build(objs);
    }

    // ------------------------------------------------------------------

    private static List<string> Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations.Select(v => v.Code).ToList();
    }

    /// <summary>Page resources name only a tiling pattern; the FONT lives in the pattern.</summary>
    private static byte[] PatternFontPdf(bool embedded = false)
    {
        const string patContent = "BT /PF 12 Tf 1 1 Td (x) Tj ET";
        string fontObj = embedded
            ? "8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Emb /FontDescriptor 9 0 R >>\nendobj\n"
            : "8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /NotEmbedded >>\nendobj\n";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R /MarkInfo << /Marked true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Pattern << /P0 5 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n",
            $"5 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 "
                + $"/Resources << /Font << /PF 8 0 R >> >> /Length {patContent.Length} >>"
                + $"\nstream\n{patContent}\nendstream\nendobj\n",
            "6 0 obj\n<< >>\nendobj\n",
            "7 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n",   // the font program: a stream, as a key alone is not
            fontObj,
            "9 0 obj\n<< /Type /FontDescriptor /FontName /Emb /Flags 4 /FontFile 7 0 R >>\nendobj\n",
            "10 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n",
        };
        return Build(objs);
    }

    /// <summary>Page resources name only a Type 3 font; a REAL font hides in its glyph resources.</summary>
    private static byte[] Type3FontPdf()
    {
        const string glyph = "BT /GF 12 Tf 1 1 Td (x) Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R /MarkInfo << /Marked true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /T3 5 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type3 /FontBBox [0 0 10 10] /FontMatrix [0.001 0 0 0.001 0 0] "
                + "/CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> "
                + "/FirstChar 97 /LastChar 97 /Widths [10] "
                + "/Resources << /Font << /GF 8 0 R >> >> >>\nendobj\n",
            $"6 0 obj\n<< /Length {glyph.Length} >>\nstream\n{glyph}\nendstream\nendobj\n",
            "7 0 obj\n<< >>\nendobj\n",
            "8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /NotEmbedded >>\nendobj\n",
            "9 0 obj\n<< >>\nendobj\n",
            "10 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
