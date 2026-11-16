// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.Conformance</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Conformance.txt</c>.
///
/// <para>This package is a <i>shipping</i> one — a consumer registers <c>PdfXValidationPlugin</c>
/// and friends through <c>PdfPluginHost</c> and calls <c>PdfUaChecker</c> / <c>PdfAValidator</c>
/// directly — so a removed or re-signed public member breaks them at their compile time. Without
/// this test nothing in the suite would have noticed.</para>
///
/// <para>The renderer is shared with every other baseline
/// (<see cref="PublicApiSnapshot"/> in TestHelpers), so the files are directly comparable and
/// cannot drift in format. An intended API change is expected to fail here once: replace the
/// baseline with the <c>.actual</c> file written beside it, in the same commit.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly PluginAssembly => typeof(PdfUaChecker).Assembly;

    [ApiBaselineFact]
    public void Conformance_plugin_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(PluginAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a surface that rendered as nothing would compare equal to an
    /// empty baseline and pass forever. The minimum is taken from what this package actually
    /// exports — three validators, two converters, the accessibility fixer, four
    /// <c>I*Plugin</c> implementations, and their result/violation/option types.
    /// </summary>
    [ApiBaselineFact]
    public void The_rendered_surface_is_substantial_so_an_empty_baseline_cannot_pass()
    {
        string rendered = PublicApiSnapshot.Render(PluginAssembly);

        // Named entry points a consumer actually calls; if any of these vanishes from the render,
        // the renderer — not the package — is what changed.
        Assert.Contains("Tetitulz.Pdf.Plugins.Conformance.PdfUaChecker", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Conformance.PdfAValidator", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Conformance.PdfXValidator", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.Conformance.PdfXValidationPlugin", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 120,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Conformance; it exports 24 " +
            "public types, so the renderer is filtering nearly everything out.");
    }
}
