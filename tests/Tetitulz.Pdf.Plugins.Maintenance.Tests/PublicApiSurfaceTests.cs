// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.Maintenance</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Maintenance.txt</c>.
///
/// <para>This package's surface is unusually option-heavy — <c>OptimizationOptions</c> and
/// <c>ImageOptimizationOptions</c> are settable property bags a caller configures, so <i>renaming
/// or retyping one property</i> is a source break for every consumer even though nothing about the
/// optimizer's behaviour changed. That is exactly the class of change a baseline catches and code
/// review does not.</para>
///
/// <para>The renderer is shared with every other baseline
/// (<see cref="PublicApiSnapshot"/> in TestHelpers), so the files are directly comparable and
/// cannot drift in format. An intended API change is expected to fail here once: replace the
/// baseline with the <c>.actual</c> file written beside it, in the same commit.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly PluginAssembly => typeof(PdfOptimizer).Assembly;

    [ApiBaselineFact]
    public void Maintenance_plugin_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(PluginAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a surface that rendered as nothing would compare equal to an
    /// empty baseline and pass forever. The minimum is taken from what this package actually
    /// exports — three static entry points (optimize, optimize-with-metrics, linearize), four
    /// <c>I*Plugin</c> implementations, and two option bags whose settable properties dominate the
    /// line count.
    /// </summary>
    [ApiBaselineFact]
    public void The_rendered_surface_is_substantial_so_an_empty_baseline_cannot_pass()
    {
        string rendered = PublicApiSnapshot.Render(PluginAssembly);

        // Named entry points a consumer actually calls; if any of these vanishes from the render,
        // the renderer — not the package — is what changed.
        Assert.Contains("Tetitulz.Pdf.Plugins.Maintenance.PdfOptimizer", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Maintenance.PdfLinearizer", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Maintenance.OptimizationOptions", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Maintenance.SanitizerPlugin", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 90,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Maintenance; it exports 12 " +
            "public types including two large option bags, so the renderer is filtering nearly " +
            "everything out.");
    }
}
