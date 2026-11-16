// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.Templating</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Templating.txt</c>, so a breaking change to a
/// shipping package's public API fails a test instead of reaching a consumer silently.
///
/// <para>A failure is not automatically a bug: an <i>intended</i> API change is expected to fail
/// here once, and the fix is to accept the regenerated <c>.actual</c> baseline in the same commit,
/// which turns the diff into the migration record.</para>
///
/// <para><b>Two things here are part of the contract in a way a signature alone does not
/// suggest.</b> <c>TemplateSources</c> and <c>TemplateFit</c> are enums, so the snapshot records
/// each member's literal value — reordering them is a silent break for any already-compiled
/// caller, and the baseline is what catches it. And <c>TemplateOptions</c>' defaults
/// (<c>TokenOpen</c>/<c>TokenClose</c>, <c>VerifyErasure</c>) decide what an existing caller gets
/// when it sets nothing, so a change of default belongs in a reviewed diff rather than in a
/// property initialiser nobody re-read.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void Templating_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(TemplatingPlugin).Assembly;
        string? report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to an
    /// empty file and silently pass forever.
    ///
    /// <para>The minimum is derived from what this package actually exports, with headroom: five
    /// public types — <c>PdfTemplateFiller</c>, <c>TemplateOptions</c>, <c>TemplateFillReport</c>,
    /// <c>TemplatingPlugin</c> and the two enums — rendering 40 lines today, of which
    /// <c>TemplateFillReport</c>'s seven diagnostic collections and <c>TemplateOptions</c>' nine
    /// settings are the bulk. The floor sits below that rather than at it on purpose: pinning it to
    /// the exact count would make a <i>deliberate</i> removal fail here instead of in the baseline
    /// diff, which is the assertion that is supposed to report an API change.</para>
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(TemplatingPlugin).Assembly);

        Assert.Contains("class Tetitulz.Pdf.Plugins.Templating.TemplatingPlugin", rendered);
        Assert.Contains("PdfTemplateFiller::Fill", rendered);
        Assert.Contains("TemplateOptions::TokenOpen", rendered);
        Assert.Contains("TemplateFillReport::UnmatchedKeys", rendered);

        // The two enums are the members most likely to be reordered without anyone noticing, so
        // the floor insists they rendered at all.
        Assert.Contains("enum Tetitulz.Pdf.Plugins.Templating.TemplateSources", rendered);
        Assert.Contains("enum Tetitulz.Pdf.Plugins.Templating.TemplateFit", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 30,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Templating; the renderer is " +
            "probably filtering everything out.");
    }
}
