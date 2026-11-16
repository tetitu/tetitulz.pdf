// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.PageEdits</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.PageEdits.txt</c>.
///
/// <para>This is the package whose API a silent change would hurt most, because
/// <c>PdfRedactor</c> makes a <i>security</i> promise: a caller who asks for content to be
/// destroyed and gets a differently-shaped overload — or a <c>RedactionRegion</c> whose
/// coordinates are reinterpreted — has no way to tell from the return value that they got
/// something else. A baseline is the only thing in the suite that turns "the signature moved" into
/// a failing test.</para>
///
/// <para>The renderer is shared with every other baseline
/// (<see cref="PublicApiSnapshot"/> in TestHelpers), so the files are directly comparable and
/// cannot drift in format. An intended API change is expected to fail here once: replace the
/// baseline with the <c>.actual</c> file written beside it, in the same commit.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly PluginAssembly => typeof(PdfRedactor).Assembly;

    [ApiBaselineFact]
    public void PageEdits_plugin_exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(PluginAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a surface that rendered as nothing would compare equal to an
    /// empty baseline and pass forever. The minimum is taken from what this package actually
    /// exports — the static operations (merge, split, redact, watermark), their registrable plugin
    /// wrappers, the split planner and <c>WatermarkOptions</c>; the redactor's region is Core's
    /// <c>Tetitulz.Pdf.Core.Plugins.RedactionRegion</c>, shared with the plugin contract.
    /// </summary>
    [ApiBaselineFact]
    public void The_rendered_surface_is_substantial_so_an_empty_baseline_cannot_pass()
    {
        string rendered = PublicApiSnapshot.Render(PluginAssembly);

        // Named entry points a consumer actually calls; if any of these vanishes from the render,
        // the renderer — not the package — is what changed.
        Assert.Contains("Tetitulz.Pdf.Plugins.PageEdits.PdfRedactor", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PageEdits.PdfMerger", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PageEdits.PdfSplitter", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.PageEdits.WatermarkOptions", rendered);
        Assert.Contains("PdfRedactor::RedactRegions(System.IO.Stream, System.IO.Stream, " +
            "System.Collections.Generic.IEnumerable<Tetitulz.Pdf.Core.Plugins.RedactionRegion>)", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 50,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.PageEdits; it exports 10 " +
            "public types, so the renderer is filtering nearly everything out.");
    }
}
