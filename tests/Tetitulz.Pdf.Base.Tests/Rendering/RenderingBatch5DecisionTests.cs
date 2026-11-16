// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decisions the <c>Base/Rendering</c> sweep's batch 5 (<c>.claude/sweep-rendering-5.ps1</c>) found
/// no test for: OCMD policies over more than one group, the text clip and glyph capture at a device
/// scale other than 1, the raster Type 3 path, and the OpenType tables <c>CffOtfWrapper</c> writes.
/// </summary>
public class RenderingBatch5DecisionTests
{
    // ---------------------------------------------------------------- OCMD policies

    private static PdfDictionary Ocmd(string policy, params int[] groups)
    {
        var ocgs = new PdfArray();
        foreach (int g in groups) ocgs.Add(new PdfReference(g, 0));
        var d = new PdfDictionary();
        d[PdfName.Type] = new PdfName("OCMD");
        d[new PdfName("OCGs")] = ocgs;
        if (policy != null) d[new PdfName("P")] = new PdfName(policy);
        return d;
    }

    /// <summary>
    /// OC07 and OC10: with one of two groups off, <c>/AllOn</c> hides and <c>/AnyOn</c> (also the
    /// default with no <c>/P</c>) shows — the one input that tells the two policies apart.
    /// </summary>
    [Fact]
    public void AllOnAndAnyOnDisagreeWhenOneOfTwoGroupsIsOff()
    {
        var hidden = new HashSet<int> { 5 };

        Assert.True(ContentStreamInterpreter.IsOcmdHidden(Ocmd("AllOn", 5, 6), hidden, null));
        Assert.False(ContentStreamInterpreter.IsOcmdHidden(Ocmd("AnyOn", 5, 6), hidden, null));
        Assert.False(ContentStreamInterpreter.IsOcmdHidden(Ocmd(null, 5, 6), hidden, null));
    }

    /// <summary>OC11: <c>/OCGs</c> may be a single group rather than an array (Table 99).</summary>
    [Fact]
    public void AnOcmdNamingASingleGroupDirectlyIsEvaluated()
    {
        var d = new PdfDictionary();
        d[PdfName.Type] = new PdfName("OCMD");
        d[new PdfName("OCGs")] = new PdfReference(5, 0);
        d[new PdfName("P")] = new PdfName("AllOff");

        Assert.True(ContentStreamInterpreter.IsOcmdHidden(d, new HashSet<int>(), null));    // group on
        Assert.False(ContentStreamInterpreter.IsOcmdHidden(d, new HashSet<int> { 5 }, null)); // group off
    }

    // ---------------------------------------------------------------- Type 3, raster

    /// <summary>
    /// A 200 × 100 page with one Type 3 font, <c>/T3</c>: code 65 is glyph <c>/a</c>, code 66 is
    /// named by nothing, all three codes 65–67 are 100 units wide. <paramref name="charProc"/> is
    /// <c>/a</c>'s stream; <paramref name="fontResources"/> the font's own <c>/Resources</c>.
    /// </summary>
    private static byte[] Type3Pdf(string content, string charProc,
        string fontMatrix = "[0.01 0 0 0.01 0 0]", string fontResources = null)
    {
        const string form = "0 0 100 100 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /T3 5 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix " + fontMatrix
                + " /CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /a] >>"
                + " /FirstChar 65 /LastChar 128 /Widths [" + string.Join(" ", System.Linq.Enumerable.Repeat("100", 64)) + "]"
                + (fontResources != null ? " /Resources " + fontResources : "") + " >>",
            Stream(charProc),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length " + form.Length + " >>\nstream\n" + form + "\nendstream",
        };
        return Assemble(objects);
    }

    private const string SquareGlyph = "100 0 0 0 100 100 d1 0 0 100 100 re f";

    private static bool Ink(SKBitmap bmp, float x, float y) => PixelAssert.IsInk(PixelAssert.At(bmp, x, y));

    /// <summary>T301: a singular <c>/FontMatrix</c> falls back to the 1000-unit default — a 5 pt glyph, not nothing.</summary>
    [Fact]
    public void ASingularType3FontMatrixFallsBackToTheDefault()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 10 10 Td (A) Tj ET", SquareGlyph,
            fontMatrix: "[0 0 0 0 0 0]"));

        Assert.True(Ink(bmp, 12, 12));
        Assert.False(Ink(bmp, 30, 30));
    }

    /// <summary>
    /// T303: the glyph procedure's own <c>d0</c> width (200 units, 100 pt here) moves the next glyph,
    /// not <c>/Widths</c>' 100 units — the second square starts at 110, not 60.
    /// </summary>
    [Fact]
    public void AType3GlyphAdvancesByItsD0Width()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 10 10 Td (AA) Tj ET",
            "200 0 d0 0 0 100 100 re f"));

        Assert.True(Ink(bmp, 115, 35));
        Assert.False(Ink(bmp, 85, 35));
    }

    /// <summary>T304: <c>Tz 50</c> halves a Type 3 glyph's width — the square spans 10 to 35.</summary>
    [Fact]
    public void HorizontalScalingNarrowsAType3Glyph()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 50 Tz 10 10 Td (A) Tj ET", SquareGlyph));

        Assert.True(Ink(bmp, 20, 35));
        Assert.False(Ink(bmp, 45, 35));
    }

    /// <summary>T305: a glyph procedure's named resources come from the font's own <c>/Resources</c>.</summary>
    [Fact]
    public void AType3GlyphReadsTheFontsOwnResources()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 10 10 Td (A) Tj ET",
            "100 0 0 0 100 100 d1 /Fm Do", fontResources: "<< /XObject << /Fm 7 0 R >> >>"));

        Assert.True(Ink(bmp, 35, 35));
    }

    /// <summary>
    /// T307: a code nothing names — not <c>/Differences</c>, not StandardEncoding (128 is a hole in
    /// it) — still advances by its width, so A lands at 60.
    /// </summary>
    [Fact]
    public void AnUnnamedType3CodeStillAdvances()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 10 10 Td (\\200A) Tj ET", SquareGlyph));

        Assert.True(Ink(bmp, 85, 35));
        Assert.False(Ink(bmp, 35, 35));
    }

    /// <summary>
    /// T306: a Type 3 glyph that shows its own font is stopped at the render depth limit and says so.
    /// Without the guard the recursion has no other bound on this path.
    /// </summary>
    [Fact]
    public void ARecursiveType3FontIsStoppedAtTheDepthLimit()
    {
        int thread = Environment.CurrentManagedThreadId;
        var events = new List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler = (k, d, _) =>
        {
            if (Environment.CurrentManagedThreadId == thread && k == RenderDiagnosticKind.ContentStreamError)
                lock (events) events.Add(d);
        };
        RenderDiagnostics.OnEvent += handler;
        try
        {
            using var bmp = PixelAssert.Render(Type3Pdf("BT /T3 50 Tf 10 10 Td (A) Tj ET",
                "100 0 0 0 100 100 d1 BT /T3 1 Tf (A) Tj ET 0 0 100 100 re f",
                fontResources: "<< /Font << /T3 5 0 R >> >>"));

            Assert.True(Ink(bmp, 35, 35));
        }
        finally { RenderDiagnostics.OnEvent -= handler; }

        Assert.Contains(events, d => d.Contains("Type 3 glyph nesting"));
    }

    // ---------------------------------------------------------------- text clip and capture at 2×

    private static byte[] TextPdf(string content, bool withPattern = false)
    {
        const string tile = "0 0 1 rg 0 0 10 10 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>"
                + (withPattern ? " /Pattern << /P0 6 0 R >>" : "") + " >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length "
                + tile.Length + " >>\nstream\n" + tile + "\nendstream",
        };
        return Assemble(objects);
    }

    private static double BlueFraction(byte[] pdf, int dpi)
    {
        using var bmp = PixelAssert.Render(pdf, dpi);
        int blue = PixelAssert.CountColor(bmp, new SKColor(0, 0, 255));
        return (double)blue / (bmp.Width * bmp.Height);
    }

    /// <summary>
    /// TC04: a text clip covers the same share of the page at 144 dpi as at 72. The run's outline is
    /// taken in device pixels and divided back to page points; left undivided at 2× it is drawn at
    /// twice the size, four times the area.
    /// </summary>
    [SystemFontFact("Arial")]
    public void ATextClipCoversTheSameShareOfThePageAtAnyResolution()
    {
        // Small and near the top-left, so the clip doubled from the device origin stays on the page.
        byte[] pdf = TextPdf("BT /F1 20 Tf 7 Tr 2 75 Td (MM) Tj ET 0 0 1 rg 0 0 200 100 re f");

        double at72 = BlueFraction(pdf, 72), at144 = BlueFraction(pdf, 144);
        Assert.True(at72 > 0.005, $"the clip let nothing through at 72 dpi ({at72})");
        Assert.InRange(at144 / at72, 0.67, 1.5);   // antialiased edges; the defect is about 4
    }

    /// <summary>TC05: pattern-filled text, captured the same way, likewise.</summary>
    [SystemFontFact("Arial")]
    public void PatternFilledTextCoversTheSameShareOfThePageAtAnyResolution()
    {
        byte[] pdf = TextPdf("/Pattern cs /P0 scn BT /F1 20 Tf 2 75 Td (MM) Tj ET", withPattern: true);

        double at72 = BlueFraction(pdf, 72), at144 = BlueFraction(pdf, 144);
        Assert.True(at72 > 0.005, $"the pattern text painted nothing at 72 dpi ({at72})");
        Assert.InRange(at144 / at72, 0.67, 1.5);   // antialiased edges; the defect is about 4
    }

    // ---------------------------------------------------------------- CffOtfWrapper tables

    private static byte[] Table(byte[] sfnt, string tag)
    {
        int count = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < count; i++)
        {
            int rec = 12 + i * 16;
            if (Encoding.ASCII.GetString(sfnt, rec, 4) != tag) continue;
            int off = (sfnt[rec + 8] << 24) | (sfnt[rec + 9] << 16) | (sfnt[rec + 10] << 8) | sfnt[rec + 11];
            int len = (sfnt[rec + 12] << 24) | (sfnt[rec + 13] << 16) | (sfnt[rec + 14] << 8) | sfnt[rec + 15];
            var t = new byte[len];
            Array.Copy(sfnt, off, t, 0, len);
            return t;
        }
        throw new InvalidOperationException("no " + tag);
    }

    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

    /// <summary>
    /// CW01 and CW02: the exported font's <c>hmtx</c> carries each glyph's width, the default width
    /// where none was extracted (NaN); CW03: its <c>maxp</c> is version 0.5, the only one a CFF font
    /// may carry (OpenType <c>maxp</c>), read from the raw bytes since a parser would accept either.
    /// </summary>
    [Fact]
    public void TheWrappedFontCarriesItsWidthsAndACffMaxp()
    {
        var font = new CffFont(new[] { new byte[] { 14 }, new byte[] { 14 }, new byte[] { 14 } },
            Array.Empty<byte[]>(), Array.Empty<byte[]>(), new Dictionary<int, int>(), 600f, 0f);
        font.GlyphWidths[1] = 450f;

        byte[] otf = CffOtfWrapper.TryBuild(new byte[] { 1, 0, 4, 1 }, font);
        Assert.NotNull(otf);

        byte[] hmtx = Table(otf, "hmtx");
        Assert.Equal(600, U16(hmtx, 0));      // NaN → the default width
        Assert.Equal(450, U16(hmtx, 4));
        Assert.Equal(600, U16(hmtx, 8));

        byte[] maxp = Table(otf, "maxp");
        Assert.Equal(0x0000, U16(maxp, 0));
        Assert.Equal(0x5000, U16(maxp, 2));
        Assert.Equal(3, U16(maxp, 4));
    }

    // ---------------------------------------------------------------- fixture assembly

    private static string Stream(string data) =>
        string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", data.Length, data);

    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
