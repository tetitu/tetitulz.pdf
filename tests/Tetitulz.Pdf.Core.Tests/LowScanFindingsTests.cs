// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Regression tests for the LOW assembly-scan findings. Each names the defect it pins.
/// </summary>
public class LowScanFindingsTests
{
    // ==================================================================
    //  Clamp passed NaN straight through to the colour pipeline
    // ==================================================================

    [Fact]
    public void Type4_FunctionProducingNaN_ReturnsAFiniteValueAndReportsTheSubstitution()
    {
        // Math.Pow(-8, 0.5) is NaN. `v < min ? min : v > max ? max : v` leaves NaN alone because
        // BOTH comparisons are false, so it escaped the evaluator despite /Range, reached
        // PdfColor.Channel, and Math.Round(NaN) cast to 0 — a black band indistinguishable from a
        // legitimately black stop. Infinity is handled correctly by the same expression, which is
        // what made the gap easy to miss.
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1));
        fn.Set(new PdfName("Range"), range);
        fn.RawData = Encoding.ASCII.GetBytes("{ pop -8 0.5 exp }");

        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });

        Assert.False(float.IsNaN(r[0]));
        Assert.Equal(0f, r[0]);
        Assert.NotNull(PdfFunctionEvaluator.LastType0Failure);
        Assert.Contains("NaN", PdfFunctionEvaluator.LastType0Failure!);
    }

    [Fact]
    public void Clamp_StillPinsInfinityToTheRangeBound()
    {
        // The NaN guard must not disturb the infinity behaviour that already worked.
        var fn = new PdfStream();
        fn.Set(new PdfName("FunctionType"), new PdfNumber(4));
        var domain = new PdfArray();
        domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(new PdfName("Domain"), domain);
        var range = new PdfArray();
        range.Add(new PdfNumber(0)); range.Add(new PdfNumber(1));
        fn.Set(new PdfName("Range"), range);
        fn.RawData = Encoding.ASCII.GetBytes("{ pop 1 0 div }");

        float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f });

        Assert.False(float.IsNaN(r[0]));
        Assert.InRange(r[0], 0f, 1f);
    }

    // ==================================================================
    //  RunLengthDecode refused a stream landing exactly on the ceiling
    // ==================================================================

    [Fact]
    public void RunLength_StreamDecodingToExactlyTheCeiling_Succeeds()
    {
        // Both cap checks were `>=` where Flate and LZW use `>`, so the ceiling was exclusive for
        // this filter alone. MaxOutputBytes exists so a caller can thread its own limit down and
        // the accessibility tagger and PDF/X validator both size theirs from /DL — a stream whose
        // /DL is exactly n and which decodes to exactly n bytes was refused as unscannable.
        // Literal run of 8 bytes: length byte 7 → copy 8, then EOD.
        byte[] encoded = { 7, 1, 2, 3, 4, 5, 6, 7, 8, 128 };

        var filter = new RunLengthDecodeFilter { MaxOutputBytes = 8 };
        byte[] output = filter.Decode(encoded, parameters: null);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, output);
    }

    [Fact]
    public void RunLength_StreamDecodingToOneByteOverTheCeiling_IsRefused()
    {
        byte[] encoded = { 8, 1, 2, 3, 4, 5, 6, 7, 8, 9, 128 };   // 9 literal bytes

        var filter = new RunLengthDecodeFilter { MaxOutputBytes = 8 };

        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => filter.Decode(encoded, parameters: null));
        Assert.Contains("RunLengthDecode", ex.Message);
    }

    [Fact]
    public void RunLength_RepeatRunLandingExactlyOnTheCeiling_Succeeds()
    {
        // The second of the two `>=` checks, on the repeat-run branch.
        byte[] encoded = { 249, 0xAB, 128 };   // 257 - 249 = 8 repeats

        var filter = new RunLengthDecodeFilter { MaxOutputBytes = 8 };
        byte[] output = filter.Decode(encoded, parameters: null);

        Assert.Equal(8, output.Length);
        Assert.All(output, b => Assert.Equal(0xAB, b));
    }

    // ==================================================================
    //  LZW bit position was an int while the total-bit count was a long
    // ==================================================================

    [Fact]
    public void Lzw_ReadBitsPastIntMaxValue_ReturnsWhatItHasInsteadOfThrowing()
    {
        // bitPos used to be an int while totalBits had already been widened for exactly this
        // reason. Past ~268 MB of input it wrapped negative, the caller's `bitPos > totalBits`
        // guard compared a negative int against a positive long and stayed false, and
        // `data[byteIndex]` threw IndexOutOfRangeException — out through FilterPipeline.Decode and
        // PdfStream.GetDecodedData, neither of which catches it, as an untyped runtime exception
        // rather than the InvalidDataException every other bomb guard here raises.
        //
        // Asserting the guard directly rather than building a 300 MB stream: the point is that a
        // bit position past int.MaxValue is representable and bounds-checked, which is what the
        // widening buys.
        byte[] data = { 0xFF, 0xFF, 0xFF, 0xFF };

        int result = LzwDecodeFilter.ReadBits(data, (long)int.MaxValue + 64, 9);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Lzw_ReadBitsAtANegativePosition_ReturnsWhatItHasInsteadOfThrowing()
    {
        byte[] data = { 0xFF, 0xFF, 0xFF, 0xFF };

        int result = LzwDecodeFilter.ReadBits(data, -64, 9);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Lzw_ReadBitsWithinTheStream_StillReadsMsbFirst()
    {
        // Guards against a "fix" that bounds-checks its way into returning nothing.
        // 0b1_0100_0001 read as 9 bits from position 0 of {0xA0, 0x80} is 0x141.
        byte[] data = { 0xA0, 0x80 };

        Assert.Equal(0x141, LzwDecodeFilter.ReadBits(data, 0, 9));
    }

    // ==================================================================
    //  CCITT: the no-/Rows fallback contradicted the encoder's own ceiling
    // ==================================================================

    [Fact]
    public void Ccitt_WithoutRowsParameter_DecodesPastTheOldSixtyFiveThousandRowFallback()
    {
        // CcittFaxEncoder's caps comment says they are "deliberately identical to the read side so
        // a stream this encoder produces is always one the decoder will accept", and MaxEncodeRows
        // is 200 000 to mirror MaxCcittRows — but the no-/Rows fallback was 65 535, so a long
        // receipt or banner scan written with an EOFB and no /Rows silently lost every row past
        // 65 535 and reported a clean decode.
        //
        // All-white G4 rows are one V0 bit each, so 70 000 rows is 8 750 bytes of input.
        const int rows = 70_000;
        byte[] encoded = new byte[(rows + 7) / 8];
        for (int i = 0; i < encoded.Length; i++) encoded[i] = 0xFF;

        CcittFaxDecoder.DecodeGroup4(encoded, columns: 8, rows: 0, endOfBlock: false,
            out int rowsDecoded);

        Assert.Equal(rows, rowsDecoded);
    }

    [Fact]
    public void Ccitt_TheNoRowsFallbackMatchesTheAbsoluteRowCap()
    {
        // The two constants must not drift apart again. The pixel budget in ClampRowLimit still
        // bounds the work, so the lower fallback bought nothing.
        byte[] encoded = new byte[32];
        for (int i = 0; i < encoded.Length; i++) encoded[i] = 0xFF;

        // /Columns 1 puts the pixel budget far out of reach, so the row cap is what binds.
        CcittFaxDecoder.DecodeGroup4(encoded, columns: 1, rows: 0, endOfBlock: false, out int decoded);

        // 32 bytes = 256 V0 bits = 256 rows; nothing clamped it to a smaller default.
        Assert.Equal(256, decoded);
    }

    // ==================================================================
    //  FontFileLocator's resolved-path cache grew without bound
    // ==================================================================

    [Fact]
    public void FontFileLocator_ManyDistinctFamilyNames_StopsGrowingAtTheCap()
    {
        // The key is built from a PDF /BaseFont name, so the FILE chooses how many distinct keys
        // exist — a document declaring 100 000 font resources left 100 000 entries alive for the
        // process lifetime, keyed on untrusted strings.
        FontFileLocator.ResetCache();
        try
        {
            for (int i = 0; i < PdfLimits.MaxCachedFontPathLookups + 500; i++)
                FontFileLocator.FindFamilyFile("NoSuchFamily" + i, bold: false, italic: false);

            Assert.Equal(PdfLimits.MaxCachedFontPathLookups, FontFileLocator.CachedLookupCount);
        }
        finally { FontFileLocator.ResetCache(); }
    }

    [Fact]
    public void FontFileLocator_BelowTheCap_StillCaches()
    {
        // The cap must not become "never cache": the whole point of the dictionary is that a real
        // document's handful of families are resolved once, not once per page.
        FontFileLocator.ResetCache();
        try
        {
            FontFileLocator.FindFamilyFile("NoSuchFamilyAtAll", bold: false, italic: false);
            Assert.Equal(1, FontFileLocator.CachedLookupCount);

            FontFileLocator.FindFamilyFile("NoSuchFamilyAtAll", bold: false, italic: false);
            Assert.Equal(1, FontFileLocator.CachedLookupCount);   // served from the cache
        }
        finally { FontFileLocator.ResetCache(); }
    }
}
