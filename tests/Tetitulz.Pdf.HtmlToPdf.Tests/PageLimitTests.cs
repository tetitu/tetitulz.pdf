// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <see cref="HtmlToPdfOptions.MaxPages"/> (2026-10-20, the owner's decision: a parameter, unlimited by default):
/// markup that would run past the limit ends at it, reported through <see cref="HtmlToPdfOptions.OnDiagnostic"/> —
/// the channel that until the same day heard the font resolver's reports alone.
/// </summary>
public class PageLimitTests
{
    /// <summary>The amplification the limit exists for: a font of a thousand points over a few hundred words.</summary>
    private static readonly string Amplifying =
        "<html><body><p style='font-size:1000pt'>" + string.Join(" ", Enumerable.Repeat("word", 300)) + "</p></body></html>";

    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.Pages.Count;
    }

    [Fact]
    public void MarkupPastTheLimit_EndsAtItAndSaysSo()
    {
        int unlimited = Pages(HtmlToPdfConverter.ConvertHtmlToBytes(Amplifying, new HtmlToPdfOptions()));
        Assert.True(unlimited > 10, $"the control runs past the limit: {unlimited} pages");   // potency

        var seen = new List<BuilderDiagnostic>();
        byte[] limited = HtmlToPdfConverter.ConvertHtmlToBytes(Amplifying, new HtmlToPdfOptions { MaxPages = 5, OnDiagnostic = seen.Add });
        Assert.Equal(5, Pages(limited));
        Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);
    }

    [Fact]
    public void ANegativeLimit_IsRefused_AndZeroIsNoLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HtmlToPdfOptions { MaxPages = -1 });
        Assert.Equal(0, new HtmlToPdfOptions().MaxPages);
    }
}
