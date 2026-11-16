// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.JavaScript</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.JavaScript.txt</c>, so a breaking change to a
/// shipping package's public API fails a test instead of reaching a consumer silently.
///
/// <para>A failure is not automatically a bug: an <i>intended</i> API change is expected to fail
/// here once, and the fix is to accept the regenerated <c>.actual</c> baseline in the same commit,
/// which turns the diff into the migration record.</para>
///
/// <para><b>This package's baseline is unusually load-bearing.</b> Most of its surface is the
/// nested <c>Js*Proxy</c> classes, whose members are named in a common viewer's JavaScript casing
/// (<c>event.value</c>, <c>doc.getField</c>, <c>app.alert</c>) because Jint binds to them by
/// reflection. Renaming one is invisible to the C# compiler and to every caller in this
/// repository, but it silently breaks the document scripts the plugin exists to run — so the
/// snapshot is the only mechanism that notices.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void JavaScript_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(JintJavaScriptPlugin).Assembly;
        string? report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to an
    /// empty file and silently pass forever.
    ///
    /// <para>The package exports two types, <c>JintJavaScriptPlugin</c> and <c>PdfJsEngine</c>; the
    /// object model a document's scripts call (<c>app</c>, <c>event</c>, <c>getField</c>) is script,
    /// <c>AcrobatObjectModel.js</c>, and a rename there is caught by <c>AcrobatParityTests</c>, not by
    /// this surface. The floor names the engine's entry points.</para>
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(JintJavaScriptPlugin).Assembly);

        Assert.Contains("Tetitulz.Pdf.Plugins.JavaScript.JintJavaScriptPlugin", rendered);
        Assert.Contains("Tetitulz.Pdf.Plugins.JavaScript.PdfJsEngine", rendered);
        Assert.Contains("PdfJsEngine::Execute", rendered);
        Assert.Contains("PdfJsEngine::LastUnsupportedRequest", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 8,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.JavaScript; the renderer is " +
            "probably filtering everything out.");
    }
}
