// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Accessibility</c>'s exported surface. The companion for the four
/// library-ring assemblies lives in <c>Base.Tests/TestInfrastructure/PublicApiSurfaceTests.cs</c>;
/// both share one renderer in <see cref="PublicApiSnapshot"/> so their baselines are directly
/// comparable and cannot drift in format.
/// </summary>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void Accessibility_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(PdfAccessibility).Assembly;
        string? report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>A floor: an empty render would match an empty baseline and pass forever.</summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(PdfAccessibility).Assembly);
        Assert.Contains("Tetitulz.Pdf.Accessibility.PdfAccessibility", rendered);
        Assert.True(rendered.Split('\n').Length > 100,
            $"Only {rendered.Split('\n').Length} lines rendered; the renderer is filtering too much.");
    }
}
