// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// A CCITT decode's output is sized by <c>/Columns</c> and the rows the data names, not by its
/// bytes — a row of one Group 4 <c>V0</c> code is a whole white row — so each row is charged to
/// the caller's budget (<see cref="StreamFilter.Work"/>, the page's image decoding) before it is
/// decoded, and the caller's refusal reaches the caller as itself, never as a raw-bytes fallback.
/// </summary>
public sealed class CcittFaxWorkBudgetTests
{
    private const int Columns = 64, Rows = 2000;

    public static readonly TheoryData<int> Codings = new() { -1, 0, 1 };

    [Theory]
    [MemberData(nameof(Codings))]
    public void Adversarial_rows_past_the_callers_budget_are_refused_by_it(int k)
    {
        var page = new WorkBudget(10_000, "page image work");

        var ex = Assert.Throws<WorkBudgetExceededException>(() => Decode(k, page));

        Assert.Same(page, ex.Budget);
    }

    /// <summary>The control: with room, the same data decodes whole and costs exactly its pixels.</summary>
    [Theory]
    [MemberData(nameof(Codings))]
    public void Rows_within_the_callers_budget_decode_and_are_charged_their_pixels(int k)
    {
        var page = new WorkBudget(1_000_000, "page image work");

        byte[] decoded = Decode(k, page);

        Assert.Equal(Rows * Columns / 8, decoded.Length);
        Assert.All(decoded, b => Assert.Equal(0xFF, b));   // white rows: under /BlackIs1 false a 1 bit is white
        Assert.Equal((long)Rows * Columns, page.Spent);
    }

    private static byte[] Decode(int k, WorkBudget work)
    {
        var parms = new PdfDictionary();
        parms.Set(PdfName.K, new PdfNumber(k));
        parms.Set(PdfName.Columns, new PdfNumber(Columns));
        parms.Set(PdfName.Rows, new PdfNumber(Rows));
        return FilterPipeline.Decode(Encoded(k), PdfName.CCITTFaxDecode, parms, PdfLimits.MaxDecompressedBytes, work);
    }

    /// <summary>
    /// <see cref="Rows"/> white rows of <see cref="Columns"/> pixels: Group 4 a <c>V0</c> each; Group 3
    /// 1-D a white 64 make-up (11011) and a white 0 terminator (00110101) each; Group 3 2-D an EOL,
    /// the 2-D tag (0) and a <c>V0</c> each.
    /// </summary>
    private static byte[] Encoded(int k)
    {
        var bits = new List<bool>();
        void Put(string s) { foreach (char c in s) bits.Add(c == '1'); }
        for (int r = 0; r < Rows; r++)
        {
            if (k < 0) Put("1");
            else if (k == 0) Put("11011" + "00110101");
            else Put("000000000001" + (r == 0 ? "1" + "11011" + "00110101" : "0" + "1"));
        }
        var bytes = new byte[(bits.Count + 7) / 8 + 4];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }
}
