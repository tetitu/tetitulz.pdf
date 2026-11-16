// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A separator inside a quoted string separates nothing: the one CSS splitter
/// (<c>HtmlToBuilderMapper.SplitTopLevel</c>) reads declarations, tokens and comma lists past a
/// quoted <c>;</c> or <c>,</c>. The dedup sweep's row D10 (2026-10-21) made it ignore quotes and no
/// test failed. Pure primaries make the fill operator name the colour: <c>1 0 0 rg</c> is red,
/// <c>0 0 1 rg</c> blue.
/// </summary>
public class CssQuotedSeparatorTests
{
    private const string Red = "1 0 0 rg";
    private const string Blue = "0 0 1 rg";

    [Fact]
    public void ASemicolonInsideAQuotedValue_DoesNotEndTheDeclaration()
    {
        // The quoted text is the font family's name; a split inside it would read a later
        // "color:#0000ff" declaration — whole, the closing quote left to the piece after it — and
        // draw the word blue.
        string dec = DecodeStreams(ToPdf(
            "<p style=\"color: #ff0000; font-family: 'Odd;color:#0000ff;x'\">Word</p>"));

        Assert.Contains(Red, dec, StringComparison.Ordinal);
        Assert.DoesNotContain(Blue, dec, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDeclarationOutsideTheQuotes_IsRead()
    {
        // The control: written outside a string, the later colour wins, so the test above can see
        // a split if one happens.
        string dec = DecodeStreams(ToPdf(
            "<p style=\"color: #ff0000; font-family: 'Odd'; color:#0000ff\">Word</p>"));

        Assert.Contains(Blue, dec, StringComparison.Ordinal);
        Assert.DoesNotContain(Red, dec, StringComparison.Ordinal);
    }
}
