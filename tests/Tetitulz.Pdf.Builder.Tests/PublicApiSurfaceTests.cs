// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Builder</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para>Builder is a <i>foundational feature</i>: <c>DocxToPdf</c> and <c>HtmlToPdf</c> both take a
/// <c>ProjectReference</c> on it and target its document model, so a change here breaks two other
/// shipping packages before it ever reaches an external consumer. That makes the baseline worth
/// more for this assembly than for a leaf one.</para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>, which turns the diff
/// into the migration record. What the guard prevents is a change nobody noticed.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly BuilderAssembly => typeof(DocumentBuilder).Assembly;

    [ApiBaselineFact]
    public void Builder_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(BuilderAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself. A baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever, so this asserts the render actually reached Builder's surface:
    /// the entry point, the document model it produces, and the four exporters that consume it.
    /// The line minimum is drawn from what the package really exports (the recorded baseline is
    /// well above it), not from a round number.
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(BuilderAssembly);

        Assert.Contains("Tetitulz.Pdf.Builder.DocumentBuilder", rendered);
        Assert.Contains("Tetitulz.Pdf.Builder.Model.Document", rendered);
        Assert.Contains("Tetitulz.Pdf.Builder.Export.PdfExporter", rendered);
        Assert.Contains("Tetitulz.Pdf.Builder.Export.DocxExporter", rendered);
        Assert.Contains("Tetitulz.Pdf.Builder.Export.HtmlExporter", rendered);
        Assert.Contains("Tetitulz.Pdf.Builder.Export.OdtExporter", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 250,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Builder; the renderer is filtering too much.");
    }
}
