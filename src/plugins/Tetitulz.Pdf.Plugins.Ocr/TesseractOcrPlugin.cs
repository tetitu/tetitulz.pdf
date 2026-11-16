// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Ocr
{
    /// <summary>
    /// <see cref="IOcrPlugin"/> that recognises text in raw pixel data using Tesseract OCR.
    ///
    /// Requires Tesseract language data files (tessdata directory).
    /// Set the <c>TESSDATA_PREFIX</c> environment variable or supply the path to
    /// <see cref="TesseractOcrPlugin(string, string)"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new TesseractOcrPlugin(
    ///       tessDataPath: "/usr/share/tesseract-ocr/4.00/tessdata",
    ///       language: "eng"));
    /// </code>
    ///
    /// Drop <c>Tetitulz.Pdf.Plugins.Ocr.dll</c> into the plugin folder and call
    /// <c>PdfPluginHost.Instance.RegisterFromDirectory(pluginFolder)</c>.
    /// In that case the plugin uses the <c>TESSDATA_PREFIX</c> environment variable.
    ///
    /// <para><b>Hebrew documents want <c>"heb+eng"</c></b> (both <c>.traineddata</c> files in the
    /// tessdata folder): an Israeli invoice prints its part codes, e-mail addresses and English
    /// labels beside the Hebrew, and a one-language model reads the other script poorly. Scan at
    /// 300 dpi or more; the Hebrew model is reported weakest on pointed text (niqqud), which invoices
    /// rarely carry.</para>
    /// </summary>
    public sealed class TesseractOcrPlugin : IOcrWordPlugin
    {
        private readonly string? _tessDataPath;
        private readonly string _language;

        // The Tesseract engine loads language data and allocates native handles, so
        // it is cached and reused across pages/calls instead of being rebuilt each
        // time. TesseractEngine is NOT thread-safe, so all access is serialized.
        private Tesseract.TesseractEngine? _engine;
        private readonly object _engineLock = new object();

        /// <param name="tessDataPath">
        /// Path to the tessdata directory. Pass <c>null</c> to read from
        /// the <c>TESSDATA_PREFIX</c> environment variable.
        /// </param>
        /// <param name="language">Tesseract language code (default "eng").</param>
        public TesseractOcrPlugin(string? tessDataPath = null, string language = "eng")
        {
            _tessDataPath = tessDataPath;
            _language     = language ?? "eng";
        }

        public string Name        => "Tetitulz.Plugins.TesseractOcr";
        public string Description => $"OCR via Tesseract ({_language}).";
        public Version Version    => new Version(1, 0, 0);

        // ── Diagnostics ──────────────────────────────────────────────────────
        //
        // Every recognition failure is recorded here, on a slot a Release build keeps
        // (System.Diagnostics.Debug.WriteLine is [Conditional("DEBUG")], compiled out of the
        // shipped assembly). Otherwise a missing native libtesseract/libleptonica, a wrong language
        // file or a Tesseract version mismatch would look exactly like "the page had no text":
        // RecognizeText returns null either way, the same value PdfOperations.RecognizeTextWithOcr
        // returns when no OCR plugin is registered at all.
        //
        // [ThreadStatic] per CLAUDE.md: a Last* slot read after an operation must belong to the
        // thread that ran it. It is reset at the TOP of RecognizeText on every path, so a value
        // left by an earlier page cannot be mistaken for this one's.

        [ThreadStatic] private static string? _lastError;

        /// <summary>
        /// Why the last <see cref="RecognizeText"/> on this thread returned <c>null</c>, or
        /// <c>null</c> when it succeeded (or was never called on this thread).
        /// </summary>
        /// <remarks>
        /// This is the only way to tell "the image carried no text" from "the engine could not be
        /// constructed". Both return <c>null</c> from <see cref="RecognizeText"/> by contract,
        /// because the caller uses <c>null</c> to fall through to the next OCR plugin.
        /// </remarks>
        public static string? LastError => _lastError;

        public void Initialize()
        {
            string path = ResolvedTessDataPath();
            if (!Directory.Exists(path))
                throw new InvalidOperationException(
                    $"TesseractOcrPlugin: tessdata folder not found at '{path}'. " +
                    "Set TESSDATA_PREFIX or pass tessDataPath to the constructor.");

            // Directory.Exists proves the corpus is on disk, not that the engine can load. A
            // container with a tessdata directory and no native libtesseract passed this check and
            // then failed EVERY recognition, which the caller could not distinguish from blank
            // pages. Constructing one engine here moves that failure to registration, where a host
            // can act on it — the same place the missing-tessdata error is already reported.
            try
            {
                using var probe = new Tesseract.TesseractEngine(path, _language, Tesseract.EngineMode.Default);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"TesseractOcrPlugin: the Tesseract engine could not be created for language " +
                    $"'{_language}' from tessdata folder '{path}': {ex.Message} " +
                    "(a missing native libtesseract/libleptonica, or a missing language file, " +
                    "reaches this path).", ex);
            }
        }

        /// <summary>
        /// Disposes the cached engine and makes the plugin <b>terminal</b>: a later
        /// <see cref="RecognizeText"/> refuses instead of building a fresh engine.
        /// </summary>
        /// <remarks>
        /// Without the flag, a call in flight when the host unregisters the plugin would reach
        /// <c>_engine ??= new TesseractEngine(...)</c> after Shutdown and allocate native handles
        /// that nothing disposes — the host has dropped its reference and will not call Shutdown
        /// again — leaking language data and Leptonica handles once per plugin cycle.
        /// </remarks>
        public void Shutdown()
        {
            lock (_engineLock)
            {
                _shutdown = true;
                _engine?.Dispose();
                _engine = null;
            }
        }

        // Set inside _engineLock, and read inside it, so the check and the construction below
        // cannot straddle a concurrent Shutdown.
        private bool _shutdown;

        /// <summary>
        /// Returns <c>true</c> for grayscale (1 component) and RGB (3 components) at 8 bpp.
        /// </summary>
        public bool CanProcess(int components, int bitsPerComponent)
            => bitsPerComponent == 8 && (components == 1 || components == 3);

        /// <summary>
        /// Recognises text in <paramref name="pixels"/> using Tesseract.
        /// Returns <c>null</c> if recognition fails (caller falls back to next plugin);
        /// <see cref="LastError"/> then says why.
        /// </summary>
        public string? RecognizeText(byte[] pixels, int width, int height, int components)
        {
            // Reset on EVERY path, at the top: a slot a caller reads after an operation must not
            // still hold the previous page's message when this one succeeded.
            _lastError = null;

            if (!ValidateInput(pixels, width, height, components)) return null;

            try
            {
                // Convert raw pixels to a PNG in memory so Tesseract.Pix can load it.
                byte[] png = RawToPng(pixels, width, height, components);
                lock (_engineLock)
                {
                    if (_shutdown)
                    {
                        _lastError = "TesseractOcrPlugin: the plugin has been shut down; no engine " +
                                     "is created for a call that arrives after Shutdown.";
                        return null;
                    }
                    var engine = _engine ??= new Tesseract.TesseractEngine(
                        ResolvedTessDataPath(), _language, Tesseract.EngineMode.Default);
                    using var pix  = Tesseract.Pix.LoadFromMemory(png);
                    using var page = engine.Process(pix);
                    return page.GetText()?.Trim() ?? string.Empty;
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                // Recognition failed (corrupt data, unsupported format, engine error).
                // Return null so the caller falls back to the next OCR plugin — but RECORD why,
                // on a slot that survives a Release build.
                _lastError =
                    $"TesseractOcrPlugin: recognition failed ({ex.GetType().Name}): {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Recognises text and reports where each word is — the half a searchable PDF needs.
        /// </summary>
        /// <remarks>
        /// <para><b>The rectangles are in image pixels, top-left origin</b>, exactly as Tesseract
        /// reports them; converting to PDF points needs the dpi and the page height, which only the
        /// caller knows.</para>
        /// <para><b>Word order is Tesseract's, and is not re-sorted.</b> Its iterator walks a line
        /// in reading order, which for Hebrew and Arabic is rightmost first, and the text of each
        /// word is in logical order. Sorting by ascending x here would silently turn a
        /// right-to-left page into a text layer that extracts backwards.</para>
        /// <para>Returns an empty list when the engine ran and found nothing, and <c>null</c> when
        /// it could not run — <see cref="LastError"/> says which.</para>
        /// </remarks>
        public IReadOnlyList<OcrWordBox>? RecognizeWords(byte[] pixels, int width, int height, int components)
        {
            _lastError = null;

            if (!ValidateInput(pixels, width, height, components)) return null;

            try
            {
                byte[] png = RawToPng(pixels, width, height, components);
                lock (_engineLock)
                {
                    if (_shutdown)
                    {
                        _lastError = "TesseractOcrPlugin: the plugin has been shut down; no engine " +
                                     "is created for a call that arrives after Shutdown.";
                        return null;
                    }
                    var engine = _engine ??= new Tesseract.TesseractEngine(
                        ResolvedTessDataPath(), _language, Tesseract.EngineMode.Default);
                    using var pix  = Tesseract.Pix.LoadFromMemory(png);
                    using var page = engine.Process(pix);

                    var boxes = new List<OcrWordBox>();
                    using var iter = page.GetIterator();
                    iter.Begin();
                    do
                    {
                        do
                        {
                            string? text = iter.GetText(Tesseract.PageIteratorLevel.Word);
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            if (!iter.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word, out var rect)) continue;

                            boxes.Add(new OcrWordBox(
                                text.Trim(), rect.X1, rect.Y1, rect.Width, rect.Height,
                                // Tesseract reports 0-100; the contract is 0-1.
                                iter.GetConfidence(Tesseract.PageIteratorLevel.Word) / 100f));
                        }
                        while (iter.Next(Tesseract.PageIteratorLevel.TextLine, Tesseract.PageIteratorLevel.Word));
                    }
                    while (iter.Next(Tesseract.PageIteratorLevel.TextLine));

                    return boxes;
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                _lastError =
                    $"TesseractOcrPlugin: word recognition failed ({ex.GetType().Name}): {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// The input checks <see cref="RecognizeText"/> and <see cref="RecognizeWords"/> share.
        /// Sets <see cref="LastError"/> and returns false when the input cannot be recognised.
        /// </summary>
        /// <remarks>
        /// <b>The components check re-applies the routing predicate, and that is not redundant.</b>
        /// <see cref="CanProcess"/> is what the dispatcher consults, but these are public methods
        /// on a public interface and a host reaching the plugin directly can hand it anything.
        /// <c>WritePng</c> maps every components value other than 1 to PNG colour type 2 (RGB,
        /// 3 bytes/pixel) while striding width * components, so a 4-component buffer produced a
        /// structurally valid PNG whose scanlines are misaligned — Leptonica reads 3w bytes per row
        /// and shears the image, and Tesseract returns plausible-looking garbage. A wrong answer
        /// instead of a refusal is the one direction the documented contract must not fail in.
        /// <para>One copy for both entry points, because two would drift and the second one would
        /// be the lenient one.</para>
        /// </remarks>
        private bool ValidateInput(byte[] pixels, int width, int height, int components)
        {
            if (pixels == null || pixels.Length == 0)
            {
                _lastError = "TesseractOcrPlugin: no pixel data was supplied.";
                return false;
            }

            if (!CanProcess(components, 8))
            {
                _lastError = $"TesseractOcrPlugin: {components}-component input is not supported " +
                             "(only 1 = grayscale and 3 = RGB, at 8 bits per component).";
                return false;
            }

            if (width <= 0 || height <= 0)
            {
                _lastError = $"TesseractOcrPlugin: the image dimensions {width}x{height} are empty.";
                return false;
            }

            if (pixels.Length < (long)width * height * components)
            {
                _lastError = $"TesseractOcrPlugin: the pixel buffer holds {pixels.Length} bytes, " +
                             $"short of the {(long)width * height * components} a {width}x{height} " +
                             $"image with {components} component(s) needs.";
                return false;
            }

            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string ResolvedTessDataPath()
            => _tessDataPath
               ?? Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
               ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        /// <summary>
        /// Raw interleaved 8-bit pixel data, grey or RGB, as the PNG Tesseract loads — through
        /// <see cref="Base.Rendering.PdfRawImageEncoder"/>, Base's one encoder.
        /// </summary>
        internal static byte[] RawToPng(byte[] pixels, int width, int height, int components)
            => Base.Rendering.PdfRawImageEncoder.EncodePng(pixels, width, height, components);
    }
}
