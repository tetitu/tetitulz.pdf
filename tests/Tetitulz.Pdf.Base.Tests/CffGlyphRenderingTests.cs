// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for CFF Type 2 charstring evaluation and CffGlyphSource (GAP 22.2).
/// All tests use in-memory CffFont instances — no disk I/O required.
/// </summary>
public class CffGlyphRenderingTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static CffFont MakeFont(
        byte[][] charstrings,
        Dictionary<int, int> charCodeToGid = null,
        float defaultWidthX = 0f,
        float nominalWidthX = 0f)
    {
        return new CffFont(
            charstrings,
            System.Array.Empty<byte[]>(),
            System.Array.Empty<byte[]>(),
            charCodeToGid ?? new Dictionary<int, int>(),
            defaultWidthX,
            nominalWidthX);
    }

    // ----------------------------------------------------------------
    //  Endchar-only glyph
    // ----------------------------------------------------------------

    [Fact]
    public void Evaluate_Endchar_ReturnsEmptyPath()
    {
        var font = MakeFont(new[] { new byte[] { 14 } }); // GID 0: endchar
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.True(outline.Path.IsEmpty, "A glyph with only endchar should produce an empty path");
    }

    [Fact]
    public void Evaluate_Endchar_UsesDefaultWidth()
    {
        var font = MakeFont(new[] { new byte[] { 14 } }, defaultWidthX: 250f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.Equal(250f, outline.AdvanceWidth);
    }

    [Fact]
    public void Evaluate_ExplicitWidthInEndchar_OverridesDefault()
    {
        // Width = 500 supplied as arg to endchar (single operand before op 14)
        // 500 = [248, 136] in Type2 encoding, then 14
        var font = MakeFont(
            new[] { new byte[] { 248, 136, 14 } },
            defaultWidthX: 0f,
            nominalWidthX: 0f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.Equal(500f, outline.AdvanceWidth, precision: 1);
    }

    // ----------------------------------------------------------------
    //  random — the same glyph draws the same
    // ----------------------------------------------------------------

    // random 1000 mul 0 rmoveto 100 0 rlineto endchar: the path starts at x = random × 1000.
    private static readonly byte[] RandomStart = { 12, 23, 250, 124, 12, 24, 139, 21, 239, 139, 5, 14 };

    [Fact]
    public void Evaluate_Random_DrawsTheSameGlyphOnEveryEvaluationAndThread()
    {
        var font = MakeFont(new[] { RandomStart });
        float first = Type2CharstringEvaluator.Evaluate(font, 0).Path.Bounds.Left;
        float again = Type2CharstringEvaluator.Evaluate(font, 0).Path.Bounds.Left;
        float elsewhere = 0;
        var thread = new System.Threading.Thread(() => elsewhere = Type2CharstringEvaluator.Evaluate(font, 0).Path.Bounds.Left);
        thread.Start();
        thread.Join();

        Assert.True(first > 0 && first <= 1000, $"random × 1000 was {first}");
        Assert.Equal(first, again);
        Assert.Equal(first, elsewhere);
    }

    // ----------------------------------------------------------------
    //  Simple rectangle glyph
    // ----------------------------------------------------------------

    // GID 1 charstring: rmoveto(width=500,dx=0,dy=0) + rlineto(0,400,400,0,0,-400) + endchar
    private static readonly byte[] RectangleCharstring =
    {
        248, 136,           // width = 500 (first stack arg before rmoveto = explicit width)
        139, 139,           // dx=0, dy=0
        21,                 // rmoveto
        139, 248, 36,       // 0, 400
        248, 36, 139,       // 400, 0
        139, 252, 36,       // 0, -400
        5,                  // rlineto
        14                  // endchar
    };

    [Fact]
    public void Evaluate_RectangleGlyph_ReturnsNonEmptyPath()
    {
        var font = MakeFont(new[] { RectangleCharstring });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.False(outline.Path.IsEmpty, "Rectangle glyph should produce a non-empty path");
    }

    [Fact]
    public void Evaluate_RectangleGlyph_AdvanceWidthIs500()
    {
        var font = MakeFont(new[] { RectangleCharstring }, nominalWidthX: 0f, defaultWidthX: 0f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.Equal(500f, outline.AdvanceWidth, precision: 1);
    }

    [Fact]
    public void Evaluate_RectangleGlyph_PathHasLines()
    {
        var font = MakeFont(new[] { RectangleCharstring });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        // Path should have a moveto and line segments
        var bounds = outline.Path.Bounds;
        Assert.True(bounds.Width > 0 || bounds.Height > 0,
            "Path bounds should have non-zero extent");
    }

    // ----------------------------------------------------------------
    //  NominalWidthX combination
    // ----------------------------------------------------------------

    [Fact]
    public void Evaluate_WithNominalWidthX_AddsToExplicitWidth()
    {
        // nominalWidthX=100, explicit width operand=400 → advance = 500
        var font = MakeFont(
            new[] { new byte[] { 248, 136, 14 } }, // explicit width=500 in charstring
            nominalWidthX: 0f, defaultWidthX: 0f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        // With nominalWidthX=0, advance = 0 + 500 = 500
        Assert.Equal(500f, outline.AdvanceWidth, precision: 1);
    }

    [Fact]
    public void Evaluate_WithNominalWidthX_Nonzero()
    {
        // nominalWidthX=100, charstring width operand=400 → advance = 500
        // 400 = [248, 36]
        var font = MakeFont(
            new[] { new byte[] { 248, 36, 14 } },
            nominalWidthX: 100f, defaultWidthX: 0f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.Equal(500f, outline.AdvanceWidth, precision: 1);
    }

    // ----------------------------------------------------------------
    //  hmoveto / vmoveto
    // ----------------------------------------------------------------

    [Fact]
    public void Evaluate_Hmoveto_ProducesMoveto()
    {
        // hmoveto with dx=50, then endchar
        // 50+139=189
        byte[] cs = { 189, 22, 14 }; // hmoveto dx=50, endchar
        var font = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // A moveto and nothing else: one point, at the horizontal offset the charstring asked for —
        // asserted exactly, since a point count alone passes for an empty path, the wrong point or a
        // vertical one.
        Assert.Equal("MC", Verbs(outline.Path));
        Assert.Equal(1, outline.Path.PointCount);
        Assert.Equal(new SKPoint(50f, 0f), outline.Path[0]);
    }

    [Fact]
    public void Evaluate_Vmoveto_ProducesMoveto()
    {
        // vmoveto with dy=50, then endchar
        byte[] cs = { 189, 4, 14 }; // vmoveto dy=50, endchar
        var font = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // A moveto and nothing else: one point, at the vertical offset the charstring asked for.
        Assert.Equal("MC", Verbs(outline.Path));
        Assert.Equal(1, outline.Path.PointCount);
        Assert.Equal(new SKPoint(0f, 50f), outline.Path[0]);
    }

    // ----------------------------------------------------------------
    //  Curve operators
    // ----------------------------------------------------------------

    [Fact]
    public void Evaluate_Rrcurveto_ProducesNonEmptyPath()
    {
        // rmoveto(0,0) + rrcurveto(50,50, 50,50, 50,0) + endchar
        byte[] cs =
        {
            139, 139, 21,       // rmoveto dx=0, dy=0 (no width)
            // 50+139=189
            189, 189, 189, 189, 189, 139, // dx1,dy1, dx2,dy2, dx3,dy3
            8,                  // rrcurveto
            14                  // endchar
        };
        var font = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.False(outline.Path.IsEmpty);
    }

    [Fact]
    public void Evaluate_Hhcurveto_ProducesNonEmptyPath()
    {
        // rmoveto(0,0) + hhcurveto(50,50,0,50) + endchar
        byte[] cs =
        {
            139, 139, 21,
            189, 189, 139, 189,   // dx1, dx2, dy2, dx3
            27,                   // hhcurveto
            14
        };
        var font = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.False(outline.Path.IsEmpty);
    }

    [Fact]
    public void Evaluate_Vvcurveto_ProducesNonEmptyPath()
    {
        // rmoveto(0,0) + vvcurveto(50,50,0,50) + endchar
        byte[] cs =
        {
            139, 139, 21,
            189, 189, 139, 189,   // dy1, dx2, dy2, dy3
            26,                   // vvcurveto
            14
        };
        var font = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.False(outline.Path.IsEmpty);
    }

    // ----------------------------------------------------------------
    //  CffGlyphSource
    // ----------------------------------------------------------------

    [Fact]
    public void CffGlyphSource_GetOutline_KnownCode_ReturnsOutline()
    {
        var font = MakeFont(
            new[] { new byte[] { 14 }, RectangleCharstring },
            charCodeToGid: new Dictionary<int, int> { [65] = 1 });
        var source = new CffGlyphSource(font);

        var outline = source.GetOutline(65);
        Assert.NotNull(outline);
        Assert.False(outline.Value.Path.IsEmpty);
    }

    [Fact]
    public void CffGlyphSource_GetOutline_UnknownCode_FallsBackToNotdef()
    {
        var font = MakeFont(
            new[] { new byte[] { 14 } }, // only .notdef
            charCodeToGid: new Dictionary<int, int>());
        var source = new CffGlyphSource(font);

        // Code 99 is not mapped → GID 0 (.notdef)
        var outline = source.GetOutline(99);
        Assert.NotNull(outline);
        Assert.True(outline.Value.Path.IsEmpty, ".notdef endchar should produce empty path");
    }

    [Fact]
    public void CffGlyphSource_GetAdvanceWidth_UsesGlyphWidth()
    {
        var font = MakeFont(
            new[] { new byte[] { 14 }, RectangleCharstring },
            charCodeToGid: new Dictionary<int, int> { [65] = 1 },
            defaultWidthX: 0f, nominalWidthX: 0f);
        var source = new CffGlyphSource(font);

        float w = source.GetAdvanceWidth(65);
        Assert.Equal(500f, w, precision: 1);
    }

    [Fact]
    public void CffGlyphSource_OutlineIsCached()
    {
        var font = MakeFont(
            new[] { RectangleCharstring },
            charCodeToGid: new Dictionary<int, int> { [65] = 0 });
        var source = new CffGlyphSource(font);

        // Call twice; should return the same SKPath instance (cached).
        var outline1 = source.GetOutline(65);
        var outline2 = source.GetOutline(65);
        Assert.Same(outline1.Value.Path, outline2.Value.Path);
    }

    // ----------------------------------------------------------------
    //  Out-of-bounds / edge cases
    // ----------------------------------------------------------------

    [Fact]
    public void Evaluate_NegativeGid_ReturnsDefaultOutline()
    {
        var font = MakeFont(new[] { new byte[] { 14 } });
        var outline = Type2CharstringEvaluator.Evaluate(font, -1);
        Assert.Equal(font.DefaultWidthX, outline.AdvanceWidth);
    }

    [Fact]
    public void Evaluate_GidBeyondCount_ReturnsDefaultOutline()
    {
        var font = MakeFont(new[] { new byte[] { 14 } });
        var outline = Type2CharstringEvaluator.Evaluate(font, 999);
        Assert.Equal(font.DefaultWidthX, outline.AdvanceWidth);
    }

    [Fact]
    public void Evaluate_EmptyCharstring_ProducesAnEmptyPath()
    {
        var font = MakeFont(new[] { System.Array.Empty<byte>() });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // Nothing in, nothing out — not a stray moveto, not a phantom contour.
        Assert.True(outline.Path.IsEmpty);
        Assert.Equal(0, outline.Path.PointCount);
    }

    // ----------------------------------------------------------------
    //  flex1 — snap-back axis must return to the STARTING point
    // ----------------------------------------------------------------

    [Fact]
    public void OpFlex1_VerticalDominates_XSnapsBackToStartX()
    {
        // 11 args: dx1=0,dy1=100, dx2=0,dy2=100, dx3=0,dy3=100,
        //          dx4=0,dy4=100, dx5=100,dy5=100, d6=50
        // total dx=100, total dy=500 → |dy|>|dx| → vertical dominates → X snaps to startX=0
        // 5th control point (x2,y2) = (100, 500)
        // Expected endpoint: (startX=0, y2+d6=550) — NOT (x2=100, 550)
        byte[] cs =
        {
            139, 239,   // dx1=0,  dy1=100
            139, 239,   // dx2=0,  dy2=100
            139, 239,   // dx3=0,  dy3=100
            139, 239,   // dx4=0,  dy4=100
            239, 239,   // dx5=100, dy5=100
            189,        // d6=50
            12, 37,     // escape, flex1
            14          // endchar
        };
        var font    = MakeFont(new[] { cs }, defaultWidthX: 700f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.False(outline.Path.IsEmpty);
        var pts = outline.Path.Points;
        // Last point is the flex1 endpoint.
        var last = pts[pts.Length - 1];
        Assert.Equal(0f,   last.X, 1f);   // X snapped back to startX=0
        Assert.Equal(550f, last.Y, 1f);   // Y = y2(500) + d6(50)
    }

    [Fact]
    public void OpFlex1_HorizontalDominates_YSnapsBackToStartY()
    {
        // 11 args: dx1=100,dy1=0, dx2=100,dy2=0, dx3=100,dy3=0,
        //          dx4=100,dy4=0, dx5=100,dy5=100, d6=50
        // total dx=500, total dy=100 → |dx|>|dy| → horizontal dominates → Y snaps to startY=0
        // 5th control point (x2,y2) = (500, 100)
        // Expected endpoint: (x2+d6=550, startY=0) — NOT (550, y2=100)
        byte[] cs =
        {
            239, 139,   // dx1=100, dy1=0
            239, 139,   // dx2=100, dy2=0
            239, 139,   // dx3=100, dy3=0
            239, 139,   // dx4=100, dy4=0
            239, 239,   // dx5=100, dy5=100
            189,        // d6=50
            12, 37,     // escape, flex1
            14          // endchar
        };
        var font    = MakeFont(new[] { cs }, defaultWidthX: 700f);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.False(outline.Path.IsEmpty);
        var pts  = outline.Path.Points;
        var last = pts[pts.Length - 1];
        Assert.Equal(550f, last.X, 1f);  // X = x2(500) + d6(50)
        Assert.Equal(0f,   last.Y, 1f);  // Y snapped back to startY=0
    }

    // ----------------------------------------------------------------
    //  Bug regression: callgsubr (byte 29) was eaten as a 4-byte integer
    // ----------------------------------------------------------------

    [Fact]
    public void CallGsubr_ExecutesGlobalSubroutine()
    {
        // Glyph charstring: push subr-index (-107 → bias 107 = subr 0) then callgsubr (29).
        // Global subr 0: rmoveto(dx=50,dy=0) + endchar.
        // Expected: path contains a MoveTo at (50, 0), advance = defaultWidthX = 400.
        //
        // Encoding: -107 is byte value 32 (b0=139 → 0; b0=32 → 32-139=-107).
        // Subr bias for 1 subr = 107, so index = -107 + 107 = 0. ✓
        // rmoveto: dx=50 (b0=189→50), dy=0 (b0=139→0), op 21.
        byte[] subr0 = { 189, 139, 21, 14 }; // rmoveto(50, 0) + endchar
        byte[] glyph = { 32, 29, 14 };        // push -107, callgsubr(29), endchar (fallthrough)

        var font = new CffFont(
            new[] { glyph },
            localSubrs:  System.Array.Empty<byte[]>(),
            globalSubrs: new[] { subr0 },
            charCodeToGid: new Dictionary<int, int>(),
            defaultWidthX: 400f,
            nominalWidthX: 0f);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.False(outline.Path.IsEmpty, "callgsubr must execute the global subroutine, producing a non-empty path");
        Assert.Equal(400f, outline.AdvanceWidth);
        // First path point after MoveTo should be (50, 0)
        var pts = outline.Path.Points;
        Assert.True(pts.Length > 0);
        Assert.Equal(50f, pts[0].X, 1f);
        Assert.Equal(0f,  pts[0].Y, 1f);
    }

    // ----------------------------------------------------------------
    //  Bug regression: vhcurveto / hvcurveto with n%4==0 (not n%8==0)
    //  crashed with IndexOutOfRangeException for n=4 and n=12.
    // ----------------------------------------------------------------

    [Fact]
    public void OpVHCurveto_FourArgs_ProducesExactlyOneCubic()
    {
        // vhcurveto with exactly 4 args (1 curve, no extra endpoint arg).
        // All values = 0 (byte 139 = push 0) for simplicity.
        // Stack: [0, 0, 0, 0], op 30 (vhcurveto).
        byte[] cs = { 139, 139, 139, 139, 30, 14 };
        var font    = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // Four arguments is exactly one curve. Counting the emitted cubics is what distinguishes
        // "the operator was decoded" from "the operator was skipped without throwing".
        Assert.Equal("MC", Verbs(outline.Path));
        Assert.Equal(4, outline.Path.PointCount);
    }

    [Fact]
    public void OpHVCurveto_FourArgs_ProducesExactlyOneCubic()
    {
        // hvcurveto with exactly 4 args, op 31.
        byte[] cs = { 139, 139, 139, 139, 31, 14 };
        var font    = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.Equal("MC", Verbs(outline.Path));
        Assert.Equal(4, outline.Path.PointCount);
    }

    /// <summary>
    /// <c>hvcurveto</c> emits one cubic per four arguments, so 8 args are two curves and 12 args
    /// are three.
    ///
    /// <para>This is the test for the bug its predecessor's comment described and never checked:
    /// the condition was <c>n % 8</c> where it should be <c>n % 4</c>, which changes how many
    /// curves are produced. Counting the emitted cubics is the only assertion that can see that;
    /// "did not crash" is true whether the operator produces three curves, one, or none.</para>
    /// </summary>
    [Theory]
    [InlineData(2, "MCC")]     // 8 args
    [InlineData(3, "MCCC")]    // 12 args
    public void OpHVCurveto_EmitsOneCubicPerFourArguments(int curves, string expectedVerbs)
    {
        var cs = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < curves * 4; i++) cs.Add(139);   // 139 = push 0
        cs.Add(31);                                          // hvcurveto
        cs.Add(14);                                          // endchar

        var font    = MakeFont(new[] { cs.ToArray() });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.Equal(expectedVerbs, Verbs(outline.Path));
        Assert.Equal(1 + curves * 3, outline.Path.PointCount);
    }

    [Fact]
    public void OpVHCurveto_FiveArgs_AppliesEndpointCorrection()
    {
        // vhcurveto with 5 args (TN #5177 §4.4):  [dya, dxb, dyb, dx3, dyf]
        // The extra 5th arg is dyf — a small Y correction at the endpoint.
        // dya=100, dxb=0, dyb=0, dx3=0, dyf=50
        // End point per spec: X = x2 + dx3 = 0+0 = 0,  Y = y2 + dyf = 100+50 = 150
        byte[] cs =
        {
            239,        // dya=100 (b0=239 → 100)
            139,        // dxb=0
            139,        // dyb=0
            139,        // dx3=0
            189,        // dyf=50 (b0=189 → 50)
            30,         // vhcurveto
            14          // endchar
        };
        var font    = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.False(outline.Path.IsEmpty);
        var pts  = outline.Path.Points;
        var last = pts[pts.Length - 1];
        Assert.Equal(0f,   last.X, 1f);  // dx3=0, no horizontal correction
        Assert.Equal(150f, last.Y, 1f);  // y2=100 + dyf=50 = 150
    }

    // ----------------------------------------------------------------
    //  Issue A: endchar in subroutine must terminate entire charstring
    // ----------------------------------------------------------------

    [Fact]
    public void EndcharInLocalSubr_StopsExecution()
    {
        // Subr 0: rmoveto(10,10) then endchar — no lineto follows in the subr
        // Main charstring: callsubr(0) then lineto(50,0)
        // The lineto must NOT execute because endchar in the subroutine terminates everything.
        //
        // Subr 0 bytes:
        //   251 (=10 encoded as b0=248, b1=18 → (248-247)*256+18+108 = 266? no)
        //   Actually encode 10: b0 in 32-246 range → b0 = 10+139 = 149
        //   rmoveto: dx=10 (149), dy=10 (149), op=21
        //   endchar: op=14
        //
        // Main charstring:
        //   callsubr index = 0 - bias(0 subrs=107) = -107, encoded: -(107-139)= hmm
        //   With 1 subr, bias = 107. subr index = 0, so push (0 - 107) = -107.
        //   -107 encodes as: b0=251+0=251, b1=0 → -(0*256)-0-108 = -108. That's -108, not -107.
        //   Let's use 2 subrs so bias=107 still, and pick subr 1.
        //   push (1 - 107) = -106: b0=251, b1=1 → -(0*256)-1-108=-109. Still off.
        //   Encode -107 using shortint: byte 28 + 0xFF 0x95 (= -107)
        //   -107 as int16 = 0xFF95
        //   callsubr (10), endchar (14)
        //
        // Then the main charstring has a lineto after callsubr that must NOT run:
        //   push 50 (b0=50+139=189), push 0 (b0=139), rlineto (5)
        //   endchar (14)
        byte[] subr0 = { 149, 149, 21, 14 }; // rmoveto(10,10), endchar
        // bias for 1 subr = 107, so to call subr[0] push (0-107)=-107
        // shortint encoding: 28 0xFF 0x95
        byte[] main =
        {
            28, 0xFF, 0x95,  // push -107 (shortint)
            10,              // callsubr → calls subr 0
            // The following must NOT execute:
            189, 139,        // push 50, push 0
            5,               // rlineto
            14               // endchar
        };

        var font = new CffFont(
            new[] { main },
            localSubrs: new[] { subr0 },
            globalSubrs: System.Array.Empty<byte[]>(),
            charCodeToGid: new Dictionary<int, int>(),
            defaultWidthX: 0f,
            nominalWidthX: 0f);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // The path should have a moveto from the subroutine (non-empty),
        // but NOT a lineto (which would add a second point distinct from the moveto).
        // After rmoveto(10,10) + endchar the path has only a MoveTo — Path.PointCount == 1.
        Assert.True(outline.Path.PointCount <= 1,
            $"Expected ≤1 point (moveto only), got {outline.Path.PointCount} — lineto after endchar must not execute");
    }

    [Fact]
    public void EndcharInGlobalSubr_StopsExecution()
    {
        // Same as above but using callgsubr (29).
        byte[] gsubr0 = { 14 }; // endchar immediately
        // bias for 1 gsubr = 107, push (0-107) = -107
        byte[] main =
        {
            28, 0xFF, 0x95,  // push -107
            29,              // callgsubr → endchar in gsubr
            // Must NOT execute:
            189, 139, 5,     // rlineto(50,0)
            14
        };

        var font = new CffFont(
            new[] { main },
            localSubrs: System.Array.Empty<byte[]>(),
            globalSubrs: new[] { gsubr0 },
            charCodeToGid: new Dictionary<int, int>(),
            defaultWidthX: 500f,
            nominalWidthX: 0f);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        // Path must be empty (no moveto happened before endchar in gsubr)
        Assert.True(outline.Path.IsEmpty,
            "endchar in global subroutine must terminate the entire charstring");
    }

    // ----------------------------------------------------------------
    //  Issue B: escape operator 15 = index
    // ----------------------------------------------------------------

    [Fact]
    public void EscapeIndex_PushesCopyOfNthElement()
    {
        // Charstring: push 10, 20, 30 onto stack, then 12 15 (escape index) with n=1
        // Stack before index: [10, 20, 30], n=1 (0-based from top: top=30, 1=20)
        // After: [10, 20, 30, 20]
        // We then pop two values and use them in rmoveto, producing a path at (20, 30).
        //
        // Encoding:
        //   push 10  = 149 (10+139)
        //   push 20  = 159 (20+139)
        //   push 30  = 169 (30+139)
        //   push 1   = 140 (1+139)
        //   12 15    = escape index → stack becomes [10, 20, 30, 20]
        //   (stack now has 4 items; rmoveto with no explicit width needs 2)
        //   pop 2 for rmoveto: dx=30 dy=20 → no, rmoveto pops last two
        //   Actually rmoveto (21) uses Stack[0]=dx, Stack[1]=dy when count==2,
        //   but our stack is [10, 20, 30, 20] (4 items), so it treats Stack[0]=width.
        //   Let's keep it simple: push only the n+1 needed values, use ifelse or dup differently.
        //
        // Simpler: push 5 and 3 on stack, index with n=0 duplicates top (5).
        //   Stack before: [3, 5], n=0 → push copy of top(5) → [3, 5, 5]
        //   Then pop two: 5 5 → rmoveto(5, 5) → path at (5,5)
        //   (3 is the "width" since count=3 is odd and no explicit width)
        byte[] cs =
        {
            142,     // push 3 (3+139=142)
            144,     // push 5 (5+139=144)
            144,     // push 5 (5+139=144)  — after index, we need stack size ≥2 for rmoveto
            139,     // push 0 (n=0 for index: duplicate top element)
            12, 15,  // escape 15 = index
            // stack is now [3, 5, 5, 5] — rmoveto will take width=3, dx=5, dy=5
            21,      // rmoveto
            14       // endchar
        };

        var font    = MakeFont(new[] { cs });
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.False(outline.Path.IsEmpty, "index op should leave valid values for rmoveto");
        var pts = outline.Path.Points;
        Assert.True(pts.Length >= 1);
        Assert.Equal(5f, pts[0].X, 1f);
        Assert.Equal(5f, pts[0].Y, 1f);
    }

    /// <summary>
    /// The path's verb sequence as initials — <c>"MCCC"</c> is a moveto followed by three cubics.
    ///
    /// <para>Charstring operators are only observable through the geometry they emit, and with
    /// zero-valued arguments (the simplest fixtures) every point lands at the origin, so bounds and
    /// pixels say nothing. The verb sequence is what distinguishes "decoded into N curves" from
    /// "silently skipped" — which is exactly the distinction the <c>_DoesNotCrash</c> tests this
    /// replaces could not make.</para>
    /// </summary>
    private static string Verbs(SKPath path)
    {
        var sb = new System.Text.StringBuilder();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
            sb.Append(verb.ToString()[0]);
        return sb.ToString();
    }
}
