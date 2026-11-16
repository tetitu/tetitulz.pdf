// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The word spans of the layout HTML: each word of a line is its own <c>pdf-word</c> span, and the
/// words are separated by one space, with none before the first.
/// </summary>
public class HtmlWordSpanTests
{
    [Fact]
    public void WordsOnALine_AreSeparatedByOneSpaceAndTheFirstHasNoLeadingSpace()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, new HtmlConversionOptions { EmbedImages = false });

        Assert.Contains("<span class=\"pdf-word\">Hello</span> <span class=\"pdf-word\">World</span>", html, StringComparison.Ordinal);
        Assert.Contains("\"><span class=\"pdf-word\">Hello</span>", html, StringComparison.Ordinal);
    }
}
