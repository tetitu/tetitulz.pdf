// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Adversarial-input suite. Each test feeds the kind of malformed input that would,
/// unguarded, force a multi-GB allocation, an uncaught exception out of
/// document-open, or an unbounded loop / StackOverflow.
/// </summary>
public class SecurityHardeningTests
{
    // ---- Parser: attacker-controlled stream /Length must not drive a huge allocation ----

    [Fact]
    public void Parser_HugeStreamLength_ClampsInsteadOfAllocatingGigabytes()
    {
        // A tiny file whose content stream declares /Length 2,000,000,000. Kept as it is,
        // that value would mean `new byte[2_000_000_000]` → OOM. The parser scans for the
        // real endstream and hard-clamps to the remaining file size.
        const string content = "BT ET";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(sb.Length); sb.Append(body); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length 2000000000 >>\nstream\n{content}\nendstream\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
        using var doc = PdfDocument.Open(ms);
        var stream = doc.GetObject(4) as PdfStream;
        Assert.NotNull(stream);
        // Clamped: nowhere near 2e9, and well within the (tiny) file size.
        Assert.True(stream.RawData.Length < 4096, $"stream length not clamped: {stream.RawData.Length}");
        Assert.Contains("BT ET", Encoding.ASCII.GetString(stream.RawData));
    }

    // ---- Tokenizer: oversized integer tokens saturate, never throw ----

    [Fact]
    public void PdfToken_OversizedInteger_SaturatesInsteadOfThrowing()
    {
        var t = new PdfToken(PdfTokenType.Number, "99999999999", 0); // > int.MaxValue, fits long
        Assert.Equal(int.MaxValue, t.IntValue);
        Assert.Equal(99999999999L, t.LongValue);

        // Past long too: saturated, not 0 — a /Length of 21 digits read as 0 and bypassed every
        // "too large" check downstream (the review of 2026-10-16, R1.3). PdfTokenNumericTests has the ranges.
        var huge = new PdfToken(PdfTokenType.Number, "999999999999999999999", 0); // > long
        Assert.Equal(int.MaxValue, huge.IntValue);
        Assert.Equal(long.MaxValue, huge.LongValue);

        var neg = new PdfToken(PdfTokenType.Number, "-99999999999", 0);
        Assert.Equal(int.MinValue, neg.IntValue);
    }

    // ---- Filter pipeline: a pathologically long /Filter chain is rejected ----

    [Fact]
    public void FilterPipeline_ExcessivelyLongFilterChain_Throws()
    {
        var filters = new PdfArray();
        for (int i = 0; i < 17; i++) filters.Add(new PdfName("FlateDecode")); // > 16-filter cap
        Assert.Throws<PdfFilterException>(() =>
            FilterPipeline.Decode(new byte[] { 1, 2, 3 }, filters, null));
    }

    // ---- Functions: a Type 3 stitching function with an indirect A→B→A cycle terminates ----

    [Fact]
    public void PdfFunctionEvaluator_Type3IndirectCycle_TerminatesWithoutStackOverflow()
    {
        // fnA.Functions = [ref B]; fnB.Functions = [ref A]; resolver wires the cycle.
        var refA = new PdfReference(10, 0);
        var refB = new PdfReference(11, 0);

        PdfDictionary MakeStitch(PdfReference child)
        {
            var d = new PdfDictionary();
            d.Set(new PdfName("FunctionType"), new PdfNumber(3));
            d.Set(new PdfName("Domain"), new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(1) }));
            d.Set(new PdfName("Bounds"), new PdfArray());
            d.Set(new PdfName("Encode"), new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(1) }));
            d.Set(new PdfName("Functions"), new PdfArray(new PdfObject[] { child }));
            return d;
        }

        var fnA = MakeStitch(refB);
        var fnB = MakeStitch(refA);
        PdfObject Resolve(PdfObject o) =>
            o is PdfReference r ? (r.ReferenceObjectNumber == 10 ? fnA : fnB) : o;

        // Must return a finite array quickly (depth-capped), not StackOverflow / hang.
        float[] result = PdfFunctionEvaluator.Evaluate(fnA, new[] { 0.5f }, Resolve);
        Assert.NotNull(result);
    }
}
