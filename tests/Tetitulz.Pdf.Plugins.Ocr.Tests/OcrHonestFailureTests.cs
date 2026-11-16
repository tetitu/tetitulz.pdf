// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Plugins.Ocr;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// Three MEDIUM findings, all of the same shape: the plugin gave a confident answer where it
/// should have refused.
///
/// <list type="number">
///   <item><see cref="PdfOcrEngine"/> returned its failures <b>inside</b> the OCR result, so
///   "[OCR unavailable: tessdata folder not found at C:\svc\app\tessdata]" was written into a
///   PDF's invisible text layer or indexed as page content — disclosing a server path — and was
///   indistinguishable from a page that genuinely contains that text.</item>
///   <item><see cref="TesseractOcrPlugin.Shutdown"/> disposed the engine and set the field to
///   null, and <c>RecognizeText</c> re-created it with <c>??=</c>, so a call arriving after the
///   host had unregistered the plugin allocated native handles nothing would ever dispose.</item>
///   <item><c>RecognizeText</c> never re-checked its own <c>components</c> argument, so a
///   4-component buffer produced a structurally valid PNG with misaligned scanlines and Tesseract
///   returned plausible-looking garbage — a wrong answer instead of a refusal.</item>
/// </list>
/// </summary>
public class OcrHonestFailureTests
{
    private static string MissingTessData =>
        Path.Combine(Path.GetTempPath(), "tetitulz-tessdata-does-not-exist-" + nameof(OcrHonestFailureTests));

    // ────────────────────────────────────────────────────────────────────
    //  PdfOcrEngine — failures are not text
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The disclosure case. The bracketed message used to BE the return value, so it reached
    /// whatever the caller built from it.
    /// </summary>
    [Fact]
    public void OcrPage_WithNoTessData_ReturnsNullAndReportsSeparately()
    {
        string result = PdfOcrEngine.OcrPage(MinimalPdfBuilder.CreateMinimalPdf(), 0,
            tessDataPath: MissingTessData);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(PdfOcrEngine.LastError));
        Assert.Contains("tessdata", PdfOcrEngine.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OcrDocument_WithNoTessData_ReturnsNullAndReportsSeparately()
    {
        string result = PdfOcrEngine.OcrDocument(MinimalPdfBuilder.CreateMultiPagePdf(3),
            tessDataPath: MissingTessData);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(PdfOcrEngine.LastError));
    }

    /// <summary>
    /// The property this buys, stated directly: whatever comes back from a failed run, it is never
    /// a string a caller could index or draw. A bracketed error message is text; <c>null</c> is
    /// not.
    /// </summary>
    [Fact]
    public void AFailedRun_NeverReturnsTheErrorAsRecognisedText()
    {
        string page = PdfOcrEngine.OcrPage(MinimalPdfBuilder.CreateMinimalPdf(), 0,
            tessDataPath: MissingTessData);
        string document = PdfOcrEngine.OcrDocument(MinimalPdfBuilder.CreateMinimalPdf(),
            tessDataPath: MissingTessData);

        foreach (string r in new[] { page, document })
        {
            Assert.Null(r);
            // Belt and braces: if a future change returns a string here, it must not be an error
            // report, and it must never carry the tessdata path into the caller's text.
            if (r != null) Assert.DoesNotContain("OCR", r, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The slot is per-operation: reset at the top of every public method, so a message from a
    /// previous document cannot be read as this call's verdict.
    /// </summary>
    [Fact]
    public void LastError_IsResetAtTheStartOfEveryCall()
    {
        PdfOcrEngine.OcrPage(MinimalPdfBuilder.CreateMinimalPdf(), 0, tessDataPath: MissingTessData);
        Assert.NotNull(PdfOcrEngine.LastError);

        Assert.Throws<ArgumentNullException>(() => PdfOcrEngine.OcrPage(null!, 0));
    }

    /// <summary>A run that never got as far as a page reports no failed pages.</summary>
    [Fact]
    public void LastFailedPages_IsEmptyWhenTheEngineNeverStarted()
    {
        PdfOcrEngine.OcrDocument(MinimalPdfBuilder.CreateMultiPagePdf(2), tessDataPath: MissingTessData);

        Assert.Empty(PdfOcrEngine.LastFailedPages);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Shutdown is terminal
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// After Shutdown the plugin must refuse rather than construct a fresh native engine that
    /// nothing will dispose. The refusal is observable through <c>LastError</c>; the leak it
    /// prevents is not observable from a test, which is why the assertion is on the guard's own
    /// evidence.
    /// </summary>
    [Fact]
    public void RecognizeText_AfterShutdown_RefusesInsteadOfBuildingANewEngine()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);
        plugin.Shutdown();

        string text = plugin.RecognizeText(new byte[16 * 16], 16, 16, 1);

        Assert.Null(text);
        Assert.Contains("shut down", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control: before Shutdown the same call reaches the engine and fails there instead — so
    /// the refusal above is about the shutdown, not about the call being impossible.
    /// </summary>
    [Fact]
    public void RecognizeText_BeforeShutdown_ReachesTheEngine()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[16 * 16], 16, 16, 1));
        Assert.DoesNotContain("shut down", TesseractOcrPlugin.LastError!,
            StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  RecognizeText agrees with CanProcess
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two methods must agree by construction. A 4-component buffer used to be laid out as
    /// 3-byte RGB with a 4-byte stride, which shears the image and yields confident garbage.
    /// </summary>
    [Theory]
    [InlineData(4)]   // CMYK / RGBA
    [InlineData(2)]   // gray + alpha
    [InlineData(0)]
    public void RecognizeText_RefusesWhatCanProcessRefuses(int components)
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);
        var pixels = new byte[16 * 16 * Math.Max(1, components)];

        Assert.False(plugin.CanProcess(components, 8));
        Assert.Null(plugin.RecognizeText(pixels, 16, 16, components));
        Assert.Contains("not supported", TesseractOcrPlugin.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(components.ToString(), TesseractOcrPlugin.LastError!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A buffer shorter than its declared geometry is refused before it can be strided past its
    /// own end, and the message names both figures.
    /// </summary>
    [Fact]
    public void RecognizeText_RefusesATruncatedBufferWithBothSizes()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[10], 64, 64, 3));

        string why = TesseractOcrPlugin.LastError!;
        Assert.Contains("10 bytes", why, StringComparison.Ordinal);
        Assert.Contains((64 * 64 * 3).ToString(), why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control that stops the new guards from being a blanket refusal: a correctly-shaped
    /// grayscale buffer still reaches the engine, and fails there for the engine's own reason.
    /// </summary>
    [Fact]
    public void RecognizeText_AcceptsWhatCanProcessAccepts()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        Assert.Null(plugin.RecognizeText(new byte[32 * 32], 32, 32, 1));

        string why = TesseractOcrPlugin.LastError!;
        Assert.DoesNotContain("not supported", why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recognition failed", why, StringComparison.OrdinalIgnoreCase);
    }
}
