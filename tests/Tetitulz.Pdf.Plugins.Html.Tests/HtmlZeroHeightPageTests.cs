// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// A page whose MediaBox has zero height is measured as zero points tall, so the layout's text scale
/// must come from the render scale, not from a division by that height. Otherwise every word's CSS
/// position is infinite.
/// </summary>
public class HtmlZeroHeightPageTests
{
    [Fact]
    public void ZeroHeightPage_TextOverlayPositionsStayFinite()
    {
        // The same-length swap keeps every byte offset in the file's xref valid: "792" -> "000".
        string source = Encoding.ASCII.GetString(MinimalPdfBuilder.CreateMinimalPdf("Hello World"));
        string zeroHeight = source.Replace("/MediaBox [0 0 612 792]", "/MediaBox [0 0 612 000]");
        Assert.NotEqual(source, zeroHeight);

        // The text layer of ConvertToHtml is where the text scale applies; the layout path positions
        // its lines from the render scale and never reads it.
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(zeroHeight));
        string html = PdfHtmlConverter.ConvertToHtml(ms, new HtmlConversionOptions { EmbedImages = false, IncludeText = true });

        Assert.Contains(">Hello</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
    }
}
