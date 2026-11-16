// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// <see cref="PdfFunctionEvaluator"/> memoises every loop-invariant key of a function dictionary
/// (per dictionary instance, in a <c>ConditionalWeakTable</c>) and evaluates through per-depth
/// scratch buffers instead of allocating 3-5 arrays per call. Both are invisible to a single
/// evaluation, so these tests exercise what only *repeated* and *nested* evaluation can break.
///
/// <para>Every assertion here is on a value the function must produce, never on "it returned
/// something": a memo that hands back stale or garbage parameters is perfectly self-consistent, so
/// a stability-only test would pass with the parsing deleted.</para>
/// </summary>
public class FunctionEvaluatorMemoTests
{
    // ================================================================
    //  Fixtures
    // ================================================================

    private static PdfArray Nums(params double[] v)
    {
        var a = new PdfArray();
        foreach (double d in v) a.Add(new PdfNumber(d));
        return a;
    }

    /// <summary>1-in / 3-out sampled function over two samples: (0, 128, 255) then (255, 0, 64).</summary>
    private static PdfStream Type0TwoSample()
    {
        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(0));
        fn.Set(PdfName.Domain, Nums(0, 1));
        fn.Set(PdfName.Range, Nums(0, 1, 0, 1, 0, 1));
        fn.Set(PdfName.Size, Nums(2));
        fn.Set(PdfName.BitsPerSample, new PdfNumber(8));
        fn.RawData = new byte[] { 0, 128, 255, 255, 0, 64 };
        return fn;
    }

    /// <summary>Exponential function: C0 -&gt; C1 with the given exponent.</summary>
    private static PdfDictionary Type2(double[] c0, double[] c1, double n = 1, PdfArray domain = null)
    {
        var fn = new PdfDictionary();
        fn.Set(PdfName.FunctionType, new PdfNumber(2));
        fn.Set(PdfName.Domain, domain ?? Nums(0, 1));
        fn.Set(PdfName.N, new PdfNumber(n));
        fn.Set(PdfName.C0, Nums(c0));
        fn.Set(PdfName.C1, Nums(c1));
        return fn;
    }

    /// <summary>Stitching function over the supplied sub-functions, split evenly by /Bounds.</summary>
    private static PdfDictionary Type3(PdfObject[] subs, double[] bounds, double[] encode)
    {
        var fn = new PdfDictionary();
        fn.Set(PdfName.FunctionType, new PdfNumber(3));
        fn.Set(PdfName.Domain, Nums(0, 1));
        var arr = new PdfArray();
        foreach (var s in subs) arr.Add(s);
        fn.Set(PdfName.Functions, arr);
        fn.Set(PdfName.Bounds, Nums(bounds));
        fn.Set(PdfName.Encode, Nums(encode));
        return fn;
    }

    private static PdfStream Type4(string program, PdfArray range = null)
    {
        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(4));
        fn.Set(PdfName.Domain, Nums(0, 1));
        fn.Set(PdfName.Range, range ?? Nums(0, 1));
        fn.RawData = Encoding.ASCII.GetBytes(program);
        return fn;
    }

    // ================================================================
    //  The memo must not change what a function evaluates to
    // ================================================================

    [Fact]
    public void Type0RepeatedEvaluationKeepsProducingTheSampledValues()
    {
        var fn = Type0TwoSample();

        // Sample 0 is (0, 128, 255)/255, sample 1 is (255, 0, 64)/255; /Size 2 so t interpolates
        // linearly between them. These are the values, not merely "the same values as last time".
        AssertSampled(fn, 0f, 0f, 128 / 255f, 1f);
        AssertSampled(fn, 1f, 1f, 0f, 64 / 255f);
        AssertSampled(fn, 0.5f, 0.5f, 64 / 255f, (255 + 64) / 2f / 255f);

        // Drive it the way a Type 1 shading grid does, then re-check every anchor.
        for (int i = 0; i <= 1000; i++) PdfFunctionEvaluator.Evaluate(fn, new[] { i / 1000f });

        AssertSampled(fn, 0f, 0f, 128 / 255f, 1f);
        AssertSampled(fn, 1f, 1f, 0f, 64 / 255f);
        AssertSampled(fn, 0.5f, 0.5f, 64 / 255f, (255 + 64) / 2f / 255f);
    }

    private static void AssertSampled(PdfDictionary fn, float t, float r, float g, float b)
    {
        float[] o = PdfFunctionEvaluator.Evaluate(fn, new[] { t });
        Assert.Equal(3, o.Length);
        Assert.Equal(r, o[0], 4);
        Assert.Equal(g, o[1], 4);
        Assert.Equal(b, o[2], 4);
    }

    [Fact]
    public void Type2RepeatedEvaluationKeepsInterpolatingBetweenC0AndC1()
    {
        var fn = Type2(new double[] { 0, 0, 0 }, new double[] { 1, 0.5, 0.25 });

        for (int pass = 0; pass < 3; pass++)
        {
            float[] o = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });
            Assert.Equal(0.5f, o[0], 5);
            Assert.Equal(0.25f, o[1], 5);
            Assert.Equal(0.125f, o[2], 5);
            for (int i = 0; i < 200; i++) PdfFunctionEvaluator.Evaluate(fn, new[] { i / 199f });
        }
    }

    [Fact]
    public void Type3RepeatedEvaluationKeepsSelectingTheSubFunctionItsBoundsName()
    {
        // Below 0.5 the first sub-function runs (0 -> 1 on its own re-encoded input), above it the
        // second (1 -> 0). /Encode [0 1 0 1] maps each half onto the full sub-domain.
        var fn = Type3(
            new PdfObject[] { Type2(new double[] { 0 }, new double[] { 1 }), Type2(new double[] { 1 }, new double[] { 0 }) },
            new double[] { 0.5 }, new double[] { 0, 1, 0, 1 });

        for (int pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0.0f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.00f })[0], 4);
            Assert.Equal(0.5f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.25f })[0], 4);
            Assert.Equal(1.0f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.50f })[0], 4);
            Assert.Equal(0.5f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.75f })[0], 4);
            for (int i = 0; i < 200; i++) PdfFunctionEvaluator.Evaluate(fn, new[] { i / 199f });
        }
    }

    [Fact]
    public void Type4RepeatedEvaluationKeepsRunningTheProgramFromAnEmptyStack()
    {
        // The evaluation stack and the procedure-token cache are now rented from a per-thread slot
        // and returned in a finally. A stack that came back with the previous call's residue would
        // push this program's output off the top.
        var fn = Type4("{ 2 mul }");
        for (int i = 0; i < 500; i++)
        {
            float[] o = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.25f });
            Assert.Single(o);
            Assert.Equal(0.5f, o[0], 5);
        }
    }

    // ================================================================
    //  The memo must not be shared between two different dictionaries
    // ================================================================

    [Fact]
    public void TwoFunctionsDifferingOnlyInDomainDoNotShareAMemoEntry()
    {
        // Identical but for /Domain: the second clamps its input to 0.5 before the exponential runs.
        var wide   = Type2(new double[] { 0 }, new double[] { 1 }, 1, Nums(0, 1));
        var narrow = Type2(new double[] { 0 }, new double[] { 1 }, 1, Nums(0, 0.5));

        // Interleaved, because a memo keyed on anything the two share (a hash of their contents,
        // say, or a single static slot) would only be caught by alternating between them.
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0.75f, PdfFunctionEvaluator.Evaluate(wide,   new[] { 0.75f })[0], 5);
            Assert.Equal(0.50f, PdfFunctionEvaluator.Evaluate(narrow, new[] { 0.75f })[0], 5);
        }
    }

    [Fact]
    public void TheSameFunctionReadWithAndWithoutAResolverKeepsBothAnswers()
    {
        // /Domain is an INDIRECT reference. Without a resolver it reads as absent, so nothing is
        // clamped; with one it resolves to [0 0.5] and clamps. The memo records which resolver it
        // was parsed under precisely so these two cannot serve each other's answer.
        var fn = Type2(new double[] { 0 }, new double[] { 1 });
        fn.Set(PdfName.Domain, new PdfReference(9, 0));
        Func<PdfObject, PdfObject> resolve = o => o is PdfReference ? Nums(0, 0.5) : o;

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(0.75f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.75f })[0], 5);
            Assert.Equal(0.50f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.75f }, resolve)[0], 5);
        }
    }

    [Fact]
    public void ReplacingAType0SampleStreamChangesWhatTheNextEvaluationReturns()
    {
        // The memo covers the PARAMETERS, never the samples: those still come from
        // PdfStream.GetDecodedData, whose memo the RawData setter drops. This pins that boundary.
        var fn = Type0TwoSample();
        Assert.Equal(0f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0f })[0], 4);

        fn.RawData = new byte[] { 255, 128, 255, 255, 0, 64 };
        Assert.Equal(1f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0f })[0], 4);
    }

    // ================================================================
    //  Scratch buffers must not leak between depths or out to callers
    // ================================================================

    [Fact]
    public void NestedStitchingFunctionsEvaluateCorrectlyAtEveryDepth()
    {
        // Type 3 -> Type 3 -> Type 2. Each Evaluate level owns its own scratch slots; one shared
        // set would let the inner call overwrite the outer one's clamped input mid-evaluation.
        var innerA = Type2(new double[] { 0.0 }, new double[] { 0.25 });
        var innerB = Type2(new double[] { 0.25 }, new double[] { 0.50 });
        var middle = Type3(new PdfObject[] { innerA, innerB }, new double[] { 0.5 }, new double[] { 0, 1, 0, 1 });
        var outerB = Type2(new double[] { 0.50 }, new double[] { 1.00 });
        var outer  = Type3(new PdfObject[] { middle, outerB }, new double[] { 0.5 }, new double[] { 0, 1, 0, 1 });

        // t = 0.25 -> outer picks `middle` and re-encodes to 0.5 -> middle picks innerB and
        // re-encodes to 0.0 -> innerB yields its C0, 0.25.
        Assert.Equal(0.25f, PdfFunctionEvaluator.Evaluate(outer, new[] { 0.25f })[0], 4);
        // t = 0.125 -> middle at 0.25 -> innerA at 0.5 -> 0.125.
        Assert.Equal(0.125f, PdfFunctionEvaluator.Evaluate(outer, new[] { 0.125f })[0], 4);
        // t = 0.75 -> outerB at 0.5 -> 0.75.
        Assert.Equal(0.75f, PdfFunctionEvaluator.Evaluate(outer, new[] { 0.75f })[0], 4);

        for (int i = 0; i < 200; i++) PdfFunctionEvaluator.Evaluate(outer, new[] { i / 199f });
        Assert.Equal(0.25f, PdfFunctionEvaluator.Evaluate(outer, new[] { 0.25f })[0], 4);
    }

    [Fact]
    public void EvaluateLeavesTheCallersInputArrayUntouched()
    {
        // Both renderers hand the SAME float[] to every sample of a grid and refill it per sample;
        // clamping in place would corrupt the caller's loop variable.
        var fn = Type2(new double[] { 0 }, new double[] { 1 }, 1, Nums(0, 0.25));
        var input = new float[] { 0.9f };

        PdfFunctionEvaluator.Evaluate(fn, input);

        Assert.Equal(0.9f, input[0]);
    }

    [Fact]
    public void EachEvaluationReturnsAnArrayTheNextOneDoesNotOverwrite()
    {
        // The intermediate buffers are pooled; the RESULT must not be, or a caller that keeps one
        // (an image LUT builder, say) would find every entry equal to the last colour computed.
        var fn = Type2(new double[] { 0, 0, 0 }, new double[] { 1, 1, 1 });

        float[] first = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.25f });
        float[] second = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.75f });

        Assert.NotSame(first, second);
        Assert.Equal(0.25f, first[0], 5);
        Assert.Equal(0.75f, second[0], 5);
    }

    [Fact]
    public void OutputsAreClampedToRangeOnEveryEvaluationNotOnlyTheFirst()
    {
        // ClampToRange now writes into the array the evaluator just produced instead of allocating
        // a copy. Range [0 0.5] must still bite on call 200 as hard as on call 1.
        var fn = Type2(new double[] { 0 }, new double[] { 1 });
        fn.Set(PdfName.Range, Nums(0, 0.5));

        for (int i = 0; i < 200; i++)
            Assert.Equal(0.5f, PdfFunctionEvaluator.Evaluate(fn, new[] { 0.9f })[0], 5);
    }

    // ================================================================
    //  The DoS bound stays per call
    // ================================================================

    [Fact]
    public void AdversarialType4NestedLoopsAreRefusedOnEveryEvaluationNotJustTheFirst()
    {
        // Nested repeats multiply past MaxPsTotalOps at procedure depth 2, so only the aggregate
        // WorkBudget can stop them. It is created fresh per call ON PURPOSE — pooling it, as the
        // stack and the token cache now are, would let one image's cost be charged to an earlier
        // one, and a *later* call would then be refused for work it never did.
        //
        // The assertion is the guard's own evidence — the typed exception and the budget it names —
        // never a clock.
        var fn = Type4("{ 1000000 { 1000000 { 1 add } repeat } repeat }");

        for (int call = 0; call < 3; call++)
        {
            var ex = Assert.Throws<WorkBudgetExceededException>(
                () => PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f }));
            Assert.Contains("Type 4 PostScript function", ex.Message);
        }
    }

    [Fact]
    public void AnEvaluationAfterABudgetRefusalDoesNotInheritTheAbortedStack()
    {
        // The refusal unwinds out of EvalPs leaving the loop's accumulator on the rented stack, so
        // the pool has to hand back a CLEAN one.
        //
        // The follow-up program must under-produce for this to be observable at all: §7.10.5 takes
        // the TOPMOST n values, so a program that leaves n or more of its own hides any residue
        // beneath them. `{ pop 0.5 }` declares three outputs and leaves one, and the two the
        // evaluator has to invent must be 0 — not the previous call's leftovers.
        var bomb = Type4("{ 1000000 { 1000000 { 1 add } repeat } repeat }");
        var underProducing = Type4("{ pop 0.5 }", Nums(0, 1000000, 0, 1000000, 0, 1000000));

        float[] clean = PdfFunctionEvaluator.Evaluate(underProducing, new[] { 0.25f });
        Assert.Equal(new[] { 0f, 0f, 0.5f }, clean);

        Assert.Throws<WorkBudgetExceededException>(() => PdfFunctionEvaluator.Evaluate(bomb, new[] { 0.5f }));

        float[] afterRefusal = PdfFunctionEvaluator.Evaluate(underProducing, new[] { 0.25f });
        Assert.Equal(new[] { 0f, 0f, 0.5f }, afterRefusal);
    }

    [Fact]
    public void ADeeplyNestedType3CycleIsStillRefusedByTheDepthGuard()
    {
        // A → [B], B → [A] through indirect references: the memo must not let the recursion guard
        // lose track, and a cached FnParams must not make the cycle cheaper to spin.
        var a = new PdfDictionary();
        var b = new PdfDictionary();
        var objects = new Dictionary<int, PdfObject> { { 1, a }, { 2, b } };
        Func<PdfObject, PdfObject> resolve = o =>
            o is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out var v) ? v : o;

        foreach (var (self, other) in new[] { (a, 2), (b, 1) })
        {
            self.Set(PdfName.FunctionType, new PdfNumber(3));
            self.Set(PdfName.Domain, Nums(0, 1));
            var funcs = new PdfArray();
            funcs.Add(new PdfReference(other, 0));
            self.Set(PdfName.Functions, funcs);
            self.Set(PdfName.Bounds, new PdfArray());
            self.Set(PdfName.Encode, Nums(0, 1));
        }

        // Terminates, and returns the guard's sentinel rather than a colour it never computed.
        float[] o = PdfFunctionEvaluator.Evaluate(a, new[] { 0.5f }, resolve);
        Assert.Single(o);
        Assert.Equal(0f, o[0]);
    }
}
