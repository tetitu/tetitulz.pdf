// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Comparing two strings — <c>eq</c>, <c>ne</c>, <c>gt</c>, <c>ge</c>, <c>lt</c>, <c>le</c> — reads them byte by byte,
/// and is charged the shorter's length, as every other walk of a string is. Uncharged, each comparison was one step
/// of <c>PsLimits.MaxTotalOps</c>: <c>a b eq</c> over two equal million-byte strings cost five milliseconds, and a
/// loop of it ran for hours inside the twenty-million-step budget.
/// </summary>
/// <remarks>
/// The evidence is the budget, never a clock: fifty comparisons of 100 000-byte strings are five million bytes,
/// past a budget of one million; uncharged they are a few hundred steps. The control is the same loop over
/// 1 000-byte strings, which a charge of fifty thousand leaves well inside the budget.
/// </remarks>
public class StringComparisonChargeTests
{
    private static PostScriptConversionResult Run(int length, string comparison)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
                $"/a {length} string def /b {length} string def 1 1 50 {{ pop a b {comparison} pop }} for"),
            new PostScriptConversionOptions { MaxTotalOps = 1_000_000 });

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    [InlineData("lt")]
    [InlineData("ge")]
    public void ComparingLongStrings_SpendsTheBudgetByTheirLength(string comparison)
    {
        Assert.True(Run(100_000, comparison).BudgetExhausted, "fifty comparisons of 100 000 bytes must spend the budget");
        Assert.False(Run(1_000, comparison).BudgetExhausted, "fifty comparisons of 1 000 bytes must not");
    }
}
