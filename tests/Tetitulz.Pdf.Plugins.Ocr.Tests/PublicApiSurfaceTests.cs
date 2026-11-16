// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// Pins <c>Tetitulz.Pdf.Plugins.Ocr</c>'s exported surface against
/// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Ocr.txt</c>, so a breaking change to a shipping
/// package's public API fails a test instead of reaching a consumer silently.
///
/// <para>A failure is not automatically a bug: an <i>intended</i> API change is expected to fail
/// here once, and the fix is to accept the regenerated <c>.actual</c> baseline in the same commit,
/// which turns the diff into the migration record.</para>
///
/// <para><b>The snapshot covers the signatures, not the behaviour.</b> This package's public
/// methods need a Tesseract install and language data to do anything, so most of its behavioural
/// tests can only assert the graceful-degradation path. The surface, by contrast, is checkable
/// everywhere — which makes the baseline the one guard here that runs on a bare CI machine.</para>
/// </summary>
public class PublicApiSurfaceTests
{
    [ApiBaselineFact]
    public void Ocr_exported_surface_matches_its_baseline()
    {
        Assembly assembly = typeof(TesseractOcrPlugin).Assembly;
        string? report = PublicApiSnapshot.Compare(assembly);
        Assert.True(report == null, report);
    }

    /// <summary>
    /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to an
    /// empty file and silently pass forever.
    ///
    /// <para>This package exports two types across 12 lines, so — as with <c>Plugins.CMap</c> —
    /// the named members carry more of the floor than the line count does. <c>RecognizeText</c> and
    /// <c>CanProcess</c> are the <c>IOcrPlugin</c> contract the host dispatches through, and
    /// <c>OcrPage</c>/<c>OcrDocument</c> are the whole of the caller-facing API.</para>
    /// </summary>
    [ApiBaselineFact]
    public void The_renderer_produces_a_substantial_surface()
    {
        string rendered = PublicApiSnapshot.Render(typeof(TesseractOcrPlugin).Assembly);

        Assert.Contains("class Tetitulz.Pdf.Plugins.Ocr.TesseractOcrPlugin", rendered);
        Assert.Contains("class Tetitulz.Pdf.Plugins.Ocr.PdfOcrEngine", rendered);
        Assert.Contains("TesseractOcrPlugin::RecognizeText", rendered);
        Assert.Contains("TesseractOcrPlugin::CanProcess", rendered);
        Assert.Contains("PdfOcrEngine::OcrPage", rendered);
        Assert.Contains("PdfOcrEngine::OcrDocument", rendered);

        int lines = rendered.Split('\n').Length;
        Assert.True(lines > 8,
            $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Ocr; the renderer is probably " +
            "filtering everything out.");
    }
}
