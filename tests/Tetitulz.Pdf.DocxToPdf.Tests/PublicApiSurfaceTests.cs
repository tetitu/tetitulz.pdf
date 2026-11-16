// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.DocxToPdf</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para>This package is deliberately a <i>thin</i> public surface over a large private one: four
/// exported types and an interface in front of a whole rendering stack (layout, drawings, charts,
/// math typesetting) that is entirely <c>internal</c>. That ratio is what makes the baseline useful
/// here — the surface is small enough that any addition to it is a decision worth seeing in a diff,
/// and promoting a renderer type to <c>public</c> by accident is exactly the mistake it catches.
/// </para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly DocxToPdfAssembly => typeof(DocxToPdfConverter).Assembly;

    [ApiBaselineFact]
    public void DocxToPdf_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(DocxToPdfAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself. A baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever, so this asserts the render actually reached the package's whole
    /// entry surface: the static façade, the injectable service and its interface, the options
    /// object, the engine selector and the exception callers catch.
    ///
    /// <para>The line minimum is drawn from what this package really exports — a deliberately small
    /// number, because most of the assembly is internal. Setting it at the level of a
    /// <i>bigger</i> package would be the same mistake as setting it to zero, in the other
    /// direction.</para>
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(DocxToPdfAssembly);

        Assert.Contains("Tetitulz.Pdf.DocxToPdf.DocxToPdfConverter", rendered);
        Assert.Contains("Tetitulz.Pdf.DocxToPdf.DocxToPdfConverterService", rendered);
        Assert.Contains("Tetitulz.Pdf.DocxToPdf.IDocxToPdfConverter", rendered);
        Assert.Contains("Tetitulz.Pdf.DocxToPdf.DocxToPdfOptions", rendered);
        Assert.Contains("Tetitulz.Pdf.DocxToPdf.DocxToPdfException", rendered);
        Assert.Contains("Tetitulz.Pdf.DocxToPdf.ConversionEngine", rendered);

        // 43 lines today. A render that collapsed to type declarations alone would be 6, and a
        // render that produced nothing would be 0 — both well under this.
        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 30,
            $"Only {lines} lines rendered for Tetitulz.Pdf.DocxToPdf; the renderer is filtering too much.");
    }
}
