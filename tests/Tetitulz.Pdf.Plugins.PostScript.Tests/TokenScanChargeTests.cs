// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>string token</c> is charged the bytes its scan reads, at half a step a byte (the measured price: about 44 ns a
/// byte against 92 ns a step). Only the string's copy was charged, a step per 64 bytes, so a 100 000-byte token cost
/// 1 563 steps and 4.4 ms of scanning: a loop of them ran a minute inside a budget meant to bound two seconds.
/// </summary>
/// <remarks>
/// The evidence is the budget: fifty tokens of a 100 000-byte word scan five million bytes, which at half a step
/// each spend a budget of one million; charged only for the copy they are some eighty thousand steps. The control,
/// a ten-byte word, stays well inside.
/// </remarks>
public class TokenScanChargeTests
{
    private static PostScriptConversionResult Run(int length)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
                $"/a {length} string def 0 1 {length - 1} {{ a exch 97 put }} for 1 1 50 {{ pop a token pop pop pop }} for"),
            new PostScriptConversionOptions { MaxTotalOps = 1_000_000 });

    [Fact]
    public void ALongToken_SpendsTheBudgetByWhatItsScanReads()
    {
        Assert.True(Run(100_000).BudgetExhausted, "fifty 100 000-byte tokens must spend the budget");
        Assert.False(Run(10).BudgetExhausted, "fifty ten-byte tokens must not");
    }
}
