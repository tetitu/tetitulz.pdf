// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG export asks the heavy-CFF question the raster path asks before it hands a program to
/// Skia: a bare CFF program found heavy is outlined by our evaluator rather than through its OTF
/// wrapper, and an OpenType CFF program Skia loaded itself through the hinting probe's outlines —
/// each reported once. Our outlines are in charstring space, so their <c>use</c> carries the
/// 1/1000 glyph scale with the 24 pt size (0.024); Skia's are in em units and carry 24. The
/// control, a real program, stays on Skia: three outlines at 24, nothing said.
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class SvgHeavyCffGuardTests
{
    public static TheoryData<string> Programs => new() { "bare", "opentype" };

    [Theory]
    [MemberData(nameof(Programs))]
    public void AHeavyProgram_IsOutlinedByOurEvaluator_AndSaidOnce(string program)
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 10_000;
            PdfLimits.MaxCharstringStepsPerPage = 30_000;
            byte[] pdf = Pdf(program, CffHintingHeavyFontTests.BoxThenBomb);
            string svg = null;

            var events = CharstringPageBudgetTests.Collect(
                () => svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0),
                RenderDiagnosticKind.FontHintingSkipped);

            Assert.Single(events);
            var scales = UseScales(svg);
            Assert.NotEmpty(scales);
            Assert.All(scales, a => Assert.InRange(a, 0.023f, 0.025f));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void ARealProgram_StaysOnSkia(string program)
    {
        byte[] pdf = Pdf(program, CffHintingHeavyFontTests.Box);
        string svg = null;

        var events = CharstringPageBudgetTests.Collect(
            () => svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0),
            RenderDiagnosticKind.FontHintingSkipped);

        Assert.Empty(events);
        var scales = UseScales(svg);
        Assert.Equal(3, scales.Count);
        Assert.All(scales, a => Assert.InRange(a, 23.9f, 24.1f));
    }

    private static byte[] Pdf(string program, byte[] glyph) => program == "bare"
        ? CharstringPageBudgetTests.PagePdf(glyph)
        : CffHintingHeavyFontTests.OpenTypePdf(glyph);

    /// <summary>The <c>a</c> term of each drawn glyph's <c>use</c> matrix: the outline's own scale times the size.</summary>
    private static List<float> UseScales(string svg) =>
        XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "use")
            .Select(e => Regex.Match(e.Attribute("transform")!.Value, @"matrix\((-?\d+(?:\.\d+)?)"))
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
}
