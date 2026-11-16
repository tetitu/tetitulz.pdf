// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Plugins.Ocr;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// A recognition failure must leave evidence a shipped build can read.
///
/// <para><b>The bug.</b> Every failure was swallowed into
/// <c>System.Diagnostics.Debug.WriteLine</c>, which is <c>[Conditional("DEBUG")]</c> and therefore
/// compiled out of the Release assembly. The plugin exposed no <c>Last*Error</c> and no diagnostics
/// sink, so a missing native <c>libtesseract</c>, a wrong language file or a version mismatch was
/// indistinguishable from "the page had no text": <see cref="TesseractOcrPlugin.RecognizeText"/>
/// returned <c>null</c>, and so does <c>PdfOperations.RecognizeTextWithOcr</c> when no OCR plugin
/// is registered at all. An operator saw empty OCR output on every page, forever, with no message
/// in any log.</para>
///
/// <para>Returning <c>null</c> is still the contract — the caller uses it to fall through to the
/// next OCR plugin — so the fix is a diagnostic beside it, not a different return.</para>
/// </summary>
public class OcrDiagnosticsTests
{
    /// <summary>A tessdata path that certainly does not exist, so the engine cannot initialise.</summary>
    private static string MissingTessData =>
        Path.Combine(Path.GetTempPath(), "tetitulz-tessdata-does-not-exist-" + nameof(OcrDiagnosticsTests));

    /// <summary>
    /// The field case: recognition fails because no engine can be built. <c>null</c> comes back —
    /// and a reason with it.
    /// </summary>
    [Fact]
    public void RecognitionFailure_LeavesAReasonOnLastError()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        string? text = plugin.RecognizeText(new byte[16 * 16], 16, 16, 1);

        Assert.Null(text);
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError),
            "a failed recognition must be distinguishable from a page with no text");
        Assert.Contains("TesseractOcrPlugin", TesseractOcrPlugin.LastError!, StringComparison.Ordinal);
        Assert.Contains("recognition failed", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Malformed input reaches the same slot: a buffer shorter than its declared dimensions is a
    /// caller error, and reporting it is how the caller finds out.
    /// </summary>
    [Fact]
    public void MalformedPixelBuffer_LeavesAReasonOnLastError()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[10], 64, 64, 3));
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError));
    }

    /// <summary>
    /// An empty buffer is refused before any engine work, and says so — otherwise this one case
    /// would still be silent.
    /// </summary>
    [Fact]
    public void EmptyPixelBuffer_LeavesAReasonOnLastError()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1));
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError));
        Assert.Contains("no pixel data", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);

        Assert.Null(plugin.RecognizeText(null!, 10, 10, 1));
        Assert.False(string.IsNullOrWhiteSpace(TesseractOcrPlugin.LastError));
    }

    /// <summary>
    /// The two failures report <b>different</b> reasons. A single constant string would satisfy
    /// every assertion above while telling the operator nothing.
    /// </summary>
    [Fact]
    public void DifferentFailures_ReportDifferentReasons()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1);
        string? empty = TesseractOcrPlugin.LastError;

        plugin.RecognizeText(new byte[16 * 16], 16, 16, 1);
        string? engine = TesseractOcrPlugin.LastError;

        Assert.NotNull(empty);
        Assert.NotNull(engine);
        Assert.NotEqual(empty, engine);
    }

    /// <summary>
    /// The slot is reset at the top of the call, on every path — a message left by a previous page
    /// must never be readable as this page's verdict. That is the failure mode CLAUDE.md records
    /// for <c>LastPermsValidation</c>: thread-local storage is not per-operation scope.
    /// </summary>
    [Fact]
    public void LastError_IsResetAtTheStartOfEveryCall()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        plugin.RecognizeText(new byte[16 * 16], 16, 16, 1);
        Assert.NotNull(TesseractOcrPlugin.LastError);

        // A call that is refused for a different reason must not inherit the previous message.
        plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1);
        Assert.Contains("no pixel data", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recognition failed", TesseractOcrPlugin.LastError!,
            StringComparison.OrdinalIgnoreCase);
    }
}
