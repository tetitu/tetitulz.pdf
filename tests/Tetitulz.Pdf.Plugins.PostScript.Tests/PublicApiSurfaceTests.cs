// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.PostScript</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.PostScript.txt</c>.
///
/// <para>Nearly all of this package is <c>internal</c> — a ~10 600-line interpreter behind a
/// deliberately small public façade, which is what keeps the PostScript dependency out of
/// <c>Base</c>'s package graph. That makes the baseline unusually valuable and unusually cheap: it
/// records exactly which handful of types a consumer may bind to, so an <c>internal</c> type
/// accidentally promoted to <c>public</c> during interpreter work shows up as an added line rather
/// than as a permanent compatibility obligation nobody chose.</para>
///
/// <para>The renderer is shared with every other baseline
/// (<see cref="PublicApiSnapshot"/> in TestHelpers), so the files are directly comparable and
/// cannot drift in format. An intended API change is expected to fail here once: replace the
/// baseline with the <c>.actual</c> file written beside it, in the same commit.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly PluginAssembly => typeof(PostScriptXObjectPlugin).Assembly;

    [ApiBaselineFact]
    public void PostScript_plugin_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(PluginAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a surface that rendered as nothing would compare equal to an
    /// empty baseline and pass forever. The minimum is taken from what this package actually
    /// exports — the three registrable plugins, the whole-document conversion entry point with its
    /// options/result types, the diagnostics sink, and <c>PsLimits</c>, whose DoS constants are
    /// each a line of their own.
    /// </summary>
    [ApiBaselineFact]
    public void The_rendered_surface_is_substantial_so_an_empty_baseline_cannot_pass()
    {
        string rendered = PublicApiSnapshot.Render(PluginAssembly);

        // Named entry points a consumer actually calls; if any of these vanishes from the render,
        // the renderer — not the package — is what changed.
        Assert.Contains("Tetitulz.Pdf.Plugins.PostScript.PostScriptXObjectPlugin", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PostScript.EpsImportPlugin", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PostScript.PostScriptConversion", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PostScript.PsLimits", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 70,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.PostScript; it exports 11 " +
            "public types including the PsLimits constant table, so the renderer is filtering " +
            "nearly everything out.");
    }
}
