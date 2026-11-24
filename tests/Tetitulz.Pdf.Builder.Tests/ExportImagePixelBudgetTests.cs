// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// One export's pictures decode under one budget of pixels (<c>PdfLimits.MaxImagePixelsPerExport</c>): each
/// picture is decoded once and held to the authored-image size, and neither bounds how many there are.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class ExportImagePixelBudgetTests
{
    /// <summary>
    /// Ten distinct eight-by-eight pictures under a budget of 200 pixels: three are drawn, the rest are not, and
    /// the cut is said once. The control, under the default budget, draws all ten and says nothing.
    /// </summary>
    [Theory]
    [InlineData(200L, 3, 1)]
    [InlineData(PdfLimits.DefaultMaxImagePixelsPerExport, 10, 0)]
    public void AdversarialManyPictures_AreDecodedUnderTheExportsPixelBudget(long budget, int drawn, int reports)
    {
        var section = new Section();
        for (int i = 0; i < 10; i++)
        {
            var p = new Paragraph();
            p.Runs.Add(new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 20, HeightPt = 20 });
            section.Children.Add(p);
        }
        var doc = new Document();
        doc.Sections.Add(section);

        var events = new List<BuilderDiagnostic>();
        void Listen(BuilderDiagnostic d) => events.Add(d);
        BuilderDiagnostics.OnEvent += Listen;
        try
        {
            PdfLimits.MaxImagePixelsPerExport = budget;
            string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

            Assert.Equal(drawn, Regex.Matches(content, @"/\S+ Do\b").Count);
            Assert.Equal(reports, events.Count(e => e.Detail.Contains("PdfLimits.MaxImagePixelsPerExport", System.StringComparison.Ordinal)));
        }
        finally
        {
            BuilderDiagnostics.OnEvent -= Listen;
            PdfLimits.ResetToDefaults();
        }
    }
}
