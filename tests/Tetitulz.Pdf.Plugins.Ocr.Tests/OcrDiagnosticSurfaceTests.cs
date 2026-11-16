// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using Tetitulz.Pdf.Plugins.Ocr;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// A failed recognition says <i>why</i> on <see cref="TesseractOcrPlugin.LastError"/>.
///
/// <para><b>Why this exists.</b> Every failure was swallowed into
/// <c>System.Diagnostics.Debug.WriteLine</c>, which is <c>[Conditional("DEBUG")]</c> and therefore
/// absent from the shipped assembly. With no other diagnostic surface, a missing native
/// <c>libtesseract</c>, a wrong language file or a version mismatch produced exactly the same
/// observable result as a page that genuinely had no text — <c>null</c> — which is also what
/// <c>PdfOperations.RecognizeTextWithOcr</c> returns when no OCR plugin is registered at all. The
/// operator saw empty OCR output on every page with nothing in any log.</para>
///
/// <para>None of this needs Tesseract installed: the failures asserted below happen before the
/// native engine is reached, or because it cannot be reached.</para>
/// </summary>
public class OcrDiagnosticSurfaceTests
{
    private static string MissingTessData =>
        Path.Combine(Path.GetTempPath(), "tetitulz-tessdata-does-not-exist-" + nameof(OcrDiagnosticSurfaceTests));

    /// <summary>
    /// The field's most common failure: a host with a tessdata directory but no usable engine.
    /// <c>null</c> comes back either way, so the reason is the only thing that distinguishes it
    /// from a blank page.
    /// </summary>
    [Fact]
    public void RecognizeText_WithNoEngineAvailable_ReportsTheReason()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[16 * 16], 16, 16, 1));
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError));
    }

    /// <summary>A malformed buffer is a different failure and must produce a different reason.</summary>
    [Fact]
    public void RecognizeText_WithATruncatedBuffer_ReportsTheReason()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[10], 64, 64, 3));
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError));
    }

    [Fact]
    public void RecognizeText_WithNoPixels_ReportsTheReason()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1));
        Assert.Contains("pixel", TesseractOcrPlugin.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Per-thread storage is not per-call scope.</b> The slot is reset at the start of every
    /// call, on every path, so a value read after one call can never be the previous call's — the
    /// exact defect CLAUDE.md records for <c>LastPermsValidation</c>.
    /// </summary>
    [Fact]
    public void LastError_IsClearedAtTheStartOfEveryCall()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        plugin.RecognizeText(new byte[10], 64, 64, 3);
        Assert.NotNull(TesseractOcrPlugin.LastError);

        // A second call whose failure is a different one must not leave the first call's text.
        plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1);
        Assert.Contains("pixel", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>[ThreadStatic]</c>, per CLAUDE.md: a thread that never ran the operation sees
    /// <c>null</c> — the honest answer — rather than a neighbour's message.
    /// </summary>
    [Fact]
    public void LastError_IsIsolatedPerThread()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);
        plugin.RecognizeText(new byte[10], 64, 64, 3);
        Assert.NotNull(TesseractOcrPlugin.LastError);

        string? seenOnOtherThread = "not-read";
        var t = new Thread(() => seenOnOtherThread = TesseractOcrPlugin.LastError);
        t.Start();
        t.Join();

        Assert.Null(seenOnOtherThread);
    }
}
