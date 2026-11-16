// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Adversarial-input guard: a file-controlled <c>/Rows</c> must not size an allocation.
///
/// <para>All three CCITT decoders opened with
/// <c>new List&lt;byte[]&gt;(rows &gt; 0 ? rows : 64)</c> — the raw <c>/Rows</c> — and only then
/// called <c>ClampRowLimit</c>. The clamp bounded the decode loop; the allocation had already
/// happened. A ~200-byte image XObject declaring
/// <c>/DecodeParms &lt;&lt;/K -1 /Columns 8 /Rows 500000000&gt;&gt;</c> reserved a 4 GB
/// <c>byte[][]</c> backing array before a single bit was read, and
/// <c>CcittFaxDecodeFilter</c> bounds <c>/Columns</c> but places no bound at all on
/// <c>/Rows</c>. On a host with headroom the allocation succeeded and the memory was simply
/// consumed; on a constrained one it threw <see cref="OutOfMemoryException"/>, which the
/// filter's <c>catch</c> swallowed into a raw passthrough. Either way a few hundred bytes of
/// input bought gigabytes.</para>
///
/// <para><b>The assertion is the allocation itself, measured</b> — that is the guard's own
/// evidence, and it is the only observable that distinguishes the fixed code from the broken
/// code (both return the same tiny bitmap). It is not a wall-clock measurement: the same
/// numbers come back on a fast machine and a slow one.</para>
/// </summary>
public class CcittFaxRowsAllocationTests
{
    /// <summary>
    /// Large enough that the pre-fix reservation (8 bytes per element) is ~400 MB — three orders
    /// of magnitude above the ceiling asserted below — while staying survivable if someone runs
    /// these tests against the unfixed code.
    /// </summary>
    private const int AdversarialRows = 50_000_000;

    /// <summary>
    /// Generous: the fixed decoder reserves at most 4 096 entries (~32 KB) plus whatever the two
    /// input bytes decode into. Anything approaching the pre-fix 400 MB fails this by 100×.
    /// </summary>
    private const long AllocationCeilingBytes = 8L * 1024 * 1024;

    [Fact]
    public void DecodeGroup4_OversizedDeclaredRows_DoesNotReserveMemoryForThem()
    {
        // Two bytes of payload: whatever they decode to, the decode is over almost immediately.
        byte[] encoded = { 0xFF, 0x00 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        int rowsDecoded;
        try
        {
            CcittFaxDecoder.DecodeGroup4(encoded, columns: 8, rows: AdversarialRows,
                endOfBlock: false, out rowsDecoded);
        }
        catch (Exception)
        {
            // A malformed two-byte stream may fail on a Huffman prefix; the filter catches that
            // and falls back. Either outcome is fine — what is under test is that the DECLARED
            // row count bought no memory, and that holds on both paths.
            rowsDecoded = 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < AllocationCeilingBytes,
            $"/Rows {AdversarialRows} with a 2-byte payload allocated {allocated:N0} bytes; " +
            "the row list must be sized from the clamped row limit, not from the declared /Rows.");
        Assert.InRange(rowsDecoded, 0, 4096);
    }

    [Fact]
    public void DecodeGroup3_1D_OversizedDeclaredRows_DoesNotReserveMemoryForThem()
    {
        byte[] encoded = { 0xFF, 0x00 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CcittFaxDecoder.DecodeGroup3_1D(encoded, columns: 8, rows: AdversarialRows,
                endOfBlock: false, encodedByteAlign: false, requireEol: false, out _);
        }
        catch (Exception) { }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < AllocationCeilingBytes,
            $"G3 1D allocated {allocated:N0} bytes for a declared /Rows of {AdversarialRows}.");
    }

    [Fact]
    public void DecodeGroup3_2D_OversizedDeclaredRows_DoesNotReserveMemoryForThem()
    {
        byte[] encoded = { 0xFF, 0x00 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CcittFaxDecoder.DecodeGroup3_2D(encoded, columns: 8, rows: AdversarialRows,
                endOfBlock: false, encodedByteAlign: false, out _);
        }
        catch (Exception) { }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < AllocationCeilingBytes,
            $"G3 2D allocated {allocated:N0} bytes for a declared /Rows of {AdversarialRows}.");
    }

    [Fact]
    public void Decode_OversizedRowsThroughTheFilter_ReturnsABitmapSizedByTheClampNotTheDeclaration()
    {
        // The end-to-end shape the finding describes: a tiny image XObject whose /DecodeParms
        // declare half a billion rows.
        var parms = new PdfDictionary();
        parms[new PdfName("K")] = new PdfNumber(-1);
        parms[new PdfName("Columns")] = new PdfNumber(8);
        parms[new PdfName("Rows")] = new PdfNumber(500_000_000);
        parms[new PdfName("BlackIs1")] = PdfBoolean.False;
        parms[new PdfName("EndOfBlock")] = PdfBoolean.False;

        var filter = new CcittFaxDecodeFilter();

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] output = filter.Decode(new byte[] { 0xFF, 0x00 }, parms);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < AllocationCeilingBytes,
            $"The filter allocated {allocated:N0} bytes for /Rows 500000000.");
        // One byte per row at 8 columns, so the output length IS the decoded row count.
        Assert.InRange(output.Length, 0, 4096);
    }

    /// <summary>
    /// A decode that would outgrow the <i>caller's</i> <see cref="StreamFilter.MaxOutputBytes"/> is
    /// refused, and refused as a throw rather than as the raw-bytes passthrough.
    /// </summary>
    /// <remarks>
    /// The clamp above bounds the <b>allocation</b>; it does not honour the caller's ceiling.
    /// Measured before this: 1 000 bytes of all-<c>0xFF</c> G4 with <c>/Columns 8000 /Rows 8000</c>
    /// produced 8 000 000 bytes against a 1 MiB ceiling, returned silently with a null
    /// <c>LastFallbackReason</c> — an 8x overrun of a number the caller had asked for.
    /// <para>The <c>LastFallbackReason</c> assertion is the load-bearing one: <c>Decode</c> ends in
    /// a catch-all that turns any exception into "decode failed, here are the compressed bytes", so
    /// a ceiling check written <i>inside</i> the try would swallow this throw and report the wrong
    /// cause entirely. It sits outside for that reason, and this is what pins it there.</para>
    /// </remarks>
    [Fact]
    public void Decode_OutputBeyondTheCallersCeiling_IsRefusedRatherThanReturned()
    {
        var filter = new CcittFaxDecodeFilter { MaxOutputBytes = 1024 * 1024 };

        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => filter.Decode(AllWhiteG4(1000), WideParms()));

        Assert.Contains("1048576", ex.Message, StringComparison.Ordinal);
        Assert.Null(CcittFaxDecodeFilter.LastFallbackReason);
    }

    /// <summary>
    /// The floor. The same decode under a ceiling that exactly fits is untouched — the check is
    /// <c>&gt;</c>, not <c>&gt;=</c>, so a stream whose declared <c>/DL</c> equals its decoded size
    /// is not refused for being precisely the size it said it was.
    /// </summary>
    [Fact]
    public void Decode_OutputExactlyAtTheCallersCeiling_IsAllowed()
    {
        var filter = new CcittFaxDecodeFilter { MaxOutputBytes = 8_000_000 };

        byte[] output = filter.Decode(AllWhiteG4(1000), WideParms());

        Assert.Null(CcittFaxDecodeFilter.LastFallbackReason);
        Assert.Equal(8_000_000, output.Length);
    }

    /// <summary>8 000 rows of 8 000 columns — every 0xFF bit is a V0 against an all-white line.</summary>
    private static PdfDictionary WideParms()
    {
        var parms = new PdfDictionary();
        parms[new PdfName("K")]       = new PdfNumber(-1);
        parms[new PdfName("Columns")] = new PdfNumber(8000);
        parms[new PdfName("Rows")]    = new PdfNumber(8000);
        return parms;
    }

    private static byte[] AllWhiteG4(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++) data[i] = 0xFF;
        return data;
    }

    [Fact]
    public void DecodeGroup4_DeclaredRowsWithinTheClamp_StillDecodesEveryRow()
    {
        // The clamp must not become a cap on legitimate input: a real fax page's /Rows is
        // honoured exactly, so sizing the list from min(rowLimit, 4096) changed nothing but the
        // reservation. All-white G4 rows: V0 ("1") per row against an all-white reference.
        byte[] encoded = BitsToBytes(new string('1', 32));

        CcittFaxDecoder.DecodeGroup4(encoded, columns: 8, rows: 32,
            endOfBlock: false, out int rowsDecoded);

        Assert.Equal(32, rowsDecoded);
    }

    private static byte[] BitsToBytes(string bits)
    {
        var result = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1') result[i / 8] |= (byte)(0x80 >> (i % 8));
        return result;
    }
}
