// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A Type 3 glyph that draws nothing must still consume its advance, by the same rule in both
/// renderers.
/// </summary>
/// <remarks>
/// <para><b>Why this was invisible.</b> Both divergences are in the ADVANCE, so nothing about them
/// shows in the glyph that failed — only in where every glyph <i>after</i> it lands, and the error
/// accumulates across the run. A pixel-comparing parity suite structurally cannot see that: each
/// renderer is internally consistent, and the two disagree about position, not colour.</para>
///
/// <para><b>Why the fixture uses <c>/FontMatrix [1 0 0 1 0 0]</c>.</b> §9.6.5 lets a Type 3 font
/// declare any matrix. At the conventional <c>[0.001 …]</c> the 0.5-em-versus-0 disagreement is
/// 0.0005·FontSize points — real, and far too small for any assertion to catch, which is exactly
/// how it survived. At the unit matrix it is half the font size per glyph. Our own PostScript
/// converter emits both halves of this: <c>PdfResourceCollector</c> writes <c>/Widths</c> covering
/// only the glyphs it built, and copies the font's matrix verbatim.</para>
/// </remarks>
public class Type3AdvanceParityTests
{
    /// <summary>
    /// A code outside <c>/FirstChar…/LastChar</c> advances by <b>0</b>, as the raster path's
    /// <c>GetType3Width</c> answers — not by half an em.
    /// </summary>
    [Fact]
    public void CodeOutsideWidthsRange_AdvancesByZero_InTheSvgExport()
    {
        // /Widths covers code 65 only. Code 66 is out of range and absent from /CharProcs, so it
        // draws nothing; 'A' follows it and must land where the raster path puts it.
        string svg = RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 10 10 Td (\\102\\101) Tj ET"));

        Assert.Equal(10d, FirstGlyphX(svg), 1);
    }

    /// <summary>
    /// A CharProc whose stream will not decode also advances — losing the advance loses the
    /// position of every later glyph, not the glyph that failed.
    /// </summary>
    [Fact]
    public void UndecodableCharProc_StillAdvances_InTheSvgExport()
    {
        // Code 65's CharProc claims /FlateDecode over bytes that are not deflate, so
        // GetDecodedData throws. Code 66 is DEFINED here so there is a later glyph to measure —
        // it must be displaced by 65's full width (50), landing at 60 rather than 10.
        string svg = RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 10 10 Td (\\101\\102) Tj ET",
                                        corruptA: true, defineB: true));

        Assert.Equal(60d, FirstGlyphX(svg), 1);
    }

    /// <summary>
    /// The floor: an ordinary two-glyph run still advances by the declared width, so neither
    /// assertion above can be satisfied by an exporter that has stopped advancing at all.
    /// </summary>
    [Fact]
    public void OrdinaryRun_AdvancesByTheDeclaredWidth()
    {
        string svg = RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 10 10 Td (\\101\\101) Tj ET"));

        // Two drawn glyphs: the first at x=10, the second one full width (50) later.
        var xs = GlyphXs(svg);
        Assert.Equal(2, xs.Count);
        Assert.Equal(10d, xs[0], 1);
        Assert.Equal(60d, xs[1], 1);
    }

    /// <summary>
    /// §9.4.4 adds <c>Tc</c> to <b>every</b> glyph's advance, Type 3 included.
    /// </summary>
    /// <remarks>
    /// <b>This term was entirely untested.</b> Dropping <c>charSpacing</c> from the advance left all
    /// 3 108 Base tests green — the three copies of the formula agreed with each other, and the only
    /// assertions on any of them used <c>Tc = 0</c>. It is the same blind spot this class was
    /// created for, one term over: the glyph that "fails" looks perfect and everything after it
    /// slides.
    /// </remarks>
    [Fact]
    public void CharSpacingIsAddedToEveryAdvance()
    {
        // Width 1 at the unit matrix and 50 pt gives 50; Tc 7 makes each advance 57.
        var xs = GlyphXs(RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 7 Tc 10 10 Td (\\101\\101) Tj ET")));

        Assert.Equal(2, xs.Count);
        Assert.Equal(10d, xs[0], 1);
        Assert.Equal(67d, xs[1], 1);
    }

    /// <summary>
    /// §9.3.3 adds <c>Tw</c> only for single-byte code <b>32</b>, and it applies even when that code
    /// contributes no width of its own.
    /// </summary>
    /// <remarks>
    /// Code 32 is outside this font's <c>/Widths</c> and absent from <c>/CharProcs</c>, so it draws
    /// nothing and its glyph width is 0 — which makes the gap between the two drawn glyphs
    /// <i>only</i> the first glyph's width plus <c>Tw</c>. That isolates the word-spacing term from
    /// everything else in the formula.
    /// </remarks>
    [Fact]
    public void WordSpacingAppliesToCode32EvenWithNoWidth()
    {
        var xs = GlyphXs(RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 5 Tw 10 10 Td (\\101\\040\\101) Tj ET")));

        Assert.Equal(2, xs.Count);
        Assert.Equal(10d, xs[0], 1);
        Assert.Equal(65d, xs[1], 1);   // 10 + width 50 + Tw 5
    }

    /// <summary>
    /// The floor for the pair above: with <c>Tc</c> and <c>Tw</c> at their defaults the same run
    /// advances by the width alone, so neither assertion can be met by a renderer that adds a
    /// constant regardless.
    /// </summary>
    [Fact]
    public void WithNoSpacingTheAdvanceIsTheWidthAlone()
    {
        var xs = GlyphXs(RenderSvg(Type3Pdf(content: "BT /T3 50 Tf 10 10 Td (\\101\\040\\101) Tj ET")));

        Assert.Equal(2, xs.Count);
        Assert.Equal(60d, xs[1], 1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The pen x of the first drawn glyph.</summary>
    private static double FirstGlyphX(string svg) => GlyphXs(svg)[0];

    /// <summary>
    /// The leading x of every filled glyph path, in document order.
    /// </summary>
    /// <remarks>
    /// A Type 3 glyph is emitted with its geometry already in page coordinates — the CharProc here
    /// draws <c>0 0 1 1 re f</c>, so the path's opening <c>M{x},{y}</c> IS the pen position. Read
    /// from the path data rather than a group transform, which this exporter does not use for
    /// Type 3. The page-flip wrapper carries no <c>d</c>, so it cannot be mistaken for a glyph.
    /// </remarks>
    private static System.Collections.Generic.List<double> GlyphXs(string svg)
    {
        var xs = new System.Collections.Generic.List<double>();
        foreach (var e in XDocument.Parse(svg).Descendants())
        {
            if (e.Name.LocalName != "path") continue;
            if ((e.Attribute("fill")?.Value ?? "none") == "none") continue;

            string d = e.Attribute("d")?.Value;
            if (d == null || !d.StartsWith("M", StringComparison.Ordinal)) continue;

            string head = d.Substring(1).Split(' ')[0];   // "10,10"
            string xPart = head.Split(',')[0];
            if (double.TryParse(xPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                xs.Add(x);
        }
        Assert.True(xs.Count > 0, "No drawn Type 3 glyph was emitted:\n" + svg);
        return xs;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// A Type 3 font at the UNIT font matrix with <c>/Widths [1]</c> covering code 65 only, and a
    /// <c>/CharProcs</c> holding just <c>/A</c>. Code 66 is therefore both out of the width range
    /// and undefined.
    /// </summary>
    private static byte[] Type3Pdf(string content, bool corruptA = false, bool defineB = false)
    {
        const string glyph = "1 0 0 1 0 0 d0 0 0 1 1 re f\n";
        string procA = corruptA
            ? "<< /Filter /FlateDecode /Length 8 >>\nstream\nNOTFLATE\nendstream"
            : $"<< /Length {glyph.Length} >>\nstream\n{glyph}endstream";

        // defineB false: code 66 is /Missing — absent from /CharProcs AND outside /Widths, which
        // is the out-of-range case. defineB true: 66 is a real, drawable glyph, so a later glyph
        // exists to measure the corrupt one's advance against.
        string charProcs = defineB ? "<< /A 7 0 R /B 8 0 R >>" : "<< /A 7 0 R >>";
        string diffs     = defineB ? "[65 /A 66 /B]" : "[65 /A 66 /Missing]";
        string widths    = defineB ? "/FirstChar 65 /LastChar 66 /Widths [1 1]"
                                   : "/FirstChar 65 /LastChar 65 /Widths [1]";

        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Contents 4 0 R" +
            " /Resources << /Font << /T3 5 0 R >> >> >>",
            Stream(content + "\n"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0]" +
            $" /CharProcs 6 0 R /Encoding << /Type /Encoding /Differences {diffs} >>" +
            $" {widths} /Resources << >> >>",
            charProcs,
            procA,
        };
        if (defineB) objects.Add($"<< /Length {glyph.Length} >>\nstream\n{glyph}endstream");

        return Build(objects.ToArray());
    }

    private static string Stream(string data) =>
        $"<< /Length {data.Length} >>\nstream\n{data}endstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
