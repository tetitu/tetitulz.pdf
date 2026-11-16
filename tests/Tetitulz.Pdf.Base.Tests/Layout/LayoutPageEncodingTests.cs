// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Layout;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Layout;

/// <summary>
/// The layout page's text bytes and page size: written for the <c>/WinAnsiEncoding</c> Helvetica it
/// declares, and at the size asked for.
/// </summary>
public class LayoutPageEncodingTests
{
    /// <summary>
    /// Each character goes out as its WinAnsi code: € at 0x80, the en dash at 0x96, é at 0xE9.
    /// Hebrew alef and U+0080, which WinAnsi does not hold, go out as '?'. Through Latin-1 the euro
    /// and the dash were '?', and U+0080 came out as 0x80, a euro sign.
    /// </summary>
    [Fact]
    public void TextIsWrittenInWinAnsiCodes()
    {
        var doc = new PdfLayoutDocument();
        PdfPageBuilder page = doc.AddPage();
        page.DrawText("€–éא\u0080", 10, 10, 12, 0, 0, 0);

        byte[] content = page.Build();
        string latin1 = Encoding.Latin1.GetString(content);
        int open = latin1.IndexOf('(');
        int close = latin1.IndexOf(") Tj", StringComparison.Ordinal);
        byte[] literal = content.AsSpan(open + 1, close - open - 1).ToArray();

        Assert.Equal(new byte[] { 0x80, 0x96, 0xE9, (byte)'?', (byte)'?' }, literal);
    }

    /// <summary>
    /// A4, 595.28 by 841.89, is written as asked. The MediaBox was rounded to whole points, 595 by
    /// 842. The control, Letter's whole numbers, is unchanged.
    /// </summary>
    [Theory]
    [InlineData(595.28f, 841.89f, "/MediaBox [0 0 595.28 841.89]")]
    [InlineData(612f, 792f, "/MediaBox [0 0 612 792]")]
    public void TheMediaBoxIsTheSizeAskedFor(float width, float height, string expected)
    {
        var doc = new PdfLayoutDocument();
        doc.AddPage(width, height).DrawText("x", 10, 10, 12, 0, 0, 0);

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Contains(expected, Encoding.Latin1.GetString(ms.ToArray()), StringComparison.Ordinal);
    }
}
