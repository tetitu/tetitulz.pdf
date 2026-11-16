// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §9.6.6.4: a simple font's glyph is selected through the character its encoding gives the code,
/// one glyph a code; <c>/ToUnicode</c> says what the text means and takes no part. A code the CMap
/// maps to another letter, or to two characters, draws the same pixels as the page without the
/// CMap: where the glyph came from the text, the first drew that letter and the second drew one
/// glyph a character and moved every later glyph against its code. The control, the replacement
/// character, is the case the ladder already held.
/// </summary>
public sealed class GlyphSelectionByEncodingTests
{
    private const string Head = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
                                "/CMapName /T def /CMapType 2 def\n" +
                                "1 begincodespacerange <00> <FF> endcodespacerange\n";
    private const string Tail = "endcmap CMapName currentdict /CMap defineresource pop end end";

    [Theory]
    [InlineData("BT /F1 48 Tf 40 400 Td (A) Tj ET", "1 beginbfchar <41> <0042> endbfchar\n")]
    [InlineData("BT /F1 48 Tf 40 400 Td (AA) Tj ET", "1 beginbfchar <41> <00410042> endbfchar\n")]
    [InlineData("BT /F1 48 Tf 40 400 Td (A.) Tj ET", "1 beginbfchar <2E> <FFFD> endbfchar\n")]
    public void TheGlyphIsTheEncodings_WhateverToUnicodeSays(string content, string mapping)
    {
        byte[] ttf = ToUnicodeReplacementGlyphTests.LoadOpenSans();
        RenderedPage mapped = Render(ToUnicodeReplacementGlyphTests.BuildPdf(content, ttf, Head + mapping + Tail));
        RenderedPage plain = Render(ToUnicodeReplacementGlyphTests.BuildPdf(content, ttf, toUnicode: null));

        Assert.True(InkedPixels(plain) > 0, "the control drew nothing, so the comparison would say nothing");
        Assert.Equal(plain.Pixels, mapped.Pixels);
    }

    /// <summary>The potency control: another letter does draw other pixels.</summary>
    [Fact]
    public void AnotherLetter_DrawsOtherPixels()
    {
        byte[] ttf = ToUnicodeReplacementGlyphTests.LoadOpenSans();
        RenderedPage a = Render(ToUnicodeReplacementGlyphTests.BuildPdf("BT /F1 48 Tf 40 400 Td (A) Tj ET", ttf, null));
        RenderedPage b = Render(ToUnicodeReplacementGlyphTests.BuildPdf("BT /F1 48 Tf 40 400 Td (B) Tj ET", ttf, null));
        Assert.NotEqual(a.Pixels, b.Pixels);
    }

    private static RenderedPage Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfOperations.RenderPageToPixels(ms, 0, 72);
    }

    private static int InkedPixels(RenderedPage page)
    {
        int count = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.Pixels[y * page.Stride + x * 4] < 128) count++;
        return count;
    }
}
