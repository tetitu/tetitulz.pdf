// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.HtmlScript</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.HtmlScript.txt</c>.
/// </summary>
/// <remarks>
/// <b>Most of this surface is the vendored AngleSharp.Js</b> (VENDORED.md), whose types stay public
/// because AngleSharp.Js finds its DOM globals by scanning its own assembly's exported types. So the
/// baseline doubles as the record of what was vendored: re-vendoring a newer release shows up here as
/// the diff to review, and a type lost from the scan — a global that silently stopped existing in page
/// scripts — cannot pass unnoticed.
/// </remarks>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void HtmlScript_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(JintHtmlScriptPlugin).Assembly;
        string? report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself — a baseline that rendered as nothing would compare equal to an
    /// empty file and pass forever. It names the plugin and one vendored DOM global by type.
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(JintHtmlScriptPlugin).Assembly);

        Assert.Contains("Tetitulz.Pdf.Plugins.HtmlScript.JintHtmlScriptPlugin", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.JsScriptingOptions::ConfigureEngine", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 20,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.HtmlScript; the renderer is " +
            "probably filtering everything out.");
    }
}
