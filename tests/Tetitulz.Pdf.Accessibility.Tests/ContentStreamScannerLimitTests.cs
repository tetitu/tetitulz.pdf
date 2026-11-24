// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The accessibility content scanner's ceilings, pinned at their values, and its edges. A page
/// this scanner refuses is left untagged rather than edited, so
/// each ceiling decides between "refuse" and "allocate for an attacker".
/// </summary>
public class ContentStreamScannerLimitTests
{
    private static ContentScanResult Scan(string content)
        => ContentStreamScanner.ScanDetailed(Encoding.Latin1.GetBytes(content));

    private static string Repeat(string item, int count) => string.Join(" ", Enumerable.Repeat(item, count));

    /// <summary>8,192 operands may wait for their operator; one more refuses the stream.</summary>
    [Fact]
    public void PendingOperandsAreCappedAt8192()
    {
        Assert.False(Scan(Repeat("1", 8192) + " n").LimitExceeded);

        var over = Scan(Repeat("1", 8193) + " n");
        Assert.True(over.LimitExceeded);
        Assert.Contains("8192 operands", over.Error);
    }

    /// <summary>One array operand may hold 500,000 elements; one more refuses the stream.</summary>
    [Fact]
    public void ArrayElementsAreCappedAt500000()
    {
        Assert.False(Scan("[" + Repeat("1", 500_000) + "] TJ").LimitExceeded);

        var over = Scan("[" + Repeat("1", 500_001) + "] TJ");
        Assert.True(over.LimitExceeded);
        Assert.Contains("500000 elements", over.Error);
    }

    /// <summary>Arrays nest 64 deep; a 65th level is refused.</summary>
    [Fact]
    public void NestingIsCappedAtSixtyFourLevels()
    {
        Assert.Null(Scan(new string('[', 64) + new string(']', 64) + " TJ").Error);

        var deeper = Scan(new string('[', 65) + new string(']', 65) + " TJ");
        Assert.Contains("nesting exceeds 64", deeper.Error);
    }

    /// <summary>A keyword of 256 bytes is read; at 257 the stream is refused as having no delimiter.</summary>
    [Fact]
    public void KeywordsAreCappedAt256Bytes()
    {
        var at = Scan(new string('a', 256));
        Assert.False(at.LimitExceeded);

        var over = Scan(new string('a', 257));
        Assert.True(over.LimitExceeded);
        Assert.Contains("runs past 256", over.Error);
    }

    /// <summary>A malformed number keeps its place as the operand 0, so operand counts hold.</summary>
    [Fact]
    public void AMalformedNumberIsTheOperandZero()
    {
        var token = Scan("--3 w").Tokens.Single(t => t.Operator == "w");
        Assert.Equal(0d, ((PdfNumber)token.Operands[0]).DoubleValue);
    }

    // ---------------------------------------------------------------- inline image edges

    /// <summary>
    /// An inline image ending in a lone <c>E</c> at the very end of the stream has no terminator.
    /// That is reported as such, both through a declared length and through the heuristic, and not
    /// as a scanner failure from reading past the end.
    /// </summary>
    [Theory]
    [InlineData("BI /W 8 /H 1 /F /CCF /L 3 ID abcE")]
    [InlineData("BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID abcE")]
    public void AnImageEndingInALoneEIsReportedAsUnterminated(string content)
    {
        Assert.Contains("no locatable EI terminator", Scan(content).Error);
    }

    /// <summary>
    /// The heuristic's look-ahead is the four bytes after a candidate <c>EI</c>, its delimiter included: a high byte in the
    /// fourth marks the candidate as sample data, and the image runs on to the real terminator.
    /// </summary>
    [Fact]
    public void AHighByteFourBytesAfterEiKeepsTheImageGoing()
    {
        var result = Scan("BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID ab EI xyÿ cd EI Q");

        Assert.Null(result.Error);
        Assert.Equal(new[] { "BI", "Q" }, result.Tokens.Select(t => t.Operator));
    }
}
