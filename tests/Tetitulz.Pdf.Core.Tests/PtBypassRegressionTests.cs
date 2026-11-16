// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Net;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Net;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Regressions for the bypasses found by the adversarial penetration-test execution pass
/// (round 36): each was confirmed by running a crafted attack against the
/// shipped guard, then fixed. These lock the fixes in.
/// </summary>
public class PtBypassRegressionTests
{
    /// <summary>
    /// Wall-clock ceiling for the DoS-guard tests below. Deliberately far above any real runtime
    /// (these abort in well under a second on an idle machine): its job is to catch an unbounded
    /// hang, not to measure performance. A tight bound turns CPU contention from a parallel test
    /// run into a false regression report — see GAPS.md §8a.
    /// </summary>
    private const int HangCeilingMs = 120_000;

    // ---- WorkBudget: the centralised aggregate loop bound ----

    [Fact]
    public void WorkBudget_Throws_WhenExhausted()
    {
        var b = new WorkBudget(10, "test");
        for (int i = 0; i < 10; i++) b.Tick();
        Assert.Equal(10, b.Spent);
        Assert.Throws<WorkBudgetExceededException>(() => b.Tick());
    }

    // ---- Type 4 PostScript: nested loops multiply past the per-loop cap → aggregate budget ----

    [Fact]
    public void Type4_NestedLoopBomb_AbortsQuickly()
    {
        // 1e5^3 = 1e15 body executions at proc-depth 3 (depth guard never fires). The per-loop
        // MaxPsLoopIterations cap cannot bound the product; the WorkBudget must, and fast.
        var fn = BuildType4("{ 100000 { 100000 { 100000 { 1 pop } repeat } repeat } repeat 0.5 }");
        var sw = Stopwatch.StartNew();

        // The WorkBudget specifically — not "any exception". ThrowsAny would be satisfied by a
        // NullReferenceException from a broken evaluator, i.e. by the guard NOT working.
        Assert.ThrowsAny<WorkBudgetExceededException>(
            () => PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f }));
        sw.Stop();

        // NOTE on the ceiling: this is a HANG-CATCHER, not a performance assertion. It was
        // 10s/5s/2s and went red on a loaded machine — this bomb aborts in ~0.5s idle but took
        // 12.7s during a parallel whole-solution run, a 22x contention factor with the code
        // unchanged. A test that fails because fourteen assemblies shared a CPU reports a
        // regression that did not happen, and a suite that cries wolf is how a real one gets
        // waved through. The behaviour under test is "terminates instead of hanging", which the
        // typed guard evidence above establishes; the clock only has to exclude an actual hang.
        Assert.True(sw.ElapsedMilliseconds < HangCeilingMs,
            $"nested-loop bomb took {sw.ElapsedMilliseconds}ms — that is a hang, not slow hardware");
    }

    [Fact]
    public void Type4_LoopWithIfRecursion_AbortsQuickly()
    {
        // 1e6 repeat whose body re-tokenises a 40-deep if-recursion each iteration. Pre-fix the
        // re-Tokenize cost (uncounted) hung the evaluator; proc-token caching + budget bound it.
        string ifNest = "1";
        for (int i = 0; i < 40; i++) ifNest = "1 { " + ifNest + " } if";
        var fn = BuildType4("{ 1000000 { " + ifNest + " pop } repeat 0.5 }");
        var sw = Stopwatch.StartNew();

        Assert.ThrowsAny<WorkBudgetExceededException>(
            () => PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f }));
        sw.Stop();

        // See the ceiling note above. This is the test that actually went red under load (12709ms).
        Assert.True(sw.ElapsedMilliseconds < HangCeilingMs,
            $"loop-in-if bomb took {sw.ElapsedMilliseconds}ms — that is a hang, not slow hardware");
    }

    [Fact]
    public void Type4_LegitFunction_StillEvaluates()
    {
        // The budget/caching must not break a normal function.
        var fn = BuildType4("{ 5 { 2 add } repeat }");
        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        Assert.Equal(10f, r[0], 3);
    }

    // ---- CCITTFax: per-row byte[] object overhead bomb (narrow columns, huge rows) ----

    [Fact]
    public void Ccitt_NarrowColumnsHugeRows_OutputBounded()
    {
        var parms = new PdfDictionary();
        parms.Set(new PdfName("K"), new PdfNumber(-1));       // Group 4
        parms.Set(new PdfName("Columns"), new PdfNumber(1));  // 1px wide → pixel budget alone allows ~64M rows
        parms.Set(new PdfName("Rows"), new PdfNumber(64_000_000));
        byte[] body = new byte[1024 * 1024];                 // 1 MB of all-0xFF (all V0 → 1 row per bit)
        for (int i = 0; i < body.Length; i++) body[i] = 0xFF;

        var sw = Stopwatch.StartNew();
        byte[] outp = new CcittFaxDecodeFilter().Decode(body, parms);
        sw.Stop();

        Assert.NotNull(outp);
        Assert.True(outp.Length <= 300_000, $"CCITT output not row-capped: {outp.Length} bytes");
        // The row cap above is the real evidence that the guard fired; the clock only excludes a
        // hang. See the ceiling note on Type4_NestedLoopBomb_AbortsQuickly.
        Assert.True(sw.ElapsedMilliseconds < HangCeilingMs,
            $"CCITT decode took {sw.ElapsedMilliseconds}ms — that is a hang, not slow hardware");
    }

    // ---- SSRF: IPv6 scope-loopback + embedded-IPv4 transition prefixes must be blocked ----

    [Theory]
    [InlineData("::1%1")]                 // scoped loopback (IPAddress.IsLoopback returns false for this)
    [InlineData("::1%9")]
    [InlineData("::")]                    // unspecified == this-host
    [InlineData("::127.0.0.1")]           // IPv4-compatible ::/96
    [InlineData("::a9fe:a9fe")]           // ::169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::7f00:1")]       // NAT64 → 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 → metadata
    [InlineData("2002:7f00:1::")]         // 6to4 → 127.0.0.1
    [InlineData("::ffff:127.0.0.1")]      // standard IPv4-mapped loopback
    [InlineData("64:ff9b:1::a00:1")]      // local-use NAT64 (RFC 8215) → wherever the network translates it
    [InlineData("64:ff9b:1:ffff::1")]     // the same prefix, another embedding
    [InlineData("2001::1")]               // Teredo
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]   // Teredo, an obscured IPv4 inside
    [InlineData("2001:db8::1")]           // documentation
    public void Ssrf_BlockedIpv6Forms_AreUnsafe(string addr)
    {
        Assert.True(PrivateAddressGuard.IsPrivateOrReserved(IPAddress.Parse(addr)), $"{addr} should be private/reserved");
        Assert.False(PrivateAddressGuard.IsSafeHost(addr), $"{addr} should be unsafe");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    // The neighbours of each refused range, so a range is refused by its own bounds:
    [InlineData("198.17.255.255")]        // below 198.18.0.0/15
    [InlineData("198.20.0.1")]            // above it
    [InlineData("192.0.3.1")]             // beside 192.0.2.0/24
    [InlineData("198.51.101.1")]          // beside 198.51.100.0/24
    [InlineData("203.0.114.1")]           // beside 203.0.113.0/24
    [InlineData("2001:1::1")]             // beside Teredo's 2001::/32
    [InlineData("2001:db9::1")]           // beside 2001:db8::/32
    [InlineData("64:ff9b:2::1")]          // beside 64:ff9b:1::/48
    public void Ssrf_PublicForms_AreAllowed(string addr)
    {
        Assert.False(PrivateAddressGuard.IsPrivateOrReserved(IPAddress.Parse(addr)), $"{addr} should be public");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("198.18.0.1")]            // benchmarking range, first half
    [InlineData("198.19.255.254")]        // and its last address but one
    [InlineData("192.0.2.1")]             // documentation ranges
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    public void Ssrf_BlockedIpv4Forms_AreUnsafe(string addr)
    {
        Assert.True(PrivateAddressGuard.IsPrivateOrReserved(IPAddress.Parse(addr)), addr);
        Assert.False(PrivateAddressGuard.IsSafeHost(addr), addr);
    }

    private static PdfStream BuildType4(string psCode)
    {
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(-10000)); domain.Add(new PdfNumber(10000));
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        range.Add(new PdfNumber(-10000)); range.Add(new PdfNumber(10000));
        fn.Set(new PdfName("Range"), range);
        fn.RawData = System.Text.Encoding.ASCII.GetBytes(psCode);
        return fn;
    }
}
