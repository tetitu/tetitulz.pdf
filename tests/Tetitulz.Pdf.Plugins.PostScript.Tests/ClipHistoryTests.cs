// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>clipsave</c>/<c>cliprestore</c>, <c>initclip</c> and <c>gstate</c> objects (PLRM §8.2) over the
/// engine's clip history: PDF cannot widen a clip, so a widening closes the emitted levels the clips
/// were taken under and takes the rest of the wanted region again. <c>clipsave</c>/<c>cliprestore</c>
/// and the three <c>gstate</c> operators were recognised and reported, leaving the clip as it was;
/// <c>initclip</c> collapsed every <c>gsave</c> level, so a <c>grestore</c> after it restored nothing.
///
/// <para>Asserted by reading the stream as a reader does: <see cref="Paints"/> runs its <c>q</c>,
/// <c>Q</c>, <c>cm</c> and clips and answers the transform and the region each paint is drawn in.</para>
/// </summary>
public sealed class ClipHistoryTests
{
    private const string Big = " 0 0 100 100 rectfill";

    private static PostScriptConversionResult Convert(string program, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program), options);

    /// <summary>What a reader has in force at a paint: the transform, and the clipping region's device-space box (null for none).</summary>
    private sealed record Paint(double[] Ctm, double[] Region);

    /// <summary>Every paint of the stream, with the state a reader would draw it in.</summary>
    private static Paint[] Paints(PostScriptConversionResult result)
    {
        var paints = new List<Paint>();
        var saved = new Stack<(double[] Ctm, double[] Region)>();
        double[] ctm = { 1, 0, 0, 1, 0, 0 };
        double[] region = null;
        var points = new List<(double X, double Y)>();
        bool pendingClip = false;

        foreach (var op in ContentStreamAssert.Parse(result.Content))
        {
            double[] n = op.Operands.Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0).ToArray();
            switch (op.Operator)
            {
                case "q": saved.Push((ctm, region)); break;
                case "Q": (ctm, region) = saved.Pop(); break;
                case "cm": ctm = Multiply(n, ctm); break;
                case "m": case "l": points.Add(Apply(ctm, n[0], n[1])); break;
                case "c": points.Add(Apply(ctm, n[0], n[1])); points.Add(Apply(ctm, n[2], n[3])); points.Add(Apply(ctm, n[4], n[5])); break;
                case "re":
                    points.Add(Apply(ctm, n[0], n[1])); points.Add(Apply(ctm, n[0] + n[2], n[1]));
                    points.Add(Apply(ctm, n[0] + n[2], n[1] + n[3])); points.Add(Apply(ctm, n[0], n[1] + n[3]));
                    break;
                case "W": case "W*": pendingClip = true; break;
                case "n":
                    if (pendingClip) region = Intersect(region, Box(points));
                    pendingClip = false;
                    points.Clear();
                    break;
                case "f": case "f*": case "S": case "B": case "b":
                    paints.Add(new Paint(ctm, region));
                    points.Clear();
                    break;
            }
        }
        return paints.ToArray();
    }

    private static double[] Multiply(double[] m, double[] c) => new[]
    {
        m[0] * c[0] + m[1] * c[2], m[0] * c[1] + m[1] * c[3],
        m[2] * c[0] + m[3] * c[2], m[2] * c[1] + m[3] * c[3],
        m[4] * c[0] + m[5] * c[2] + c[4], m[4] * c[1] + m[5] * c[3] + c[5],
    };

    private static (double, double) Apply(double[] m, double x, double y) => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);

    private static double[] Box(List<(double X, double Y)> points) => new[]
        { points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y) };

    private static double[] Intersect(double[] a, double[] b) => a == null ? b
        : new[] { System.Math.Max(a[0], b[0]), System.Math.Max(a[1], b[1]), System.Math.Min(a[2], b[2]), System.Math.Min(a[3], b[3]) };

    private static void AssertRegion(double[] expected, Paint paint)
    {
        if (expected == null) { Assert.Null(paint.Region); return; }
        Assert.NotNull(paint.Region);
        // To a thousandth of a point: a region taken again under a written cm carries its six digits.
        Assert.Equal(expected, paint.Region.Select(v => System.Math.Round(v, 3)).ToArray());
    }

    private static readonly double[] Identity = { 1, 0, 0, 1, 0, 0 };

    // ------------------------------------------------------------------
    //  clipsave / cliprestore
    // ------------------------------------------------------------------

    /// <summary><c>cliprestore</c> widens back to the region <c>clipsave</c> saved: the clip before it kept, the one after it dropped.</summary>
    [Fact]
    public void Cliprestore_WidensBackToTheSavedRegion()
    {
        var r = Convert("0 0 50 50 rectclip clipsave 0 0 10 10 rectclip" + Big + " cliprestore" + Big);
        var paints = Paints(r);
        Assert.Equal(2, paints.Length);
        AssertRegion(new double[] { 0, 0, 10, 10 }, paints[0]);
        AssertRegion(new double[] { 0, 0, 50, 50 }, paints[1]);
        ContentStreamAssert.AssertBalancedSaveRestore(r.Content);
    }

    /// <summary>The stack pops: two saves restored in turn give each saved region back, the page's last.</summary>
    [Fact]
    public void Cliprestore_PopsTheStack()
    {
        var paints = Paints(Convert("clipsave 0 0 50 50 rectclip clipsave 0 0 10 10 rectclip cliprestore" + Big + " cliprestore" + Big));
        Assert.Equal(2, paints.Length);
        AssertRegion(new double[] { 0, 0, 50, 50 }, paints[0]);
        AssertRegion(null, paints[1]);
    }

    /// <summary>With the stack empty, <c>cliprestore</c> takes the region the innermost <c>gsave</c> saved, as the reference renderer does.</summary>
    [Fact]
    public void CliprestoreWithAnEmptyStack_TakesTheRegionTheGsaveSaved()
    {
        var paints = Paints(Convert("0 0 50 50 rectclip gsave 0 0 10 10 rectclip cliprestore" + Big + " grestore"));
        AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(paints));
    }

    /// <summary>
    /// The clipping path stack is part of the graphics state: a save made inside a <c>gsave</c> is gone
    /// after its <c>grestore</c>, so the <c>cliprestore</c> after it finds the stack empty, not the 50 square.
    /// </summary>
    [Fact]
    public void TheClipStack_IsPartOfTheGraphicsState()
    {
        var inside = Paints(Convert("gsave 0 0 50 50 rectclip clipsave grestore 0 0 10 10 rectclip cliprestore" + Big));
        AssertRegion(null, Assert.Single(inside));
        var outside = Paints(Convert("0 0 50 50 rectclip clipsave 0 0 10 10 rectclip cliprestore" + Big));
        AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(outside));
    }

    /// <summary><c>clippath</c> reads the restored region back.</summary>
    [Fact]
    public void Clippath_AfterCliprestore_AnswersTheRestoredRegion()
    {
        var r = Convert("0 0 50 50 rectclip clipsave 0 0 10 10 rectclip cliprestore" +
                        " clippath pathbbox 50 eq exch 50 eq and exch 0 eq and exch 0 eq and {" + Big + "} if");
        Assert.Single(Paints(r));
    }

    // ------------------------------------------------------------------
    //  initclip
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>initclip</c> inside a <c>gsave</c> keeps the level: the <c>grestore</c> after it puts back
    /// the transform. It collapsed every level, so the rectangle after the <c>grestore</c> was drawn twice its size.
    /// </summary>
    [Fact]
    public void InitclipInsideGsave_KeepsTheLevel_SoGrestorePutsBackTheTransform()
    {
        var r = Convert("gsave 2 2 scale 0 0 5 5 rectclip initclip grestore 0 0 10 10 rectfill");
        var paint = Assert.Single(Paints(r));
        Assert.Equal(Identity, paint.Ctm);
        AssertRegion(null, paint);
        Assert.Equal(new[] { 0.0, 0, 10, 10 }, r.BoundingBox);
    }

    /// <summary><c>initclip</c> leaves the transform as it is: the levels it closes took it with them, and it is written again.</summary>
    [Fact]
    public void Initclip_KeepsTheTransform()
    {
        var paint = Assert.Single(Paints(Convert("2 2 scale 0 0 5 5 rectclip initclip 0 0 10 10 rectfill")));
        Assert.Equal(new double[] { 2, 0, 0, 2, 0, 0 }, paint.Ctm);
        AssertRegion(null, paint);
    }

    /// <summary>A <c>grestore</c> to a level whose bracket a widening closed takes that level's region again.</summary>
    [Fact]
    public void GrestoreAfterAWidening_TakesTheLevelsRegionAgain()
    {
        var r = Convert("0 0 50 50 rectclip gsave 0 0 10 10 rectclip initclip" + Big + " grestore" + Big);
        var paints = Paints(r);
        Assert.Equal(2, paints.Length);
        AssertRegion(null, paints[0]);
        AssertRegion(new double[] { 0, 0, 50, 50 }, paints[1]);
        ContentStreamAssert.AssertBalancedSaveRestore(r.Content);
    }

    /// <summary>A region taken again keeps its place on the page under a transform that changed since it was taken.</summary>
    [Fact]
    public void ARegionTakenAgain_StaysWhereItWasTaken()
    {
        var paints = Paints(Convert("0 0 50 50 rectclip gsave 0 0 10 10 rectclip 3 3 scale initclip" + Big + " grestore" + Big));
        Assert.Equal(new double[] { 3, 0, 0, 3, 0, 0 }, paints[0].Ctm);
        AssertRegion(null, paints[0]);
        // The difference written as one cm, a third to the stream's six digits: within a millionth.
        Assert.Equal(Identity, paints[1].Ctm.Select(v => System.Math.Round(v, 5)).ToArray());
        AssertRegion(new double[] { 0, 0, 50, 50 }, paints[1]);
    }

    /// <summary>A region taken again can be widened again: it is taken under a bracket of its own.</summary>
    [Fact]
    public void ARegionTakenAgain_CanBeWidenedAgain()
    {
        var paints = Paints(Convert("0 0 50 50 rectclip gsave 0 0 10 10 rectclip initclip grestore initclip" + Big));
        AssertRegion(null, Assert.Single(paints));
    }

    /// <summary>A clip taken under a scale is taken again where it was on the page, not in the space in force.</summary>
    [Fact]
    public void AClipTakenUnderAScale_IsTakenAgainWhereItWas()
    {
        var paints = Paints(Convert("2 2 scale 0 0 25 25 rectclip gsave 0 0 5 5 rectclip initclip grestore" + Big));
        AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(paints));
    }

    /// <summary><c>grestoreall</c> and <c>restore</c> take the region of the level they return to again, as <c>grestore</c> does.</summary>
    [Theory]
    [InlineData("0 0 50 50 rectclip gsave gsave 0 0 10 10 rectclip initclip grestoreall")]
    [InlineData("0 0 50 50 rectclip save 0 0 10 10 rectclip initclip restore")]
    public void GrestoreallAndRestore_TakeTheLevelsRegionAgain(string program)
        => AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(Paints(Convert(program + Big))));

    /// <summary>
    /// Two regions of as many clips are different regions: <c>setgstate</c> from one to the other
    /// closes the first, and the second's own clip is in force.
    /// </summary>
    [Fact]
    public void ARegionOfAsManyClips_IsNotTheOneInForce()
    {
        var paints = Paints(Convert("0 0 50 50 rectclip /a gstate def initclip 0 0 10 10 rectclip a setgstate" + Big));
        AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(paints));
    }

    /// <summary><c>clippath</c> answers two clips' intersection, the region's box.</summary>
    [Fact]
    public void Clippath_AfterTwoClips_AnswersTheirIntersection()
    {
        var r = Convert("0 0 50 50 rectclip 20 20 50 50 rectclip" +
                        " clippath pathbbox 50 eq exch 50 eq and exch 20 eq and exch 20 eq and {" + Big + "} if");
        Assert.Single(Paints(r));
    }

    /// <summary>
    /// Under a host's base matrix the stream starts in that space: a transform written after a
    /// widening is the difference from it, so the paint keeps the scale the base gives it and no more.
    /// </summary>
    [Fact]
    public void UnderABaseMatrix_AWideningWritesNoExtraTransform()
    {
        var options = new PostScriptConversionOptions { BaseMatrix = new double[] { 2, 0, 0, 2, 0, 0 } };
        var paint = Assert.Single(Paints(Convert("0 0 5 5 rectclip initclip 0 0 10 10 rectfill", options)));
        Assert.Equal(Identity, paint.Ctm);
    }

    /// <summary>
    /// A <c>grestore</c> closes the levels its own level opened and no more: the fill after an inner
    /// <c>gsave</c>'s <c>grestore</c> is still inside the outer <c>gsave</c>'s bracket. (After a
    /// <c>save</c>, <c>grestore</c> pops nothing at all — FifthReviewRegressionTests.)
    /// </summary>
    [Fact]
    public void AGrestore_ClosesOnlyItsOwnLevels()
    {
        var ops = ContentStreamAssert.Operators(Convert("gsave gsave 0 0 1 1 rectfill grestore 0 0 2 2 rectfill grestore").Content);
        int lastFill = System.Array.LastIndexOf(ops, "f");
        Assert.Equal(1, ops.Take(lastFill).Count(o => o == "Q"));
        Assert.Equal(1, ops.Skip(lastFill).Count(o => o == "Q"));
    }

    /// <summary>A <c>gsave</c> inherits the clipping path stack: a save made before it is restored inside it.</summary>
    [Fact]
    public void AGsave_InheritsTheClipStack()
    {
        var paints = Paints(Convert("0 0 50 50 rectclip clipsave 0 0 10 10 rectclip gsave cliprestore" + Big + " grestore"));
        AssertRegion(new double[] { 0, 0, 50, 50 }, Assert.Single(paints));
    }

    /// <summary>A fragment's <c>showpage</c> resets the transform, one written at depth 0 included.</summary>
    [Fact]
    public void ShowpageInAFragment_UndoesATopLevelTransform()
    {
        var r = Convert("2 2 scale showpage 0 0 10 10 rectfill");
        Assert.Equal(Identity, Assert.Single(Paints(r)).Ctm);
    }

    /// <summary>
    /// A clip and a clip save hold memory, charged to the allocation budget: a loop taking clips ends
    /// on the budget's <c>limitcheck</c> long before the operation count would end it — so the fill
    /// after it still runs, which an exhausted operation count would not let it.
    /// </summary>
    [Theory]
    [InlineData("{ 0 0 10 10 rectclip } loop")]
    [InlineData("{ clipsave } loop")]
    public void HostileClipLoops_AreChargedToTheAllocationBudget(string loop)
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 200_000, MaxTotalOps = 2_000_000 };
        var r = Convert(loop + Big, options);
        Assert.Single(Paints(r));
        Assert.Contains("limitcheck", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  gstate objects
    // ------------------------------------------------------------------

    /// <summary><c>setgstate</c> puts back the saved transform and region, wider than the one in force.</summary>
    [Fact]
    public void Setgstate_PutsBackTheTransformAndTheRegion()
    {
        var r = Convert("0 0 50 50 rectclip /g gstate def 2 2 scale 0 0 5 5 rectclip g setgstate 0 0 10 10 rectfill");
        var paint = Assert.Single(Paints(r));
        Assert.Equal(Identity, paint.Ctm);
        AssertRegion(new double[] { 0, 0, 50, 50 }, paint);
        Assert.Equal(new[] { 0.0, 0, 10, 10 }, r.BoundingBox);
    }

    /// <summary><c>currentgstate</c> copies into the object it is given; the colour comes back with <c>setgstate</c>.</summary>
    [Fact]
    public void Currentgstate_FillsTheObject_AndSetgstatePutsItsColourBack()
    {
        var ops = ContentStreamAssert.Parse(Convert("/g gstate def 0 0 1 setrgbcolor g currentgstate pop 0 setgray g setgstate" + Big).Content)
                                     .Select(o => o.ToString()).ToArray();
        Assert.Contains("0 0 1 rg", ops);
        var control = ContentStreamAssert.Parse(Convert("/g gstate def 0 0 1 setrgbcolor 0 setgray g setgstate" + Big).Content)
                                         .Select(o => o.ToString()).ToArray();
        Assert.DoesNotContain("0 0 1 rg", control);
    }

    /// <summary>The path is part of the state: <c>setgstate</c> puts back the one <c>gstate</c> copied.</summary>
    [Fact]
    public void Setgstate_PutsBackThePath()
    {
        var paints = Paints(Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath /g gstate def newpath g setgstate fill"));
        Assert.Single(paints);
        Assert.Empty(Paints(Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath /g gstate def newpath fill")));
    }

    /// <summary>A graphics state object is of <c>gstatetype</c>.</summary>
    [Fact]
    public void AGstate_IsOfGstatetype()
        => Assert.Single(Paints(Convert("gstate type /gstatetype eq {" + Big + "} if")));

    /// <summary>Anything but a graphics state object is a <c>typecheck</c> for <c>setgstate</c> and <c>currentgstate</c>; a real one is the control.</summary>
    [Theory]
    [InlineData("4 setgstate", true)]
    [InlineData("4 currentgstate", true)]
    [InlineData("gstate setgstate", false)]
    [InlineData("gstate currentgstate pop", false)]
    public void ANonGstate_IsATypecheck(string body, bool refused)
    {
        var paints = Paints(Convert("{ " + body + " } stopped { $error /errorname get /typecheck eq {" + Big + "} if } if"));
        Assert.Equal(refused ? 1 : 0, paints.Length);
    }

    /// <summary>Whatever the widening closes and opens, every <c>q</c> has its <c>Q</c>.</summary>
    [Theory]
    [InlineData("0 0 50 50 rectclip gsave clipsave 0 0 10 10 rectclip gsave cliprestore grestore grestore" + Big)]
    [InlineData("/g gstate def gsave 0 0 10 10 rectclip gsave g setgstate grestore grestore" + Big)]
    [InlineData("save 0 0 10 10 rectclip initclip 0 0 20 20 rectclip restore" + Big)]
    public void Widenings_LeaveQAndBigQBalanced(string program)
        => ContentStreamAssert.AssertBalancedSaveRestore(Convert(program).Content);

    /// <summary>
    /// Taking a region again is work the program asked for with one operator: charged by its segments,
    /// so a loop that widens past two thousand clips runs out of budget long before its operator count
    /// would; the same loop over one clip is the control.
    /// </summary>
    [Theory]
    [InlineData(2000, true)]
    [InlineData(1, false)]
    public void HostileRegionRetaking_IsChargedByItsSegments(int clips, bool exhausted)
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };
        var r = Convert($"{clips} {{ 0 0 10 10 rectclip }} repeat 30 {{ gsave initclip grestore }} repeat", options);
        Assert.Equal(exhausted, r.BudgetExhausted);
    }
}
