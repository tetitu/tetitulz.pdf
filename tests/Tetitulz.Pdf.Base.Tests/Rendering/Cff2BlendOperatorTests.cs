// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests for the CFF2 blend operator (opcode 16) in Type2CharstringEvaluator.
/// Closes GAPS.md R13 deferral (variable-font axis blending).
/// </summary>
public class Cff2BlendOperatorTests
{
    private static CffFont MakeCff2Font(byte[][] charstrings, int blendRegionCount = 0)
        => new CffFont(
            charstrings,
            Array.Empty<byte[]>(),
            Array.Empty<byte[]>(),
            new Dictionary<int, int>(),
            defaultWidthX: 500f,
            nominalWidthX: 0f,
            glyphNameToGid: null,
            isCff2: true,
            blendRegionCount: blendRegionCount);

    // ------------------------------------------------------------------
    //  blend at default axis (axisCount = 0): pops count, keeps defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Blend_ZeroAxes_RemovesCountOnly_LeavesDefaultsOnStack()
    {
        // Charstring: push 100, push 200, push 300, push 3 (n=3), blend, endchar
        // blend with axisCount=0 → pop count(3) → stack = [100, 200, 300]
        // endchar consumes them and terminates.
        // Expected: glyph evaluates without exception and advance width = 500 (default).
        byte[] cs = new byte[]
        {
            // 100 in charstring: 100+139=239=0xEF
            0xEF,
            // 200: 247 + (200-108)/256 → two-byte: b0=247+(92/256)=247+0=247, b1=92. Actually:
            // For 200: use 2-byte form. 200-108=92; b0=247+92/256=247+0=247, b1=92%256=92.
            0xF7, 0x5C,  // 200 (two-byte: (247-247)*256+92+108=200) ✓
            // 300: 300-108=192; b0=247+192/256=247+0=247, b1=192. 247,192 → (0)*256+192+108=300. ✓
            0xF7, 0xC0,  // 300
            // 3 (n=3): 3+139=142=0x8E
            0x8E,
            0x10,        // blend (opcode 16)
            0x0E,        // endchar
        };
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 0);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.NotNull(outline.Path);
        Assert.Equal(500f, outline.AdvanceWidth);
    }

    /// <summary>
    /// <c>blend</c> leaves only the default values on the stack and drops the region deltas.
    /// That is a <b>stack</b> effect, which a path cannot show on its own — so the charstring
    /// continues with <c>vlineto</c>, which consumes whatever the stack holds. One surviving value
    /// draws one vertical line; had the two deltas remained, <c>vlineto</c> would have drawn three
    /// alternating segments instead.
    ///
    /// <para>The predecessor spelled the expected stack out in a comment and then asserted
    /// <c>NotNull(outline.Path)</c>, which is true for every one of those outcomes.</para>
    /// </summary>
    [Fact]
    public void Blend_TwoAxes_RemovesDeltasKeepsDefaults()
    {
        // push 100 (default), 10 (delta axis0), 20 (delta axis1), n=1, blend -> stack = [100];
        // then vlineto (0x07) consumes it; endchar.
        byte[] cs = { 0xEF, 0x95, 0x9B, 0x8C, 0x10, 0x07, 0x0E };
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 2);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.Equal("ML", Verbs(outline.Path));          // exactly one line segment
        Assert.Equal(2, outline.Path.PointCount);
        Assert.Equal(0f, outline.Path.Bounds.Width);      // vertical: no horizontal extent
        Assert.Equal(100f, outline.Path.Bounds.Height);   // ...and it is the default, 100
    }

    /// <summary>
    /// Three defaults and six deltas in, three defaults out. <c>hlineto</c> then consumes the
    /// stack alternately, so the surviving count is visible as the number of segments: three
    /// values give three lines (50 across, 60 up, 70 across). Nine surviving values would give
    /// nine — which is exactly what the predecessor's <c>NotNull</c> could not tell apart.
    /// </summary>
    [Fact]
    public void Blend_MultipleValues_TwoAxes_KeepsAllDefaults()
    {
        // n=3, k=2: stack before blend = [d0, d1, d2, Δ0_0, Δ0_1, Δ1_0, Δ1_1, Δ2_0, Δ2_1, 3]
        // After blend: [d0, d1, d2]
        // Encoded: d0=50, d1=60, d2=70; deltas all=0; n=3.
        // 50→0xC6(50+139=189=0xBD)... 50+139=189. 60→199=0xC7. 70→209=0xD1. 0→139=0x8B. 3→142=0x8E.
        byte[] cs = new byte[]
        {
            (byte)(50+139),  // 50 = d0
            (byte)(60+139),  // 60 = d1
            (byte)(70+139),  // 70 = d2
            0x8B, 0x8B,      // Δ0_0=0, Δ0_1=0  (deltas for d0)
            0x8B, 0x8B,      // Δ1_0=0, Δ1_1=0  (deltas for d1)
            0x8B, 0x8B,      // Δ2_0=0, Δ2_1=0  (deltas for d2)
            (byte)(3+139),   // n=3
            0x10,            // blend  -> stack = [50, 60, 70]
            0x06,            // hlineto — consumes all three, alternating h/v
            0x0E,            // endchar
        };
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 2);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);

        Assert.Equal("MLLL", Verbs(outline.Path));        // three segments, not nine
        Assert.Equal(4, outline.Path.PointCount);
        Assert.Equal(120f, outline.Path.Bounds.Width);    // 50 + 70 across
        Assert.Equal(60f,  outline.Path.Bounds.Height);   // 60 up
    }

    [Fact]
    public void Blend_FollowedByMovetoLineto_ProducesNonEmptyPath()
    {
        // A charstring that blends a moveto argument, then draws.
        // push 0 (dx), 0 (dy); blend n=2 with k=0 (no axes → no deltas removed);
        // rmoveto; push 100, 0; rlineto; endchar.
        // Opcode 21 = rmoveto, 5 = rlineto.
        byte[] cs = new byte[]
        {
            0x8B, 0x8B,     // dx=0, dy=0 (defaults)
            (byte)(2+139),  // n=2
            0x10,           // blend (with axisCount=0 → no deltas)
            21,             // rmoveto (pops dx=0, dy=0)
            (byte)(100+139), 0x8B,  // 100, 0
            5,              // rlineto
            14,             // endchar
        };
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 0);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.NotNull(outline.Path);
        Assert.False(outline.Path.IsEmpty);
    }

    [Fact]
    public void Blend_EmptyStack_DoesNotThrow()
    {
        // blend with empty stack should be silently ignored.
        byte[] cs = new byte[] { 0x10, 0x0E }; // blend then endchar
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 0);
        var ex = Record.Exception(() => Type2CharstringEvaluator.Evaluate(font, 0));
        Assert.Null(ex);
    }

    [Fact]
    public void Blend_NegativeN_DoesNotThrow()
    {
        // n=-1 is malformed; should be silently discarded.
        // Encode -1: b0=251+((-1+108)/256)=... actually -1 in charstring:
        // -1 → use two-byte negative: b0=254 b1=108-(-1+108)/... let me just use b0=138 = 138-139=-1
        byte[] cs = new byte[] { 0x8A, 0x10, 0x0E }; // -1, blend, endchar
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 0);
        var ex = Record.Exception(() => Type2CharstringEvaluator.Evaluate(font, 0));
        Assert.Null(ex);
    }

    // ------------------------------------------------------------------
    //  CFF2 font end-to-end: evaluate a glyph with only endchar
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_Cff2Font_EndcharGlyph_DoesNotThrow()
    {
        byte[] cs = new byte[] { 14 }; // endchar
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 0);
        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.NotNull(outline.Path);
        Assert.Equal(500f, outline.AdvanceWidth); // falls back to defaultWidthX
    }

    [Fact]
    public void Evaluate_Cff2Font_IsCff2_IsTrue()
    {
        byte[] cs = new byte[] { 14 };
        var font = MakeCff2Font(new[] { cs }, blendRegionCount: 2);
        Assert.True(font.IsCff2);
        Assert.Equal(2, font.BlendRegionCount);
    }

    /// <summary>
    /// The path's verb sequence as initials ("MLLL" = a moveto and three lines). Charstring stack
    /// effects are only observable through the geometry the following operators produce, and with
    /// these fixtures the segment <i>count</i> is what distinguishes a correct blend from one that
    /// left its deltas behind.
    /// </summary>
    private static string Verbs(SKPath path)
    {
        var sb = new System.Text.StringBuilder();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done) sb.Append(verb.ToString()[0]);
        return sb.ToString();
    }
}
