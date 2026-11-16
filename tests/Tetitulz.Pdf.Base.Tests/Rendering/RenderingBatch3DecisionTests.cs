// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decisions the <c>Base/Rendering</c> sweep's batch 3 (<c>.claude/sweep-rendering-3.ps1</c>) found
/// no test for: the two charstring evaluators' number forms and operators, <c>Type1GlyphSource</c>'s
/// advance and code range, and <c>GlyphIdResolver</c>'s CID mapping and Unicode fallback ladder.
/// </summary>
public class RenderingBatch3DecisionTests
{
    private const byte Hstem = 1, Rlineto = 5, Close1 = 9, CallSubr = 10, Return = 11, Hsbw = 13,
        Endchar = 14, Rmoveto = 21, Escape = 12;

    private static void Num(List<byte> cs, int v)
    {
        if (v >= -107 && v <= 107) { cs.Add((byte)(v + 139)); return; }
        if (v >= 108 && v <= 1131) { int d = v - 108; cs.Add((byte)(247 + (d >> 8))); cs.Add((byte)(d & 0xFF)); return; }
        if (v <= -108 && v >= -1131) { int d = -v - 108; cs.Add((byte)(251 + (d >> 8))); cs.Add((byte)(d & 0xFF)); return; }
        throw new System.ArgumentOutOfRangeException(nameof(v));
    }

    private static void Square(List<byte> cs, int side)
    {
        Num(cs, side); Num(cs, 0); cs.Add(Rlineto);
        Num(cs, 0); Num(cs, side); cs.Add(Rlineto);
        Num(cs, -side); Num(cs, 0); cs.Add(Rlineto);
    }

    /// <summary>A Type 1 glyph: <c>0 500 hsbw</c>, the given prefix (which moves the point), a 40-unit square.</summary>
    private static SKRect Type1Bounds(System.Action<List<byte>> moveTo, IReadOnlyList<byte[]> subrs = null)
    {
        var cs = new List<byte>();
        Num(cs, 0); Num(cs, 500); cs.Add(Hsbw);
        moveTo(cs);
        Square(cs, 40);
        cs.Add(Close1); cs.Add(Endchar);
        var font = new Type1Font(new string[256], new Dictionary<string, byte[]> { ["g"] = cs.ToArray() },
            subrs ?? new List<byte[]>());
        using SKPath path = Type1CharstringEvaluator.Evaluate(font, "g").Path;
        return path.Bounds;
    }

    // ---------------------------------------------------------------- Type 1 evaluator

    /// <summary>T102: the 251–254 form is −(b0 − 251)·256 − b1 − 108; −200 is 251, 92.</summary>
    [Fact]
    public void Type1NegativeTwoByteNumberDecodes()
    {
        SKRect b = Type1Bounds(cs => { Num(cs, -200); Num(cs, 0); cs.Add(Rmoveto); });
        Assert.Equal(-200, (double)b.Left, 1);
    }

    /// <summary>T103: Type 1's byte 255 is a 32-bit signed integer (not Type 2's 16.16) — 2000 here.</summary>
    [Fact]
    public void Type1ByteTwoFiftyFiveIsAThirtyTwoBitInteger()
    {
        SKRect b = Type1Bounds(cs => { cs.AddRange(new byte[] { 255, 0, 0, 0x07, 0xD0 }); Num(cs, 0); cs.Add(Rmoveto); });
        Assert.Equal(2000, (double)b.Left, 1);
    }

    /// <summary>T104: <c>a b div</c> pushes a / b — 600 2 div is 300, not 2/600.</summary>
    [Fact]
    public void Type1DivDividesTheLowerOperandByTheTop()
    {
        SKRect moved = Type1Bounds(cs => { Num(cs, 600); Num(cs, 2); cs.Add(Escape); cs.Add(12); Num(cs, 0); cs.Add(Rmoveto); });
        Assert.Equal(300, (double)moved.Left, 1);
    }

    /// <summary>T106: <c>sbw</c> places the current point at (sbx, sby) — a glyph set with sby 20 starts 20 up.</summary>
    [Fact]
    public void Type1SbwStartsTheGlyphAtItsVerticalSidebearing()
    {
        var cs = new List<byte>();
        Num(cs, 30); Num(cs, 20); Num(cs, 500); Num(cs, 0); cs.Add(Escape); cs.Add(7);
        Num(cs, 0); Num(cs, 0); cs.Add(Rmoveto);
        Square(cs, 40);
        cs.Add(Close1); cs.Add(Endchar);
        var font = new Type1Font(new string[256], new Dictionary<string, byte[]> { ["g"] = cs.ToArray() }, new List<byte[]>());

        var outline = Type1CharstringEvaluator.Evaluate(font, "g");
        using SKPath path = outline.Path;

        Assert.Equal(30, (double)path.Bounds.Left, 1);
        Assert.Equal(20, (double)path.Bounds.Top, 1);
        Assert.Equal(500, (double)outline.AdvanceWidth, 1);
    }

    /// <summary>A chain of <paramref name="depth"/> subrs, each calling the next; the last draws the square.</summary>
    private static List<byte[]> SubrChain(int depth)
    {
        var subrs = new List<byte[]>();
        for (int i = 0; i < depth - 1; i++)
        {
            var s = new List<byte>(); Num(s, i + 1); s.Add(CallSubr); s.Add(Return);
            subrs.Add(s.ToArray());
        }
        var last = new List<byte>(); Num(last, 0); Num(last, 0); last.Add(Rmoveto); Square(last, 40); last.Add(Return);
        subrs.Add(last.ToArray());
        return subrs;
    }

    /// <summary>
    /// T107: subr nesting stops at <c>PdfLimits.MaxCharstringCallDepth</c> (10, the glyph itself counting
    /// one). A chain twelve deep never reaches its drawing subr; three deep draws it.
    /// </summary>
    [Fact]
    public void Type1SubrNestingStopsAtTheCallDepthLimit()
    {
        var shallow = new List<byte>();
        Num(shallow, 0); shallow.Add(CallSubr);
        SKRect drawn = Type1BoundsNoSquare(shallow, SubrChain(3));
        Assert.Equal(40, (double)drawn.Width, 1);

        SKRect refused = Type1BoundsNoSquare(shallow, SubrChain(12));
        Assert.True(refused.IsEmpty);
    }

    private static SKRect Type1BoundsNoSquare(List<byte> body, List<byte[]> subrs)
    {
        var cs = new List<byte>();
        Num(cs, 0); Num(cs, 500); cs.Add(Hsbw);
        cs.AddRange(body);
        cs.Add(Close1); cs.Add(Endchar);
        var font = new Type1Font(new string[256], new Dictionary<string, byte[]> { ["g"] = cs.ToArray() }, subrs);
        using SKPath path = Type1CharstringEvaluator.Evaluate(font, "g").Path;
        return path.Bounds;
    }

    // ---------------------------------------------------------------- Type 2 evaluator

    private static Type2CharstringEvaluator.GlyphOutline Type2(byte[] cs) =>
        Type2CharstringEvaluator.Evaluate(new CffFont(new[] { cs }, System.Array.Empty<byte[]>(),
            System.Array.Empty<byte[]>(), new Dictionary<int, int>(), 0f, 0f), 0);

    /// <summary>T201: Type 2's byte 255 is 16.16 fixed — 0x000A8000 is 10.5.</summary>
    [Fact]
    public void Type2ByteTwoFiftyFiveIsSixteenDotSixteenFixed()
    {
        var cs = new List<byte> { 255, 0x00, 0x0A, 0x80, 0x00 };
        Num(cs, 0); cs.Add(Rmoveto);
        Square(cs, 40);
        cs.Add(Endchar);

        var outline = Type2(cs.ToArray());
        using SKPath path = outline.Path;
        Assert.Equal(10.5, (double)path.Bounds.Left, 3);
    }

    /// <summary>T203: each hstem pair's y is relative to the previous stem's top — 10 20 5 30 is (10, 20), (35, 30).</summary>
    [Fact]
    public void Type2HorizontalStemsAccumulateFromThePreviousTop()
    {
        var cs = new List<byte>();
        Num(cs, 10); Num(cs, 20); Num(cs, 5); Num(cs, 30); cs.Add(Hstem);
        Num(cs, 0); Num(cs, 0); cs.Add(Rmoveto);
        Square(cs, 40);
        cs.Add(Endchar);

        var outline = Type2(cs.ToArray());
        outline.Path.Dispose();
        Assert.Equal(new[] { (10f, 20f), (35f, 30f) }, outline.HorizontalStems.ToArray());
    }

    // ---------------------------------------------------------------- Type1GlyphSource

    private static Type1Font SourceFont(float matrixScale)
    {
        var encoding = new string[256];
        encoding[0x41] = "A";
        var cs = new List<byte>();
        Num(cs, 0); Num(cs, 400); cs.Add(Hsbw);
        cs.Add(Endchar);
        return new Type1Font(encoding, new Dictionary<string, byte[]> { ["A"] = cs.ToArray() }, new List<byte[]>(), matrixScale);
    }

    /// <summary>TG01: the advance follows the font's matrix — 400 units at 0.002 is 800 thousandths, at 0.001 400.</summary>
    [Fact]
    public void Type1GlyphSourceScalesTheAdvanceByTheFontMatrix()
    {
        using var wide = new Type1GlyphSource(SourceFont(0.002f));
        using var standard = new Type1GlyphSource(SourceFont(0.001f));
        Assert.Equal(800, (double)wide.GetAdvanceWidth(0x41), 2);
        Assert.Equal(400, (double)standard.GetAdvanceWidth(0x41), 2);
    }

    /// <summary>TG02: a code with no glyph and no <c>.notdef</c> advances half an em; a mapped code its own width.</summary>
    [Fact]
    public void Type1GlyphSourceGivesAnUnmappedCodeHalfAnEm()
    {
        using var source = new Type1GlyphSource(SourceFont(0.001f));
        Assert.Equal(500, (double)source.GetAdvanceWidth(0x42), 2);
        Assert.Equal(400, (double)source.GetAdvanceWidth(0x41), 2);
    }

    /// <summary>TG03: a code past the encoding's end names <c>.notdef</c> rather than reading off the array.</summary>
    [Fact]
    public void Type1GlyphSourceNamesNotdefForACodePastTheEncoding()
    {
        using var source = new Type1GlyphSource(SourceFont(0.001f));
        Assert.Equal(".notdef", source.GetGlyphName(300));
        Assert.Equal("A", source.GetGlyphName(0x41));
    }

    // ---------------------------------------------------------------- GlyphIdResolver

    private static SKTypeface Face(string characters) =>
        SKTypeface.FromData(SKData.CreateCopy(MinimalOpenTypeCff.Build(characters)));

    private static ushort ResolveOne(SKTypeface face, string text, int code) =>
        GlyphIdResolver.Resolve(new FontCache(), null, null, null,
            new[] { new CMapCodec.Code(code, code, 1) }, text, face)[0];

    /// <summary>
    /// GI03: when the decoded character is printable ASCII the face lacks, the raw code is tried as a
    /// code point — "Q" decoded from code 0x41 finds the face's "A".
    /// </summary>
    [Fact]
    public void ResolverFallsBackToTheRawAsciiCode()
    {
        using var face = Face("Af");
        ushort a = GlyphIdResolver.Glyph(face, 'A');
        Assert.NotEqual(0, a);
        Assert.Equal(0, GlyphIdResolver.Glyph(face, 'Q'));

        Assert.Equal(a, ResolveOne(face, "Q", 0x41));
    }

    /// <summary>GI04: a ligature the face lacks falls back to its first component — U+FB01 to "f".</summary>
    [Fact]
    public void ResolverFallsBackToALigaturesFirstComponent()
    {
        using var face = Face("Af");
        ushort f = GlyphIdResolver.Glyph(face, 'f');
        Assert.NotEqual(0, f);

        Assert.Equal(f, ResolveOne(face, "ﬁ", 0x02));
    }

    /// <summary>
    /// GI05: a one-byte code whose decoded character the face lacks is tried at U+F000 | code, the
    /// legacy symbol-font convention. The face holds only U+F041, so code 0x41 finds it there and
    /// nowhere else.
    /// </summary>
    [Fact]
    public void ResolverFallsBackToTheWindowsPrivateUseArea()
    {
        using var face = Face("");
        ushort pua = GlyphIdResolver.Glyph(face, 0xF041);
        Assert.NotEqual(0, pua);
        Assert.Equal(0, GlyphIdResolver.Glyph(face, 0x41));

        Assert.Equal(pua, ResolveOne(face, "é", 0x41));
    }

    /// <summary>GI09: a supplementary-plane character is one code point, one glyph id.</summary>
    [Fact]
    public void FromUnicodeReadsASurrogatePairAsOneCodePoint()
    {
        using var face = Face("A");
        Assert.Equal(new ushort[] { 0, GlyphIdResolver.Glyph(face, 'A') },
            GlyphIdResolver.FromUnicode("\U0001D400A", face));
    }

    /// <summary>
    /// GI01 and GI02: <c>/CIDToGIDMap</c> is indexed by the CID the encoding CMap gives a code, never by
    /// the code. The CMap here maps code 0x0041 to CID 7; the map stream sends CID 7 to GID 9, and the
    /// Identity map sends it to GID 7.
    /// </summary>
    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 7)]
    public void ResolverIndexesTheCidToGidMapByCidNotByCode(bool identity, int expectedGid)
    {
        byte[] pdf = CidMappedFontPdf(identity);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        using var cache = new FontCache();
        var resources = doc.Pages[0].Resources;

        var codes = GlyphIdResolver.DecodeCodes(cache, "F1", resources, doc, new byte[] { 0x00, 0x41 });
        Assert.Single(codes);
        Assert.Equal(0x41, codes[0].Value);
        Assert.Equal(7, codes[0].Cid);

        ushort[] gids = GlyphIdResolver.Resolve(cache, "F1", resources, doc, codes, "A", null);
        Assert.Equal(new[] { (ushort)expectedGid }, gids);
    }

    private static byte[] CidMappedFontPdf(bool identity)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        string Stream(string data) => string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", data.Length, data);

        sb.Append("%PDF-1.4\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj(Stream("BT /F1 24 Tf 72 700 Td <0041> Tj ET"));
        Obj("<< /Type /Font /Subtype /Type0 /BaseFont /Probe /Encoding 6 0 R /DescendantFonts [7 0 R] >>");
        Obj(Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n"
            + "/CMapName /Probe def /CMapType 1 def\n"
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "1 begincidrange <0041> <0041> 7 endcidrange\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end"));
        Obj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Probe"
            + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
            + " /DW 1000 /CIDToGIDMap " + (identity ? "/Identity" : "8 0 R") + " >>");
        // CIDs 0..7 at two bytes each; only CID 7 maps (to GID 9). A lookup by the code (65) is past the end.
        var map = new string('\0', 14) + "\0\u0009";
        Obj(Stream(map));

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.GetEncoding("ISO-8859-1").GetBytes(sb.ToString());
    }
}
