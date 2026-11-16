// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Functions;

/// <summary>
/// A Type 4 function's operand stack is capped at the ISO 32000-1 §7.10.5 limit of 100, so a
/// program cannot grow it without bound (DoS finding F1).
/// </summary>
/// <remarks>
/// <c>copy</c> pushes the top n values again, so <c>n copy</c> with n the stack's own size doubles
/// it: a few dozen such tokens — a few hundred bytes of function stream — reach 2^30 floats (~4 GB),
/// one work-budget tick a token, because the per-token budget cannot see a single token that
/// allocates 2^k. The push now refuses past 100 (a §7.10.5 <c>rangecheck</c>), raising the same
/// <see cref="WorkBudgetExceededException"/> the work budget does, so the function fails and the
/// caller reports it rather than the process running out of memory. Evaluated on a worker thread
/// with a join, so a regression grows the heap on that thread and fails as a test rather than taking
/// the run down (the pattern <see cref="Type4ProgramSyntaxTests"/> uses).
/// </remarks>
public class Type4OperandStackCapTests
{
    private static PdfStream Build(string program)
    {
        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(4));
        var domain = new PdfArray(); domain.Add(new PdfNumber(-10000)); domain.Add(new PdfNumber(10000));
        fn.Set(PdfName.Domain, domain);
        var range = new PdfArray(); range.Add(new PdfNumber(-10000)); range.Add(new PdfNumber(10000));
        fn.Set(PdfName.Range, range);
        fn.RawData = Encoding.ASCII.GetBytes(program);
        return fn;
    }

    [Fact]
    public void ACopyBomb_IsRefused_WithoutGrowingTheOperandStackUnbounded()
    {
        // Each `1000000 copy` copies the whole current stack (min(n, size) = size), doubling it:
        // without the cap this reaches 2^k floats within ten tokens. The cap refuses the push that
        // would cross 100, so the stack never exceeds it and the allocation stays small.
        var bomb = new StringBuilder("{ 0.5");
        for (int i = 0; i < 20; i++) bomb.Append(" 1000000 copy");
        bomb.Append(" }");

        bool refused = false;
        long allocated = -1;
        Exception unexpected = null;
        var worker = new Thread(() =>
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                try { PdfFunctionEvaluator.Evaluate(Build(bomb.ToString()), new[] { 0.5f }); }
                catch (WorkBudgetExceededException) { refused = true; }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception ex) { unexpected = ex; }
        }) { IsBackground = true };
        worker.Start();

        // Pre-fix this thread grew the heap to gigabytes; the join would not return in time, or the
        // test host would be killed over its memory ceiling. Either is the kill.
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the copy-bomb did not finish — the stack was not capped");
        Assert.Null(unexpected);
        Assert.True(refused, "the copy-bomb was not refused as a work-budget overflow");
        // ~100 floats plus the per-copy snapshot list, not 2^k: comfortably under a megabyte.
        Assert.True(allocated >= 0 && allocated < 5_000_000, $"the operand stack grew unbounded: {allocated} bytes allocated");
    }

    [Fact]
    public void ALegitimateCopy_StillEvaluates()
    {
        // The control: a real `copy` on a small stack is unaffected by the cap. `1 copy` duplicates
        // the input, `pop` drops the duplicate, `0.5 add` gives input + 0.5 = 0.75 for input 0.25.
        float[] result = null;
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try { result = PdfFunctionEvaluator.Evaluate(Build("{ 1 copy pop 0.5 add }"), new[] { 0.25f }); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        Assert.Equal(0.75f, result[0], 3);
    }
}
