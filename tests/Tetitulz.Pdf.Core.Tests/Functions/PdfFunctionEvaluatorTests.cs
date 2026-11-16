// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// Tests for the PostScript Type 4 calculator and other PDF function types.
/// These verify fixes from the PostScript learning session.
/// </summary>
public class PdfFunctionEvaluatorTests
{
    // ================================================================
    //  Type 2 — Exponential interpolation
    // ================================================================

    [Fact]
    public void Type2_LinearInterpolation_MidpointCorrect()
    {
        var fn = new PdfDictionary();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(2));
        fn.Set(new PdfName("N"), new PdfNumber(1)); // linear
        var domain = new PdfArray();
        domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(new PdfName("Domain"), domain);
        var c0 = new PdfArray(); c0.Add(new PdfNumber(0));
        var c1 = new PdfArray(); c1.Add(new PdfNumber(1));
        fn.Set(new PdfName("C0"), c0);
        fn.Set(new PdfName("C1"), c1);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });
        Assert.Equal(0.5f, result[0], 3);
    }

    [Fact]
    public void Type2_QuadraticInterpolation()
    {
        var fn = new PdfDictionary();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(2));
        fn.Set(new PdfName("N"), new PdfNumber(2)); // quadratic
        var domain = new PdfArray();
        domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(new PdfName("Domain"), domain);
        var c0 = new PdfArray(); c0.Add(new PdfNumber(0));
        var c1 = new PdfArray(); c1.Add(new PdfNumber(1));
        fn.Set(new PdfName("C0"), c0);
        fn.Set(new PdfName("C1"), c1);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });
        Assert.Equal(0.25f, result[0], 3); // 0.5^2 = 0.25
    }

    // ================================================================
    //  Type 4 — PostScript calculator: basic arithmetic
    // ================================================================

    [Fact]
    public void Type4_Add_CorrectResult()
    {
        var fn = BuildType4Function("{ 3 add }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 2f });
        Assert.Equal(5f, result[0], 3);
    }

    [Fact]
    public void Type4_Mul_CorrectResult()
    {
        var fn = BuildType4Function("{ 4 mul }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 3f });
        Assert.Equal(12f, result[0], 3);
    }

    [Fact]
    public void Type4_Sub_CorrectResult()
    {
        var fn = BuildType4Function("{ 3 sub }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 10f });
        Assert.Equal(7f, result[0], 3);
    }

    [Fact]
    public void Type4_Div_CorrectResult()
    {
        var fn = BuildType4Function("{ 2 div }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 10f });
        Assert.Equal(5f, result[0], 3);
    }

    [Fact]
    public void Type4_Neg_CorrectResult()
    {
        var fn = BuildType4Function("{ neg }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 5f });
        Assert.Equal(-5f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — PostScript calculator: if/ifelse (was broken!)
    // ================================================================

    [Fact]
    public void Type4_If_TrueCondition_ExecutesProc()
    {
        // PostScript: input dup 0.5 gt { 2 mul } if
        var fn = BuildType4Function("{ dup 0.5 gt { 2 mul } if }", 0, 1, -10, 10);
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.8f });
        Assert.Equal(1.6f, result[0], 2);
    }

    [Fact]
    public void Type4_If_FalseCondition_SkipsProc()
    {
        var fn = BuildType4Function("{ dup 0.5 gt { 2 mul } if }", 0, 1, -10, 10);
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.3f });
        Assert.Equal(0.3f, result[0], 2);
    }

    [Fact]
    public void Type4_IfElse_TrueBranch()
    {
        var fn = BuildType4Function("{ 0.5 gt { 1 } { 0 } ifelse }", 0, 1, -10, 10);
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.8f });
        Assert.Equal(1f, result[0], 3);
    }

    [Fact]
    public void Type4_IfElse_FalseBranch()
    {
        var fn = BuildType4Function("{ 0.5 gt { 1 } { 0 } ifelse }", 0, 1, -10, 10);
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.2f });
        Assert.Equal(0f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — PostScript calculator: cvi (convert to integer)
    // ================================================================

    [Fact]
    public void Type4_Cvi_TruncatesToInteger()
    {
        var fn = BuildType4Function("{ cvi }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 3.7f });
        Assert.Equal(3f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — PostScript calculator: stack ops
    // ================================================================

    [Fact]
    public void Type4_Dup_DuplicatesTop()
    {
        var fn = BuildType4Function("{ dup mul }"); // square
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 5f });
        Assert.Equal(25f, result[0], 3);
    }

    [Fact]
    public void Type4_Exch_SwapsTopTwo()
    {
        // Stack: push input(3), push 10 → [3, 10]. exch → [10, 3]. sub → 10-3 = 7.
        var fn = BuildType4Function("{ 10 exch sub }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 3f });
        Assert.Equal(7f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — PostScript calculator: trig functions (degrees!)
    // ================================================================

    [Fact]
    public void Type4_Sin_UsesDegrees()
    {
        // PostScript sin takes degrees, not radians!
        var fn = BuildType4Function("{ sin }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 90f });
        Assert.Equal(1f, result[0], 3);
    }

    [Fact]
    public void Type4_Cos_UsesDegrees()
    {
        var fn = BuildType4Function("{ cos }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        Assert.Equal(1f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — PostScript calculator: comparison
    // ================================================================

    [Fact]
    public void Type4_Eq_EqualValues_PushesTrue()
    {
        var fn = BuildType4Function("{ 5 eq }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 5f });
        Assert.Equal(1f, result[0], 3);
    }

    [Fact]
    public void Type4_Gt_GreaterValue_PushesTrue()
    {
        var fn = BuildType4Function("{ 3 gt }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 5f });
        Assert.Equal(1f, result[0], 3);
    }

    // ================================================================
    //  Type 4 — real-world CMYK→RGB conversion function
    // ================================================================

    [Fact]
    public void Type4_CmykToRgb_BlackProducesZeroRgb()
    {
        // Common real-world Type 4: CMYK→RGB
        // { 4 1 roll 1 exch sub 3 { 3 index add neg 1 add 0 max 4 1 roll } repeat pop pop pop }
        // Simplified version for testing:
        // C M Y K → R=1-C-K, G=1-M-K, B=1-Y-K clamped to [0,1]
        var fn = BuildType4FunctionMultiInput(
            "{ 4 -1 roll 1 exch sub 3 index sub dup 0 lt { pop 0 } if " +
            "5 1 roll 1 exch sub 2 index sub dup 0 lt { pop 0 } if " +
            "5 1 roll 1 exch sub 1 index sub dup 0 lt { pop 0 } if " +
            "4 1 roll pop }",
            4, 3);

        // Pure black (K=1): should give R=G=B=0
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f, 0f, 0f, 1f });
        Assert.True(result.Length >= 3);
        Assert.InRange(result[0], -0.01f, 0.01f); // R ≈ 0
    }

    // ================================================================
    //  Type 0 — sampled function, two inputs (§7.10.2 bilinear + /Decode)
    // ================================================================

    /// <summary>
    /// A two-input sampled function's bilinear interpolation, and the <c>/Decode</c> step that
    /// follows it.
    /// </summary>
    /// <remarks>
    /// <para><b>The two-input branch had no test at all.</b> The suite covered one input (through
    /// the raster colour tests) and three, four and nine (through the multilinear ones), and the
    /// <c>m == 2</c> branch in between — the one a function-based shading uses, because its inputs
    /// are x and y — was reached by nothing. That only became visible when the three copies of the
    /// <c>/Decode</c> step were merged: a mutation of the survivor has to redden a fixture from
    /// every branch that reaches it, and there was none to redden here.</para>
    ///
    /// <para>The samples are the four corners 0, 0, 0, 255 over a 2x2 grid, so the interpolated
    /// value at (x, y) is <c>x*y</c> — an identity the test can predict without re-deriving the
    /// interpolation. <c>/Decode [0 100]</c> is deliberately not the identity, so the decode step
    /// is visible in the answer rather than being a multiplication by one.</para>
    /// </remarks>
    [Theory]
    [InlineData(1f, 1f, 100f)]     // the 255 corner, decoded to the top of [0 100]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 0f, 0f)]       // a zero corner: decode maps 0 to /Decode's minimum, not to 100
    [InlineData(0.5f, 0.5f, 25f)]  // 0.5 * 0.5 = 0.25 of full scale
    public void Type0_TwoInputs_InterpolatesBilinearlyAndThenDecodes(float x, float y, float expected)
    {
        var fn = BuildType0Function(
            inputs: 2, sizePerAxis: 2, samples: new byte[] { 0, 0, 0, 255 }, decode: new float[] { 0, 100 });

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { x, y });

        Assert.Equal(expected, result[0], 3);
    }

    /// <summary>
    /// With no <c>/Decode</c>, Table 38 makes <c>/Range</c> the default — so the same fixture
    /// answers on <c>/Range</c>'s scale. Without this the test above would pass on an evaluator
    /// that ignored <c>/Decode</c> and read <c>/Range</c> directly, since the two are equal there.
    /// </summary>
    [Fact]
    public void Type0_TwoInputs_AbsentDecodeFallsBackToRange()
    {
        var fn = BuildType0Function(
            inputs: 2, sizePerAxis: 2, samples: new byte[] { 0, 0, 0, 255 }, decode: null);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 1f, 1f });

        Assert.Equal(100f, result[0], 3);
    }

    /// <summary>
    /// <b>A <c>/Decode</c> interval that does not start at zero.</b> Every sampled-function fixture
    /// in the tree used <c>[0 1]</c> or <c>[0 100]</c>, so the <c>dmin +</c> term of the map was
    /// multiplied by nothing and deleting it changed no answer anywhere — an equivalent mutation
    /// only because no fixture had ever moved the interval's floor.
    /// </summary>
    [Theory]
    [InlineData(1f, 1f, 20f)]
    [InlineData(0f, 0f, 10f)]   // sample 0 decodes to the interval's MINIMUM, not to zero
    [InlineData(0.5f, 0.5f, 12.5f)]
    public void Type0_DecodeWithANonZeroMinimum_ShiftsAsWellAsScales(float x, float y, float expected)
    {
        var fn = BuildType0Function(
            inputs: 2, sizePerAxis: 2, samples: new byte[] { 0, 0, 0, 255 }, decode: new float[] { 10, 20 },
            range: new float[] { 0, 100 });

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { x, y });

        Assert.Equal(expected, result[0], 3);
    }

    /// <summary>
    /// <b>Each output takes its own pair of <c>/Decode</c> entries</b> (§7.10.2: the array has
    /// <c>2 x n</c> numbers). Every fixture in the tree declared a single output, so an evaluator
    /// that read entry 0 and 1 for every output answered identically — the index arithmetic was
    /// unfalsifiable until a function had two outputs whose intervals differ.
    /// </summary>
    [Fact]
    public void Type0_EachOutputTakesItsOwnDecodeInterval()
    {
        // Two 8-bit outputs per grid point over a 2x2 grid: four points, eight bytes, and only the
        // (1,1) corner is non-zero, so evaluating there reads 255 for both outputs.
        var fn = BuildType0Function(
            inputs: 2, sizePerAxis: 2,
            samples: new byte[] { 0, 0, 0, 0, 0, 0, 255, 255 },
            decode: new float[] { 0, 100, 200, 300 },
            range: new float[] { 0, 100, 200, 300 });

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 1f, 1f });

        Assert.Equal(2, result.Length);
        Assert.Equal(100f, result[0], 3);
        Assert.Equal(300f, result[1], 3);
    }

    /// <summary>
    /// The three-or-more-input branch decodes too. Its own fixtures all declare <c>/Range [0 1]</c>
    /// and no <c>/Decode</c>, which makes the map the identity — so deleting the call from that
    /// branch alone changed none of their answers. A non-identity interval is what makes the call
    /// site falsifiable.
    /// </summary>
    [Fact]
    public void Type0_ThreeInputs_DecodesAfterMultilinearInterpolation()
    {
        // 2x2x2 grid, one output, only the (1,1,1) corner set.
        var fn = BuildType0Function(
            inputs: 3, sizePerAxis: 2,
            samples: new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 },
            decode: new float[] { 10, 20 },
            range: new float[] { 0, 100 });

        Assert.Equal(20f, PdfFunctionEvaluator.Evaluate(fn, new[] { 1f, 1f, 1f })[0], 3);
        Assert.Equal(10f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0f, 0f, 0f })[0], 3);
    }

    // ================================================================
    //  Helpers
    // ================================================================

    /// <summary>
    /// A Type 0 sampled function over <paramref name="inputs"/> axes of
    /// <paramref name="sizePerAxis"/> entries each, 8-bit samples, <paramref name="range"/> (or
    /// <c>[0 100]</c>), and <paramref name="decode"/> written only when it is non-null.
    /// </summary>
    private static PdfStream BuildType0Function(
        int inputs, int sizePerAxis, byte[] samples, float[] decode, float[] range = null)
    {
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(0));

        var domain = new PdfArray();
        var size = new PdfArray();
        for (int i = 0; i < inputs; i++)
        {
            domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
            size.Add(new PdfNumber(sizePerAxis));
        }
        fn.Set(new PdfName("Domain"), domain);
        fn.Set(new PdfName("Size"), size);

        var rangeArray = new PdfArray();
        foreach (float v in range ?? new float[] { 0, 100 }) rangeArray.Add(new PdfNumber(v));
        fn.Set(new PdfName("Range"), rangeArray);

        if (decode != null)
        {
            var decodeArray = new PdfArray();
            foreach (float v in decode) decodeArray.Add(new PdfNumber(v));
            fn.Set(new PdfName("Decode"), decodeArray);
        }

        fn.Set(new PdfName("BitsPerSample"), new PdfNumber(8));
        fn.RawData = samples;
        return fn;
    }

    private static PdfStream BuildType4Function(string psCode,
        float domainMin = -1000, float domainMax = 1000,
        float rangeMin = -1000, float rangeMax = 1000)
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes(psCode);
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(domainMin)); domain.Add(new PdfNumber(domainMax));
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        range.Add(new PdfNumber(rangeMin)); range.Add(new PdfNumber(rangeMax));
        fn.Set(new PdfName("Range"), range);
        fn.RawData = data;
        return fn;
    }

    private static PdfStream BuildType4FunctionMultiInput(string psCode, int inputs, int outputs)
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes(psCode);
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        for (int i = 0; i < inputs; i++) { domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1)); }
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        for (int i = 0; i < outputs; i++) { range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1)); }
        fn.Set(new PdfName("Range"), range);
        fn.RawData = data;
        return fn;
    }
}
