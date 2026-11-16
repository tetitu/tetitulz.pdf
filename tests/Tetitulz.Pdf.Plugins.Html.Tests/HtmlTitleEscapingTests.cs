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
/// The title is written through the HTML encoder: every one of the five special characters becomes
/// its entity, the text around them is kept whole, and text with none passes unchanged.
/// </summary>
public class HtmlTitleEscapingTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("A & B", "A &amp; B")]
    [InlineData("A&B", "A&amp;B")]
    [InlineData("<b>x</b>", "&lt;b&gt;x&lt;/b&gt;")]
    [InlineData("say \"hi\" it's", "say &quot;hi&quot; it&#39;s")]
    public void Title_EscapesEachSpecialCharacterAndKeepsTheRestWhole(string title, string expected)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Body");
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, new HtmlConversionOptions { Title = title, EmbedImages = false });

        Assert.Contains($"<title>{expected}</title>", html, StringComparison.Ordinal);
    }
}
