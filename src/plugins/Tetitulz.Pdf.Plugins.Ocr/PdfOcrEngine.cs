// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Plugins.Ocr
{
    /// <summary>
    /// OCR engine for scanned PDFs. Renders each page to image, runs Tesseract OCR,
    /// and returns searchable text. Requires Tesseract language data files (tessdata).
    /// </summary>
    public static class PdfOcrEngine
    {
        // ── Diagnostics ──────────────────────────────────────────────────────────────────
        //
        // Failures are reported here, never returned inside the OCR result: a message such as
        // "[OCR unavailable: tessdata folder not found at C:\svc\app\tessdata]" in the result
        // string would be treated as recognised text — written into a PDF's invisible text layer
        // or indexed as page content, disclosing a server path — and could not be told from a page
        // that genuinely holds that text.
        //
        // A failure returns null (OcrPage) or leaves the page empty (OcrDocument) and reports
        // here. [ThreadStatic] per CLAUDE.md, reset at the top of each public method.

        [ThreadStatic] private static string? _lastError;
        [ThreadStatic] private static int[]? _failedPages;

        /// <summary>
        /// Why the last call on this thread returned <c>null</c> or skipped pages, or <c>null</c>
        /// when it succeeded.
        /// </summary>
        public static string? LastError => _lastError;

        /// <summary>
        /// Zero-based indices of the pages the last <see cref="OcrDocument"/> on this thread could
        /// not read, or an empty array. A partial run is visible rather than silently interleaved
        /// with error text.
        /// </summary>
        public static IReadOnlyList<int> LastFailedPages =>
            _failedPages ?? Array.Empty<int>();

        /// <summary>
        /// Performs OCR on a PDF page and returns the recognized text, or <c>null</c> when OCR
        /// could not run — <see cref="LastError"/> then says why.
        /// </summary>
        /// <param name="pdfBytes">PDF file bytes.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="language">Tesseract language code (default "eng").</param>
        /// <param name="tessDataPath">Path to tessdata folder. If null, uses TESSDATA_PREFIX env var.</param>
        /// <param name="dpi">Render DPI for OCR (default 300 for accuracy).</param>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted and no decryptor is registered.</exception>
        public static string? OcrPage(byte[] pdfBytes, int pageIndex, string language = "eng",
            string? tessDataPath = null, int dpi = 300)
        {
            if (pdfBytes == null) throw new ArgumentNullException(nameof(pdfBytes));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The render resolution must be positive.");

            _lastError = null;
            _failedPages = null;
            RefuseUnreadableEncryption(pdfBytes);

            string dataPath = ResolveTessData(tessDataPath);
            if (!Directory.Exists(dataPath))
            {
                _lastError = $"OCR unavailable: tessdata folder not found at '{dataPath}'.";
                return null;
            }

            byte[] png;
            using (var ms = new MemoryStream(pdfBytes))
                png = PdfPageRenderer.RenderPage(ms, pageIndex, dpi);

            try
            {
                using var engine = new Tesseract.TesseractEngine(dataPath, language, Tesseract.EngineMode.Default);
                using var pix = Tesseract.Pix.LoadFromMemory(png);
                using var page = engine.Process(pix);
                return page.GetText()?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _lastError = $"OCR failed on page {pageIndex} ({ex.GetType().Name}): {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Performs OCR on all pages and returns the combined text, or <c>null</c> when OCR could
        /// not run at all. Pages that failed individually contribute nothing and are listed on
        /// <see cref="LastFailedPages"/>, with the reason on <see cref="LastError"/>.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted and no decryptor is registered.</exception>
        public static string? OcrDocument(byte[] pdfBytes, string language = "eng",
            string? tessDataPath = null, int dpi = 300)
        {
            if (pdfBytes == null) throw new ArgumentNullException(nameof(pdfBytes));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The render resolution must be positive.");

            _lastError = null;
            _failedPages = null;
            RefuseUnreadableEncryption(pdfBytes);

            string dataPath = ResolveTessData(tessDataPath);
            if (!Directory.Exists(dataPath))
            {
                _lastError = $"OCR unavailable: tessdata folder not found at '{dataPath}'.";
                return null;
            }

            var sb = new System.Text.StringBuilder();
            var failed = new List<int>();
            string? firstFailure = null;
            int pageCount = 0;

            try
            {
                // One session: the document parsed — and, encrypted, decrypted — once, its pages
                // counted from what the renderer reads: a count taken from the file as stored would
                // be zero for an encrypted file, which the renderer decrypts.
                using var input = new MemoryStream(pdfBytes, writable: false);
                using var session = PdfRenderSession.Open(input);
                pageCount = session.PageCount;

                // Build the engine ONCE and reuse it across all pages — engine
                // construction loads language data + native handles and is far too
                // expensive to repeat per page.
                using var engine = new Tesseract.TesseractEngine(dataPath, language, Tesseract.EngineMode.Default);
                for (int i = 0; i < pageCount; i++)
                {
                    if (i > 0) sb.AppendLine();
                    try
                    {
                        // Inside the page's own try: a page that will not render is one failed
                        // page, listed, not the end of the document.
                        byte[] png = session.RenderPage(i, dpi);
                        using var pix = Tesseract.Pix.LoadFromMemory(png);
                        using var page = engine.Process(pix);
                        sb.Append(page.GetText()?.Trim() ?? "");
                    }
                    catch (Exception ex)
                    {
                        // Nothing is appended: an error message inside the text would be indexed
                        // as page content.
                        failed.Add(i);
                        firstFailure ??= $"{ex.GetType().Name}: {ex.Message}";
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = $"OCR failed ({ex.GetType().Name}): {ex.Message}";
                return null;
            }

            if (failed.Count > 0)
            {
                _failedPages = failed.ToArray();
                _lastError = $"{failed.Count} of {pageCount} page(s) could not be recognised " +
                             $"(first failure: {firstFailure}); their text is missing from the result.";
            }

            return sb.ToString();
        }

        /// <summary>
        /// Refuses an encrypted document no decryptor is registered to open. The renderer then
        /// draws its still-encrypted content, so every page came back blank and the run "succeeded"
        /// with no text — a batch indexer could not tell that from a scan with nothing on it.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted and cannot be decrypted here.</exception>
        private static void RefuseUnreadableEncryption(byte[] pdfBytes)
        {
            if (Tetitulz.Pdf.Base.Encryption.PdfDecryptorHolder.Current != null) return;
            using var ms = new MemoryStream(pdfBytes, writable: false);
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
            if (doc.IsEncrypted)
                throw new PdfEncryptedDocumentException(
                    "The document is encrypted and no decryptor is registered, so its pages would " +
                    "render blank and the OCR would read nothing. Register one " +
                    "(PdfEncryptionOperations.Register()) or decrypt the document first.");
        }

        private static string ResolveTessData(string? tessDataPath) =>
            tessDataPath
            ?? Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
    }
}
