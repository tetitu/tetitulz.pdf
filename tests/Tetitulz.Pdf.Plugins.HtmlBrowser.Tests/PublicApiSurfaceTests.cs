// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.HtmlBrowser</c>'s exported surface against <c>tests/ApiSnapshots</c>.
///
/// <para><b>A baseline needs a comparison.</b> The repo-shape guard
/// (<c>ApiBaselineCoverageTests</c>) asserts that every packable project <i>has</i> a baseline file,
/// which is not the same as asserting that anything <i>compares</i> against it — and the difference
/// is invisible, because a baseline nothing reads is byte-identical to one that passes.</para>
///
/// <para>This plugin's surface matters little on its own, but the coverage guard demands a
/// comparison for every package rather than a file: a rule with exceptions becomes a habit.</para>
///
/// <para>A failure is not automatically a bug — an intended API change fails here once, and the fix
/// is to accept the regenerated <c>.actual</c> file <i>in the same commit</i>, which turns the diff
/// into the migration record. What the guard prevents is a change nobody noticed.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    private static Assembly TargetAssembly => typeof(PlaywrightHtmlBrowserPlugin).Assembly;

    [ApiBaselineFact]
    public void Exported_surface_matches_its_baseline()
    {
        string? report = PublicApiSnapshot.Compare(TargetAssembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself. A baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever, so this asserts the render actually reached the plugin's
    /// surface: the plugin type and the two members a host calls on it.
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(TargetAssembly);

        Assert.Contains("Tetitulz.Pdf.Plugins.HtmlBrowser.PlaywrightHtmlBrowserPlugin", rendered);
        Assert.Contains("::RenderAsync(", rendered);
        Assert.Contains("::InstallBrowsers(", rendered);
    }
}
