// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Adversarial regression from remediation round 37: the JBIG2 user-defined Huffman-table
/// parser (<see cref="Jbig2Decoder.ParseUserDefinedHuffmanTable"/>) iterated
/// "until the accumulated range exceeds htHigh" with htLow/htHigh and every per-row rangeLen
/// read straight from the (attacker-controlled) segment body. A crafted table with a large
/// htHigh and rangeLen==0 looped ~2.1e9 times growing an unbounded list, and rangeLen≡31
/// (mod 32) made <c>1 &lt;&lt; rangeLen</c> negative — a true infinite loop. The fix caps the
/// row count and rejects an overflowing shift (returns null → standard-table fallback).
/// </summary>
public class Jbig2HuffmanTableDosTests
{
    private static byte[] Be32(long v) => new[]
    {
        (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
        (byte)((v >> 8) & 0xFF),  (byte)(v & 0xFF),
    };

    private static byte[] TableBody(byte flags, long htLow, long htHigh, params byte[] rowBits)
    {
        // Layout read by ParseUserDefinedHuffmanTable: [flags][htLow:4 BE][htHigh:4 BE][rows…].
        var body = new byte[1 + 4 + 4 + rowBits.Length];
        body[0] = flags;
        Array.Copy(Be32(htLow), 0, body, 1, 4);
        Array.Copy(Be32(htHigh), 0, body, 5, 4);
        Array.Copy(rowBits, 0, body, 9, rowBits.Length);
        return body;
    }

    [Fact]
    public void RangeLenZero_LargeHtHigh_TerminatesAndReturnsNull()
    {
        // flags 0x00 → htps=1, htrs=1, no tails. With no row bytes the bit reader returns 0
        // past EOF, so every rangeLen is 0 → currentRangeLow advances by 1 toward a 2^31-1
        // htHigh: pre-fix ~2.1e9 iterations; post-fix bails at the row cap.
        byte[] body = TableBody(0x00, htLow: 0, htHigh: 0x7FFFFFFF);

        Assert.Null(RunBounded(body,
            "ParseUserDefinedHuffmanTable must terminate on an adversarial table (it spun ~2.1e9 iterations pre-fix)."));
    }

    [Fact]
    public void NegativeShiftRangeLen_TerminatesAndReturnsNull()
    {
        // flags 0x50 → htrs=6 (rangeLen can encode 31), htps=1. Row bits chosen so the first
        // decoded rangeLen is 31: 1<<31 is negative, so currentRangeLow never reaches htHigh —
        // an infinite loop pre-fix. 0xBF = 1 0111111 → prefLen=1, rangeLen=011111(b)=31.
        byte[] body = TableBody(0x50, htLow: 0, htHigh: 0x7FFFFFFF, 0xBF, 0xFF);

        Assert.Null(RunBounded(body,
            "An overflowing 1<<rangeLen must not produce a non-terminating loop."));
    }

    /// <summary>
    /// Runs the parser on a dedicated thread and returns its result, failing with
    /// <paramref name="whyItMustTerminate"/> if it has not finished in a minute.
    /// </summary>
    /// <remarks>
    /// <para><b>The assertion these tests make is <see cref="Assert.Null(object)"/> — the guard's own
    /// evidence, a refusal — never a wall clock.</b> The timeout here is a hang-guard for the test
    /// host, not the behavioural claim, and it is deliberately far larger than the ~8&#160;ms the
    /// guarded parser actually takes.</para>
    ///
    /// <para><b>It used to be <c>Task.Run(…)</c> plus <c>task.Wait(10 s)</c>, and that flaked.</b>
    /// Both tests passed alone in 8&#160;ms and one failed at exactly 10&#160;s in a full-solution
    /// run — because a queued work item is not scheduled promptly when 26 test assemblies are
    /// saturating the thread pool, so the clock measured the pool's backlog rather than the parser.
    /// A dedicated thread removes the dependency instead of padding the number, which would have
    /// left the same race with a longer fuse.</para>
    ///
    /// <para>Potency is not established by this timeout and cannot be: it is established by
    /// mutating the guard out and timing <i>that</i>, which is where the ~2.1e9 iterations and the
    /// non-terminating loop in the class remarks were measured.</para>
    /// </remarks>
    private static Jbig2Decoder.Jbig2HuffmanTable RunBounded(byte[] body, string whyItMustTerminate)
    {
        Jbig2Decoder.Jbig2HuffmanTable result = null;
        var worker = new Thread(() => result = Jbig2Decoder.ParseUserDefinedHuffmanTable(body))
        {
            IsBackground = true,   // a regression must not keep the test host alive
        };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromMinutes(1)), whyItMustTerminate);
        return result;
    }
}
