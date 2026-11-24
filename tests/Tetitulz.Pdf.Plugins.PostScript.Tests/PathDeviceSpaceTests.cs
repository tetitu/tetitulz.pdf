// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The current path lives in <b>device</b> space (PLRM §4.4, §4.6): once a segment is appended, a
/// change to the CTM moves the pen's future positions but never the segment. Every program that
/// constructs a path under one transform and paints it under another depends on that — the Blue
/// Book's own ellipse procedure (<c>matrix currentmatrix … translate … scale … arc setmatrix
/// stroke</c>) is the canonical case, and <c>rectstroke</c>'s pen matrix is defined on the same
/// rule: the matrix is concatenated <i>after</i> the rectangle exists and affects the pen alone.
///
/// <para>This engine keeps the path in user space so it can re-emit it after a <c>cm</c>, which is
/// right only while the CTM stands still: left as it was when the CTM moved, the ellipse would come
/// out as a unit circle at the origin, a line built before <c>2 2 scale</c> twice as long, and a
/// <c>rectstroke</c> matrix would move the rectangle. The path is re-expressed in the new user space
/// whenever the CTM changes under it, so the emitted coordinates land where the program drew them.
/// Every assertion below reads the path's emitted operands, which are device coordinates once the
/// emitted <c>cm</c> operators are composed.</para>
/// </summary>
public sealed class PathDeviceSpaceTests
{
    private static PostScriptConversionResult Convert(string ps, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps), options);

    private static byte[] Content(string ps) => Convert(ps).Content;

    /// <summary>The operands of the <paramref name="index"/>th occurrence of an operator.</summary>
    private static double[] OperandsOf(byte[] content, string op, int index)
    {
        var found = ContentStreamAssert.Parse(content).Where(o => o.Operator == op).ElementAt(index);
        return found.Operands
                    .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
    }

    /// <summary>A line of 100 built at identity is still 100 device units long when stroked under a scale.</summary>
    [Fact]
    public void Scale_AfterConstruction_LeavesTheLineWhereItWasDrawn()
    {
        byte[] content = Content("newpath 0 0 moveto 100 0 lineto 2 2 scale stroke");

        // The scale is emitted, and the segment is re-expressed under it: 50 user units under a
        // 2x transform is the 100 device units the program drew.
        Assert.Equal(new[] { 2.0, 0.0, 0.0, 2.0, 0.0, 0.0 }, ContentStreamAssert.FirstOperandsOf(content, "cm"));
        double[] l = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(50.0, l[0], 6);
        Assert.Equal(0.0, l[1], 6);
    }

    /// <summary>
    /// The control: the same line built <i>after</i> the scale is 200 device units long, because
    /// then the scale is in force while the pen moves.
    /// </summary>
    [Fact]
    public void Scale_BeforeConstruction_StillScalesTheLine()
    {
        byte[] content = Content("2 2 scale newpath 0 0 moveto 100 0 lineto stroke");

        double[] l = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(100.0, l[0], 6);
    }

    /// <summary>
    /// The Blue Book ellipse: an arc drawn under <c>translate</c> and a non-uniform <c>scale</c>,
    /// the matrix restored, then stroked. The arc must sit at (100, 50) with radii 40 and 20 — not
    /// be a unit circle at the origin.
    /// </summary>
    [Fact]
    public void EllipseIdiom_KeepsTheArcWhereItWasDrawn()
    {
        byte[] content = Content(
            "/savematrix matrix currentmatrix def " +
            "100 50 translate 40 20 scale newpath 0 0 1 0 360 arc savematrix setmatrix stroke");

        // After setmatrix the CTM is identity again, so the emitted coordinates are device
        // coordinates: the arc starts at angle 0, which is (140, 50).
        double[] m = ContentStreamAssert.FirstOperandsOf(content, "m");
        Assert.Equal(140.0, m[0], 4);
        Assert.Equal(50.0, m[1], 4);

        // The control points move with the end points: the first quarter's first control point is
        // k = 4/3·tan(22.5°) of the radius up from the start, and the y radius is 20.
        double[] first = OperandsOf(content, "c", 0);
        Assert.Equal(140.0, first[0], 4);
        Assert.Equal(50.0 + 20.0 * 4.0 / 3.0 * Math.Tan(Math.PI / 8), first[1], 4);

        // The second quarter ends at 180 degrees: (60, 50). The end point is the last pair of a `c`.
        double[] c = OperandsOf(content, "c", 1);
        Assert.Equal(60.0, c[4], 4);
        Assert.Equal(50.0, c[5], 4);

        // The fourth quarter ends back at the start.
        double[] last = OperandsOf(content, "c", 3);
        Assert.Equal(140.0, last[4], 4);
        Assert.Equal(50.0, last[5], 4);

        ContentStreamAssert.AssertBalancedSaveRestore(content);
    }

    /// <summary>
    /// <c>rectstroke</c>'s matrix is concatenated after the rectangle is built (PLRM: it affects the
    /// pen alone), so the rectangle's corner stays at device (10, 20): under the emitted 2x
    /// <c>cm</c> that is user (5, 10).
    /// </summary>
    [Fact]
    public void RectStroke_PenMatrix_DoesNotMoveTheRectangle()
    {
        byte[] content = Content("10 20 100 50 [ 2 0 0 2 0 0 ] rectstroke");

        Assert.Equal(new[] { 2.0, 0.0, 0.0, 2.0, 0.0, 0.0 }, ContentStreamAssert.FirstOperandsOf(content, "cm"));

        double[] m = ContentStreamAssert.FirstOperandsOf(content, "m");
        Assert.Equal(5.0, m[0], 6);
        Assert.Equal(10.0, m[1], 6);

        // The far corner likewise: (110, 70) device is (55, 35) user.
        double[] l = OperandsOf(content, "l", 1);
        Assert.Equal(55.0, l[0], 6);
        Assert.Equal(35.0, l[1], 6);
    }

    /// <summary>
    /// <c>setmatrix</c> while a path is open: the path stays put. The delta is emitted as a
    /// <c>cm</c>, and the already-built segment is re-expressed under it.
    /// </summary>
    [Fact]
    public void SetMatrix_WithAnOpenPath_KeepsItsDevicePosition()
    {
        byte[] content = Content("newpath 10 10 moveto 30 10 lineto [ 2 0 0 2 100 100 ] setmatrix stroke");

        // Device (30, 10) under the new CTM [2 0 0 2 100 100] is user (-35, -45).
        double[] l = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(-35.0, l[0], 6);
        Assert.Equal(-45.0, l[1], 6);
    }

    /// <summary>
    /// <c>grestore</c> puts back the path <c>gsave</c> saved (PLRM §4.2: the path is part of the graphics
    /// state): one built inside the bracket under <c>2 2 scale</c> is gone, and the path before it —
    /// a point at (5, 5) — is current again, in the restored user space.
    /// </summary>
    [Fact]
    public void GRestore_PutsBackThePathGsaveSaved()
    {
        string[] ops = ContentStreamAssert.Parse(Content(
            "newpath 5 5 moveto gsave 2 2 scale newpath 0 0 moveto 10 10 lineto grestore 20 20 lineto stroke"))
            .Select(o => o.ToString()).ToArray();
        Assert.Contains("5 5 m", ops);
        Assert.Contains("20 20 l", ops);
        Assert.DoesNotContain("10 10 l", ops);
        Assert.DoesNotContain("0 0 m", ops);
    }

    /// <summary><c>restore</c> puts back the path its <c>save</c> saved, as <c>grestore</c> does.</summary>
    [Fact]
    public void Restore_PutsBackThePathSaveSaved()
    {
        string[] ops = ContentStreamAssert.Parse(Content(
            "newpath 5 5 moveto /s save def 2 2 scale newpath 0 0 moveto 10 10 lineto s restore 20 20 lineto stroke"))
            .Select(o => o.ToString()).ToArray();
        Assert.Contains("5 5 m", ops);
        Assert.Contains("20 20 l", ops);
        Assert.DoesNotContain("10 10 l", ops);
    }
    /// <summary>
    /// A rotation under a path: a quarter turn maps device (10, 0) to user (0, -10), so a rotated
    /// re-expression is exact and not an axis-aligned approximation.
    /// </summary>
    [Fact]
    public void Rotate_AfterConstruction_ReExpressesExactly()
    {
        byte[] content = Content("newpath 0 0 moveto 10 0 lineto 90 rotate stroke");

        double[] l = ContentStreamAssert.FirstOperandsOf(content, "l");
        Assert.Equal(0.0, l[0], 6);
        Assert.Equal(-10.0, l[1], 6);
    }

    /// <summary>
    /// Re-expressing the path is O(segments) per transform, so it is charged to the operation
    /// budget like <c>clip</c> and <c>pathbbox</c>: a loop that grows the path and moves the CTM on
    /// every iteration is quadratic work and must exhaust the budget rather than run until the
    /// segment cap. The control, which grows the path without moving the CTM, finishes inside the
    /// same budget.
    /// </summary>
    [Fact]
    public void TransformingAnOpenPath_IsChargedPerSegment()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 2_000_000 };

        var hostile = Convert("newpath 0 0 moveto { 1 1 rlineto 1.0001 1 scale } loop", options);
        Assert.True(hostile.BudgetExhausted, "the per-segment charge did not reach the budget");
        Assert.DoesNotContain("LimitCheck", hostile.LastError ?? "");

        var control = Convert("newpath 0 0 moveto /n 0 def " +
                              "{ /n n 1 add def n 2000 gt { exit } if 1 1 rlineto } loop stroke", options);
        Assert.False(control.BudgetExhausted);
        Assert.Equal(2000, ContentStreamAssert.Count(control.Content, "l"));
    }
}
