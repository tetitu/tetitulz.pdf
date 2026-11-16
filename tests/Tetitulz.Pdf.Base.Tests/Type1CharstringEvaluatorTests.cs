// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Unit tests for the Type 1 charstring evaluator using hand-crafted in-memory charstrings.
/// All Type 1 number encoding: byte 139 = value 0; [250,124] = value 1000;
///   [254,124] = value -1000; byte 13 = hsbw; byte 21 = rmoveto; byte 5 = rlineto;
///   byte 9 = closepath; byte 14 = endchar.
/// </summary>
public class Type1CharstringEvaluatorTests
{
    // ---- Helpers ----

    // Push integer N onto the Type 1 charstring stack
    private static byte[] PushInt(int n)
    {
        if (n >= -107 && n <= 107) return new[] { (byte)(n + 139) };
        if (n >= 108 && n <= 1131)
        {
            n -= 108;
            return new[] { (byte)(247 + (n >> 8)), (byte)(n & 0xFF) };
        }
        if (n >= -1131 && n <= -108)
        {
            n = -n - 108;
            return new[] { (byte)(251 + (n >> 8)), (byte)(n & 0xFF) };
        }
        // 4-byte: byte 255 followed by 4 bytes big-endian
        return new byte[] { 255, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
    }

    private static byte[] CS(params object[] ops)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        foreach (var op in ops)
        {
            if (op is int n)  bytes.AddRange(PushInt(n));
            else if (op is byte b) bytes.Add(b);
        }
        return bytes.ToArray();
    }

    private static Type1Font MakeFont(byte[] charstring, string glyphName = "a")
    {
        var cs = new Dictionary<string, byte[]> { [glyphName] = charstring, [".notdef"] = new byte[] { 14 } };
        return new Type1Font(
            BuildSimpleEncoding(glyphName),
            cs,
            System.Array.Empty<byte[]>(),
            fontMatrixScale: 0.001f);
    }

    private static string[] BuildSimpleEncoding(string glyphName)
    {
        var enc = new string[256];
        for (int i = 0; i < 256; i++) enc[i] = ".notdef";
        enc[97] = glyphName; // 'a' = 0x61
        return enc;
    }

    // ---- Tests ----

    [Fact]
    public void Hsbw_SetsWidthAndCurrentPoint()
    {
        // hsbw with sbx=15, wx=588
        byte[] cs = CS(15, 588, (byte)13 /*hsbw*/, (byte)14 /*endchar*/);
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.Equal(588f, result.AdvanceWidth);
        Assert.True(result.Path.IsEmpty); // no drawing ops, path is empty
    }

    [Fact]
    public void SimpleSquare_ProducesCorrectBounds()
    {
        // hsbw sbx=0 wx=1000; rmoveto 0 0; rlineto 0 1000; rlineto 1000 0; rlineto 0 -1000; closepath; endchar
        byte[] cs = CS(
            0, 1000, (byte)13,  // hsbw: sbx=0, wx=1000
            0, 0,    (byte)21,  // rmoveto: move to (0,0)
            0, 1000, (byte)5,   // rlineto: up 1000 → (0,1000)
            1000, 0, (byte)5,   // rlineto: right 1000 → (1000,1000)
            0, -1000,(byte)5,   // rlineto: down 1000 → (1000,0)
            (byte)9,            // closepath
            (byte)14            // endchar
        );
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.Equal(1000f, result.AdvanceWidth);
        Assert.False(result.Path.IsEmpty);

        var bounds = result.Path.Bounds;
        Assert.Equal(0f, bounds.Left,   precision: 1);
        Assert.Equal(0f, bounds.Top,    precision: 1); // SKPath Y: top = min Y
        Assert.Equal(1000f, bounds.Right,  precision: 1);
        Assert.Equal(1000f, bounds.Bottom, precision: 1); // bottom = max Y (SKPath Y-down convention)
    }

    [Fact]
    public void HMoveto_AdvancesX()
    {
        // hsbw sbx=100 wx=600; hmoveto 50; endchar
        byte[] cs = CS(
            100, 600, (byte)13, // hsbw → X=100
            50,       (byte)22, // hmoveto → X=150
            (byte)14
        );
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.Equal(600f, result.AdvanceWidth);
        // Path has one moveto but no lines — bounds of a single point
        var bounds = result.Path.Bounds;
        Assert.Equal(150f, bounds.Left, precision: 1);
    }

    [Fact]
    public void VMoveto_AdvancesY()
    {
        // hsbw sbx=0 wx=500; vmoveto 300; endchar
        byte[] cs = CS(
            0, 500, (byte)13, // hsbw → X=0, Y=0
            300,    (byte)4,  // vmoveto → Y=300
            (byte)14
        );
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        // The single moveto creates a path at (0, 300)
        var bounds = result.Path.Bounds;
        Assert.Equal(0f,   bounds.Left,   precision: 1);
        Assert.Equal(300f, bounds.Bottom, precision: 1); // single moveto: top=bottom=300
    }

    [Fact]
    public void RRCurveto_ProducesNonEmptyPath()
    {
        // hsbw sbx=0 wx=1000; rmoveto 100 100; rrcurveto 0 200 200 0 200 -200; endchar
        byte[] cs = CS(
            0, 1000, (byte)13,
            100, 100, (byte)21,
            0, 200, 200, 0, 200, -200, (byte)8, // rrcurveto
            (byte)14
        );
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.False(result.Path.IsEmpty);
        Assert.Equal(1000f, result.AdvanceWidth);
    }

    [Fact]
    public void Closepath_ClosesCurrentSubpath()
    {
        // Draw a triangle using rlineto then closepath
        byte[] cs = CS(
            0, 500, (byte)13,      // hsbw
            0, 0, (byte)21,        // rmoveto to (0,0)
            500, 500, (byte)5,     // rlineto to (500,500)
            -500, 0, (byte)5,      // rlineto to (0,500)
            (byte)9,               // closepath back to (0,0)
            (byte)14               // endchar
        );
        var font = MakeFont(cs);
        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.False(result.Path.IsEmpty);
        var bounds = result.Path.Bounds;
        Assert.Equal(0f, bounds.Left, precision: 1);
        Assert.Equal(500f, bounds.Right, precision: 1);
    }

    [Fact]
    public void Callsubr_ExecutesSubroutine()
    {
        // Subroutine 0: rlineto 300 0; return
        byte[] subr0 = CS(300, 0, (byte)5, (byte)11);

        // Main: hsbw 0 500; rmoveto 0 0; callsubr(0); endchar
        byte[] main = CS(
            0, 500, (byte)13,  // hsbw
            0, 0, (byte)21,    // rmoveto
            0, (byte)10,       // callsubr(0)
            (byte)14
        );

        var cs = new Dictionary<string, byte[]> { ["a"] = main, [".notdef"] = new byte[] { 14 } };
        var font = new Type1Font(
            BuildSimpleEncoding("a"), cs,
            new[] { subr0 },
            fontMatrixScale: 0.001f);

        var result = Type1CharstringEvaluator.Evaluate(font, "a");
        Assert.False(result.Path.IsEmpty);
        // After rmoveto (0,0) and rlineto (300,0), path should have width 300
        var bounds = result.Path.Bounds;
        Assert.Equal(300f, bounds.Right, precision: 1);
    }
    // ---- Curve operators (Base.Rendering.CharstringPath, shared with the Type 2 evaluator) ----

    /// <summary>
    /// <c>rrcurveto</c> consumes its six operands in order: dx1 dy1 dx2 dy2 dx3 dy3.
    /// </summary>
    /// <remarks>
    /// <b>Nothing asserted any curve operator.</b> Swapping the last two operands — the endpoint's
    /// dx and dy — left the entire Base suite green, so the two evaluators' identical curve bodies
    /// could have been merged into a wrong one without a single test noticing. The operands here
    /// are deliberately all different: with equal values the swap is invisible.
    /// </remarks>
    [Fact]
    public void RRCurveto_ConsumesItsSixOperandsInOrder()
    {
        byte[] cs = CS(
            0, 1000, (byte)13,                   // hsbw
            0, 0,    (byte)21,                   // rmoveto to (0,0)
            100, 0, 100, 100, 0, 100, (byte)8,   // rrcurveto
            (byte)14);

        SKPoint end = LastPoint(cs);

        // (0,0) → c1 (100,0) → c2 (200,100) → end (200,200)
        Assert.Equal(200f, end.X, precision: 1);
        Assert.Equal(200f, end.Y, precision: 1);
    }

    /// <summary>
    /// <c>vhcurveto</c> alternates: the first curve departs vertically and arrives horizontally,
    /// the second departs horizontally and arrives vertically, and so on.
    /// </summary>
    /// <remarks>
    /// The operands are all different ON PURPOSE. With eight equal values the alternating and
    /// non-alternating readings produce the SAME endpoint, so a symmetric fixture cannot detect a
    /// missing toggle — which is exactly the mutation this exists to kill.
    /// </remarks>
    [Fact]
    public void VHCurveto_AlternatesBetweenVerticalAndHorizontalDeparture()
    {
        byte[] cs = CS(
            0, 1000, (byte)13,
            0, 0,    (byte)21,
            100, 20, 30, 40, 50, 60, 70, 80, (byte)30,   // vhcurveto, two curves
            (byte)14);

        SKPoint end = LastPoint(cs);

        // curve 1 (vert→horiz): c1 (0,100) c2 (20,130) end (60,130)
        // curve 2 (horiz→vert): c1 (110,130) c2 (170,200) end (170,280)
        Assert.Equal(170f, end.X, precision: 1);
        Assert.Equal(280f, end.Y, precision: 1);
    }

    /// <summary>
    /// <c>hvcurveto</c> is the mirror: the first curve departs horizontally. Same operands as
    /// <see cref="VHCurveto_AlternatesBetweenVerticalAndHorizontalDeparture"/>, so a different
    /// endpoint is the whole difference between the two operators.
    /// </summary>
    [Fact]
    public void HVCurveto_IsTheMirrorOfVHCurveto()
    {
        byte[] cs = CS(
            0, 1000, (byte)13,
            0, 0,    (byte)21,
            100, 20, 30, 40, 50, 60, 70, 80, (byte)31,   // hvcurveto
            (byte)14);

        SKPoint end = LastPoint(cs);

        // curve 1 (horiz→vert): c1 (100,0) c2 (120,30) end (120,70)
        // curve 2 (vert-then-horiz): c1 (120,120) c2 (180,190) end (260,190)
        Assert.Equal(260f, end.X, precision: 1);
        Assert.Equal(190f, end.Y, precision: 1);
    }

    /// <summary>The last point the charstring's path reaches.</summary>
    private static SKPoint LastPoint(byte[] charstring)
    {
        var result = Type1CharstringEvaluator.Evaluate(MakeFont(charstring), "a");
        Assert.False(result.Path.IsEmpty, "the charstring drew nothing");

        SKPoint[] points = result.Path.Points;
        return points[points.Length - 1];
    }

}
