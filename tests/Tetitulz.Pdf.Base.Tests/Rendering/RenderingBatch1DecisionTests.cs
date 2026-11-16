// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decisions in the rendering helpers both renderers reach that the first Rendering sweep found
/// nothing asserting.
/// </summary>
public class RenderingBatch1DecisionTests
{
    /// <summary>
    /// A dictionary holding both a key and its inline-image abbreviation (§8.9.7 Table 91) answers
    /// with the full key; the abbreviation is the fallback, and the fallback after that is the
    /// caller's.
    /// </summary>
    [Fact]
    public void Int_TheFullKeyWinsOverItsAbbreviation()
    {
        var both = new PdfDictionary();
        both[PdfName.BitsPerComponent] = new PdfNumber(8);
        both[PdfName.BPC] = new PdfNumber(1);
        Assert.Equal(8, PdfResolve.Int(null, both, PdfName.BitsPerComponent, PdfName.BPC, 4));

        var abbreviated = new PdfDictionary();
        abbreviated[PdfName.BPC] = new PdfNumber(1);
        Assert.Equal(1, PdfResolve.Int(null, abbreviated, PdfName.BitsPerComponent, PdfName.BPC, 4));

        Assert.Equal(4, PdfResolve.Int(null, new PdfDictionary(), PdfName.BitsPerComponent, PdfName.BPC, 4));
    }

    /// <summary>
    /// A matrix whose determinant is below 1e-9 — not only an exactly singular one — has no usable
    /// inverse: a scale of a millionth inverts to a million, which turns a hit test or a pattern
    /// space into noise. An invertible matrix is the control.
    /// </summary>
    [Fact]
    public void Invert_ANearlySingularMatrixHasNone()
    {
        Assert.Null(PdfMatrix.Invert(new[] { 1e-6f, 0, 0, 1e-6f, 0, 0 }));
        Assert.Equal(new[] { 0.5f, 0, 0, 0.5f, -5f, -10f }, PdfMatrix.Invert(new[] { 2f, 0, 0, 2f, 10f, 20f }));
    }

    /// <summary>
    /// The caller's own floor: under 0 the same millionth-scale matrix inverts (the SVG converter's
    /// rule), and an exactly singular one still has no inverse under any floor.
    /// </summary>
    [Fact]
    public void Invert_UnderTheCallersFloor_TakesAnyNonZeroDeterminant()
    {
        float[] inverse = Assert.IsType<float[]>(PdfMatrix.Invert(new[] { 1e-6f, 0, 0, 1e-6f, 0, 0 }, 0f));
        Assert.Equal(1e6f, inverse[0], 1f);
        Assert.Equal(1e6f, inverse[3], 1f);
        Assert.Null(PdfMatrix.Invert(new[] { 1f, 2f, 2f, 4f, 0, 0 }, 0f));
        Assert.Null(PdfMatrix.Invert(new[] { 0f, 0, 0, 0, 1, 1 }, 0f));
    }

    /// <summary>
    /// A transformed rectangle's bounds take every corner: under <c>[1 1 -1 1]</c> the corners of
    /// (0,0)–(10,20) land at x = 0, 10, −10, −20, so the leftmost is the fourth alone.
    /// </summary>
    [Fact]
    public void TransformedBounds_TakesEveryCorner()
    {
        PdfMatrix.TransformedBounds(new[] { 1f, 1, -1, 1, 0, 0 }, 0, 0, 10, 20,
            out float minX, out float minY, out float maxX, out float maxY);
        Assert.Equal((-20f, 0f, 10f, 30f), (minX, minY, maxX, maxY));
    }

    /// <summary>No matrix is not a finite one: a caller guarding a <c>cm</c> must not apply nothing as identity.</summary>
    [Fact]
    public void AllFinite_NoMatrixIsNotFinite()
    {
        Assert.False(MatrixGuard.AllFinite(null));
        Assert.True(MatrixGuard.AllFinite(new[] { 1f, 0, 0, 1, 0, 0 }));
    }

    /// <summary>
    /// A non-finite advance becomes half an em of the font size — or half a point when the size
    /// itself is unusable (zero, negative or non-finite); a finite advance is kept.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 12f, 6f)]
    [InlineData(float.PositiveInfinity, -12f, 0.5f)]
    [InlineData(float.NaN, float.NaN, 0.5f)]
    [InlineData(7f, 12f, 7f)]
    public void SanitizeAdvance(float advance, float size, float expected)
        => Assert.Equal(expected, MatrixGuard.SanitizeAdvance(advance, size));

    /// <summary>
    /// <c>[] 5 d</c> turns dashing off (§8.4.3.6), and the phase goes with it — kept, it would offset
    /// whatever pattern a later <c>d</c> sets. A real pattern keeps its phase.
    /// </summary>
    [Theory]
    [InlineData("[] 5 d", false, 0f)]
    [InlineData("[3 2] 5 d", true, 5f)]
    public void DashPattern_AnEmptyArrayDropsThePhase(string content, bool dashed, float phase)
    {
        var op = Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(System.Text.Encoding.ASCII.GetBytes(content))[0];
        Assert.True(DashPattern.TryRead(op, out float[] pattern, out float readPhase));
        Assert.Equal(dashed, pattern != null);
        Assert.Equal(phase, readPhase);
    }

    /// <summary>
    /// An ExtGState <c>/D</c> is <c>[array phase]</c> (Table 57); one without its phase is not read,
    /// and a complete one is.
    /// </summary>
    [Fact]
    public void DashPattern_AnExtGStateDashNeedsItsPhase()
    {
        PdfDictionary ExtGs(params PdfObject[] d)
        {
            var entry = new PdfArray();
            foreach (var o in d) entry.Add(o);
            var gs = new PdfDictionary();
            gs[PdfName.D] = entry;
            return gs;
        }
        PdfArray Dashes() { var a = new PdfArray(); a.Add(new PdfNumber(3)); a.Add(new PdfNumber(2)); return a; }

        Assert.False(DashPattern.TryReadExtGState(null, ExtGs(Dashes()), out _, out _));
        Assert.True(DashPattern.TryReadExtGState(null, ExtGs(Dashes(), new PdfNumber(4)), out float[] pattern, out float phase));
        Assert.Equal(new[] { 3f, 2f }, pattern);
        Assert.Equal(4f, phase);
    }

    /// <summary>
    /// A negative phase is brought into one period of the pattern; over a pattern whose period is
    /// zero there is no period to bring it into, and it is 0 — not the NaN <c>% 0</c> makes.
    /// </summary>
    [Theory]
    [InlineData(-1f, new[] { 3f, 2f }, 4f)]
    [InlineData(-3f, new[] { 0f, 0f }, 0f)]
    [InlineData(2f, new[] { 3f, 2f }, 2f)]
    public void DashPattern_NormalisePhase(float phase, float[] pattern, float expected)
        => Assert.Equal(expected, DashPattern.NormalisePhase(phase, pattern));

    /// <summary>
    /// Scaled for the device, a negative dash length is no length — a negative interval is not one
    /// Skia or SVG accepts — and an odd-length pattern is repeated once to make its on/off pairs.
    /// </summary>
    [Fact]
    public void DashPattern_ScaledLengthsAreNeverNegative()
    {
        Assert.Equal(new[] { 0f, 6f }, DashPattern.ToEvenLengthScaled(new[] { -2f, 3f }, 2f));
        Assert.Equal(new[] { 1f, 2f, 3f, 1f, 2f, 3f }, DashPattern.ToEvenLengthScaled(new[] { 1f, 2f, 3f }, 1f));
    }

    /// <summary>
    /// <c>/QuadPoints</c> shorter than one quadrilateral (eight numbers) is not present — the caller
    /// falls back to <c>/Rect</c> — while eight numbers are one quad.
    /// </summary>
    [Theory]
    [InlineData(6, false, 0)]
    [InlineData(8, true, 1)]
    public void QuadPoints_ShorterThanOneQuadIsAbsent(int count, bool present, int quads)
    {
        var points = new PdfArray();
        for (int i = 0; i < count; i++) points.Add(new PdfNumber(i * 10));
        var annot = new PdfDictionary();
        annot[PdfName.QuadPoints] = points;

        var read = AnnotationQuadPoints.Read(null, annot, out bool isPresent);
        Assert.Equal((present, quads), (isPresent, read.Count));
    }

    /// <summary>
    /// A quad with any corner past the coordinate ceiling — the sentinel some producers write for
    /// "no quad" — or not a number is skipped, whichever corner it is; a sound quad beside it is kept.
    /// </summary>
    [Theory]
    [InlineData(4, 1e30)]
    [InlineData(6, 1e30)]
    [InlineData(5, double.NaN)]
    public void QuadPoints_ASentinelCornerSkipsItsQuad(int index, double value)
    {
        var points = new PdfArray();
        for (int i = 0; i < 16; i++) points.Add(new PdfNumber(i == index ? value : i * 10));
        var annot = new PdfDictionary();
        annot[PdfName.QuadPoints] = points;

        var read = AnnotationQuadPoints.Read(null, annot, out bool present);
        Assert.True(present);
        var kept = Assert.Single(read);
        Assert.Equal(80f, kept.X0);
    }

    /// <summary>
    /// A form budget's refusal names the ceiling it refused at — its own, not the process-wide
    /// default it used to quote whatever it had been given.
    /// </summary>
    [Fact]
    public void RenderBudget_TheRefusalNamesItsOwnCeiling()
    {
        var budget = new RenderBudget(3);
        Assert.Contains("exceeded 3 form-XObject invocations", budget.RefusalMessage);
    }

    /// <summary>
    /// Both renderers clear the transfer function on <c>/Identity</c> and on <c>/Default</c> (§10.5,
    /// Table 58: "the device's own"), through this one test; any other name is not an identity.
    /// </summary>
    [Theory]
    [InlineData("Identity", true)]
    [InlineData("Default", true)]
    [InlineData("Other", false)]
    public void TransferFunction_IdentityNames(string name, bool identity)
        => Assert.Equal(identity, TransferFunctionLut.IsIdentityName(new PdfName(name)));

    /// <summary>A Type 2 function from <paramref name="c0"/> at 0 to <paramref name="c1"/> at 1, linear.</summary>
    private static PdfDictionary Linear(double c0, double c1)
    {
        PdfArray One(double v) { var a = new PdfArray(); a.Add(new PdfNumber(v)); return a; }
        var domain = new PdfArray(); domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        var fn = new PdfDictionary();
        fn[PdfName.FunctionType] = new PdfNumber(2);
        fn[PdfName.Domain] = domain;
        fn[PdfName.C0] = One(c0);
        fn[PdfName.C1] = One(c1);
        fn[PdfName.N] = new PdfNumber(1);
        return fn;
    }

    /// <summary>
    /// An array <c>/TR</c> gives each channel its own function, in order: here red is the identity
    /// and green inverts, so green's table is green's.
    /// </summary>
    [Fact]
    public void TransferFunction_AnArrayGivesEachChannelItsOwn()
    {
        var tr = new PdfArray();
        tr.Add(Linear(0, 1));
        tr.Add(Linear(1, 0));
        tr.Add(Linear(0, 1));
        byte[][] lut = TransferFunctionLut.Build(tr, null);
        Assert.Equal((0, 255, 0), ((int)lut[0][0], (int)lut[1][0], (int)lut[2][0]));
        Assert.Equal((255, 0, 255), ((int)lut[0][255], (int)lut[1][255], (int)lut[2][255]));
    }

    /// <summary>
    /// An array <c>/TR</c> of identity names transfers nothing: the answer is no table at all, so a
    /// renderer skips the pass, rather than a table of three empty channels.
    /// </summary>
    [Fact]
    public void TransferFunction_AnArrayOfIdentitiesIsNoTable()
    {
        var tr = new PdfArray();
        for (int i = 0; i < 3; i++) tr.Add(PdfName.Identity);
        Assert.Null(TransferFunctionLut.Build(tr, null));
    }

    /// <summary>
    /// A function that does not evaluate — an unknown type — leaves its channel as it was rather than
    /// painting it black: the evaluator answers one with zeros, so the type is checked first.
    /// </summary>
    [Fact]
    public void TransferFunction_AFunctionThatDoesNotEvaluateIsTheIdentity()
    {
        var broken = new PdfDictionary();
        broken[PdfName.FunctionType] = new PdfNumber(99);
        Assert.Null(TransferFunctionLut.BuildChannel(broken, null));

        // Through the whole table: the broken red channel is left alone, green still transfers.
        var tr = new PdfArray();
        tr.Add(broken);
        tr.Add(Linear(1, 0));
        tr.Add(PdfName.Identity);
        byte[][] lut = TransferFunctionLut.Build(tr, null);
        Assert.Null(lut[0]);
        Assert.Equal(255, (int)lut[1][0]);
        Assert.Equal(new SkiaSharp.SKColor(10, 245, 30), TransferFunctionLut.Apply(lut, new SkiaSharp.SKColor(10, 10, 30)));
    }

    /// <summary>
    /// A Type 4 function with no <c>/Range</c> whose program leaves nothing on the stack evaluates to
    /// no output at all; its channel is left as it was, value for value.
    /// </summary>
    [Fact]
    public void TransferFunction_AFunctionWithNoOutputIsTheIdentity()
    {
        var domain = new PdfArray(); domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        var program = new PdfStream { RawData = System.Text.Encoding.ASCII.GetBytes("{ pop }") };
        program[PdfName.FunctionType] = new PdfNumber(4);
        program[PdfName.Domain] = domain;

        byte[] lut = TransferFunctionLut.BuildChannel(program, null);
        Assert.Equal((0, 128, 255), ((int)lut[0], (int)lut[128], (int)lut[255]));
    }

    /// <summary>An index outside the array, either side, reads as 0 — a short array is not an error.</summary>
    [Theory]
    [InlineData(-1, 0.0)]
    [InlineData(1, 2.5)]
    [InlineData(2, 0.0)]
    public void ArrayDouble_OutsideTheArrayIsZero(int index, double expected)
    {
        var arr = new PdfArray();
        arr.Add(new PdfNumber(1));
        arr.Add(new PdfNumber(2.5));
        Assert.Equal(expected, PdfResolve.ArrayDouble(null, arr, index));
    }
}
