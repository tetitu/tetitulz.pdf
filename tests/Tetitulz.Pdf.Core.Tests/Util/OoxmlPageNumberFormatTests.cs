// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// <see cref="OrdinalNumbering.FormatOoxmlNumber"/> — the five <c>w:pgNumType/@w:fmt</c> values a
/// page number is written in, and the decimal every other value falls to.
/// </summary>
/// <remarks>
/// <para><b>Why this is one function and not two.</b> The Builder's PDF exporter and
/// <c>DocxToPdf</c>'s legacy renderer each keyed the same five strings onto the same two
/// <see cref="OrdinalNumbering"/> primitives, in switches that had to agree and whose agreement was
/// asserted only by a comment. The vocabulary is OOXML's, so it belongs beside the primitives it is
/// written in terms of, where both features reach it without either referencing the other.</para>
///
/// <para><b>It is not the PDF page-label vocabulary.</b> ISO 32000-1 Table 159 spells its styles
/// <c>/R</c>, <c>/r</c>, <c>/A</c>, <c>/a</c>, <c>/D</c> and its alphabet is *repeated* letters, so
/// a Table 159 style must never be passed here — the last two rows below pin that a Table 159
/// spelling falls to decimal rather than being silently honoured as something else.</para>
/// </remarks>
public class OoxmlPageNumberFormatTests
{
    [Theory]
    [InlineData("upperRoman", 4, "IV")]
    [InlineData("lowerRoman", 4, "iv")]
    [InlineData("upperLetter", 4, "D")]
    [InlineData("lowerLetter", 4, "d")]
    [InlineData("decimal", 4, "4")]
    public void EachFormatWritesItsOwnAlphabet(string format, int n, string expected)
        => Assert.Equal(expected, OrdinalNumbering.FormatOoxmlNumber(n, format));

    /// <summary>Past one letter, so a caller cannot pass by reading the first 26 values alone.</summary>
    [Theory]
    [InlineData("lowerLetter", 27, "aa")]
    [InlineData("lowerLetter", 28, "ab")]
    [InlineData("upperLetter", 28, "AB")]
    [InlineData("upperRoman", 1990, "MCMXC")]
    public void TheFormatsAgreeWithTheirPrimitivesPastTheEasyRange(string format, int n, string expected)
        => Assert.Equal(expected, OrdinalNumbering.FormatOoxmlNumber(n, format));

    /// <summary>
    /// Anything the vocabulary does not name is decimal — including a PDF page-label style, which
    /// is a different vocabulary that must not be honoured here.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cardinalText")]
    [InlineData("R")]
    [InlineData("a")]
    public void AnUnnamedFormatIsDecimal(string? format)
        => Assert.Equal("7", OrdinalNumbering.FormatOoxmlNumber(7, format));

    /// <summary>
    /// The case of the format name is part of it: OOXML spells these in camel case, and matching
    /// loosely would accept a Table 159 <c>"A"</c> as <c>upperLetter</c>.
    /// </summary>
    [Theory]
    [InlineData("UPPERROMAN")]
    [InlineData("UpperRoman")]
    [InlineData("lowerroman")]
    public void TheFormatNameIsMatchedExactly(string format)
        => Assert.Equal("4", OrdinalNumbering.FormatOoxmlNumber(4, format));

    /// <summary>
    /// Out of Roman range the answer is decimal, the fallback every caller already depended on.
    /// </summary>
    [Theory]
    [InlineData(0, "0")]
    [InlineData(-3, "-3")]
    [InlineData(4000, "4000")]
    public void OutOfRomanRangeIsDecimal(int n, string expected)
        => Assert.Equal(expected, OrdinalNumbering.FormatOoxmlNumber(n, "upperRoman"));
}
