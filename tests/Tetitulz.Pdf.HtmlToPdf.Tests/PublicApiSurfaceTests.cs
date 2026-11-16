// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.HtmlToPdf</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para><c>HtmlToPdfOptions</c> is where this package's baseline earns its keep. It is the whole
/// configuration contract for two very different engines — the managed one and the Playwright
/// browser one — so it accretes properties faster than anything else here, and a rename or a
/// changed default on it is a silent behaviour change for every caller that set the old name. The
/// baseline records constants' <i>values</i> too, which is what makes a changed default visible at
/// all.</para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly HtmlToPdfAssembly => typeof(HtmlToPdfConverter).Assembly;

    [ApiBaselineFact]
    public void HtmlToPdf_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(HtmlToPdfAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself. A baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever, so this asserts the render actually reached the package's entry
    /// surface: the static façade, the injectable service and its interface, the options object,
    /// both engine selectors, and the exception callers catch.
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(HtmlToPdfAssembly);

        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.HtmlToPdfConverter", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.HtmlToPdfConverterService", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.IHtmlToPdfConverter", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.HtmlToPdfOptions", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.HtmlToPdfException", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.HtmlRenderEngine", rendered);
        Assert.Contains("Tetitulz.Pdf.HtmlToPdf.BrowserEngine", rendered);

        // 65 lines today, 29 of them HtmlToPdfOptions members. A render that collapsed to type
        // declarations alone would be 7, and a render that produced nothing would be 0.
        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 45,
            $"Only {lines} lines rendered for Tetitulz.Pdf.HtmlToPdf; the renderer is filtering too much.");
    }
}
