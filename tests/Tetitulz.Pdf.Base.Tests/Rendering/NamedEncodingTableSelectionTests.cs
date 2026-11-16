// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>FontCache.GetNamedEncodingTable</c> turns a §9.6.6 <c>/Encoding</c> or <c>/BaseEncoding</c>
/// name into the char code → glyph name table that resolution walks. This asserts which table each
/// name selects, at the codes where the tables disagree.
///
/// <para><b>Why 0x27 and 0x60.</b> They are the only two codes below 0x80 where StandardEncoding
/// and WinAnsiEncoding differ — <c>quoteright</c>/<c>quoteleft</c> against
/// <c>quotesingle</c>/<c>grave</c> — so they are the whole observable difference for ordinary Latin
/// text. Selecting the wrong table there fails nothing and throws nothing: the font is found, the
/// page draws, and one character is a different glyph.</para>
///
/// <para><b>What it guards specifically.</b> The StandardEncoding branch no longer returns a table
/// FontCache owns; it returns <c>Core.Fonts.AdobeStandardEncodingNames.Table</c>, the single copy
/// that <c>Type1CharstringEvaluator</c> reads too. Repointing that branch at one of the three
/// FontCache-local tables sitting beside it is a one-token edit that compiles.</para>
/// </summary>
public class NamedEncodingTableSelectionTests
{
    [Theory]
    [InlineData("StandardEncoding", 0x27, "quoteright")]
    [InlineData("StandardEncoding", 0x60, "quoteleft")]
    [InlineData("StandardEncoding", 0xA4, "fraction")]     // WinAnsi has "currency" here
    [InlineData("WinAnsiEncoding", 0x27, "quotesingle")]
    [InlineData("WinAnsiEncoding", 0x60, "grave")]
    [InlineData("WinAnsiEncoding", 0xA4, "currency")]
    [InlineData("MacRomanEncoding", 0x27, "quotesingle")]
    [InlineData("MacExpertEncoding", 0x27, "Acutesmall")]
    public void EachNamedEncodingSelectsTheTableThatDefinesThatCode(string encoding, int code, string expected)
    {
        IReadOnlyList<string> table = FontCache.GetNamedEncodingTable(encoding);

        Assert.NotNull(table);
        Assert.Equal(256, table.Count);
        Assert.Equal(expected, table[code]);
    }

    /// <summary>
    /// An unrecognised name yields <c>null</c>, which is what makes the caller leave the font's own
    /// encoding alone. Returning a default table instead would silently re-map every code.
    /// </summary>
    [Theory]
    [InlineData("PDFDocEncoding")]
    [InlineData("winansiencoding")]      // §7.3.5 names are case-sensitive
    [InlineData("")]
    public void AnUnrecognisedEncodingNameSelectsNoTable(string encoding)
    {
        Assert.Null(FontCache.GetNamedEncodingTable(encoding));
    }
}
