// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// An oversized <c>/Rows</c> must not buy a proportional allocation.
/// </summary>
/// <remarks>
/// <para>
/// Sizing the scanline list straight from the file-controlled <c>/Rows</c> —
/// <c>new List&lt;byte[]&gt;(rows &gt; 0 ? rows : 64)</c> — ahead of the <c>ClampRowLimit</c> call
/// that bounds the decode loop would protect the loop and nothing else: the backing
/// <c>byte[][]</c> would already be committed. <c>CcittFaxDecodeFilter</c> caps <c>/Columns</c> at
/// 1,000,000 but applies no bound to <c>/Rows</c>, so a couple of hundred bytes of image XObject
/// would buy gigabytes.
/// </para>
/// <para>
/// <b>These assert allocation, not elapsed time.</b> The decoded output is correctly clamped
/// either way, so asserting on the returned bitmap alone cannot see this defect — the
/// evidence is the allocation the guard prevents. A DoS-guard test that finishes fast has quite
/// possibly not run.
/// </para>
/// </remarks>
public class CcittRowsPreallocationTests
{
    /// <summary>
    /// Rows claimed by the adversarial fixture. At 8 bytes per array reference this is a 400 MB
    /// backing array if handed to the list constructor raw, and it is well under the row clamp's
    /// own ceiling only after clamping.
    /// </summary>
    private const int AdversarialRows = 50_000_000;

    /// <summary>
    /// Ceiling for the whole decode. The fixed path pre-sizes to at most 4096 references (32 KB)
    /// and decodes two bytes of payload, so this leaves three orders of magnitude of headroom
    /// while still being ~25x below the 400 MB the unguarded allocation commits.
    /// </summary>
    private const long MaxAllocatedBytes = 16L * 1024 * 1024;

    [Theory]
    [InlineData(-1)] // Group 4 (T.6)
    [InlineData(0)]  // Group 3 1-D
    [InlineData(4)]  // Group 3 mixed 1-D/2-D
    public void OversizedRows_DoesNotPreallocateProportionalMemory(int k)
    {
        var filter = new CcittFaxDecodeFilter();
        var parms = BuildParams(k, columns: 8, rows: AdversarialRows);
        byte[] payload = { 0x00, 0x00 };

        // Warm the decoder's static tables and JIT on the same path, so the measurement below
        // charges only this decode.
        filter.Decode(payload, BuildParams(k, columns: 8, rows: 4));

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = filter.Decode(payload, parms);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(decoded);
        Assert.True(allocated < MaxAllocatedBytes,
            $"/K {k} with /Rows {AdversarialRows} allocated {allocated:N0} bytes; the scanline list " +
            "is being sized from the raw /Rows again instead of from the clamped row limit.");
    }

    /// <summary>
    /// The clamp itself still has to work: an oversized <c>/Rows</c> with a two-byte payload must
    /// produce a bitmap bounded by what was actually decoded, not by what was claimed.
    /// </summary>
    [Fact]
    public void OversizedRows_ProducesABitmapBoundedByTheDecodedRows()
    {
        var filter = new CcittFaxDecodeFilter();
        byte[] decoded = filter.Decode(new byte[] { 0x00, 0x00 },
            BuildParams(k: -1, columns: 8, rows: AdversarialRows));

        // 8 columns = 1 byte per packed row. A two-byte G4 payload cannot yield 50 million rows.
        Assert.True(decoded.Length < 1024,
            $"expected a bitmap bounded by the decoded row count, got {decoded.Length:N0} bytes.");
    }

    /// <summary>
    /// A legitimate row count must still decode: the guard bounds the pre-size, not the image.
    /// </summary>
    [Fact]
    public void ModestRows_StillDecodesEveryRowTheDataContains()
    {
        var filter = new CcittFaxDecodeFilter();

        // An all-white G4 image: each row is a single V0 code (bit 1) against an all-white
        // reference line. 8 set bits = 8 rows, 1 byte each at /Columns 8.
        byte[] payload = { 0xFF };
        byte[] decoded = filter.Decode(payload, BuildParams(k: -1, columns: 8, rows: 8));

        Assert.Equal(8, decoded.Length);
    }

    private static PdfDictionary BuildParams(int k, int columns, int rows)
    {
        var parms = new PdfDictionary();
        parms.Set(PdfName.K, new PdfNumber(k));
        parms.Set(PdfName.Columns, new PdfNumber(columns));
        parms.Set(PdfName.Rows, new PdfNumber(rows));
        parms.Set(PdfName.EndOfBlock, new PdfBoolean(false));
        return parms;
    }
}
