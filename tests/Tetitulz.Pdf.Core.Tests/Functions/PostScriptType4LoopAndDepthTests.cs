// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// Locks in §59 (B2) and §60 (B3): Type 4 PostScript evaluator now
/// implements <c>repeat</c> / <c>for</c> / <c>bind</c>, and bounds
/// procedure recursion at <c>PdfFunctionEvaluator.MaxPsProcedureDepth</c> so an
/// adversarial Type 4 program cannot stack-overflow the renderer. The tests below
/// nest far beyond that cap, so they hold whatever its exact value is.
/// </summary>
public class PostScriptType4LoopAndDepthTests
{
    // ----------------------------------------------------------------
    //  B2 — repeat / for / bind
    // ----------------------------------------------------------------

    [Fact]
    public void Type4_Repeat_ExecutesProcedureNTimes()
    {
        // PostScript: count {proc} repeat.  Stack starts at [0] (input),
        // 5 is pushed, repeat consumes count=5 leaving [0], then runs
        // {2 add} five times: 0→2→4→6→8→10.
        var fn = BuildType4Function("{ 5 { 2 add } repeat }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        Assert.Equal(10f, result[0], 3);
    }

    [Fact]
    public void Type4_Repeat_ZeroCount_NoOp()
    {
        // count = 0 → proc never executes; the seed value falls through.
        var fn = BuildType4Function("{ 0 { 2 add } repeat }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 7f });
        Assert.Equal(7f, result[0], 3);
    }

    [Fact]
    public void Type4_For_ExecutesAcrossRange()
    {
        // PostScript: init incr limit {proc} for.
        // Seed [0] (input), then "1 1 5" pushes init=1, incr=1, limit=5.
        // Loop pushes i and runs {add}, summing 1+2+3+4+5 to 15.
        var fn = BuildType4Function("{ 1 1 5 { add } for }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        Assert.Equal(15f, result[0], 3);
    }

    [Fact]
    public void Type4_For_NegativeIncrement_DescendsToLimit()
    {
        // Negative increment: 5 -1 1 {add} for → 5+4+3+2+1 = 15.
        var fn = BuildType4Function("{ 5 -1 1 { add } for }");
        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        Assert.Equal(15f, result[0], 3);
    }

    [Fact]
    public void Type4_Bind_TreatedAsNoOp_NoUnknownOpRecorded()
    {
        // bind is a real PS optimisation operator; in our evaluator it's a
        // no-op. It must NOT appear in LastUnknownPostScriptOps.
        var fn = BuildType4Function("{ { 1 add } bind exec }");
        // exec is unknown but bind should not be — the LastUnknownPostScriptOps
        // list should NOT contain "bind".
        try { PdfFunctionEvaluator.Evaluate(fn, new[] { 0f }); } catch { }
        var unknown = PdfFunctionEvaluator.LastUnknownPostScriptOps;
        Assert.DoesNotContain("bind", unknown);
    }

    // ----------------------------------------------------------------
    //  B3 — depth guard
    // ----------------------------------------------------------------

    [Fact]
    public void Type4_DeepNestedIfElse_ThrowsBeforeStackOverflow()
    {
        // Build a Type 4 program with 5000 nested ifelse blocks. Every
        // nesting level adds one frame to the procedure-recursion stack.
        // Pre-§60 (B3), this would have stack-overflowed the CLR at
        // ~3000-5000 frames depending on machine. Now it must throw
        // a managed InvalidOperationException with "depth" in the message.
        string body = "1";
        for (int i = 0; i < 5000; i++)
            body = "1 { " + body + " } { 0 } ifelse";

        var fn = BuildType4Function("{ " + body + " }");

        var ex = Assert.ThrowsAny<Exception>(() =>
            PdfFunctionEvaluator.Evaluate(fn, new[] { 1f }));

        // The exception must mention "depth" — that's the documented signal.
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------
    //  Helper (mirrors the helper inside PdfFunctionEvaluatorTests)
    // ----------------------------------------------------------------

    private static PdfStream BuildType4Function(string psCode,
        float domainMin = -10000, float domainMax = 10000,
        float rangeMin = -10000, float rangeMax = 10000)
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
}
