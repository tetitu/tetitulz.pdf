// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Closes the three font gaps left open by the 2026-08-06 rendering audit (GAPS.md §2,
/// "Still open" under group 2):
///
/// <list type="number">
///   <item>a symbolic TrueType font must select glyphs through its built-in <c>(3,0)</c> cmap
///         (§9.6.6.4), and a nonsymbolic one with no <c>/Encoding</c> defaults to
///         StandardEncoding, not WinAnsi — the <c>/Flags</c> Symbolic bit was read nowhere in
///         the tree;</item>
///   <item>a TrueType <c>/Differences</c> name that is not in the Adobe glyph list must still
///         resolve — through the font's own <c>post</c> table, or the <c>gNN</c>/<c>cidNN</c>
///         subset convention;</item>
///   <item>the vertical position vector <c>v</c> (<c>/DW2[0]</c>, and <c>v1x</c>/<c>v1y</c> in a
///         <c>/W2</c> entry) must be readable, not parsed into a comment.</item>
/// </list>
///
/// <para><b>Why the fixtures build a real font.</b> Gaps 1 and 2 are both "which glyph id came
/// out", and a glyph id is only meaningful against a specific font program. Every rendering
/// assertion here therefore draws through an sfnt built in this file whose glyph 1 is a
/// <b>narrow tall</b> box and glyph 2 a <b>wide short</b> one — so picking the wrong glyph is
/// visible in the ink bounds, on any host, with no system font involved and no way for a blank
/// page or a substituted face to pass.</para>
/// </summary>
public sealed class FontGapClosureTests
{
    // Glyph 1 and glyph 2 are deliberately transposed rectangles: whichever one the renderer
    // picked is readable straight off the ink bounds.
    private const int NarrowTallGid = 1;
    private const int WideShortGid  = 2;
    private const int NarrowTallW = 200, NarrowTallH = 800;
    private const int WideShortW  = 800, WideShortH  = 200;

    private const float FontSize = 100f;   // 1000 upem / 100 pt ⇒ 10 glyph units per point
    private const float PenX = 100f, PenY = 400f;

    // ==================================================================
    //  Gap 1 — symbolic TrueType fonts and their (3,0) cmap
    // ==================================================================

    /// <summary>
    /// The fixture font carries <b>both</b> a <c>(3,0)</c> symbol subtable (code 0x41 → the wide
    /// glyph) and a <c>(3,1)</c> Unicode one (U+0041 → the narrow glyph). Every platform's font
    /// engine prefers the Unicode table, so a Unicode round-trip
    /// (<c>Decode</c> → <c>SKTypeface.GetGlyph</c>) draws the narrow glyph and §9.6.6.4's rule
    /// draws the wide one. There is no host on which both answers coincide, and no system font
    /// is involved in either.
    /// </summary>
    [Fact]
    public void SymbolicTrueType_NoEncoding_SelectsGlyphThroughItsOwn30Cmap()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } },
            symbolCmap:  new Dictionary<int, int> { { 0x41, WideShortGid  } });

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: true, encoding: null, showByte: 0x41);

        AssertDrewGlyph(pdf, WideShortW, WideShortH);
    }

    /// <summary>
    /// The same font, with the Symbolic bit cleared and everything else identical, must keep the
    /// Unicode behaviour — the rule is keyed on <c>/Flags</c>, so a nonsymbolic font may not be
    /// dragged onto the <c>(3,0)</c> table. This is the other half of the claim: without it a
    /// "fix" that always preferred (3,0) would pass the test above.
    /// </summary>
    [Fact]
    public void NonSymbolicTrueType_NoEncoding_KeepsTheUnicodeCmap()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } },
            symbolCmap:  new Dictionary<int, int> { { 0x41, WideShortGid  } });

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: false, encoding: null, showByte: 0x41);

        AssertDrewGlyph(pdf, NarrowTallW, NarrowTallH);
    }

    /// <summary>
    /// A symbolic font that <i>does</i> carry an <c>/Encoding</c> is outside §9.6.6.4's built-in
    /// rule, and must keep resolving through that encoding.
    /// </summary>
    [Fact]
    public void SymbolicTrueType_WithEncoding_DoesNotUseTheBuiltInCmap()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } },
            symbolCmap:  new Dictionary<int, int> { { 0x41, WideShortGid  } });

        byte[] pdf = BuildSimpleFontPdf(
            font, symbolic: true, encoding: "/WinAnsiEncoding", showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.IsSymbolic);
        Assert.False(ttf.UsesBuiltInEncoding);
        Assert.False(ttf.TryGetGlyphId(0x41, out _));

        AssertDrewGlyph(pdf, NarrowTallW, NarrowTallH);
    }

    /// <summary>
    /// Real symbol fonts (marlett, wingdings, the legacy Hebrew faces this repo already has a
    /// fallback for) put their <c>(3,0)</c> entries at <c>0xF000 | code</c> rather than at the
    /// raw code. §9.6.6.4 names both, so both must resolve — and the code here is 0xE1, which
    /// the old last-ditch fallback would only have reached when the decoded character happened
    /// to be non-ASCII.
    /// </summary>
    [Fact]
    public void SymbolicTrueType_F000Convention_ResolvesTheRawByte()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: null,
            symbolCmap:  new Dictionary<int, int> { { 0xF0E1, WideShortGid } });

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: true, encoding: null, showByte: 0xE1);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0xE1, out ushort gid));
        Assert.Equal(WideShortGid, gid);
    }

    /// <summary>
    /// A symbolic font with no <c>(3,0)</c> table selects through its <c>(1,0)</c> Macintosh one
    /// (§9.6.6.4 names it as the alternative). The control: a code that table does not map
    /// still reports no glyph.
    /// </summary>
    [Fact]
    public void SymbolicTrueType_WithOnlyAMacCmap_SelectsThroughIt()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: null, symbolCmap: null,
            macCmap: new Dictionary<int, int> { { 0x41, WideShortGid } });

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: true, encoding: null, showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(WideShortGid, gid);
        Assert.False(ttf.TryGetGlyphId(0x42, out _));
    }

    /// <summary>
    /// A code the built-in table does not map must say so rather than inventing a glyph — the
    /// caller then falls through to the Unicode path.
    /// </summary>
    [Fact]
    public void SymbolicTrueType_UnmappedCode_ReportsNoGlyph()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: null,
            symbolCmap:  new Dictionary<int, int> { { 0x41, WideShortGid } });

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: true, encoding: null, showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0x41, out _));
        Assert.False(ttf.TryGetGlyphId(0x42, out ushort none));
        Assert.Equal(0, none);
    }

    /// <summary>
    /// §9.6.6.4's other half: with no <c>/Encoding</c> the implicit base encoding of a
    /// nonsymbolic font is <b>StandardEncoding</b>. The two tables differ at 0xA4 —
    /// <c>fraction</c> (U+2044) in StandardEncoding, <c>currency</c> (U+00A4) in WinAnsi — which
    /// is what a WinAnsi default would answer for every font.
    /// </summary>
    [Fact]
    public void NonSymbolicTrueType_NoEncoding_DecodesWithStandardEncodingNotWinAnsi()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } }, symbolCmap: null);
        byte[] pdf = BuildSimpleFontPdf(font, symbolic: false, encoding: null, showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.Equal("⁄", ttf.Decode(new byte[] { 0xA4 }));   // fraction, not currency
        Assert.NotEqual("¤", ttf.Decode(new byte[] { 0xA4 }));
    }

    // ==================================================================
    //  Gap 2 — /Differences names the Adobe glyph list cannot resolve
    // ==================================================================

    /// <summary>
    /// <c>/Differences [65 /boxwide]</c> where "boxwide" is a name only the embedded font's
    /// <c>post</c> table knows. The name resolves through neither the Adobe glyph list nor any
    /// encoding, so without the <c>post</c> table byte 0x41 would silently fall back to the base
    /// encoding's 'A' — the narrow glyph. §9.6.6.4 consults the <c>post</c> table first, which
    /// names the wide one.
    /// </summary>
    [Fact]
    public void Differences_SubsetName_ResolvesThroughThePostTable()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } },
            symbolCmap:  null,
            postNames:   new Dictionary<int, string> { { WideShortGid, "boxwide" } });

        byte[] pdf = BuildSimpleFontPdf(
            font, symbolic: false, encoding: "<< /Differences [65 /boxwide] >>", showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(WideShortGid, gid);

        AssertDrewGlyph(pdf, WideShortW, WideShortH);
    }

    /// <summary>
    /// The subset conventions that spell a glyph id into the name. No <c>post</c> table is
    /// present, so nothing but the convention can answer.
    /// </summary>
    [Theory]
    [InlineData("g2")]
    [InlineData("G2")]
    [InlineData("glyph2")]
    [InlineData("cid2")]
    [InlineData("index2")]
    public void Differences_GlyphIndexName_ResolvesToThatGlyphId(string name)
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } }, symbolCmap: null);

        byte[] pdf = BuildSimpleFontPdf(
            font, symbolic: false, encoding: $"<< /Differences [65 /{name}] >>", showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(WideShortGid, gid);

        AssertDrewGlyph(pdf, WideShortW, WideShortH);
    }

    /// <summary>
    /// Ordinary glyph names must NOT be mistaken for the <c>gNN</c> convention — several real
    /// Adobe names begin with 'g', and one of them resolving to a glyph index would corrupt
    /// perfectly good documents.
    /// </summary>
    [Theory]
    [InlineData("g")]
    [InlineData("grave")]
    [InlineData("germandbls")]
    [InlineData("gcircumflex")]
    [InlineData("cid")]
    [InlineData("g12x")]
    [InlineData("g123456")]         // beyond a 16-bit glyph id
    // 2^32: an int32 accumulator wraps this to exactly 0, so only bounding the DIGIT COUNT
    // rejects it — the "is it a plausible glyph id" range check cannot see the overflow.
    [InlineData("g4294967296")]
    public void GlyphIndexNameParser_RejectsNamesThatAreNotGlyphIndices(string name)
        => Assert.False(PdfTrueTypeFont.TryParseGlyphIndexName(name, out _));

    [Theory]
    [InlineData("g0", 0)]
    [InlineData("g35", 35)]
    [InlineData("cid12", 12)]
    [InlineData("glyph007", 7)]
    [InlineData("index65535", 65535)]
    public void GlyphIndexNameParser_AcceptsTheSubsetConventions(string name, int expected)
    {
        Assert.True(PdfTrueTypeFont.TryParseGlyphIndexName(name, out int gid));
        Assert.Equal(expected, gid);
    }

    /// <summary>
    /// The <c>uniXXXX</c> convention resolves to a character, not a glyph id, and stays on the
    /// Unicode path — pinned here because gap 2 lists it and because the post-table change must
    /// not disturb it.
    /// </summary>
    [Fact]
    public void Differences_UniName_StillDecodesToItsCodePoint()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } }, symbolCmap: null);

        byte[] pdf = BuildSimpleFontPdf(
            font, symbolic: false, encoding: "<< /Differences [65 /uni05D0 /u05D1] >>", showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.Equal("אב", ttf.Decode(new byte[] { 0x41, 0x42 }));
    }

    /// <summary>
    /// A <c>post</c> entry wins over the <c>gNN</c> reading of the same name, because the font
    /// author's own table is the authority on what it named its glyphs.
    /// </summary>
    [Fact]
    public void Differences_PostTableWinsOverTheGlyphIndexConvention()
    {
        // The name "g1" is spelled like glyph 1, but this font's post table says it is glyph 2.
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, NarrowTallGid } },
            symbolCmap:  null,
            postNames:   new Dictionary<int, string> { { WideShortGid, "g1" } });

        byte[] pdf = BuildSimpleFontPdf(
            font, symbolic: false, encoding: "<< /Differences [65 /g1] >>", showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(WideShortGid, gid);
    }

    /// <summary>
    /// A glyph id indexes the <b>embedded</b> program. When there is none — a non-embedded
    /// TrueType resolved to a system face — the built-in path must decline rather than aim a
    /// stale index at a substitute font, and it must say why.
    /// </summary>
    [Fact]
    public void NonEmbeddedTrueType_HasNoBuiltInMapping_AndSaysWhy()
    {
        byte[] pdf = BuildSimpleFontPdf(
            fontProgram: null, symbolic: true, encoding: null, showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.UsesBuiltInEncoding);
        Assert.False(ttf.HasBuiltInGlyphMapping);
        Assert.False(ttf.TryGetGlyphId(0x41, out _));
        Assert.Contains("FontFile2", PdfTrueTypeFont.LastFontProgramError);
    }

    /// <summary>
    /// A glyph id from the embedded program is meaningless against a <b>substitute</b> face, so
    /// when the program is readable to us but not loadable as a typeface, the built-in path must
    /// stand down — and say so, rather than aim a stale index at whatever font the host supplied.
    ///
    /// <para>The fixture is the exact shape that separates the two: a font with a valid
    /// <c>cmap</c> and no outline tables at all. The parser reads its <c>(3,0)</c> mapping
    /// happily; no rasteriser can build a face from it.</para>
    /// </summary>
    [Fact]
    public void UnloadableFontProgram_BuiltInMappingIsReadButNotApplied_AndIsReported()
    {
        byte[] font = TestFont.Build(
            unicodeCmap:  null,
            symbolCmap:   new Dictionary<int, int> { { 0x41, WideShortGid } },
            outlinesOnly: false);

        byte[] pdf = BuildSimpleFontPdf(font, symbolic: true, encoding: null, showByte: 0x41);

        // Our own reader does understand the program …
        var ttf = LoadTrueTypeFont(pdf);
        Assert.True(ttf.HasBuiltInGlyphMapping);
        Assert.True(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(WideShortGid, gid);

        // … but rendering must not use it, because the face on screen is a substitute.
        var reported = new List<string>();
        void OnEvent(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.FontLoadFailed) reported.Add(detail);
        }

        RenderDiagnostics.OnEvent += OnEvent;
        try { using var _ = PixelAssert.Render(pdf); }
        finally { RenderDiagnostics.OnEvent -= OnEvent; }

        Assert.Contains(reported, d => d.Contains("system substitute"));
    }

    /// <summary>
    /// Adversarial input: a <c>/FontFile2</c> that is not a font, and one that is a truncated
    /// sfnt whose table directory points past the end. Neither may throw, hang or produce a
    /// mapping, and both must leave a reason behind.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedFontProgram_IsRefusedWithAReason(int variant)
    {
        byte[] good = TestFont.Build(
            unicodeCmap: null, symbolCmap: new Dictionary<int, int> { { 0x41, WideShortGid } });

        byte[] bad = variant switch
        {
            0 => Encoding.ASCII.GetBytes("this is not a font at all, not even close"),
            1 => good.Take(40).ToArray(),                       // header + a truncated directory
            2 => CorruptTableOffsets(good),                     // offsets past the end of the file
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        byte[] pdf = BuildSimpleFontPdf(bad, symbolic: true, encoding: null, showByte: 0x41);

        var ttf = LoadTrueTypeFont(pdf);
        Assert.False(ttf.TryGetGlyphId(0x41, out ushort gid));
        Assert.Equal(0, gid);
        Assert.False(ttf.HasBuiltInGlyphMapping);
    }

    // ==================================================================
    //  Gap 3 — the vertical position vector
    // ==================================================================

    /// <summary>
    /// Table 117's defaults with no <c>/DW2</c> at all: the position vector is
    /// <c>(w0 / 2, 880)</c>, so it depends on the glyph's own horizontal width — 0.3 em here,
    /// from <c>/DW 600</c>, not a hard-coded half.
    /// </summary>
    [Fact]
    public void VerticalMetrics_NoDw2_PositionVectorIsHalfTheGlyphWidthAnd880()
    {
        byte[] pdf = BuildVerticalCidPdf(dw: "600", dw2: null, w2: null);

        var m = VerticalMetricsOf(pdf, cid: 1);
        Assert.False(m.IsExplicit);
        Assert.Equal(1.0f,  m.AdvanceEm,   3);   // /DW2 default w1y = -1000
        Assert.Equal(0.30f, m.PositionXEm, 3);   // 600 / 2 / 1000
        Assert.Equal(0.88f, m.PositionYEm, 3);   // Table 117 default
    }

    /// <summary>
    /// <c>/DW2[0]</c> is the position vector's y. It was read into a comment and dropped, which
    /// is the whole of this gap for the default case: every vertical glyph sat 880 units off.
    /// </summary>
    [Fact]
    public void VerticalMetrics_Dw2_PositionYComesFromItsFirstElement()
    {
        byte[] pdf = BuildVerticalCidPdf(dw: "1000", dw2: "[500 -800]", w2: null);

        var m = VerticalMetricsOf(pdf, cid: 1);
        Assert.Equal(0.5f, m.PositionYEm, 3);
        Assert.Equal(0.8f, m.AdvanceEm,   3);
        Assert.Equal(0.5f, m.PositionXEm, 3);    // still w0 / 2
    }

    /// <summary>
    /// Wave 17 R13.4: a number inside <c>/DW2</c> or a <c>/W2</c> entry may be an indirect
    /// reference, as one inside <c>/W</c> may — and <c>/W</c>'s reader resolved its elements while
    /// these two read them as stored. CID 1 has a <c>/W2</c> entry made of references; CID 2 falls
    /// to a <c>/DW2</c> made of references. Read raw, both come back as Table 117's defaults
    /// (advance 1.0, position y 0.88), which is what this test must not see.
    /// </summary>
    [Fact]
    public void VerticalMetrics_Dw2AndW2ElementsWrittenAsReferences_AreResolved()
    {
        byte[] pdf = BuildVerticalCidPdf(dw: "1000", dw2: "[7 0 R 8 0 R]", w2: "[1 [9 0 R 10 0 R 11 0 R]]",
            Obj(7, "500"), Obj(8, "-800"), Obj(9, "-700"), Obj(10, "250"), Obj(11, "870"));

        var inW2 = VerticalMetricsOf(pdf, cid: 1);
        Assert.True(inW2.IsExplicit);
        Assert.Equal(0.7f,  inW2.AdvanceEm,   3);
        Assert.Equal(0.25f, inW2.PositionXEm, 3);
        Assert.Equal(0.87f, inW2.PositionYEm, 3);

        var byDefault = VerticalMetricsOf(pdf, cid: 2);
        Assert.False(byDefault.IsExplicit);
        Assert.Equal(0.8f, byDefault.AdvanceEm,   3);
        Assert.Equal(0.5f, byDefault.PositionYEm, 3);
    }

    /// <summary>
    /// The range form is <c>cfirst clast w1y v1x v1y</c> — five numbers. Both the vector and the
    /// five-number stride are asserted here: the SECOND range only reads correctly if the first
    /// was stepped over by 5.
    /// </summary>
    [Fact]
    public void VerticalMetrics_W2RangeForm_CarriesTheVectorAndKeepsTheFiveNumberStride()
    {
        byte[] pdf = BuildVerticalCidPdf(
            dw: "1000", dw2: "[880 -1000]",
            w2: "[1 3 -900 450 780  10 12 -700 300 650]");

        var first = VerticalMetricsOf(pdf, cid: 2);
        Assert.True(first.IsExplicit);
        Assert.Equal(0.90f, first.AdvanceEm,   3);
        Assert.Equal(0.45f, first.PositionXEm, 3);
        Assert.Equal(0.78f, first.PositionYEm, 3);

        var second = VerticalMetricsOf(pdf, cid: 11);
        Assert.True(second.IsExplicit);
        Assert.Equal(0.70f, second.AdvanceEm,   3);
        Assert.Equal(0.30f, second.PositionXEm, 3);
        Assert.Equal(0.65f, second.PositionYEm, 3);
    }

    /// <summary>
    /// The array form is <c>c [w1y v1x v1y  w1y v1x v1y …]</c>. The second triplet is asserted,
    /// so a reader that took only the first number of each entry cannot pass.
    /// </summary>
    [Fact]
    public void VerticalMetrics_W2ArrayForm_CarriesTheVectorPerGlyph()
    {
        byte[] pdf = BuildVerticalCidPdf(
            dw: "1000", dw2: "[880 -1000]",
            w2: "[5 [-1000 500 880  -900 450 800]]");

        var g5 = VerticalMetricsOf(pdf, cid: 5);
        Assert.True(g5.IsExplicit);
        Assert.Equal(1.00f, g5.AdvanceEm,   3);
        Assert.Equal(0.50f, g5.PositionXEm, 3);
        Assert.Equal(0.88f, g5.PositionYEm, 3);

        var g6 = VerticalMetricsOf(pdf, cid: 6);
        Assert.True(g6.IsExplicit);
        Assert.Equal(0.90f, g6.AdvanceEm,   3);
        Assert.Equal(0.45f, g6.PositionXEm, 3);
        Assert.Equal(0.80f, g6.PositionYEm, 3);
    }

    /// <summary>
    /// A CID outside every <c>/W2</c> entry falls back to the <c>/DW2</c> default and reports
    /// that it did — <c>IsExplicit</c> is what lets a caller tell the two apart.
    /// </summary>
    [Fact]
    public void VerticalMetrics_CidOutsideW2_FallsBackToTheDefaultAndSaysSo()
    {
        byte[] pdf = BuildVerticalCidPdf(
            dw: "1000", dw2: "[860 -1000]", w2: "[1 3 -900 450 780]");

        var m = VerticalMetricsOf(pdf, cid: 99);
        Assert.False(m.IsExplicit);
        Assert.Equal(0.86f, m.PositionYEm, 3);
        Assert.Equal(1.00f, m.AdvanceEm,   3);
    }

    /// <summary>
    /// The advance projection must keep agreeing with the full metrics, or the renderer and any
    /// caller of the new API would drift apart.
    /// </summary>
    [Fact]
    public void GetVerticalAdvance_AgreesWithGetVerticalMetrics()
    {
        byte[] pdf = BuildVerticalCidPdf(
            dw: "1000", dw2: "[880 -1000]", w2: "[1 3 -900 450 780]");

        using var doc   = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        var res = doc.Pages[0].Resources;

        foreach (int cid in new[] { 1, 2, 3, 42 })
            Assert.Equal(
                cache.GetVerticalMetrics("F1", cid, res, doc, 1f).AdvanceEm,
                cache.GetVerticalAdvance("F1", cid, res, doc, 1f), 4);
    }

    /// <summary>
    /// The cache reads a font's <c>/W2</c> once, however many glyphs ask: it was scanned for every
    /// glyph shown, glyphs × entries. The answers are the scan's (the Core index's own tests hold
    /// that), so the read count is the evidence.
    /// </summary>
    [Fact]
    public void GetVerticalMetrics_ReadsTheW2ArrayOnce_ForEveryGlyph()
    {
        byte[] pdf = BuildVerticalCidPdf(
            dw: "1000", dw2: "[880 -1000]", w2: "[1 3 -900 450 780]");

        using var doc   = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        var res = doc.Pages[0].Resources;

        long before = Core.Fonts.CidVerticalMetrics.Index.ReadCount;
        foreach (int cid in new[] { 1, 2, 3, 42, 2, 1 })
            cache.GetVerticalMetrics("F1", cid, res, doc, 1f);

        Assert.Equal(1, Core.Fonts.CidVerticalMetrics.Index.ReadCount - before);
        Assert.True(cache.GetVerticalMetrics("F1", 2, res, doc, 1f).IsExplicit);
        Assert.False(cache.GetVerticalMetrics("F1", 42, res, doc, 1f).IsExplicit);
    }

    // ==================================================================
    //  Shared assertions and fixtures
    // ==================================================================

    /// <summary>
    /// Asserts that the page's single glyph was drawn at the pen with the given glyph-space
    /// extent. Both dimensions are checked, which is what distinguishes the two fixture glyphs
    /// from each other, from a system substitute and from a blank page.
    /// </summary>
    private static void AssertDrewGlyph(byte[] pdf, int glyphWidthUnits, int glyphHeightUnits)
    {
        using SKBitmap bmp = PixelAssert.Render(pdf);
        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        var (left, top, right, bottom) = box!.Value;

        // 1000 units per em at FontSize pt ⇒ units × FontSize / 1000 points, 1 pt = 1 px at 72 dpi.
        float expectedW = glyphWidthUnits  * FontSize / 1000f;
        float expectedH = glyphHeightUnits * FontSize / 1000f;

        const int Tol = 3;
        Assert.InRange(right - left, expectedW - Tol, expectedW + Tol);
        Assert.InRange(bottom - top, expectedH - Tol, expectedH + Tol);
        Assert.InRange(left,   PixelAssert.X(PenX) - Tol,     PixelAssert.X(PenX) + Tol);
        Assert.InRange(bottom, PixelAssert.Y(bmp, PenY) - Tol, PixelAssert.Y(bmp, PenY) + Tol);
    }

    private static PdfTrueTypeFont LoadTrueTypeFont(byte[] pdf)
    {
        using var doc   = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        var font = cache.GetPdfFont("F1", doc.Pages[0].Resources, doc);
        return Assert.IsType<PdfTrueTypeFont>(font);
    }

    private static VerticalGlyphMetrics VerticalMetricsOf(byte[] pdf, int cid)
    {
        using var doc   = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        return cache.GetVerticalMetrics("F1", cid, doc.Pages[0].Resources, doc, 1f);
    }

    /// <summary>Rewrites every table offset in the directory to just past the end of the file.</summary>
    private static byte[] CorruptTableOffsets(byte[] font)
    {
        var copy = (byte[])font.Clone();
        int numTables = (copy[4] << 8) | copy[5];
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16 + 8;
            uint bogus = (uint)copy.Length + 1024;
            copy[rec]     = (byte)(bogus >> 24);
            copy[rec + 1] = (byte)(bogus >> 16);
            copy[rec + 2] = (byte)(bogus >> 8);
            copy[rec + 3] = (byte)bogus;
        }
        return copy;
    }

    // ------------------------------------------------------------------
    //  PDF fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page PDF showing a single byte in a simple TrueType font, with the font program
    /// embedded as <c>/FontFile2</c> when one is supplied.
    /// </summary>
    private static byte[] BuildSimpleFontPdf(
        byte[] fontProgram, bool symbolic, string encoding, int showByte)
    {
        // §9.8.2 Table 123: bit 3 (value 4) Symbolic, bit 6 (value 32) Nonsymbolic.
        int flags = symbolic ? 4 : 32;

        string content =
            $"BT /F1 {FontSize} Tf {PenX} {PenY} Td <{showByte:X2}> Tj ET";

        var objects = new List<byte[]>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
                   " /Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", Encoding.ASCII.GetBytes(content)),
            Obj(5, "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont" +
                   $" /FirstChar {showByte} /LastChar {showByte} /Widths [{WideShortW}]" +
                   (encoding != null ? " /Encoding " + encoding : "") +
                   " /FontDescriptor 6 0 R >>"),
            Obj(6, "<< /Type /FontDescriptor /FontName /TestFont" +
                   $" /Flags {flags} /ItalicAngle 0 /StemV 80 /Ascent 800 /Descent -200" +
                   " /CapHeight 700 /FontBBox [0 -200 1000 900]" +
                   (fontProgram != null ? " /FontFile2 7 0 R" : "") + " >>"),
        };

        if (fontProgram != null)
            objects.Add(StreamObj(7, $"/Length1 {fontProgram.Length}", fontProgram));

        return AssemblePdf(objects);
    }

    /// <summary>A one-page PDF whose only font is a vertical (Identity-V) CIDFontType2.</summary>
    /// <param name="dw">The <c>/DW</c> value, as written.</param>
    /// <param name="dw2">The <c>/DW2</c> array, as written; null for none.</param>
    /// <param name="w2">The <c>/W2</c> array, as written; null for none.</param>
    /// <param name="extraObjects">Further objects, numbered from 7 — the targets of references the arrays carry.</param>
    private static byte[] BuildVerticalCidPdf(string dw, string dw2, string w2, params byte[][] extraObjects)
    {
        string cid =
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestVert" +
            " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
            $" /DW {dw}" +
            (dw2 != null ? " /DW2 " + dw2 : "") +
            (w2  != null ? " /W2 "  + w2  : "") + " >>";

        var objects = new List<byte[]>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
                   " /Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", Encoding.ASCII.GetBytes("BT /F1 20 Tf 100 700 Td <0001> Tj ET")),
            Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /TestVert /Encoding /Identity-V" +
                   " /DescendantFonts [6 0 R] >>"),
            Obj(6, cid),
        };
        objects.AddRange(extraObjects);
        return AssemblePdf(objects);
    }

    private static byte[] Obj(int number, string body)
        => Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n");

    private static byte[] StreamObj(int number, string extraKeys, byte[] data)
    {
        var ms = new MemoryStream();
        byte[] head = Encoding.ASCII.GetBytes(
            $"{number} 0 obj\n<< {extraKeys} /Length {data.Length} >>\nstream\n");
        ms.Write(head, 0, head.Length);
        ms.Write(data, 0, data.Length);
        byte[] tail = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] AssemblePdf(IReadOnlyList<byte[]> objectBodies)
    {
        var ms = new MemoryStream();
        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void Ascii(string s) => Write(Encoding.ASCII.GetBytes(s));

        Ascii("%PDF-1.4\n");
        var offsets = new long[objectBodies.Count];
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets[i] = ms.Length;
            Write(objectBodies[i]);
        }

        long xref = ms.Length;
        Ascii($"xref\n0 {objectBodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) Ascii($"{off:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {objectBodies.Count + 1} /Root 1 0 R >>\n" +
              $"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ==================================================================
    //  Horizontal scaling against PDF /Widths
    // ==================================================================

    /// <summary>
    /// §9.4.4 puts horizontal scaling <c>Th</c> in the text matrix, so it multiplies each advance —
    /// <b>once</b>. This is the <b>embedded</b>-font path, where the advances come from the PDF
    /// <c>/Widths</c>.
    /// </summary>
    /// <remarks>
    /// <b>Why a second test for this.</b> <c>ContentStreamInterpreter</c> has two advance paths and
    /// they do different arithmetic. For a <b>substituted</b> font it sets every <c>pdfWidths</c>
    /// entry to <c>-1</c> and lets SkiaSharp measure, so the line
    /// <c>pdfWidths[gi] = glyphAdv * widthScale</c> — where <c>glyphAdv</c> already carries
    /// <c>Th</c> and <c>widthScale</c> derives from the text matrix's <b>x</b>-axis, which carries
    /// it too — never executes. Only an embedded font reaches it. Double-scaling that line leaves
    /// <c>UncoveredRenderFeatureTests.HorizontalScaling_SubstitutedFont_ScalesTheAdvancesExactlyOnce</c>
    /// green, which is exactly how this hole was found: the standard-14 fixture there could not
    /// observe the code it appeared to be about.
    /// <para><b>The numbers are exact, not a band.</b> Five glyphs, <c>/Widths 800</c> at 1000 upem
    /// and 100 pt ⇒ an 80 pt advance and an 80 pt glyph, so the run spans 4 × 80 + 80 = 400 pt at
    /// full scale and 200 pt at half. A <c>Th</c> applied twice to the advances gives
    /// 4 × 20 + 40 = 120 pt, far outside the tolerance.</para>
    /// </remarks>
    [Fact]
    public void HorizontalScaling_WithEmbeddedFontWidths_ScalesTheAdvancesExactlyOnce()
    {
        byte[] font = TestFont.Build(
            unicodeCmap: new Dictionary<int, int> { { 0x41, WideShortGid } },
            symbolCmap:  new Dictionary<int, int>());

        using SKBitmap full = PixelAssert.Render(BuildScaledRunPdf(font, 100));
        using SKBitmap half = PixelAssert.Render(BuildScaledRunPdf(font, 50));

        var f = PixelAssert.InkBounds(full);
        var h = PixelAssert.InkBounds(half);
        Assert.True(f.HasValue && h.HasValue, "one of the runs painted nothing");

        const int Tol = 4;
        Assert.InRange(f!.Value.right - f.Value.left, 400 - Tol, 400 + Tol);
        Assert.InRange(h!.Value.right - h.Value.left, 200 - Tol, 200 + Tol);
    }

    internal static byte[] ProbeScaledRunPdf(int tzPercent) =>
        BuildScaledRunPdf(
            TestFont.Build(
                unicodeCmap: new Dictionary<int, int> { { 0x41, WideShortGid } },
                symbolCmap:  new Dictionary<int, int>()),
            tzPercent);

    /// <summary>Five copies of the wide glyph in an embedded font, under a <c>Tz</c>.</summary>
    private static byte[] BuildScaledRunPdf(byte[] fontProgram, int tzPercent)
    {
        string content =
            $"BT /F1 {FontSize} Tf {tzPercent} Tz {PenX} {PenY} Td <4141414141> Tj ET";

        return AssemblePdf(new List<byte[]>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
                   " /Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", Encoding.ASCII.GetBytes(content)),
            Obj(5, "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont" +
                   $" /FirstChar 65 /LastChar 65 /Widths [{WideShortW}]" +
                   " /FontDescriptor 6 0 R >>"),
            Obj(6, "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /ItalicAngle 0" +
                   " /StemV 80 /Ascent 800 /Descent -200 /CapHeight 700" +
                   " /FontBBox [0 -200 1000 900] /FontFile2 7 0 R >>"),
            StreamObj(7, $"/Length1 {fontProgram.Length}", fontProgram),
        });
    }

    // ==================================================================
    //  A real sfnt with two recognisable glyphs
    // ==================================================================

    /// <summary>
    /// Builds a genuine TrueType font program with three glyphs — <c>.notdef</c> (empty), a
    /// narrow tall filled rectangle and a wide short one — and with whichever <c>cmap</c>
    /// subtables and <c>post</c> names a test needs.
    ///
    /// <para>Nothing here is hand-waved: SkiaSharp loads these bytes, rasterises the outlines
    /// and the assertions read the resulting pixels, so a fixture that did not really describe
    /// a font would fail rather than quietly weaken the test.</para>
    /// </summary>
    private static class TestFont
    {
        private const int UnitsPerEm = 1000;
        private const int NumGlyphs  = 3;    // .notdef + narrow-tall + wide-short

        /// <param name="outlinesOnly">
        /// When false, the <c>head</c>/<c>maxp</c>/<c>loca</c>/<c>glyf</c>/<c>hmtx</c>/<c>hhea</c>
        /// tables are omitted: the result still carries a perfectly readable <c>cmap</c> and
        /// <c>post</c>, but no font engine can build a face from it. That is the shape needed to
        /// exercise "our parser understood the program, the rasteriser did not".
        /// </param>
        internal static byte[] Build(
            IReadOnlyDictionary<int, int> unicodeCmap,
            IReadOnlyDictionary<int, int> symbolCmap,
            IReadOnlyDictionary<int, string> postNames = null,
            bool outlinesOnly = true,
            IReadOnlyDictionary<int, int> macCmap = null)
        {
            var glyf = new MemoryStream();
            var loca = new List<int> { 0 };
            loca.Add((int)glyf.Length);                                     // .notdef: no outline
            WriteRectangleGlyph(glyf, NarrowTallW, NarrowTallH); loca.Add((int)glyf.Length);
            WriteRectangleGlyph(glyf, WideShortW,  WideShortH);  loca.Add((int)glyf.Length);

            var locaBytes = new MemoryStream();
            foreach (int off in loca) U16(locaBytes, off / 2);

            var hmtx = new MemoryStream();
            for (int i = 0; i < NumGlyphs; i++) { U16(hmtx, 800); I16(hmtx, 0); }

            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { "cmap", Cmap(unicodeCmap, symbolCmap, macCmap) },
                { "name", Name() },
                { "post", Post(postNames) },
                { "OS/2", Os2() },
            };
            if (outlinesOnly)
            {
                tables["head"] = Head();
                tables["hhea"] = Hhea();
                tables["maxp"] = Maxp();
                tables["hmtx"] = hmtx.ToArray();
                tables["loca"] = locaBytes.ToArray();
                tables["glyf"] = glyf.ToArray();
            }
            return Assemble(tables);
        }

        /// <summary>A single closed contour: (0,0) (w,0) (w,h) (0,h), all points on-curve.</summary>
        private static void WriteRectangleGlyph(Stream s, int w, int h)
        {
            I16(s, 1);          // numberOfContours
            I16(s, 0); I16(s, 0); I16(s, w); I16(s, h);   // bounding box
            U16(s, 3);          // endPtsOfContours[0] — four points, indices 0..3
            U16(s, 0);          // instructionLength
            for (int i = 0; i < 4; i++) s.WriteByte(0x01);  // ON_CURVE_POINT, 16-bit deltas
            I16(s, 0); I16(s,  w); I16(s, 0); I16(s, -w);   // x deltas
            I16(s, 0); I16(s,  0); I16(s, h); I16(s,  0);   // y deltas
            while (s.Length % 4 != 0) s.WriteByte(0);
        }

        private static byte[] Head()
        {
            var s = new MemoryStream();
            U32(s, 0x00010000); U32(s, 0x00010000); U32(s, 0); U32(s, 0x5F0F3CF5);
            U16(s, 0);                 // flags
            U16(s, UnitsPerEm);
            for (int i = 0; i < 16; i++) s.WriteByte(0);      // created + modified
            I16(s, 0); I16(s, -200); I16(s, 1000); I16(s, 900);
            U16(s, 0);                 // macStyle
            U16(s, 8);                 // lowestRecPPEM
            I16(s, 2);                 // fontDirectionHint
            I16(s, 0);                 // indexToLocFormat: short
            I16(s, 0);                 // glyphDataFormat
            return s.ToArray();
        }

        private static byte[] Hhea()
        {
            var s = new MemoryStream();
            U32(s, 0x00010000);
            I16(s, 800); I16(s, -200); I16(s, 0);
            U16(s, 800); I16(s, 0); I16(s, 0); I16(s, 800);
            I16(s, 1); I16(s, 0); I16(s, 0);
            for (int i = 0; i < 4; i++) I16(s, 0);
            I16(s, 0);                 // metricDataFormat
            U16(s, NumGlyphs);         // numberOfHMetrics
            return s.ToArray();
        }

        private static byte[] Maxp()
        {
            var s = new MemoryStream();
            U32(s, 0x00010000);
            U16(s, NumGlyphs);
            U16(s, 4);      // maxPoints
            U16(s, 1);      // maxContours
            U16(s, 4); U16(s, 1);
            for (int i = 0; i < 9; i++) U16(s, 0);
            return s.ToArray();
        }

        private static byte[] Cmap(
            IReadOnlyDictionary<int, int> unicode, IReadOnlyDictionary<int, int> symbol,
            IReadOnlyDictionary<int, int> mac = null)
        {
            var subtables = new List<(int platform, int encoding, byte[] data)>();
            if (mac     != null) subtables.Add((1, 0, Format4(mac)));
            if (unicode != null) subtables.Add((3, 1, Format4(unicode)));
            if (symbol  != null) subtables.Add((3, 0, Format4(symbol)));
            if (subtables.Count == 0) subtables.Add((3, 1, Format4(new Dictionary<int, int>())));

            var s = new MemoryStream();
            U16(s, 0);
            U16(s, subtables.Count);
            int offset = 4 + subtables.Count * 8;
            foreach (var (platform, encoding, data) in subtables)
            {
                U16(s, platform); U16(s, encoding); U32(s, offset);
                offset += data.Length;
            }
            foreach (var (_, _, data) in subtables) s.Write(data, 0, data.Length);
            return s.ToArray();
        }

        /// <summary>A format-4 subtable with one segment per mapped code.</summary>
        private static byte[] Format4(IReadOnlyDictionary<int, int> map)
        {
            var codes = map.Keys.OrderBy(c => c).ToList();
            int segCount = codes.Count + 1;                 // + the mandatory 0xFFFF terminator

            var s = new MemoryStream();
            U16(s, 4);
            U16(s, 16 + 8 * segCount);                      // length
            U16(s, 0);                                      // language
            U16(s, segCount * 2);
            U16(s, 2); U16(s, 0); U16(s, segCount * 2 - 2); // searchRange / entrySelector / rangeShift

            foreach (int c in codes) U16(s, c);             // endCode
            U16(s, 0xFFFF);
            U16(s, 0);                                      // reservedPad
            foreach (int c in codes) U16(s, c);             // startCode
            U16(s, 0xFFFF);
            foreach (int c in codes) U16(s, (map[c] - c) & 0xFFFF);   // idDelta
            U16(s, 1);
            for (int i = 0; i < segCount; i++) U16(s, 0);   // idRangeOffset
            return s.ToArray();
        }

        private static byte[] Name()
        {
            byte[] value = Encoding.BigEndianUnicode.GetBytes("TestFont");
            var s = new MemoryStream();
            U16(s, 0); U16(s, 2); U16(s, 6 + 12 * 2);
            foreach (int nameId in new[] { 1, 6 })
            {
                U16(s, 3); U16(s, 1); U16(s, 0x0409);
                U16(s, nameId); U16(s, value.Length); U16(s, nameId == 1 ? 0 : value.Length);
            }
            s.Write(value, 0, value.Length);
            s.Write(value, 0, value.Length);
            return s.ToArray();
        }

        /// <summary>
        /// Version 3.0 (no names) when the test asked for none, else version 2.0 with the given
        /// glyph ids carrying custom names in the &gt;= 258 index range.
        /// </summary>
        private static byte[] Post(IReadOnlyDictionary<int, string> names)
        {
            var s = new MemoryStream();
            U32(s, names == null || names.Count == 0 ? 0x00030000u : 0x00020000u);
            U32(s, 0);                  // italicAngle
            I16(s, -100); I16(s, 50);   // underline position / thickness
            U32(s, 0);                  // isFixedPitch
            for (int i = 0; i < 4; i++) U32(s, 0);
            if (names == null || names.Count == 0) return s.ToArray();

            U16(s, NumGlyphs);
            var custom = new List<string>();
            for (int gid = 0; gid < NumGlyphs; gid++)
            {
                if (names.TryGetValue(gid, out string name))
                {
                    U16(s, 258 + custom.Count);
                    custom.Add(name);
                }
                else
                {
                    U16(s, 0);          // .notdef, a standard Macintosh name
                }
            }
            foreach (string name in custom)
            {
                byte[] raw = Encoding.ASCII.GetBytes(name);
                s.WriteByte((byte)raw.Length);
                s.Write(raw, 0, raw.Length);
            }
            return s.ToArray();
        }

        private static byte[] Os2()
        {
            var t = new byte[86];
            t[0] = 0; t[1] = 1;          // version 1
            t[4] = 1; t[5] = 0x90;       // usWeightClass 400
            t[6] = 0; t[7] = 5;          // usWidthClass 5
            t[62] = 0; t[63] = 0x40;     // fsSelection REGULAR
            return t;
        }

        private static byte[] Assemble(Dictionary<string, byte[]> tables)
        {
            var tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
            var output = new MemoryStream();

            U32(output, 0x00010000);
            U16(output, tags.Count);
            U16(output, 16); U16(output, 0); U16(output, tags.Count * 16 - 16);

            int dataOffset = 12 + tags.Count * 16;
            var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string tag in tags)
            {
                offsets[tag] = dataOffset;
                dataOffset += (tables[tag].Length + 3) & ~3;
            }
            foreach (string tag in tags)
            {
                byte[] name = Encoding.ASCII.GetBytes(tag);
                output.Write(name, 0, 4);
                U32(output, 0);
                U32(output, offsets[tag]);
                U32(output, tables[tag].Length);
            }
            foreach (string tag in tags)
            {
                byte[] data = tables[tag];
                output.Write(data, 0, data.Length);
                for (int pad = data.Length; pad % 4 != 0; pad++) output.WriteByte(0);
            }
            return output.ToArray();
        }

        private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void I16(Stream s, int v) => U16(s, v & 0xFFFF);
        private static void U32(Stream s, long v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }
    }
}
