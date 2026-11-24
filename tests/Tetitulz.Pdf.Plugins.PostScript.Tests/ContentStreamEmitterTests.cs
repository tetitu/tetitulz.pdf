// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The PostScript-to-PDF translation, asserted on the emitted operator text and on concrete
/// numbers. "The stream is non-empty" is not an assertion — a stream that draws nothing is
/// non-empty too — so every case here names the operators it expects and their operands.
/// </summary>
public sealed class ContentStreamEmitterTests
{
    private static byte[] Convert(string postScript,
                                  PostScriptConversionOptions? options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript), options).Content;

    // ==================================================================
    //  Paths and painting
    // ==================================================================

    [Fact]
    public void Fill_AfterSetRgbColor_EmitsRgThenThePathThenF()
    {
        byte[] content = Convert("0 0 1 setrgbcolor newpath 10 20 moveto 110 20 lineto " +
                                 "110 120 lineto closepath fill");

        ContentStreamAssert.ContainsSequence(content,
            "0 0 1 rg",
            "10 20 m",
            "110 20 l",
            "110 120 l",
            "h",
            "f");
    }

    /// <summary>
    /// The stroke/fill colour split: PostScript has one colour, PDF has two. A stroke must emit the
    /// capital <c>G</c> form, never the lowercase fill form.
    /// </summary>
    [Fact]
    public void Stroke_AfterSetGray_EmitsCapitalGNotLowercaseG()
    {
        byte[] content = Convert("0.5 setgray newpath 0 0 moveto 100 100 lineto stroke");

        ContentStreamAssert.Contains(content, "0.5 G");
        ContentStreamAssert.DoesNotContainOperator(content, "g");
    }

    [Fact]
    public void Fill_AfterSetGray_EmitsLowercaseGNotCapital()
    {
        byte[] content = Convert("0.5 setgray newpath 0 0 moveto 10 0 lineto 10 10 lineto fill");

        ContentStreamAssert.Contains(content, "0.5 g");
        ContentStreamAssert.DoesNotContainOperator(content, "G");
    }

    [Fact]
    public void FillThenStroke_WithOneColor_EmitsBothRgAndRG()
    {
        byte[] content = Convert(
            "1 0 0 setrgbcolor newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill " +
            "newpath 0 0 moveto 10 10 lineto stroke");

        ContentStreamAssert.Contains(content, "1 0 0 rg");
        ContentStreamAssert.Contains(content, "1 0 0 RG");
    }

    /// <summary>The deferral: a second fill in the same colour must not re-emit the operator.</summary>
    [Fact]
    public void SecondFillWithTheSameColor_DoesNotReEmitRg()
    {
        byte[] content = Convert(
            "0 0 1 setrgbcolor " +
            "newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill " +
            "newpath 20 20 moveto 30 20 lineto 30 30 lineto closepath fill");

        Assert.Equal(1, ContentStreamAssert.Count(content, "rg"));
        Assert.Equal(2, ContentStreamAssert.Count(content, "f"));
    }

    [Fact]
    public void EoFill_EmitsFStarNotF()
    {
        byte[] content = Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath eofill");

        ContentStreamAssert.Contains(content, "f*");
        ContentStreamAssert.DoesNotContainOperator(content, "f");
    }

    [Fact]
    public void SetCmykColor_EmitsTheKOperatorNotAConvertedRg()
    {
        byte[] content = Convert("0 1 1 0 setcmykcolor newpath 0 0 moveto 10 0 lineto " +
                                 "10 10 lineto closepath fill");

        ContentStreamAssert.Contains(content, "0 1 1 0 k");
        ContentStreamAssert.DoesNotContainOperator(content, "rg");
    }

    [Fact]
    public void SetHsbColor_IsConvertedToRgb()
    {
        // hue 0, saturation 1, brightness 1 → pure red.
        byte[] content = Convert("0 1 1 sethsbcolor newpath 0 0 moveto 10 0 lineto " +
                                 "10 10 lineto closepath fill");

        ContentStreamAssert.Contains(content, "1 0 0 rg");
    }

    /// <summary>
    /// PostScript's <c>clip</c> leaves the path intact, so a following <c>fill</c> paints the same
    /// path — which in PDF means emitting it twice, once for <c>W n</c> and once for <c>f</c>.
    /// </summary>
    [Fact]
    public void Clip_ThenFill_EmitsThePathTwiceWithWnBetween()
    {
        byte[] content = Convert("newpath 0 0 moveto 100 0 lineto 100 100 lineto closepath " +
                                 "clip fill");

        var ops = ContentStreamAssert.Operators(content);

        Assert.Equal(2, ContentStreamAssert.Count(content, "m"));
        Assert.Contains("W", ops);
        Assert.Contains("n", ops);
        Assert.Contains("f", ops);

        // W must precede n, and both must precede the second path.
        int w = Array.IndexOf(ops, "W");
        int n = Array.IndexOf(ops, "n");
        Assert.True(w < n, "W must come before n.");
    }

    [Fact]
    public void EoClip_EmitsWStar()
        => ContentStreamAssert.Contains(
            Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath eoclip"), "W*");

    // ==================================================================
    //  Relative path operators
    // ==================================================================

    [Fact]
    public void RLineTo_IsResolvedAgainstTheCurrentPoint()
    {
        byte[] content = Convert("newpath 10 10 moveto 5 5 rlineto fill");

        ContentStreamAssert.ContainsSequence(content, "10 10 m", "15 15 l");
    }

    /// <summary>
    /// Every control point of <c>rcurveto</c> is relative to the <b>current point</b>, not to the
    /// preceding control point — the classic off-by-one in curve translation.
    /// </summary>
    [Fact]
    public void RCurveTo_ResolvesEveryControlPointAgainstTheCurrentPoint()
    {
        byte[] content = Convert("newpath 100 100 moveto 10 0 20 10 30 30 rcurveto fill");

        ContentStreamAssert.ContainsSequence(content, "100 100 m", "110 100 120 110 130 130 c");
    }

    // ==================================================================
    //  Transformations
    // ==================================================================

    [Fact]
    public void Translate_EmitsCmWithTranslationOnly()
        => ContentStreamAssert.Contains(Convert("10 20 translate"), "1 0 0 1 10 20 cm");

    /// <summary>
    /// The emitter never bakes the CTM into coordinates: after <c>2 2 scale</c>, a <c>moveto</c> at
    /// (10,10) is emitted as <c>10 10 m</c> under a <c>cm</c>, not as <c>20 20 m</c>. Baking would
    /// destroy line widths and dash phases, which PDF derives from the CTM.
    /// </summary>
    [Fact]
    public void Scale_ThenMoveTo_LeavesCoordinatesInUserSpace()
    {
        byte[] content = Convert("2 2 scale newpath 10 10 moveto 20 20 lineto fill");

        ContentStreamAssert.Contains(content, "2 0 0 2 0 0 cm");
        ContentStreamAssert.ContainsSequence(content, "10 10 m", "20 20 l");

        // The scaled coordinates must NOT appear: emitting them would mean the CTM had been baked
        // into the path, which destroys line widths and dash phases.
        Assert.Equal(new double[] { 10, 10 }, ContentStreamAssert.FirstOperandsOf(content, "m"));
        Assert.Equal(new double[] { 20, 20 }, ContentStreamAssert.FirstOperandsOf(content, "l"));
    }

    [Fact]
    public void Rotate_EmitsTheRotationMatrix()
    {
        byte[] content = Convert("90 rotate");
        double[] m = ContentStreamAssert.FirstOperandsOf(content, "cm");

        Assert.Equal(0, m[0], 5);
        Assert.Equal(1, m[1], 5);
        Assert.Equal(-1, m[2], 5);
        Assert.Equal(0, m[3], 5);
    }

    /// <summary>
    /// <c>setmatrix</c> is absolute where <c>cm</c> is relative, so the emitted delta is
    /// <c>inverse(current) × target</c>: after <c>2 2 scale</c>, returning to identity is a 0.5
    /// scale.
    /// </summary>
    [Fact]
    public void SetMatrix_EmitsTheInverseTimesTheTarget()
    {
        byte[] content = Convert("2 2 scale matrix setmatrix");

        ContentStreamAssert.ContainsSequence(content, "2 0 0 2 0 0 cm", "0.5 0 0 0.5 0 0 cm");
    }

    /// <summary>
    /// A singular matrix collapses everything after it to nothing in most readers, so it is refused
    /// and reported rather than emitted.
    /// </summary>
    [Fact]
    public void SetMatrix_WithASingularMatrix_IsRefusedAndReported()
    {
        byte[] content = Convert("[ 0 0 0 0 0 0 ] setmatrix");

        ContentStreamAssert.DoesNotContainOperator(content, "cm");
        Assert.Contains("singular", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void Concat_AppliesTheGivenMatrix()
        => ContentStreamAssert.Contains(Convert("[ 1 0 0 1 5 7 ] concat"), "1 0 0 1 5 7 cm");

    /// <summary>
    /// The transform family, asserted on the NUMBERS it pushes.
    ///
    /// <para>This was <c>Assert.Null(result.LastError)</c> against <c>2 2 scale 10 10 transform</c>
    /// — which a no-op passes, because a slot nothing writes to stays null. The operands are
    /// consumed by path operators here so the results land in the stream where they can be read.
    /// All four of transform / dtransform / itransform / idtransform were unverified.</para>
    /// </summary>
    [Fact]
    public void Transform_AppliesTheGivenMatrixToAPoint()
    {
        // [2 0 0 3 5 7] maps (10,10) to (2*10+5, 3*10+7) = (25,37); dtransform excludes the
        // translation, giving (20,30).
        byte[] content = Convert("newpath 10 10 [2 0 0 3 5 7] transform moveto " +
                                 "10 10 [2 0 0 3 5 7] dtransform lineto stroke");

        ContentStreamAssert.ContainsSequence(content, "25 37 m", "20 30 l");
    }

    /// <summary>The inverse pair must round-trip what the forward pair produced.</summary>
    [Fact]
    public void ITransform_InvertsTransform()
    {
        byte[] content = Convert("newpath 25 37 [2 0 0 3 5 7] itransform moveto " +
                                 "20 30 [2 0 0 3 5 7] idtransform lineto stroke");

        ContentStreamAssert.ContainsSequence(content, "10 10 m", "10 10 l");
    }

    /// <summary>With no matrix operand the CTM is used — the overload that is easy to get wrong.</summary>
    [Fact]
    public void Transform_WithNoMatrixOperand_UsesTheCurrentMatrix()
    {
        byte[] content = Convert("2 2 scale newpath 10 10 transform moveto 1 0 rlineto stroke");
        ContentStreamAssert.Contains(content, "20 20 m");
    }

    // ==================================================================
    //  Arcs
    // ==================================================================

    /// <summary>
    /// A quarter circle is one cubic whose control points sit at the standard kappa distance —
    /// concrete numbers that no stub can produce by accident.
    /// </summary>
    [Fact]
    public void Arc_QuarterCircle_EmitsOneCurveToWithKappaControlPoints()
    {
        byte[] content = Convert("newpath 0 0 100 0 90 arc fill");

        Assert.Equal(1, ContentStreamAssert.Count(content, "c"));

        double[] c = ContentStreamAssert.FirstOperandsOf(content, "c");
        Assert.Equal(100, c[0], 3);
        Assert.Equal(55.228, c[1], 2);
        Assert.Equal(55.228, c[2], 2);
        Assert.Equal(100, c[3], 3);
        Assert.Equal(0, c[4], 3);
        Assert.Equal(100, c[5], 3);
    }

    [Fact]
    public void Arc_StartsWithAMoveToWhenThePathIsEmpty()
    {
        byte[] content = Convert("newpath 0 0 100 0 90 arc fill");

        ContentStreamAssert.Contains(content, "100 0 m");
    }

    /// <summary>PLRM: with a current point, the arc is preceded by a line to its start.</summary>
    [Fact]
    public void Arc_WithACurrentPoint_EmitsALineToTheArcStartFirst()
    {
        byte[] content = Convert("newpath 500 500 moveto 0 0 100 0 90 arc fill");

        ContentStreamAssert.ContainsSequence(content, "500 500 m", "100 0 l");
    }

    [Fact]
    public void Arc_FullCircle_EmitsFourCurveTos()
        => Assert.Equal(4, ContentStreamAssert.Count(Convert("newpath 0 0 50 0 360 arc fill"), "c"));

    /// <summary>
    /// <c>arcn</c> sweeps clockwise, so <c>0 90 arcn</c> travels 270° the other way and needs three
    /// segments where <c>arc</c> needs one.
    /// </summary>
    [Fact]
    public void ArcN_SweepsClockwise()
    {
        Assert.Equal(3, ContentStreamAssert.Count(Convert("newpath 0 0 50 0 90 arcn fill"), "c"));
        Assert.Equal(1, ContentStreamAssert.Count(Convert("newpath 0 0 50 0 90 arc fill"), "c"));
    }

    // ==================================================================
    //  Graphics state
    // ==================================================================

    [Fact]
    public void GSave_And_GRestore_EmitBalancedQAndQ()
    {
        byte[] content = Convert("gsave 1 0 0 setrgbcolor grestore");

        ContentStreamAssert.AssertBalancedSaveRestore(content);
        Assert.Equal(1, ContentStreamAssert.Count(content, "q"));
        Assert.Equal(1, ContentStreamAssert.Count(content, "Q"));
    }

    /// <summary>An unmatched <c>grestore</c> is a PostScript no-op and must not emit a stray Q.</summary>
    [Fact]
    public void GRestore_WithNothingToRestore_EmitsNoQ()
    {
        byte[] content = Convert("grestore grestore");

        ContentStreamAssert.DoesNotContainOperator(content, "Q");
        ContentStreamAssert.AssertBalancedSaveRestore(content);
    }

    /// <summary>A program that ends mid-<c>gsave</c> still yields a balanced stream.</summary>
    [Fact]
    public void UnclosedGSave_IsClosedWhenTheStreamIsFinished()
    {
        byte[] content = Convert("gsave gsave gsave 1 0 0 setrgbcolor");

        ContentStreamAssert.AssertBalancedSaveRestore(content);
        Assert.Equal(3, ContentStreamAssert.Count(content, "q"));
        Assert.Equal(3, ContentStreamAssert.Count(content, "Q"));
    }

    /// <summary>
    /// <c>Q</c> restores the PDF colour as well as everything else, so the emitter's tracked shadow
    /// has to be restored with it.
    ///
    /// <para>Both directions matter and this pins both: painting the <i>same</i> colour after the
    /// <c>Q</c> emits nothing (the state genuinely reverted to it), while painting a
    /// <i>different</i> one must emit. A shadow that were not restored would get the first case
    /// wrong; one that were simply cleared would get the second right by accident and emit a
    /// redundant operator in the first.</para>
    /// </summary>
    [Fact]
    public void ColorTracking_FollowsTheRestoreOnBothPaths()
    {
        const string prefix =
            "1 0 0 setrgbcolor newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill " +
            "gsave 0 1 0 setrgbcolor newpath 0 0 moveto 5 0 lineto 5 5 lineto closepath fill " +
            "grestore ";
        const string paint = "newpath 20 20 moveto 30 20 lineto 30 30 lineto closepath fill";

        // Same colour as before the gsave: Q already restored it, so no third operator.
        byte[] same = Convert(prefix + paint);
        Assert.Equal(2, ContentStreamAssert.Count(same, "rg"));

        // A different colour after the restore must be emitted.
        byte[] changed = Convert(prefix + "0 0 1 setrgbcolor " + paint);
        Assert.Equal(3, ContentStreamAssert.Count(changed, "rg"));
        ContentStreamAssert.Contains(changed, "0 0 1 rg");
    }

    [Fact]
    public void Save_And_Restore_BracketTheGraphicsState()
    {
        byte[] content = Convert("save 1 0 0 setrgbcolor restore");

        ContentStreamAssert.AssertBalancedSaveRestore(content);
        Assert.Equal(1, ContentStreamAssert.Count(content, "q"));
    }

    /// <summary>
    /// <c>restore</c> rolls back VM (<c>VmRestoreTests</c>): the definition made since the save is
    /// gone, and no "rolled back the graphics state but not VM" limit is reported.
    /// </summary>
    [Fact]
    public void Restore_RollsBackVm_AndReportsNoLimit()
    {
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "save /x 1 def restore /x where { pop } { newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill } ifelse"));

        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "f"));
        Assert.Null(r.LastError);
    }

    // ==================================================================
    //  Line style
    // ==================================================================

    [Fact]
    public void SetLineWidth_IsEmittedBeforeTheStrokeThatUsesIt()
    {
        byte[] content = Convert("3 setlinewidth newpath 0 0 moveto 10 10 lineto stroke");

        var ops = ContentStreamAssert.Operators(content);
        Assert.Contains("w", ops);
        Assert.True(Array.IndexOf(ops, "w") < Array.IndexOf(ops, "S"),
            "the line width must be emitted before the stroke.");
        ContentStreamAssert.Contains(content, "3 w");
    }

    /// <summary>Line style is only emitted for a stroke — a fill has no use for it.</summary>
    [Fact]
    public void SetLineWidth_IsNotEmittedForAFillOnlyStream()
    {
        byte[] content = Convert("3 setlinewidth newpath 0 0 moveto 10 0 lineto " +
                                 "10 10 lineto closepath fill");

        ContentStreamAssert.DoesNotContainOperator(content, "w");
    }

    [Fact]
    public void SetDash_EmitsTheArrayAndPhase()
    {
        byte[] content = Convert("[ 3 2 ] 1 setdash newpath 0 0 moveto 10 10 lineto stroke");

        ContentStreamAssert.Contains(content, "[3 2] 1 d");
    }

    [Fact]
    public void SetLineCapAndJoin_MapDirectlyToPdf()
    {
        byte[] content = Convert("1 setlinecap 2 setlinejoin newpath 0 0 moveto 10 10 lineto stroke");

        ContentStreamAssert.Contains(content, "1 J");
        ContentStreamAssert.Contains(content, "2 j");
    }

    [Fact]
    public void SetDash_WithTooManyEntries_IsRefused()
    {
        var sb = new StringBuilder("[ ");
        for (int i = 0; i < PsLimits.MaxDashArrayEntries + 5; i++) sb.Append("1 ");
        sb.Append("] 0 setdash");

        Convert(sb.ToString());

        Assert.Contains("LimitCheck", PostScriptDiagnostics.LastError);
    }

    // ==================================================================
    //  showpage
    // ==================================================================

    /// <summary>
    /// <c>showpage</c> in a fragment must not paint: PLRM's implied <c>erasepage</c> would blank the
    /// host PDF page the XObject sits on.
    /// </summary>
    [Fact]
    public void ShowPage_InFragmentMode_EmitsNothingThatPaints()
    {
        byte[] content = Convert("1 0 0 setrgbcolor newpath 0 0 moveto 10 0 lineto " +
                                 "10 10 lineto closepath fill showpage");

        // The fill before it survives; showpage itself adds no painting operator.
        Assert.Equal(1, ContentStreamAssert.Count(content, "f"));
        ContentStreamAssert.DoesNotContainOperator(content, "re");
        Assert.Contains("erasepage", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void ShowPage_InFragmentMode_LetsEmissionContinue()
    {
        byte[] content = Convert("showpage 0 0 1 setrgbcolor newpath 0 0 moveto 10 0 lineto " +
                                 "10 10 lineto closepath fill");

        ContentStreamAssert.Contains(content, "0 0 1 rg");
        Assert.Equal(1, ContentStreamAssert.Count(content, "f"));
    }

    [Fact]
    public void ErasePage_IsIgnoredAndReported()
    {
        byte[] content = Convert("erasepage");

        ContentStreamAssert.DoesNotContainOperator(content, "f");
        Assert.Contains("erasepage", PostScriptDiagnostics.LastError);
    }

    // ==================================================================
    //  Adversarial
    // ==================================================================

    /// <summary>
    /// At the byte cap the emitter stops at a complete operator and closes every open <c>q</c>, so
    /// the truncated stream is still valid PDF rather than a half-written one.
    /// </summary>
    [Fact]
    public void ContentStreamByteCap_TruncatesWithBalancedSaveRestore()
    {
        var options = new PostScriptConversionOptions
        {
            MaxEmittedContentBytes = 2_000,
            MaxTotalOps = 2_000_000
        };

        var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
            "gsave 10000 { newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill } repeat"),
            options);

        Assert.True(result.ContentTruncated, "the emitter should have hit the byte cap.");

        // No slack. This read "+ 64" and that tolerance was hiding a real defect: CloseAllSaves
        // writes past the cap, by two bytes per open `q` and up to 4096 of them, and the host
        // rejects the whole stream at > MaxPluginContentStreamBytes. The cap is now an invariant,
        // so the assertion is exact.
        Assert.True(result.Content.Length <= options.MaxEmittedContentBytes,
            $"the stream is {result.Content.Length} bytes, past the {options.MaxEmittedContentBytes} cap.");
        ContentStreamAssert.AssertBalancedSaveRestore(result.Content);

        // The last thing emitted must be a whole operator, not a fragment.
        var ops = ContentStreamAssert.Operators(result.Content);
        Assert.NotEmpty(ops);
        Assert.Equal("Q", ops[^1]);
    }

    /// <summary>
    /// The bomb must not escape as a CLR exception <b>and</b> must actually be bounded: a cap on the
    /// emitted <c>q</c> alone would leave the engine cloning a graphics state per iteration.
    /// <c>GraphicsStateStackDepthTests</c> owns the full
    /// evidence; the emitted-depth bound is asserted here because it is the emitter's half of it.
    /// </summary>
    [Fact]
    public void GSaveBomb_IsBoundedByTheGraphicsStateDepthCap()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 500_000 };

        PostScriptConversionResult? result = null;
        var ex = Record.Exception(() =>
            result = PostScriptConversion.ConvertFragment(
                Encoding.ASCII.GetBytes("{ gsave } loop"), options));

        Assert.Null(ex);
        Assert.Equal(Tetitulz.Pdf.Core.Constants.PdfLimits.MaxGraphicsStateStackDepth - 1,
                     ContentStreamAssert.Count(result!.Content, "q"));
    }

    [Fact]
    public void ArcWithAbsurdSweep_IsBoundedByTheSegmentCap()
    {
        byte[] content = Convert("newpath 0 0 10 0 100000 arc fill");

        Assert.True(ContentStreamAssert.Count(content, "c") <= PsLimits.MaxArcSegments,
            "the arc emitted more segments than the cap allows.");
    }

    [Fact]
    public void MalformedPathOperatorsWithoutACurrentPoint_AreReportedNotThrown()
    {
        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Convert("newpath 10 10 lineto fill"));

        Assert.Null(ex);
        Assert.Contains("current point", PostScriptDiagnostics.LastError);
    }

    // ==================================================================
    //  Reproducibility
    // ==================================================================

    /// <summary>
    /// The same PostScript must produce byte-identical output every run, or no caller can diff two
    /// conversions and no test can assert on the result. This is why <c>rand</c> is seeded from a
    /// constant and <c>usertime</c> does not read the clock.
    /// </summary>
    [Fact]
    public void Conversion_IsByteReproducible()
    {
        const string source = "0.25 setgray newpath 0 0 moveto 100 0 lineto 100 100 lineto " +
                              "closepath fill 1 1 1 setrgbcolor 0 0 50 0 360 arc stroke";

        Assert.Equal(Convert(source), Convert(source));
    }

    // ==================================================================
    //  q/Q balance across the byte cap
    // ==================================================================

    /// <summary>
    /// A <c>gsave</c> reached <b>after</b> the byte cap tripped must not produce a lone <c>Q</c>.
    ///
    /// <para>The sibling test above puts the <c>gsave</c> <i>before</i> the cap, where the <c>q</c>
    /// is really in the stream and its <c>Q</c> has a partner. This is the other order: a <c>q</c>
    /// suppressed as over-cap while the save depth advanced anyway would make the end-of-stream close
    /// write a <c>Q</c> for a <c>q</c> that was never written. That matters well beyond this stream —
    /// the renderer runs it on the page's own
    /// graphics-state stack, so the surplus <c>Q</c> pops the calling page's state and every mark
    /// after the XObject inherits the wrong colour, transform and clip.</para>
    /// </summary>
    [Fact]
    public void ContentStreamByteCap_ThenAGSave_StillEmitsBalancedSaveRestore()
    {
        var options = new PostScriptConversionOptions { MaxEmittedContentBytes = 600 };

        var source = new StringBuilder();
        for (int i = 0; i < 400; i++)
            source.Append($"newpath {i} {i} moveto {i + 10} {i} lineto {i + 10} {i + 10} lineto " +
                          "closepath fill ");
        source.Append("gsave 0 0 moveto 1 1 lineto stroke");   // no matching grestore

        byte[] content = Convert(source.ToString(), options);

        ContentStreamAssert.AssertBalancedSaveRestore(content);
    }

    /// <summary>
    /// An image places itself inside its own <c>q</c>/<c>Q</c>, and that bracket has to be counted
    /// like any other — a <c>q</c> written straight to the buffer is one the truncation path cannot
    /// close, so a cap reached between the <c>q</c> and the <c>Q</c> would leave it open.
    /// </summary>
    [Fact]
    public void ByteCapReachedDuringAnImagePlacement_LeavesNoOpenSave()
    {
        // Sized so the cap trips part-way through the image's own q … cm … Do … Q bracket.
        for (int cap = 40; cap <= 220; cap += 4)
        {
            byte[] content = Convert(
                "8 8 8 [ 8 0 0 -8 0 8 ] <" + new string('A', 128) + "> image",
                new PostScriptConversionOptions { MaxEmittedContentBytes = cap });

            ContentStreamAssert.AssertBalancedSaveRestore(content);
        }
    }

    // ==================================================================
    //  Closing every save must roll the tracked state back with it
    // ==================================================================

    /// <summary>
    /// <c>initclip</c> unwinds every emitted <c>q</c>, and each of those <c>Q</c>s restores the real
    /// PDF colour. The emitter's own record of "what colour is set" has to follow, or the next paint
    /// is skipped as unchanged when it is not — and draws in the enclosing colour.
    ///
    /// <para>Asserted by counting: the program sets blue twice, either side of the
    /// <c>initclip</c>, and both fills must actually get it: a stale record would skip the second
    /// <c>0 0 1 rg</c>, and the square would come out red.</para>
    /// </summary>
    [Fact]
    public void InitClip_RollsBackTheTrackedColour_SoTheNextFillReEmitsIt()
    {
        byte[] content = Convert(
            "1 0 0 setrgbcolor newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill " +
            "gsave " +
            "  0 0 1 setrgbcolor newpath 20 20 moveto 30 20 lineto 30 30 lineto closepath fill " +
            "  newpath 0 0 moveto 50 0 lineto 50 50 lineto closepath clip " +
            "  initclip " +
            "  0 0 1 setrgbcolor newpath 60 60 moveto 70 60 lineto 70 70 lineto closepath fill");

        Assert.Equal(2, ContentStreamAssert.Parse(content).Count(o => o.ToString() == "0 0 1 rg"));
    }

    /// <summary>
    /// The same rule through <c>showpage</c> in fragment mode, which also closes every save: the
    /// grey set before it must be re-emitted afterwards rather than assumed to still be in force.
    /// </summary>
    [Fact]
    public void ShowPageInFragmentMode_RollsBackTheTrackedColour()
    {
        byte[] content = Convert(
            "gsave 0.5 setgray newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill " +
            "showpage " +
            "0.5 setgray newpath 20 20 moveto 30 20 lineto 30 30 lineto closepath fill");

        Assert.Equal(2, ContentStreamAssert.Parse(content).Count(o => o.ToString() == "0.5 g"));
    }
}
