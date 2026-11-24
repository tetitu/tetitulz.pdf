// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Where a <c>seac</c> composite's components land, and what they may not change — in both charstring
/// evaluators. <see cref="Type1SeacStandardEncodingTests"/> pins <i>which</i> glyphs are found; it
/// places every component at the origin, so it cannot see placement.
///
/// <para><b>Type 1, measured against Skia.</b> Implementations disagree on the accent's origin
/// (the spec's "relative to the base's origin", another reader's and Skia's composite-sidebearing term).
/// The expected extents below were read from Skia 2 through an independent tool, rendering a PFA built with
/// exactly these charstrings at 1000 px per em: the accent's first point lands at
/// adx − asb + composite sbx + accent sbx, the base at its own sidebearing, and the advance is the
/// composite's. Letting the accent's own <c>hsbw</c> reset the current point to (asb, 0) would draw every
/// accent on the baseline at the base's origin, and make the last component's width the
/// composite's.</para>
///
/// <para><b>Type 2.</b> An <c>endchar</c> seac's components are charstrings of their own: a width
/// operand they carry is read and discarded, never taken as their first coordinate; their stems are
/// counted afresh for their own <c>hintmask</c>; and the codes are StandardEncoding codes resolved by
/// glyph name, not the encoding a PDF laid over the font.</para>
/// </summary>
public class SeacComponentPlacementTests
{
    // Charstring operators (ATN #5002, TN #5177).
    private const byte Hstem   = 1;
    private const byte Rlineto = 5;
    private const byte Close1  = 9;    // Type 1 closepath
    private const byte Hsbw    = 13;
    private const byte Endchar = 14;
    private const byte Hintmask = 19;
    private const byte Rmoveto = 21;

    /// <summary>Charstring number encoding, shared by both formats for |v| ≤ 1131.</summary>
    private static void Num(List<byte> cs, int v)
    {
        if (v >= -107 && v <= 107) { cs.Add((byte)(v + 139)); return; }
        if (v >= 108 && v <= 1131)
        {
            int d = v - 108;
            cs.Add((byte)(247 + (d >> 8))); cs.Add((byte)(d & 0xFF));
            return;
        }
        int n = -v - 108;
        cs.Add((byte)(251 + (n >> 8))); cs.Add((byte)(n & 0xFF));
    }

    private static void SquareBody(List<byte> cs, int side)
    {
        Num(cs, side);  Num(cs, 0);    cs.Add(Rlineto);
        Num(cs, 0);     Num(cs, side); cs.Add(Rlineto);
        Num(cs, -side); Num(cs, 0);    cs.Add(Rlineto);
    }

    // ---------------------------------------------------------------- Type 1

    private static byte[] T1Square(int sbx, int wx, int side)
    {
        var cs = new List<byte>();
        Num(cs, sbx); Num(cs, wx); cs.Add(Hsbw);
        Num(cs, 0); Num(cs, 0); cs.Add(Rmoveto);
        SquareBody(cs, side);
        cs.Add(Close1); cs.Add(Endchar);
        return cs.ToArray();
    }

    private static byte[] T1Seac(int sbx, int wx, int asb, int adx, int ady, int bchar, int achar)
    {
        var cs = new List<byte>();
        Num(cs, sbx); Num(cs, wx); cs.Add(Hsbw);
        Num(cs, asb); Num(cs, adx); Num(cs, ady); Num(cs, bchar); Num(cs, achar);
        cs.Add(12); cs.Add(6);
        return cs.ToArray();
    }

    /// <summary>"A" (0x41) a 100-unit square, "acute" (0xC2) a 40-unit square, "Aacute" the composite.</summary>
    private static Type1Font T1Font(int compositeSbx, int baseSbx, int accentSbx, int asb, int adx, int ady)
        => new Type1Font(new string[256], new Dictionary<string, byte[]>
        {
            ["A"]      = T1Square(baseSbx, 800, 100),
            ["acute"]  = T1Square(accentSbx, 400, 40),
            ["Aacute"] = T1Seac(compositeSbx, 900, asb, adx, ady, 0x41, 0xC2),
        }, new List<byte[]>());

    /// <summary>
    /// The composite's extent, against Skia's for the same font. The base square spans
    /// [baseSbx, baseSbx + 100] × [0, 100]; the accent square's right edge and top are what move.
    /// Each row varies one input from the first, so each term of the placement is pinned on its own.
    /// </summary>
    [Theory]
    //          csbx bsbx accsbx asb adx ady   Skia: left right top
    [InlineData(50,  50,  30,    30, 300, 500,           50,  390,  540)]
    [InlineData(70,  50,  30,    30, 300, 500,           50,  410,  540)]   // the composite's sbx moves the accent
    [InlineData(50,  50,  30,    10, 300, 500,           50,  410,  540)]   // asb is subtracted
    [InlineData(50,  80,  30,    30, 300, 500,           80,  390,  540)]   // the base's sbx moves only the base
    [InlineData(50,  50,  60,    30, 300, 500,           50,  420,  540)]   // the accent's own sbx is added
    [InlineData(50,  50,  30,    30, 300, 250,           50,  390,  290)]   // ady lifts the accent
    public void Type1SeacPlacesItsComponentsWhereSkiaDoes(
        int compositeSbx, int baseSbx, int accentSbx, int asb, int adx, int ady,
        int left, int right, int top)
    {
        var outline = Type1CharstringEvaluator.Evaluate(T1Font(compositeSbx, baseSbx, accentSbx, asb, adx, ady), "Aacute");
        using SKPath path = outline.Path;
        SKRect b = path.Bounds;

        Assert.Equal(left, (double)b.Left, 1);
        Assert.Equal(0, (double)b.Top, 1);
        Assert.Equal(right, (double)b.Right, 1);
        Assert.Equal(top, (double)b.Bottom, 1);
    }

    /// <summary>
    /// The composite's <c>hsbw</c> width is the glyph's advance — Skia reports 900 — not the
    /// base's 800 nor the accent's 400, which each component's own <c>hsbw</c> would otherwise write last.
    /// </summary>
    [Fact]
    public void Type1SeacKeepsTheCompositesAdvance()
    {
        var font = T1Font(50, 50, 30, 30, 300, 500);

        var composite = Type1CharstringEvaluator.Evaluate(font, "Aacute");
        composite.Path.Dispose();
        var accent = Type1CharstringEvaluator.Evaluate(font, "acute");
        accent.Path.Dispose();

        Assert.Equal(900, (double)composite.AdvanceWidth, 1);
        Assert.Equal(400, (double)accent.AdvanceWidth, 1);   // the component alone keeps its own
    }

    /// <summary>
    /// An accent set with <c>sbw</c> (sbx 30, sby 20) is lifted by its sby as well: Skia draws
    /// the composite's ink to 560 = ady + sby + 40, its right edge at 390 as with <c>hsbw</c>.
    /// </summary>
    [Fact]
    public void Type1SeacAccentSetWithSbwIsLiftedByItsVerticalSidebearing()
    {
        var accent = new List<byte>();
        Num(accent, 30); Num(accent, 20); Num(accent, 400); Num(accent, 0); accent.Add(12); accent.Add(7);
        Num(accent, 0); Num(accent, 0); accent.Add(Rmoveto);
        SquareBody(accent, 40);
        accent.Add(Close1); accent.Add(Endchar);

        var font = new Type1Font(new string[256], new Dictionary<string, byte[]>
        {
            ["A"]      = T1Square(50, 800, 100),
            ["acute"]  = accent.ToArray(),
            ["Aacute"] = T1Seac(50, 900, 30, 300, 500, 0x41, 0xC2),
        }, new List<byte[]>());

        var outline = Type1CharstringEvaluator.Evaluate(font, "Aacute");
        using SKPath path = outline.Path;
        SKRect b = path.Bounds;

        Assert.Equal(50, (double)b.Left, 1);
        Assert.Equal(390, (double)b.Right, 1);
        Assert.Equal(560, (double)b.Bottom, 1);
        Assert.Equal(900, (double)outline.AdvanceWidth, 1);
    }

    /// <summary>
    /// A component whose <c>hsbw</c> carries a single operand (read as a width) does not set the
    /// composite's advance either. The control evaluates the component alone, which takes it.
    /// </summary>
    [Fact]
    public void Type1SeacComponentWithAOneOperandHsbwKeepsTheCompositesAdvance()
    {
        var accent = new List<byte>();
        Num(accent, 400); accent.Add(Hsbw);
        Num(accent, 0); Num(accent, 0); accent.Add(Rmoveto);
        SquareBody(accent, 40);
        accent.Add(Close1); accent.Add(Endchar);

        var font = new Type1Font(new string[256], new Dictionary<string, byte[]>
        {
            ["A"]      = T1Square(50, 800, 100),
            ["acute"]  = accent.ToArray(),
            ["Aacute"] = T1Seac(50, 900, 30, 300, 500, 0x41, 0xC2),
        }, new List<byte[]>());

        var alone = Type1CharstringEvaluator.Evaluate(font, "acute");
        alone.Path.Dispose();
        var composite = Type1CharstringEvaluator.Evaluate(font, "Aacute");
        composite.Path.Dispose();

        Assert.Equal(400, (double)alone.AdvanceWidth, 1);
        Assert.Equal(900, (double)composite.AdvanceWidth, 1);
    }

    /// <summary>
    /// A seac inside a component is not followed. Here "A" is itself a seac of a 700-unit "B"; the
    /// composite draws the accent and nothing of "B". The control evaluates "A" alone, where its seac
    /// is the glyph's own and does compose "B".
    /// </summary>
    [Fact]
    public void Type1SeacDoesNotFollowASeacInsideAComponent()
    {
        var font = new Type1Font(new string[256], new Dictionary<string, byte[]>
        {
            ["B"]      = T1Square(0, 800, 700),
            ["acute"]  = T1Square(0, 400, 40),
            ["A"]      = T1Seac(0, 800, 0, 0, 0, 0x42, 0x20),
            ["Aacute"] = T1Seac(0, 900, 0, 300, 500, 0x41, 0xC2),
        }, new List<byte[]>());

        using SKPath alone = Type1CharstringEvaluator.Evaluate(font, "A").Path;
        Assert.Equal(700, (double)alone.Bounds.Width, 1);

        using SKPath composite = Type1CharstringEvaluator.Evaluate(font, "Aacute").Path;
        SKRect b = composite.Bounds;
        Assert.Equal(300, (double)b.Left, 1);
        Assert.Equal(500, (double)b.Top, 1);
        Assert.Equal(340, (double)b.Right, 1);
        Assert.Equal(540, (double)b.Bottom, 1);
    }

    // ---------------------------------------------------------------- Type 2

    /// <summary>A square at (dx, 0); <paramref name="width"/> written as the leading width operand when given.</summary>
    private static byte[] T2Square(int? width, int dx, int side)
    {
        var cs = new List<byte>();
        if (width.HasValue) Num(cs, width.Value);
        Num(cs, dx); Num(cs, 0); cs.Add(Rmoveto);
        SquareBody(cs, side);
        cs.Add(Endchar);
        return cs.ToArray();
    }

    private static byte[] T2Seac(int? width, int adx, int ady, int bchar, int achar)
    {
        var cs = new List<byte>();
        if (width.HasValue) Num(cs, width.Value);
        Num(cs, adx); Num(cs, ady); Num(cs, bchar); Num(cs, achar);
        cs.Add(Endchar);
        return cs.ToArray();
    }

    private const int DefaultWidth = 777;

    private static CffFont T2Font(byte[][] charstrings, string[] names, Dictionary<int, int> charCodeToGid = null)
    {
        var nameToGid = new Dictionary<string, int>();
        for (int i = 0; i < names.Length; i++) nameToGid[names[i]] = i;
        return new CffFont(charstrings, System.Array.Empty<byte[]>(), System.Array.Empty<byte[]>(),
            charCodeToGid ?? new Dictionary<int, int>(), DefaultWidth, 0f, nameToGid);
    }

    private static Type2CharstringEvaluator.GlyphOutline T2Evaluate(CffFont font, int gid) =>
        Type2CharstringEvaluator.Evaluate(font, gid);

    /// <summary>
    /// The base's charstring carries a width operand (500) before its <c>rmoveto</c>. The composite
    /// has already read its own width, and the old evaluator shared that state with the component —
    /// so it read the base's three operands as (dx, dy) = (500, 10) and drew the base 500 units to
    /// the right and 10 up. The component's width is its own and is discarded; dx is 10.
    /// </summary>
    [Fact]
    public void Type2SeacDoesNotReadAComponentsWidthAsItsFirstCoordinate()
    {
        var font = T2Font(
            new[] { T2Square(500, 10, 100), T2Square(null, 0, 40), T2Seac(900, 300, 500, 0x41, 0xC2) },
            new[] { "A", "acute", "Aacute" });

        var outline = T2Evaluate(font, 2);
        using SKPath path = outline.Path;
        SKRect b = path.Bounds;

        Assert.Equal(10, (double)b.Left, 1);
        Assert.Equal(0, (double)b.Top, 1);
        Assert.Equal(340, (double)b.Right, 1);
        Assert.Equal(540, (double)b.Bottom, 1);
        Assert.Equal(900, (double)outline.AdvanceWidth, 1);
    }

    /// <summary>
    /// The four seac operands are the top four. Here the width was already read (with the
    /// <c>hstem</c>), so a fifth operand below them is extra, not the width — and not adx. Read from
    /// the bottom, the accent would land at adx = 1, ady = 300.
    /// </summary>
    [Fact]
    public void Type2SeacReadsTheTopFourOperandsWhenAWidthWasAlreadyRead()
    {
        var composite = new List<byte>();
        Num(composite, 900); Num(composite, 0); Num(composite, 8); composite.Add(Hstem);
        Num(composite, 1); Num(composite, 300); Num(composite, 500); Num(composite, 0x41); Num(composite, 0xC2);
        composite.Add(Endchar);

        var font = T2Font(
            new[] { T2Square(null, 0, 100), T2Square(null, 0, 40), composite.ToArray() },
            new[] { "A", "acute", "Aacute" });

        var outline = T2Evaluate(font, 2);
        using SKPath path = outline.Path;
        SKRect b = path.Bounds;

        Assert.Equal(340, (double)b.Right, 1);
        Assert.Equal(540, (double)b.Bottom, 1);
        Assert.Equal(900, (double)outline.AdvanceWidth, 1);
    }

    /// <summary>
    /// A composite with no width of its own advances by the default width; the base's width operand
    /// (500) does not become the composite's. The control is the base alone, which does use it.
    /// </summary>
    [Fact]
    public void Type2SeacWithoutAWidthKeepsTheDefaultNotTheBasesWidth()
    {
        var font = T2Font(
            new[] { T2Square(500, 10, 100), T2Square(null, 0, 40), T2Seac(null, 300, 500, 0x41, 0xC2) },
            new[] { "A", "acute", "Aacute" });

        var baseAlone = T2Evaluate(font, 0);
        baseAlone.Path.Dispose();
        var composite = T2Evaluate(font, 2);
        using SKPath path = composite.Path;

        Assert.Equal(500, (double)baseAlone.AdvanceWidth, 1);
        Assert.Equal(DefaultWidth, (double)composite.AdvanceWidth, 1);
        Assert.Equal(10, (double)path.Bounds.Left, 1);   // and the base is placed by its dx
    }

    /// <summary>
    /// The codes are StandardEncoding codes resolved through glyph names. The font's code map here
    /// sends 0x41 to a 600-unit decoy, as a PDF <c>/Differences</c> laid over the font would; the
    /// base found must still be "A". The control shows the decoy is reachable by that code.
    /// </summary>
    [Fact]
    public void Type2SeacResolvesStandardEncodingCodesByNameNotThroughTheFontsCodeMap()
    {
        var font = T2Font(
            new[] { T2Square(null, 0, 100), T2Square(null, 0, 40), T2Seac(900, 300, 500, 0x41, 0xC2), T2Square(null, 0, 600) },
            new[] { "A", "acute", "Aacute", "decoy" },
            new Dictionary<int, int> { [0x41] = 3, [0xC2] = 3 });

        Assert.Equal(3, font.CharCodeToGid[0x41]);

        using SKPath path = T2Evaluate(font, 2).Path;
        SKRect b = path.Bounds;
        Assert.Equal(0, (double)b.Left, 1);
        Assert.Equal(340, (double)b.Right, 1);
        Assert.Equal(540, (double)b.Bottom, 1);
    }

    /// <summary>
    /// The accent counts its own stems for its <c>hintmask</c>. The composite declares nine horizontal
    /// stems before its seac, the accent one, so the accent's mask is one byte. Carried over, the count
    /// of ten asks for two, swallowing the accent's first operand and misplacing it. (The base's own
    /// stems cannot leak: each component's count is restored after it.) The accent's stem also moves
    /// with it: reported at ady + 5.
    /// </summary>
    [Fact]
    public void Type2SeacCountsEachComponentsStemsAfreshAndMovesTheAccentsStems()
    {
        var composite = new List<byte>();
        Num(composite, 900);
        for (int i = 0; i < 9; i++) { Num(composite, i == 0 ? 0 : 2); Num(composite, 8); }
        composite.Add(Hstem);
        Num(composite, 300); Num(composite, 500); Num(composite, 0x41); Num(composite, 0xC2);
        composite.Add(Endchar);

        var baseCs = new List<byte>();
        Num(baseCs, 0); Num(baseCs, 0); baseCs.Add(Rmoveto);
        SquareBody(baseCs, 100);
        baseCs.Add(Endchar);

        var accentCs = new List<byte>();
        Num(accentCs, 5); Num(accentCs, 20); accentCs.Add(Hstem);
        accentCs.Add(Hintmask); accentCs.Add(0x80);
        Num(accentCs, 7); Num(accentCs, 0); accentCs.Add(Rmoveto);
        SquareBody(accentCs, 40);
        accentCs.Add(Endchar);

        var font = T2Font(
            new[] { baseCs.ToArray(), accentCs.ToArray(), composite.ToArray() },
            new[] { "A", "acute", "Aacute" });

        var outline = T2Evaluate(font, 2);
        using SKPath path = outline.Path;
        SKRect b = path.Bounds;

        Assert.Equal(0, (double)b.Left, 1);
        Assert.Equal(347, (double)b.Right, 1);    // accent at 300 + 7, 40 wide
        Assert.Equal(540, (double)b.Bottom, 1);
        Assert.Contains(outline.HorizontalStems, s => s.y0 == 505f && s.h == 20f);
        Assert.Contains(outline.HorizontalStems, s => s.y0 == 0f && s.h == 8f);   // the composite's first
    }

    /// <summary>
    /// Not a seac case, found beside them: a <c>hintmask</c> whose stack holds implicit vstem
    /// operands is the glyph's first stack-clearing operator, so an odd count carries the width
    /// (TN #5177 §4.1; Skia reads it there too). Here 500 then one stem pair: the advance is
    /// 500 (nominal 0), not the default, and the mask is still one byte — the square lands at 10.
    /// </summary>
    [Fact]
    public void Type2HintmaskWithImplicitStemsReadsAnOddOperandAsTheWidth()
    {
        var cs = new List<byte>();
        Num(cs, 500); Num(cs, 10); Num(cs, 20); cs.Add(Hintmask); cs.Add(0x80);
        Num(cs, 10); Num(cs, 0); cs.Add(Rmoveto);
        SquareBody(cs, 40);
        cs.Add(Endchar);

        var outline = T2Evaluate(T2Font(new[] { cs.ToArray() }, new[] { "g" }), 0);
        using SKPath path = outline.Path;

        Assert.Equal(500, (double)outline.AdvanceWidth, 1);
        Assert.Equal(10, (double)path.Bounds.Left, 1);
        Assert.Equal(50, (double)path.Bounds.Right, 1);
    }

    /// <summary>
    /// A seac inside a component is not followed, as in Type 1: "A" is itself a seac of a 700-unit
    /// "B". Alone it composes "B"; inside "Aacute" only the accent is drawn.
    /// </summary>
    [Fact]
    public void Type2SeacDoesNotFollowASeacInsideAComponent()
    {
        var font = T2Font(
            new[] { T2Seac(null, 0, 0, 0x42, 0x20), T2Square(null, 0, 40), T2Seac(900, 300, 500, 0x41, 0xC2), T2Square(null, 0, 700) },
            new[] { "A", "acute", "Aacute", "B" });

        using SKPath alone = T2Evaluate(font, 0).Path;
        Assert.Equal(700, (double)alone.Bounds.Width, 1);

        using SKPath composite = T2Evaluate(font, 2).Path;
        SKRect b = composite.Bounds;
        Assert.Equal(300, (double)b.Left, 1);
        Assert.Equal(500, (double)b.Top, 1);
        Assert.Equal(340, (double)b.Right, 1);
    }
}
