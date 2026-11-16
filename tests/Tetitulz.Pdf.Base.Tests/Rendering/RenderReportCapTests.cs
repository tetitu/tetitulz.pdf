// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The per-page report allowance, <c>PdfLimits.MaxRenderReportsPerKindPerPage</c>: while a page's
/// operators run, each kind of render diagnostic is delivered at most that many times, the last
/// saying the rest are suppressed.
/// </summary>
/// <remarks>
/// A report site reached once an operator or a colour sample reports as often as the file repeats
/// it. A shading in a Separation space whose alternate is <c>/Pattern</c>, drawn a million times
/// through three levels of forms, is 2 KB and was 22 million reports; the structure fuzzer's render
/// corpus held pages of 111 000.
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class RenderReportCapTests
{
    private const int Fills = 10 * 10 * 10;

    [Fact]
    public void AdversarialRepeatedReportSite_IsDeliveredUpToThePagesAllowance()
    {
        byte[] pdf = RepeatedPatternAlternateFills(withOtherKindAfter: false);
        try
        {
            PdfLimits.MaxRenderReportsPerKindPerPage = 7;
            foreach (var reports in new[] { Raster(pdf), Svg(pdf) })
            {
                var pattern = reports.Where(r => r.Kind == RenderDiagnosticKind.PatternOperandNoName).ToList();
                Assert.Equal(7, pattern.Count);
                Assert.Contains("MaxRenderReportsPerKindPerPage", pattern[6].Detail, StringComparison.Ordinal);
                Assert.All(pattern.Take(6), r => Assert.DoesNotContain("MaxRenderReportsPerKindPerPage", r.Detail, StringComparison.Ordinal));
            }

            // Potency: without the allowance every fill reports, twice (its colour is evaluated when
            // it is set and when it is painted).
            PdfLimits.MaxRenderReportsPerKindPerPage = int.MaxValue;
            int raster = Raster(pdf).Count(r => r.Kind == RenderDiagnosticKind.PatternOperandNoName);
            int svg = Svg(pdf).Count(r => r.Kind == RenderDiagnosticKind.PatternOperandNoName);
            Assert.Equal((2 * Fills, 2 * Fills), (raster, svg));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void TheAllowance_IsPerKind_AnotherKindIsStillReportedAfterOneIsSpent()
    {
        byte[] pdf = RepeatedPatternAlternateFills(withOtherKindAfter: true);
        try
        {
            PdfLimits.MaxRenderReportsPerKindPerPage = 7;
            foreach (var reports in new[] { Raster(pdf), Svg(pdf) })
            {
                Assert.Equal(7, reports.Count(r => r.Kind == RenderDiagnosticKind.PatternOperandNoName));
                Assert.Contains(reports, r => r.Kind == RenderDiagnosticKind.ImageDecodeFailed);
            }
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void EachRender_HasItsOwnAllowance_AndReportsOutsideAPageAreNotCounted()
    {
        byte[] pdf = RepeatedPatternAlternateFills(withOtherKindAfter: false);
        try
        {
            PdfLimits.MaxRenderReportsPerKindPerPage = 7;
            var reports = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                Render(pdf);
                Render(pdf);
                // Outside any page: every one is delivered.
                for (int i = 0; i < 20; i++)
                    RenderDiagnostics.Report(RenderDiagnosticKind.PatternOperandNoName, "outside");
            });
            var pattern = reports.Where(r => r.Kind == RenderDiagnosticKind.PatternOperandNoName).ToList();
            Assert.Equal(7 + 7 + 20, pattern.Count);
            Assert.Equal(20, pattern.Count(r => r.Detail == "outside"));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static void Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
    }

    private static IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> Raster(byte[] pdf)
        => RenderFormXObjectBudgetTests.CaptureDiagnostics(() => Render(pdf));

    private static IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> Svg(byte[] pdf)
        => RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });

    /// <summary>
    /// A thousand one-unit fills in a Separation space whose alternate is <c>/Pattern</c> (§8.6.6.4
    /// forbids it): ten draws of a form drawing ten of a form of ten fills. Each fill evaluates its
    /// colour through the alternate, and each evaluation reports. With
    /// <paramref name="withOtherKindAfter"/>, the page then draws an image whose colour-key
    /// <c>/Mask</c> has the wrong number of entries.
    /// </summary>
    private static byte[] RepeatedPatternAlternateFills(bool withOtherKindAfter)
    {
        string Repeat(string s) => string.Join("\n", Enumerable.Repeat(s, 10)) + "\n";
        const string fn = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>";
        string page = Repeat("/A Do") + (withOtherKindAfter ? "q 10 0 0 10 0 0 cm /Im Do Q\n" : "");
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                "/Resources << /XObject << /A 5 0 R /Im 8 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", page),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /B 6 0 R >> >>",
                Repeat("/B Do")),
            RenderFormXObjectBudgetTests.StreamObj(6, " /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /ColorSpace << /S 7 0 R >> >>",
                Repeat("q /S cs 1 scn 0 0 1 1 re f Q")),
            RenderFormXObjectBudgetTests.Obj(7, "[/Separation /Spot /Pattern " + fn + "]"),
            RenderFormXObjectBudgetTests.StreamObj(8, " /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 " +
                "/ColorSpace /DeviceRGB /Mask [0 0 0 0] /Filter /ASCIIHexDecode", "FF0000FF0000FF0000FF0000>\n"),
        });
    }
}
