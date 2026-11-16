// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Type 1 → CFF conversion: <see cref="Type1ToCffConverter"/> and the pieces under it.
/// </summary>
/// <remarks>
/// <para><b>The result is read back with this library's own <c>CffParser</c>, not inspected as
/// bytes.</b> The claim is that the output is a font, and the strongest available check is that the
/// reader which every embedded CFF goes through can open it and find the glyphs — a byte assertion
/// would pass on a file no reader accepts.</para>
///
/// <para><b>Which makes the outline comparison the load-bearing part.</b> "It parses" is satisfied
/// by a font whose every glyph is empty, so each test re-runs the parsed charstring and compares
/// the points against what the Type 1 charstring drew.</para>
/// </remarks>
public class Type1ToCffConversionTests
{
    // ================================================================ the interpreter

    /// <summary>A charstring's outline is what its operators draw, in absolute coordinates.</summary>
    /// <remarks>
    /// <c>hsbw</c> sets the current point to the left sidebearing, so every later move is relative
    /// to it — a reader that started at the origin would place the whole glyph 50 units left here.
    /// </remarks>
    [Fact]
    public void TheInterpreterPlacesTheOutlineRelativeToTheSidebearing()
    {
        byte[] cs = Type1(
            Num(50), Num(600), Op(13),          // hsbw: sbx 50, width 600
            Num(0), Num(100), Op(21),           // rmoveto to (50, 100)
            Num(200), Num(0), Op(5),            // rlineto to (250, 100)
            Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, null);

        Assert.NotNull(glyph);
        Assert.True(glyph!.HasWidth);
        Assert.Equal(600, glyph.Width);
        Assert.Equal(2, glyph.Segments.Count);
        Assert.Equal(50, glyph.Segments[0].X1);
        Assert.Equal(100, glyph.Segments[0].Y1);
        Assert.Equal(250, glyph.Segments[1].X1);
    }

    /// <summary>A flex becomes the two curves it always was.</summary>
    /// <remarks>
    /// <para><b>Flex is a calling convention, not an operator</b>: <c>othersubr 1</c> opens it,
    /// seven <c>rmoveto</c>/<c>othersubr 2</c> pairs collect points, <c>othersubr 0</c> closes it.
    /// The first collected point is a reference the rasteriser uses to decide whether to flatten
    /// the curve and is <i>not</i> on the outline — a reader that kept it would draw a spur.</para>
    ///
    /// <para>Interpreting rather than translating is what makes this fall out: the seven moves
    /// become two curves and nothing downstream needs to know a flex was involved.</para>
    /// </remarks>
    [Fact]
    public void AFlexBecomesTwoCurvesAndDropsItsReferencePoint()
    {
        var ops = new List<byte[]>
        {
            Num(0), Num(1000), Op(13),                       // hsbw
            Num(0), Num(0), Op(21),                          // rmoveto to the origin
            Num(0), Num(1), Op(12), Op(16),                  // othersubr 1: flex begins
        };

        // Seven points: the reference, then two curves' worth. Each is an rmoveto followed by
        // othersubr 2.
        int[,] deltas = { { 10, 10 }, { 10, 20 }, { 20, 20 }, { 20, 0 },
                          { 20, -20 }, { 10, -20 }, { 10, -10 } };
        for (int i = 0; i < 7; i++)
        {
            ops.Add(Num(deltas[i, 0]));
            ops.Add(Num(deltas[i, 1]));
            ops.Add(Op(21));
            ops.Add(Num(0)); ops.Add(Num(2)); ops.Add(Op(12)); ops.Add(Op(16));
        }

        // othersubr 0 takes three arguments and leaves two for the pops that follow.
        ops.Add(Num(50)); ops.Add(Num(100)); ops.Add(Num(0));
        ops.Add(Num(3)); ops.Add(Num(0)); ops.Add(Op(12)); ops.Add(Op(16));
        ops.Add(Op(12)); ops.Add(Op(17));
        ops.Add(Op(12)); ops.Add(Op(17));
        ops.Add(Op(14));

        var glyph = Type1CharstringInterpreter.Run(Type1(ops.ToArray()), null);

        Assert.NotNull(glyph);

        var curves = glyph!.Segments
            .Where(s => s.Kind == Type1CharstringInterpreter.SegmentKind.CurveTo).ToList();
        Assert.Equal(2, curves.Count);

        // The reference point (10, 10) is not an endpoint of either curve: the first curve's
        // control points are the SECOND and THIRD collected points.
        Assert.Equal(20, curves[0].X1);
        Assert.Equal(30, curves[0].Y1);

        // No extra moveto was emitted while flexing -- one subpath, not eight.
        Assert.Single(glyph.Segments
            .Where(s => s.Kind == Type1CharstringInterpreter.SegmentKind.MoveTo));
    }

    /// <summary>
    /// The flex's end point comes back through the two pops after <c>othersubr 0</c>, and
    /// <c>setcurrentpoint</c> takes it. The flex above ends on the baseline, where a pop answering 0
    /// for y is indistinguishable; this one ends at (100, 50), and the line after it starts there.
    /// </summary>
    [Fact]
    public void AFlexHandsItsEndPointToTheNextOperator()
    {
        var ops = new List<byte[]>
        {
            Num(0), Num(1000), Op(13),
            Num(0), Num(50), Op(21),                         // start at (0, 50)
            Num(0), Num(1), Op(12), Op(16),
        };
        int[,] deltas = { { 10, 10 }, { 10, 20 }, { 20, 20 }, { 20, 0 },
                          { 20, -20 }, { 10, -20 }, { 10, -10 } };
        for (int i = 0; i < 7; i++)
        {
            ops.Add(Num(deltas[i, 0])); ops.Add(Num(deltas[i, 1])); ops.Add(Op(21));
            ops.Add(Num(0)); ops.Add(Num(2)); ops.Add(Op(12)); ops.Add(Op(16));
        }
        ops.Add(Num(50)); ops.Add(Num(100)); ops.Add(Num(50));
        ops.Add(Num(3)); ops.Add(Num(0)); ops.Add(Op(12)); ops.Add(Op(16));   // flex ends at (100, 50)
        ops.Add(Op(12)); ops.Add(Op(17)); ops.Add(Op(12)); ops.Add(Op(17));   // pop pop
        ops.Add(Op(12)); ops.Add(Op(33));                                     // setcurrentpoint
        ops.Add(Num(10)); ops.Add(Num(0)); ops.Add(Op(5));                    // rlineto
        ops.Add(Op(14));

        var glyph = Type1CharstringInterpreter.Run(Type1(ops.ToArray()), null);

        Assert.NotNull(glyph);
        var line = glyph!.Segments.Last();
        Assert.Equal(Type1CharstringInterpreter.SegmentKind.LineTo, line.Kind);
        Assert.Equal((110d, 50d), (line.X1, line.Y1));
    }

    /// <summary>
    /// An othersubr the interpreter does not know hands its arguments back for the pops that
    /// follow, as a font with private othersubrs expects, and <c>setcurrentpoint</c> moves to them.
    /// Here they are (300, 400): the line after starts there, not at (10, 10) and not at the origin.
    /// </summary>
    [Fact]
    public void AnUnknownOtherSubrHandsItsArgumentsBack()
    {
        byte[] cs = Type1(
            Num(0), Num(500), Op(13),
            Num(10), Num(10), Op(21),
            Num(300), Num(400), Num(2), Num(99), Op(12), Op(16),              // othersubr 99, two args
            Op(12), Op(17), Op(12), Op(17), Op(12), Op(33),                   // pop pop setcurrentpoint
            Num(1), Num(1), Op(5), Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, null);

        Assert.NotNull(glyph);
        var line = glyph!.Segments.Last();
        Assert.Equal((301d, 401d), (line.X1, line.Y1));
    }

    /// <summary>
    /// Hint replacement (<c>subr# 1 3 callothersubr pop callsubr</c>) leaves 3 for the pop, as
    /// Skia's decoder does, so subroutine 3 runs. Subroutine 0 draws elsewhere, and is what a
    /// pop answering 0 would call.
    /// </summary>
    [Fact]
    public void HintReplacementCallsSubroutineThree()
    {
        var subrs = new List<byte[]>
        {
            Type1(Num(0), Num(77), Op(5), Op(11)),
            Type1(Op(11)),
            Type1(Op(11)),
            Type1(Num(50), Num(0), Op(5), Op(11)),
        };
        byte[] cs = Type1(
            Num(0), Num(500), Op(13), Num(0), Num(0), Op(21),
            Num(5), Num(1), Num(3), Op(12), Op(16),                           // othersubr 3
            Op(12), Op(17), Op(10),                                           // pop callsubr
            Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, subrs);

        Assert.NotNull(glyph);
        var line = glyph!.Segments.Last();
        Assert.Equal((50d, 0d), (line.X1, line.Y1));
    }

    /// <summary>
    /// Malformed: <c>div</c> by zero answers 0, not infinity, so the outline stays finite.
    /// </summary>
    [Fact]
    public void Malformed_DivisionByZeroAnswersZero()
    {
        byte[] cs = Type1(
            Num(0), Num(500), Op(13), Num(0), Num(0), Op(21),
            Num(100), Num(0), Op(12), Op(12),                                 // 100 0 div
            Num(0), Op(5), Op(14));                                           // rlineto (quotient, 0)

        var glyph = Type1CharstringInterpreter.Run(cs, null);

        Assert.NotNull(glyph);
        Assert.Equal(0d, glyph!.Segments.Last().X1);
    }

    /// <summary>A subroutine call is executed, and a runaway one is bounded.</summary>
    /// <remarks>
    /// Named <c>Unbounded</c> so the guard reads the non-throw as the behaviour under test: a
    /// self-calling subroutine is what a hostile font ships, and the depth cap is what makes this
    /// return at all.
    /// </remarks>
    [Fact]
    public void AnUnboundedSubrRecursionTerminates()
    {
        // Subr 0 calls itself for ever.
        var subrs = new List<byte[]> { Type1(Num(0), Op(10)) };
        byte[] cs = Type1(Num(0), Num(500), Op(13), Num(0), Op(10), Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, subrs);

        // Refused rather than hung: Run answers null when the charstring could not be read.
        Assert.Null(glyph);
    }

    /// <summary>A subroutine that draws is executed rather than skipped.</summary>
    /// <remarks>
    /// The acceptance control for the refusal above — without it, "recursion terminates" would be
    /// satisfied by an interpreter that never called a subroutine at all.
    /// </remarks>
    [Fact]
    public void ASubrIsExecuted()
    {
        var subrs = new List<byte[]> { Type1(Num(100), Num(0), Op(5), Op(11)) };
        byte[] cs = Type1(Num(0), Num(500), Op(13), Num(0), Num(0), Op(21),
                          Num(0), Op(10), Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, subrs);

        Assert.NotNull(glyph);
        Assert.Contains(glyph!.Segments,
            s => s.Kind == Type1CharstringInterpreter.SegmentKind.LineTo && s.X1 == 100);
    }

    /// <summary>An <c>rrcurveto</c>'s control points are read relative to each other.</summary>
    /// <remarks>
    /// <b>Every other curve in this file comes from the flex path</b>, which builds its points
    /// absolutely and never runs the ordinary <c>rrcurveto</c> handler — so reading the second
    /// control point as absolute rather than relative to the first survived them all. Each delta
    /// here is different, so no two readings agree.
    /// </remarks>
    [Fact]
    public void ACurvesControlPointsAreRelativeToEachOther()
    {
        byte[] cs = Type1(
            Num(0), Num(700), Op(13),
            Num(100), Num(100), Op(21),          // rmoveto to (100, 100)
            Num(10), Num(20), Num(30), Num(40), Num(50), Num(60), Op(8),
            Op(14));

        var glyph = Type1CharstringInterpreter.Run(cs, null);

        Assert.NotNull(glyph);
        var curve = glyph!.Segments.Single(
            s => s.Kind == Type1CharstringInterpreter.SegmentKind.CurveTo);

        // Each delta is from the point before it: (110,120) then (140,160) then (190,220).
        Assert.Equal(110, curve.X1);
        Assert.Equal(120, curve.Y1);
        Assert.Equal(140, curve.X2);
        Assert.Equal(160, curve.Y2);
        Assert.Equal(190, curve.X3);
        Assert.Equal(220, curve.Y3);
    }

    // ================================================================ the conversion

    /// <summary>The converted font parses, and its glyphs are the ones that went in.</summary>
    [Fact]
    public void TheConvertedFontParsesAndKeepsItsGlyphs()
    {
        Type1Font font = SimpleFont();

        byte[] cff = Type1ToCffConverter.Convert(font, "TestFont");

        Assert.NotNull(Type1ToCffConverter.LastError == null ? cff : null);
        Assert.NotNull(cff);

        CffFont parsed = CffParser.Parse(cff);
        Assert.NotNull(parsed);

        // .notdef plus the two the fixture defines.
        Assert.Equal(3, parsed!.Charstrings.Count);
        Assert.True(parsed.GlyphNameToGid.ContainsKey("A"));
        Assert.True(parsed.GlyphNameToGid.ContainsKey("B"));
    }

    /// <summary>The outline survives the round trip, point for point.</summary>
    /// <remarks>
    /// <b>This is the assertion the others exist to support.</b> A font that parses and names its
    /// glyphs can still hold nothing; re-running the emitted Type 2 charstring and comparing the
    /// points against what the Type 1 charstring drew is what says the conversion carried the
    /// shape.
    /// </remarks>
    [Fact]
    public void TheOutlineSurvivesTheRoundTrip()
    {
        Type1Font font = SimpleFont();
        byte[] cff = Type1ToCffConverter.Convert(font, "TestFont");
        CffFont parsed = CffParser.Parse(cff);

        Assert.NotNull(parsed);
        int gid = parsed!.GlyphNameToGid["A"];

        List<(double X, double Y)> after = Type2Points(parsed.Charstrings[gid]);

        // The fixture's A: a triangle at (100,0) → (400,0) → (250,700).
        Assert.Equal(new List<(double, double)> { (100, 0), (400, 0), (250, 700) }, after);
    }

    /// <summary>The advance width survives.</summary>
    /// <remarks>
    /// <b>The width is a charstring PREFIX in Type 2, not an operator</b> — an odd leading argument
    /// on the first move. A converter that emitted it as a value the parser reads as a coordinate
    /// would shift the glyph and lose the width, which the outline test above would also catch;
    /// this says which of the two went wrong.
    /// </remarks>
    [Fact]
    public void TheAdvanceWidthSurvivesTheRoundTrip()
    {
        Type1Font font = SimpleFont();
        byte[] cff = Type1ToCffConverter.Convert(font, "TestFont");
        CffFont parsed = CffParser.Parse(cff);

        Assert.NotNull(parsed);
        int gid = parsed!.GlyphNameToGid["A"];

        // Read from the charstring, not from CffFont.GlyphWidths -- the parser leaves that array
        // NaN and a later pass fills it, so asserting it would be asserting nothing.
        Assert.Equal(600, WidthPrefix(parsed.Charstrings[gid]));
    }

    /// <summary>
    /// The width a Type 2 charstring declares, or <c>null</c> when it declares none.
    /// </summary>
    /// <remarks>
    /// <b>The width is a prefix, not an operator</b> (CFF §4.3): the first stem, move or
    /// <c>endchar</c> takes one more argument than it uses, and that odd leading one is the
    /// advance relative to <c>nominalWidthX</c>. <c>rmoveto</c> uses two, so three means the first
    /// is a width.
    /// </remarks>
    private static int? WidthPrefix(byte[] cs)
    {
        var stack = new List<double>();
        int i = 0;

        while (i < cs.Length)
        {
            int v = cs[i++];

            if (v == 28) { stack.Add((short)((cs[i] << 8) | cs[i + 1])); i += 2; continue; }
            if (v >= 32)
            {
                if (v <= 246) stack.Add(v - 139);
                else if (v <= 250) stack.Add(((v - 247) * 256) + cs[i++] + 108);
                else if (v <= 254) stack.Add(-((v - 251) * 256) - cs[i++] - 108);
                else { i += 4; stack.Add(0); }
                continue;
            }

            // The first operator that can carry a width decides it.
            if (v == 21) return stack.Count > 2 ? (int?)stack[0] : null;    // rmoveto takes 2
            if (v == 22 || v == 4) return stack.Count > 1 ? (int?)stack[0] : null;   // h/vmoveto take 1
            if (v == 14) return stack.Count == 1 || stack.Count == 5 ? (int?)stack[0] : null;
            if (v == 1 || v == 3 || v == 18 || v == 23)
                return (stack.Count & 1) != 0 ? (int?)stack[0] : null;      // stems take pairs

            stack.Clear();
        }

        return null;
    }

    /// <summary>A second subpath's move is emitted relative to where the first one ended.</summary>
    /// <remarks>
    /// <b>A one-subpath glyph cannot see this.</b> Its only move starts from the origin, where the
    /// absolute and relative numbers are identical — so emitting the absolute coordinate survived
    /// every other fixture here. The second subpath below starts a long way from where the first
    /// finished, and the two readings differ by exactly that distance.
    /// </remarks>
    [Fact]
    public void ASecondSubpathIsEmittedRelativeToTheFirst()
    {
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Type1(Num(0), Num(0), Op(13), Op(14)),
            ["A"] = Type1(
                Num(0), Num(600), Op(13),
                Num(0), Num(0), Op(21),              // subpath 1 at (0, 0)
                Num(100), Num(0), Op(5),             // to (100, 0)
                Op(9),
                Num(200), Num(300), Op(21),          // subpath 2 at (300, 300)
                Num(50), Num(0), Op(5),              // to (350, 300)
                Op(9), Op(14)),
        };

        var font = new Type1Font(new string[256], charStrings, new List<byte[]>());
        CffFont parsed = CffParser.Parse(Type1ToCffConverter.Convert(font, "TwoSubpaths"));

        Assert.NotNull(parsed);
        List<(double X, double Y)> points = Type2Points(parsed!.Charstrings[parsed.GlyphNameToGid["A"]]);

        Assert.Equal(
            new List<(double, double)> { (0, 0), (100, 0), (300, 300), (350, 300) },
            points);
    }

    /// <summary>A glyph name outside the standard strings survives the round trip.</summary>
    /// <remarks>
    /// <b>"A" and "B" are standard CFF strings</b> (SIDs 34 and 35), so every other fixture here
    /// takes the predefined path and the String INDEX stays empty — which made writing every custom
    /// name as SID 0 a surviving mutation. A real font's ligatures and small caps are exactly these
    /// names.
    /// </remarks>
    [Fact]
    public void ANonStandardGlyphNameSurvivesTheRoundTrip()
    {
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Type1(Num(0), Num(0), Op(13), Op(14)),
            ["uni4E2D"] = Type1(Num(0), Num(1000), Op(13), Num(0), Num(0), Op(21),
                                Num(100), Num(0), Op(5), Op(14)),
            ["f_f_i"] = Type1(Num(0), Num(900), Op(13), Num(0), Num(0), Op(21),
                              Num(200), Num(0), Op(5), Op(14)),
        };

        var font = new Type1Font(new string[256], charStrings, new List<byte[]>());
        CffFont parsed = CffParser.Parse(Type1ToCffConverter.Convert(font, "Custom"));

        Assert.NotNull(parsed);
        Assert.True(parsed!.GlyphNameToGid.ContainsKey("uni4E2D"));
        Assert.True(parsed.GlyphNameToGid.ContainsKey("f_f_i"));

        // And they are DIFFERENT glyphs: writing both as SID 0 would collapse them onto one name.
        Assert.NotEqual(parsed.GlyphNameToGid["uni4E2D"], parsed.GlyphNameToGid["f_f_i"]);
    }

    /// <summary>A font with no charstrings is refused with a reason.</summary>
    [Fact]
    public void AFontWithNoCharstringsIsRefusedByName()
    {
        var empty = new Type1Font(new string[256], new Dictionary<string, byte[]>(), new List<byte[]>());

        Assert.Null(Type1ToCffConverter.Convert(empty, "Empty"));
        Assert.Contains("no charstrings", Type1ToCffConverter.LastError, StringComparison.Ordinal);
    }

    /// <summary>A font without its own <c>.notdef</c> still gets one.</summary>
    /// <remarks>
    /// <b>Glyph 0 is where a reader lands for anything it cannot map</b>, so a CFF without it is a
    /// font that cannot answer. The fixture defines only A and B.
    /// </remarks>
    [Fact]
    public void AFontWithoutNotdefStillGetsGlyphZero()
    {
        byte[] cff = Type1ToCffConverter.Convert(SimpleFont(), "TestFont");
        CffFont parsed = CffParser.Parse(cff);

        Assert.NotNull(parsed);
        Assert.True(parsed!.GlyphNameToGid.TryGetValue(".notdef", out int gid));
        Assert.Equal(0, gid);
    }

    /// <summary>A non-default font matrix travels with the font.</summary>
    /// <remarks>
    /// A 2048-unit design space needs a 1/2048 matrix; CFF's default is 1/1000, so dropping the
    /// entry renders every glyph at roughly twice its intended size.
    /// </remarks>
    [Fact]
    public void ANonDefaultFontMatrixIsCarriedAcross()
    {
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Type1(Num(0), Num(0), Op(13), Op(14)),
            ["A"] = Type1(Num(0), Num(500), Op(13), Num(0), Num(0), Op(21), Op(14)),
        };

        var font = new Type1Font(new string[256], charStrings, new List<byte[]>(),
                                 fontMatrixScale: 1f / 2048f);

        CffFont parsed = CffParser.Parse(Type1ToCffConverter.Convert(font, "BigEm"));

        Assert.NotNull(parsed);
        Assert.Equal(1f / 2048f, parsed!.FontMatrixScale, 6);
    }

    /// <summary>
    /// A seac composite's accent offset is converted, not copied. Type 1 places the accent's origin
    /// at adx − asb + the composite's sbx; Type 2's endchar has no sidebearings and places it at
    /// adx. Here sbx is 10, asb 100 and adx 50, so the Type 2 adx is −40. The raw 50 put the accent
    /// 90 units right.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeacAccentOffsetIsConvertedToType2Terms(bool sbw)
    {
        // The composite's sidebearing from hsbw (sbx wx) or from sbw (sbx sby wx wy).
        byte[] metrics = sbw
            ? Type1(Num(10), Num(0), Num(600), Num(0), Op(12), Op(7))
            : Type1(Num(10), Num(600), Op(13));
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Type1(Num(0), Num(0), Op(13), Op(14)),
            ["A"] = Type1(Num(10), Num(600), Op(13), Op(14)),
            ["acute"] = Type1(Num(100), Num(300), Op(13), Op(14)),
            ["Aacute"] = Type1(
                metrics,                                                        // sbx 10
                Num(100), Num(50), Num(200), Num(65), Num(194), Op(12), Op(6)), // seac: asb 100, adx 50, ady 200
        };
        var font = new Type1Font(new string[256], charStrings, new List<byte[]>());

        CffFont parsed = CffParser.Parse(Type1ToCffConverter.Convert(font, "TestFont"));

        Assert.NotNull(parsed);
        List<double> args = EndcharArguments(parsed!.Charstrings[parsed.GlyphNameToGid["Aacute"]]);
        Assert.Equal(new double[] { -40, 200, 65, 194 }, args.Skip(args.Count - 4).ToArray());
    }

    // ================================================================ helpers

    /// <summary>The operands before a Type 2 charstring's <c>endchar</c>.</summary>
    private static List<double> EndcharArguments(byte[] cs)
    {
        var stack = new List<double>();
        int i = 0;
        while (i < cs.Length)
        {
            int v = cs[i++];
            if (v == 28) { stack.Add((short)((cs[i] << 8) | cs[i + 1])); i += 2; continue; }
            if (v >= 32 && v <= 246) { stack.Add(v - 139); continue; }
            if (v >= 247 && v <= 250) { stack.Add(((v - 247) * 256) + cs[i++] + 108); continue; }
            if (v >= 251 && v <= 254) { stack.Add(-((v - 251) * 256) - cs[i++] - 108); continue; }
            if (v == 14) return stack;
            stack.Clear();
        }
        return stack;
    }

    /// <summary>A font with <c>.notdef</c>, a triangular A and a rectangular B.</summary>
    private static Type1Font SimpleFont()
    {
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Type1(Num(0), Num(0), Op(13), Op(14)),

            // hsbw 0 600, then a closed triangle.
            ["A"] = Type1(
                Num(0), Num(600), Op(13),
                Num(100), Num(0), Op(21),
                Num(300), Num(0), Op(5),
                Num(-150), Num(700), Op(5),
                Op(9), Op(14)),

            ["B"] = Type1(
                Num(0), Num(550), Op(13),
                Num(50), Num(0), Op(21),
                Num(400), Num(0), Op(5),
                Num(0), Num(700), Op(5),
                Num(-400), Num(0), Op(5),
                Op(9), Op(14)),
        };

        return new Type1Font(new string[256], charStrings, new List<byte[]>());
    }

    /// <summary>
    /// The on-curve points a Type 2 charstring draws, using only the operators this writer emits.
    /// </summary>
    /// <remarks>
    /// A deliberately small reader: it understands <c>rmoveto</c>, <c>rlineto</c>,
    /// <c>rrcurveto</c> and <c>endchar</c>, which is exactly what the converter produces. Reading
    /// with the full interpreter would risk agreeing with the writer about something they both got
    /// wrong; this reads the bytes on the specification's terms.
    /// </remarks>
    private static List<(double X, double Y)> Type2Points(byte[] cs)
    {
        var points = new List<(double, double)>();
        var stack = new List<double>();
        double x = 0, y = 0;
        int i = 0;

        while (i < cs.Length)
        {
            int v = cs[i++];

            if (v == 28)
            {
                stack.Add((short)((cs[i] << 8) | cs[i + 1]));
                i += 2;
                continue;
            }
            if (v >= 32)
            {
                if (v <= 246) stack.Add(v - 139);
                else if (v <= 250) stack.Add(((v - 247) * 256) + cs[i++] + 108);
                else if (v <= 254) stack.Add(-((v - 251) * 256) - cs[i++] - 108);
                else { i += 4; stack.Add(0); }
                continue;
            }

            switch (v)
            {
                case 21:                                   // rmoveto (+ optional width prefix)
                {
                    int at = stack.Count - 2;
                    x += stack[at]; y += stack[at + 1];
                    points.Add((x, y));
                    stack.Clear();
                    break;
                }
                case 5:                                    // rlineto
                    for (int k = 0; k + 1 < stack.Count; k += 2)
                    {
                        x += stack[k]; y += stack[k + 1];
                        points.Add((x, y));
                    }
                    stack.Clear();
                    break;

                case 8:                                    // rrcurveto
                    for (int k = 0; k + 5 < stack.Count; k += 6)
                    {
                        x += stack[k] + stack[k + 2] + stack[k + 4];
                        y += stack[k + 1] + stack[k + 3] + stack[k + 5];
                        points.Add((x, y));
                    }
                    stack.Clear();
                    break;

                case 14:
                    return points;

                default:
                    stack.Clear();
                    break;
            }
        }

        return points;
    }

    private static byte[] Type1(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] part in parts) all.AddRange(part);
        return all.ToArray();
    }

    private static byte[] Op(int op) => new[] { (byte)op };

    /// <summary>A Type 1 charstring number.</summary>
    private static byte[] Num(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };

        if (v >= 108 && v <= 1131)
        {
            int a = v - 108;
            return new[] { (byte)((a >> 8) + 247), (byte)a };
        }
        if (v >= -1131 && v <= -108)
        {
            int a = -v - 108;
            return new[] { (byte)((a >> 8) + 251), (byte)a };
        }

        return new[] { (byte)255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }
}
