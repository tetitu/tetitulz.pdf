// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Adversarial regression for the round-37 charstring "billion-laughs" guard. A malicious embedded CFF
/// font whose Local Subrs each invoke the next one K times explodes to K^depth path operations —
/// the recursion-depth cap (10) bounds depth but not the multiplicative width, so a per-glyph
/// operator budget (<c>Type2CharstringEvaluator.MaxSteps</c>) is what actually stops it. This test
/// builds such a font directly (the "synthetic-CFF builder" GAPS.md §9 noted was missing) and
/// asserts evaluation terminates instead of hanging.
/// </summary>
public class CharstringStepBudgetTests
{
    [Fact]
    public void Evaluate_NestedSubrBomb_TerminatesViaStepBudget()
    {
        // bias for a 9-subr index is 107, so "call local subr j" pushes (j - 107) as a single-byte
        // number (byte = value + 139) followed by callsubr (0x0A). 0x0B = return, 0x0E = endchar.
        const int K = 64; // 8 multiplying levels ⇒ ~64^8 ≈ 2.8e14 subr calls without the budget

        var subrs = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var s = new List<byte>();
            for (int k = 0; k < K; k++) { s.Add((byte)(i + 33)); s.Add(0x0A); } // call subr (i+1)
            s.Add(0x0B);                                                          // return
            subrs.Add(s.ToArray());
        }
        subrs.Add(new byte[] { 0x0B }); // subr 8: leaf (return)

        byte[] glyph0 = { 32, 0x0A, 0x0E }; // push (0-107) → call subr 0, then endchar

        var font = new CffFont(
            charstrings: new[] { glyph0 },
            localSubrs: subrs,
            globalSubrs: Array.Empty<byte[]>(),
            charCodeToGid: new Dictionary<int, int>(),
            defaultWidthX: 0f,
            nominalWidthX: 0f);

        RunBounded(() => Type2CharstringEvaluator.Evaluate(font, 0),
            "charstring evaluation must terminate via the per-glyph step budget; without it the " +
            "nested-subr explosion (~2.8e14 operations) would never finish.");
    }

    [Fact]
    public void Evaluate_NormalSubrUse_StillProducesGlyph()
    {
        // Sanity: a legitimate subr (drawing a small box via rmoveto + rlineto) still evaluates.
        // glyph 0: 100 100 rmoveto, call subr 0, endchar.   subr 0: 50 0 rlineto, return.
        byte[] subr0 = { (byte)(50 + 139), (byte)(0 + 139), 0x05, 0x0B }; // 50 0 rlineto; return
        byte[] glyph0 =
        {
            (byte)(100 + 139), (byte)(100 + 139), 0x15,   // 100 100 rmoveto
            32, 0x0A,                                     // call subr 0
            0x0E,                                         // endchar
        };

        var font = new CffFont(
            charstrings: new[] { glyph0 },
            localSubrs: new List<byte[]> { subr0 },
            globalSubrs: Array.Empty<byte[]>(),
            charCodeToGid: new Dictionary<int, int>(),
            defaultWidthX: 0f,
            nominalWidthX: 0f);

        var outline = Type2CharstringEvaluator.Evaluate(font, 0);
        Assert.NotNull(outline.Path);
        Assert.False(outline.Path.IsEmpty); // the rmoveto/rlineto produced geometry
    }

    // ---- Type 1 charstrings: the same MaxSteps guard, different bytecode (no subr bias;
    //      callsubr (10) pops the subr number directly; the glyph must start with hsbw (13)). ----

    [Fact]
    public void Type1_Evaluate_NestedSubrBomb_TerminatesViaStepBudget()
    {
        const int K = 64; // ~64^8 ≈ 2.8e14 subr calls without the budget

        var subrs = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var s = new List<byte>();
            for (int k = 0; k < K; k++) { s.Add((byte)(i + 1 + 139)); s.Add(10); } // push (i+1); callsubr
            s.Add(11);                                                              // return
            subrs.Add(s.ToArray());
        }
        subrs.Add(new byte[] { 11 }); // subr 8: leaf (return)

        // 0 0 hsbw; push 0; callsubr (→ subr 0); endchar
        byte[] glyph = { 139, 139, 13, 139, 10, 14 };

        var font = new Type1Font(
            encoding: new string[256],
            charStrings: new Dictionary<string, byte[]> { ["A"] = glyph },
            subrs: subrs);

        RunBounded(() => Type1CharstringEvaluator.Evaluate(font, "A"),
            "Type 1 charstring evaluation must terminate via the per-glyph step budget.");
    }

    [Fact]
    public void Type1_Evaluate_NormalSubrUse_StillProducesGlyph()
    {
        // 0 0 hsbw; 100 100 rmoveto; callsubr 0; endchar.   subr 0: 50 0 rlineto; return.
        byte[] subr0 = { (byte)(50 + 139), (byte)(0 + 139), 5, 11 };
        byte[] glyph =
        {
            139, 139, 13,                                 // 0 0 hsbw
            (byte)(100 + 139), (byte)(100 + 139), 21,     // 100 100 rmoveto
            139, 10,                                       // push 0; callsubr
            14,                                            // endchar
        };

        var font = new Type1Font(
            encoding: new string[256],
            charStrings: new Dictionary<string, byte[]> { ["A"] = glyph },
            subrs: new List<byte[]> { subr0 });

        var outline = Type1CharstringEvaluator.Evaluate(font, "A");
        Assert.NotNull(outline.Path);
        Assert.False(outline.Path.IsEmpty);
    }
    /// <summary>
    /// Runs <paramref name="evaluate"/> on a dedicated thread, failing with
    /// <paramref name="whyItMustTerminate"/> if it has not finished in a minute.
    /// </summary>
    /// <remarks>
    /// <para><b>The claim is termination</b> — that is what a step budget buys, and there is no
    /// other evidence of it: an evaluator without the budget does not return a wrong answer, it
    /// does not return. The timeout is a hang-guard for the test host, not the assertion, and it is
    /// far larger than the ~320 ms the guarded evaluator actually takes.</para>
    ///
    /// <para><b>It used to be <c>Task.Run(…)</c> plus <c>task.Wait(15 s)</c>, and that flaked.</b>
    /// This class passes in 323 ms alone and one case failed at exactly 15 s in a full-solution run
    /// — a queued work item is not scheduled promptly when 26 test assemblies are saturating the
    /// thread pool, so the clock measured the pool's backlog rather than the evaluator. A dedicated
    /// thread removes the dependency instead of padding the number, which would leave the same race
    /// with a longer fuse. Same fix, same reason, as <c>Jbig2HuffmanTableDosTests</c>.</para>
    ///
    /// <para>Potency is not established by this timeout and cannot be: it comes from the fixture's
    /// own arithmetic — ~2.8e14 subr calls without the budget — recorded where the fixture is built.</para>
    /// </remarks>
    private static void RunBounded(Action evaluate, string whyItMustTerminate)
    {
        var worker = new Thread(() => evaluate()) { IsBackground = true };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromMinutes(1)), whyItMustTerminate);
    }
}
