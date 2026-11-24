// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfDocumentBuilder.AddPage"/> is the one page every producer makes — the Builder's,
/// DocxToPdf's, HtmlToPdf's. Tested by <c>&lt;= 0</c> alone, which NaN passes, a NaN side would be
/// written as a <c>/MediaBox</c> of zeros, a page with no size, in a document that built without a
/// word. It is refused, naming the side; the control is that a positive size still makes a page of
/// that size.
/// </summary>
public sealed class PdfDocumentBuilderPageSizeTests
{
    [Theory]
    [InlineData(double.NaN, 100, "widthPt")]
    [InlineData(double.PositiveInfinity, 100, "widthPt")]
    [InlineData(100, double.NaN, "heightPt")]
    [InlineData(100, double.NegativeInfinity, "heightPt")]
    [InlineData(0, 100, "widthPt")]
    [InlineData(100, -1, "heightPt")]
    public void AddPage_NonFiniteOrNonPositiveSide_IsRefusedNamingIt(double w, double h, string parameter)
    {
        var builder = new PdfDocumentBuilder();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPage(w, h));
        Assert.Equal(parameter, ex.ParamName);
    }

    [Fact]
    public void AddPage_PositiveSides_MakeAPageOfThatSize()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(200, 300);
        using var output = new MemoryStream();
        builder.Build(output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        Assert.Equal(1, doc.Pages.Count);
        PdfArray box = doc.Pages[0].MediaBox;
        Assert.Equal((200.0, 300.0), (((PdfNumber)box[2]).DoubleValue, ((PdfNumber)box[3]).DoubleValue));
    }
}
