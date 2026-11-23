// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// The structured text and graphics extractors charge a form's decoded content to the page, as the renderers do,
/// and parse a form once a page however often the page draws it.
/// </summary>
/// <remarks>
/// An invocation ceiling alone bounds nothing a form's size chooses: a megabyte of operators compressed to a few
/// hundred bytes, drawn a hundred thousand times, is a hundred gigabytes of operators run inside it.
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class ExtractorFormContentBudgetTests
{
    private const int Draws = 30;

    /// <summary>
    /// Under a ceiling of ten forms' content the text extractor runs ten draws, says why it stopped, and
    /// keeps the text the ten drew. The control, under the default ceiling, reads all thirty and says nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdversarialFormDrawnManyTimes_TheTextExtractorChargesItsContent(bool capped)
    {
        byte[] pdf = Pdf(out int formLength);
        try
        {
            if (capped) PdfLimits.MaxFormContentBytesPerPage = 10L * formLength;
            var page = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);
            string error = PdfStructuredTextExtractor.LastError;

            int shown = page.Lines.Sum(l => l.Text.Count(c => c == 'A'));
            Assert.Equal(capped ? 10 : Draws, shown);
            if (capped) Assert.Contains("PdfLimits.MaxFormContentBytesPerPage", error, StringComparison.Ordinal);
            else Assert.Null(error);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The graphics extractor under the same ceiling: ten rulings, the stop said; the control has thirty.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdversarialFormDrawnManyTimes_TheGraphicsExtractorChargesItsContent(bool capped)
    {
        byte[] pdf = Pdf(out int formLength);
        try
        {
            if (capped) PdfLimits.MaxFormContentBytesPerPage = 10L * formLength;
            var page = PdfGraphicsExtractor.ExtractPage(new MemoryStream(pdf), 0);
            string error = PdfGraphicsExtractor.LastError;

            Assert.Equal(capped ? 10 : Draws, page.HorizontalRulings.Count);
            if (capped) Assert.Contains("PdfLimits.MaxFormContentBytesPerPage", error, StringComparison.Ordinal);
            else Assert.Null(error);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>A form the page draws thirty times is parsed once, by either extractor.</summary>
    [Theory]
    [InlineData("text")]
    [InlineData("graphics")]
    public void AFormDrawnManyTimes_IsParsedOnceAPage(string extractor)
    {
        byte[] pdf = Pdf(out _);
        long before = FormContentBudget.FormParses;
        int found = extractor == "text"
            ? PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0).Lines.Count
            : PdfGraphicsExtractor.ExtractPage(new MemoryStream(pdf), 0).HorizontalRulings.Count;

        Assert.Equal(Draws, found);
        Assert.Equal(1, FormContentBudget.FormParses - before);
    }

    /// <summary>
    /// A page drawing one form <see cref="Draws"/> times, each draw fifteen points above the last; the form shows
    /// an "A" and strokes a ten-point horizontal ruling, padded with some 50 KB of <c>q Q</c> pairs.
    /// </summary>
    private static byte[] Pdf(out int formLength)
    {
        var form = new StringBuilder("BT /F1 12 Tf 0 0 Td (A) Tj ET 0 5 m 10 5 l S\n");
        while (form.Length < 50_000) form.Append("q Q\n");
        formLength = form.Length;
        var page = new StringBuilder();
        for (int i = 0; i < Draws; i++) page.Append($"q 1 0 0 1 20 {20 + i * 15} cm /F Do Q\n");
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 {40 + Draws * 15}] /Contents 4 0 R " +
                "/Resources << /XObject << /F 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", page.ToString()),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 20 20] " +
                "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>", form.ToString()),
        });
    }
}
