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
/// Both charstring evaluators fill a glyph outline with the <b>even-odd</b> rule, and that choice
/// is a deliberate deviation from the CFF specification's recommendation of non-zero winding.
/// </summary>
/// <remarks>
/// <para><b>Nothing pinned it, and mutation could not tell that from a clean sheet.</b> Flipping
/// both evaluators to <see cref="SKPathFillType.Winding"/> reddened no test in the suite. Two
/// readings fitted that survival: the outlines every fixture uses are correctly wound — outer
/// contour one way, holes the other — where the two rules <i>agree by construction</i>; or the
/// <c>SKPathBuilder</c> migration silently dropped <c>FillType</c> at <c>Snapshot()</c>. The first
/// reading was the true one, which is worse than a bug: the setting exists precisely for the
/// <b>badly wound</b> font its own comment names, and there was nothing badly wound to see it
/// with.</para>
///
/// <para><b>So the fixture is the defect.</b> A glyph whose inner contour winds the <i>same</i>
/// direction as its outer one is the only shape where the rules disagree: even-odd leaves the
/// inner region a hole, non-zero winding fills it solid. That is exactly the malformed font the
/// choice was made for, and it is now the thing under test.</para>
///
/// <para>The assertion is <see cref="SKPath.Contains"/> at a point inside both contours, which
/// consults the fill rule — not a comparison of <c>FillType</c> against itself, which would pin
/// the field while proving nothing about the fill.</para>
/// </remarks>
public sealed class GlyphFillRuleTests
{
    /// <summary>Outer and inner contour, both wound the same way — the ambiguous case.</summary>
    /// <remarks>
    /// The outer runs (0,0) → (600,0) → (600,600) → (0,600); the inner runs (200,200) →
    /// (400,200) → (400,400) → (200,400). Both go right, then up, then left, so the inner one is
    /// <b>not</b> reversed the way a well-formed font would write it. A point at the centre is
    /// enclosed twice: crossing count 2 (even-odd says outside), winding number 2 (non-zero says
    /// inside).
    /// </remarks>
    private const float InsideBothX = 300f;

    /// <inheritdoc cref="InsideBothX"/>
    private const float InsideBothY = 300f;

    /// <summary>A Type 2 charstring: two nested squares, both counter-clockwise.</summary>
    private static byte[] BadlyWoundType2Charstring() => new byte[]
    {
        // 0 0 rmoveto — no width operand, so the count is even and nothing is consumed as one.
        139, 139, 21,
        // 600 0 rlineto · 0 600 rlineto · -600 0 rlineto  (the contour closes implicitly)
        248, 236, 139, 5,
        139, 248, 236, 5,
        252, 236, 139, 5,
        // 200 -400 rmoveto — from (0,600) to (200,200), starting the inner contour
        247, 92, 252, 36, 21,
        // 200 0 rlineto · 0 200 rlineto · -200 0 rlineto — the same direction as the outer
        247, 92, 139, 5,
        139, 247, 92, 5,
        251, 92, 139, 5,
        14,  // endchar
    };

    /// <summary>The same two squares as a Type 1 charstring.</summary>
    /// <remarks>
    /// Type 1 numbers are a different encoding (32..246 for −107..107, 247..250 and 251..254 for
    /// the two-byte ranges), the width comes from an explicit <c>hsbw</c>, and contours are closed
    /// with <c>closepath</c> rather than implicitly. Written out rather than shared with the Type 2
    /// fixture because the two languages agree on nothing here but the shape.
    /// </remarks>
    private static byte[] BadlyWoundType1Charstring() => new byte[]
    {
        139, 248, 236, 13,          // 0 600 hsbw — left side bearing 0, width 600
        139, 139, 21,               // 0 0 rmoveto
        248, 236, 139, 5,           // 600 0 rlineto
        139, 248, 236, 5,           // 0 600 rlineto
        252, 236, 139, 5,           // -600 0 rlineto
        9,                          // closepath
        247, 92, 252, 36, 21,       // 200 -400 rmoveto
        247, 92, 139, 5,            // 200 0 rlineto
        139, 247, 92, 5,            // 0 200 rlineto
        251, 92, 139, 5,            // -200 0 rlineto
        9,                          // closepath
        14,                         // endchar
    };

    /// <summary>A CFF glyph's inner contour stays a hole even when it is wound the wrong way.</summary>
    [Fact]
    public void ACffGlyphWithABadlyWoundCounterKeepsItsHole()
    {
        var font = new CffFont(
            new[] { BadlyWoundType2Charstring() },
            System.Array.Empty<byte[]>(),
            System.Array.Empty<byte[]>(),
            new Dictionary<int, int>(),
            0f,
            0f);

        using SKPath path = Type2CharstringEvaluator.Evaluate(font, 0).Path;

        AssertTheCounterIsAHole(path);
    }

    /// <summary>And a Type 1 glyph does too — the two evaluators must not disagree.</summary>
    /// <remarks>
    /// They are separate implementations of the same decision, and a font reaches one or the other
    /// by which program the producer embedded, not by anything the document says. One evaluator
    /// left on winding would make the same glyph render differently depending on whether it
    /// arrived as <c>/FontFile</c> or <c>/FontFile3</c>.
    /// </remarks>
    [Fact]
    public void AType1GlyphWithABadlyWoundCounterKeepsItsHole()
    {
        var font = new Type1Font(
            new string[256],
            new Dictionary<string, byte[]> { ["badlywound"] = BadlyWoundType1Charstring() },
            System.Array.Empty<byte[]>(),
            1000f);

        using SKPath path = Type1CharstringEvaluator.Evaluate(font, "badlywound").Path;

        AssertTheCounterIsAHole(path);
    }

    /// <summary>
    /// The point at the centre is inside the outline's bounds and outside the filled region.
    /// </summary>
    /// <remarks>
    /// <b>The bounds check is the acceptance control.</b> Without it an evaluator that produced no
    /// path at all — a mangled fixture, a charstring the interpreter refused — would satisfy
    /// "the centre is not filled" perfectly, and the test would pass having drawn nothing.
    /// </remarks>
    private static void AssertTheCounterIsAHole(SKPath path)
    {
        Assert.False(path.IsEmpty, "the evaluator produced no outline, so the fixture proves nothing");

        SKRect bounds = path.Bounds;
        Assert.True(bounds.Contains(InsideBothX, InsideBothY),
            $"the centre is not even inside the outline's bounds ({bounds}); the fixture is wrong, "
            + "not the fill rule");

        Assert.True(path.Contains(InsideBothX - 250f, InsideBothY),
            "a point between the two contours is not filled, so the outer contour is missing");

        Assert.False(path.Contains(InsideBothX, InsideBothY),
            "the badly-wound counter was filled in, which is the non-zero winding rule: a "
            + "malformed font's counters disappear, which is the case even-odd is chosen for");
    }
}
