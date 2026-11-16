// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// What a large operand costs <c>resourcestatus</c>, <c>findresource</c> and <c>composefont</c>: a
/// string key was decoded once for every value an implicit category has, and an array scanned whole
/// for its first name, each for one tick; and <c>findresource</c> quoted the whole key in its error.
/// </summary>
public sealed class ResourceKeyCostTests
{
    private static PostScriptConversionResult Convert(string program, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program), options);

    private static readonly PostScriptConversionOptions Budget = new() { MaxTotalOps = 200_000 };

    [Theory]
    [InlineData(100_000, true)]
    [InlineData(1, false)]
    public void AdversarialResourceStatusOverALargeStringKey_IsChargedByItsLength(int length, bool exhausted)
    {
        var r = Convert($"/s {length} string def 3000 {{ s /Filter resourcestatus pop }} repeat", Budget);
        Assert.Equal(exhausted, r.BudgetExhausted);
    }

    [Theory]
    [InlineData(100_000, true)]
    [InlineData(1, false)]
    public void AdversarialComposeFontOverALargeArray_IsChargedByWhatItScans(int length, bool exhausted)
    {
        var r = Convert($"/a {length} array def 3000 {{ a composefont pop }} repeat", Budget);
        Assert.Equal(exhausted, r.BudgetExhausted);
    }

    [Fact]
    public void AdversarialFindResourceOfAHugeKey_QuotesItsFirst64Characters()
    {
        Convert("(" + new string('A', 100_000) + ") /Foo findresource");

        string last = PostScriptDiagnostics.LastError;
        Assert.Contains("/" + new string('A', 64) + "… in /Foo", last, StringComparison.Ordinal);
        Assert.InRange(last.Length, 0, 1000);
    }

    /// <summary>The control: a short key is quoted whole.</summary>
    [Fact]
    public void FindResourceOfAShortKey_QuotesItWhole()
    {
        Convert("(Short) /Foo findresource");
        Assert.Contains("/Short in /Foo", PostScriptDiagnostics.LastError, StringComparison.Ordinal);
    }
}
