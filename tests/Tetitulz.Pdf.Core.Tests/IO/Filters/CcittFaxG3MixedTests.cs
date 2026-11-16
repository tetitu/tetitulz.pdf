// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the T.4 Group 3 2D mixed-mode decoder ( /K &gt; 0 ).
///
/// Per ITU-T T.4 §2.5.3, each line begins with an EOL (000000000001) followed
/// by a single tag bit (1 = 1D line, 0 = 2D line).  2D lines use the same
/// Pass / Horizontal / Vertical codes as T.6.
/// </summary>
public class CcittFaxG3MixedTests
{
    private static byte[] BitsToBytes(string bits)
    {
        int n = (bits.Length + 7) / 8;
        var result = new byte[n];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1')
                result[i / 8] |= (byte)(0x80 >> (i % 8));
        return result;
    }

    private static PdfDictionary DecodeParms(int k, int columns, int? rows = null,
        bool blackIs1 = false, bool endOfBlock = true, bool encodedByteAlign = false)
    {
        var d = new PdfDictionary();
        d[new PdfName("K")]         = new PdfNumber(k);
        d[new PdfName("Columns")]   = new PdfNumber(columns);
        if (rows.HasValue) d[new PdfName("Rows")] = new PdfNumber(rows.Value);
        d[new PdfName("BlackIs1")]          = blackIs1         ? PdfBoolean.True : PdfBoolean.False;
        d[new PdfName("EndOfBlock")]        = endOfBlock       ? PdfBoolean.True : PdfBoolean.False;
        d[new PdfName("EncodedByteAlign")]  = encodedByteAlign ? PdfBoolean.True : PdfBoolean.False;
        return d;
    }

    // ==================================================================
    //  /K > 0 — two-line stream: first line 1D, second line 2D (V0 V0)
    // ==================================================================

    [Fact]
    public void G3_Mixed_Tag1D_Then_Tag2D_ProducesIdenticalRows()
    {
        // Row 1: EOL + tag 1 (1D) + White-4 + Black-4  ⇒ byte 0x0F
        // Row 2: EOL + tag 0 (2D) + V0 + V0            ⇒ same as ref (0x0F)
        // End:   6× EOL (RTC) so endOfBlock=true cleanly stops
        const string bits =
            "000000000001" + "1"                 // row 1: EOL + 1D tag
            + "1011" + "011"                     // row 1: W-4 (1011), B-4 (011)
            + "000000000001" + "0"               // row 2: EOL + 2D tag
            + "1" + "1"                          // row 2: V0, V0
            + "000000000001" + "000000000001"    // RTC (1, 2)
            + "000000000001" + "000000000001"    // RTC (3, 4)
            + "000000000001" + "000000000001";   // RTC (5, 6)

        byte[] encoded = BitsToBytes(bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: 2, columns: 8, blackIs1: true));

        Assert.Equal(2, decoded.Length);
        Assert.Equal((byte)0x0F, decoded[0]);
        Assert.Equal((byte)0x0F, decoded[1]);
    }

    [Fact]
    public void G3_Mixed_AllLines1D_StillDecodes()
    {
        // /K = 4 with all-1D tag bits — should produce two identical rows
        // purely via the 1D path; no reference-line logic exercised.
        const string bits =
            "000000000001" + "1" + "1011" + "011" +    // row 1
            "000000000001" + "1" + "1011" + "011" +    // row 2 (also 1D)
            "000000000001" + "000000000001" +          // RTC start
            "000000000001" + "000000000001" +
            "000000000001" + "000000000001";

        byte[] encoded = BitsToBytes(bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: 4, columns: 8, blackIs1: true));

        Assert.Equal(2, decoded.Length);
        Assert.Equal((byte)0x0F, decoded[0]);
        Assert.Equal((byte)0x0F, decoded[1]);
    }

    [Fact]
    public void G3_K0_RegressionStillDecodesPure1D()
    {
        // Make sure routing /K == 0 through the pure 1D decoder still works
        // after the mixed-mode refactor.  EOL + W-8.
        const string bits = "000000000001" + "10011";
        byte[] encoded = BitsToBytes(bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: 0, columns: 8, rows: 1, endOfBlock: false));
        Assert.Single(decoded);
        Assert.Equal((byte)0xFF, decoded[0]);
    }

    [Fact]
    public void G3_K2_MalformedInput_FallsBackToRaw()
    {
        // Anything that blows up inside the decoder must NOT throw out of the
        // filter; the wrapper returns the raw bytes untouched.
        byte[] garbage = new byte[] { 0x55, 0xAA, 0x01, 0x00, 0x00 };
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(garbage, DecodeParms(k: 2, columns: 8));
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded);
    }
}
