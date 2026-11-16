// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Adversarial-input guard: a run's make-up codes are summed into an <c>int</c>, and T.4 lets a
/// make-up code repeat as often as the data holds. 838 861 codes of 2560 — a megabyte and a half of
/// bits — sum past <see cref="int.MaxValue"/>; without a ceiling the run comes back negative, the
/// changing element moves backwards and the row is misdecoded with nothing said. The ceiling is
/// <see cref="CcittFaxDecoder.MaxRun"/>; the control is two make-up codes and a terminating code,
/// summed exactly.
/// </summary>
public class CcittFaxRunOverflowTests
{
    private const string WhiteMakeup2560 = "000000011111";   // T.4 extended make-up, shared by both colours
    private const string WhiteTerminating0 = "00110101";
    private const string WhiteTerminating3 = "1000";

    [Fact]
    public void DecodeColorRun_RepeatedMakeupCodesPastIntRange_AnswersTheCeilingNotANegativeRun()
    {
        const int codes = 838_861;                     // 838 861 × 2560 = 2 147 484 160 > int.MaxValue
        var bits = new StringBuilder(codes * WhiteMakeup2560.Length + 8);
        for (int i = 0; i < codes; i++) bits.Append(WhiteMakeup2560);
        bits.Append(WhiteTerminating0);

        int run = CcittFaxDecoder.DecodeColorRun(new MsbBitStream(Bytes(bits.ToString())), isBlack: false);

        Assert.Equal(CcittFaxDecoder.MaxRun, run);
        Assert.True(run > 0, "a run is a count of pixels and cannot be negative");
    }

    [Fact]
    public void DecodeColorRun_TwoMakeupCodesAndATerminatingCode_SumsExactly()
    {
        string bits = WhiteMakeup2560 + WhiteMakeup2560 + WhiteTerminating3;

        int run = CcittFaxDecoder.DecodeColorRun(new MsbBitStream(Bytes(bits)), isBlack: false);

        Assert.Equal(2560 + 2560 + 3, run);
    }

    private static byte[] Bytes(string bits)
    {
        var bytes = new List<byte>((bits.Length + 7) / 8);
        int acc = 0, n = 0;
        foreach (char c in bits)
        {
            acc = (acc << 1) | (c == '1' ? 1 : 0);
            if (++n == 8) { bytes.Add((byte)acc); acc = 0; n = 0; }
        }
        if (n > 0) bytes.Add((byte)(acc << (8 - n)));
        return bytes.ToArray();
    }
}
