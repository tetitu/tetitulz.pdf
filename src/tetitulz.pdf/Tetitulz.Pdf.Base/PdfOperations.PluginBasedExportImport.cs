// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Exports an <b>encrypted</b> document through a registered export plugin.
        /// </summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// <para>A separate overload with a <b>required</b> password: appending
        /// <c>password = null</c> to the signature above would be a binary break, and the
        /// password-free overload keeps its own call path.</para>
        /// <para><b>The password does not cross the plugin boundary — the decrypted stream does.</b>
        /// The dispatch passes a <c>Stream</c>, so <c>WithDecrypted</c> hands the plugin plaintext
        /// and the plugin contract carries no password. It is the
        /// same inversion used everywhere else here — the <i>operation</i> or the <i>data</i>
        /// travels, never the key.</para>
        /// </remarks>
        /// <param name="input">Source PDF stream (readable, seekable).</param>
        /// <param name="output">Destination stream for the converted output.</param>
        /// <param name="formatKey">Short format identifier claimed by a registered <see cref="IExportPlugin"/>.</param>
        /// <param name="options">Per-format export options, or <c>null</c> for the plugin defaults.</param>
        public static void Export(
            Stream input, Stream output, string formatKey, string password,
            ExportOptions options = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            // WithDecrypted is generic over a return value; the export writes to `output` and
            // returns nothing, so the lambda yields a discarded token rather than the method
            // growing a second shape.
            WithDecrypted(input, password, s =>
            {
                Export(s, output, formatKey, options);
                return true;
            });
        }

        /// <summary>
        /// Exports a PDF document to another format using a registered <see cref="IExportPlugin"/>.
        /// </summary>
        /// <param name="input">Source PDF stream (readable, seekable).</param>
        /// <param name="output">Destination stream for the converted output.</param>
        /// <param name="formatKey">
        /// Short format identifier, e.g. "docx", "epub", "html", "txt".
        /// Must match <see cref="IExportPlugin.FormatKey"/> of a registered plugin (case-insensitive).
        /// </param>
        /// <param name="options">Optional format-specific export settings.</param>
        /// <exception cref="NotSupportedException">
        /// Thrown when no <see cref="IExportPlugin"/> for <paramref name="formatKey"/> is registered.
        /// </exception>
        public static void Export(Stream input, Stream output, string formatKey, ExportOptions options = null)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (formatKey == null) throw new ArgumentNullException(nameof(formatKey));

            var plugin = PdfPluginHost.Instance.FindExportPlugin(formatKey)
                ?? throw new NotSupportedException(
                    $"No export plugin registered for format '{formatKey}'. " +
                    $"Register an IExportPlugin implementation via PdfPluginHost.Instance.Register(...).");

            using var doc = PdfDocument.Open(input);
            plugin.Export(doc, output, options);
        }

        /// <summary>
        /// Imports (converts) a document in another format to PDF using a registered <see cref="IImportPlugin"/>.
        /// </summary>
        /// <param name="source">Source document stream (readable).</param>
        /// <param name="pdfOutput">Destination PDF stream (writable).</param>
        /// <param name="formatKey">
        /// Short format identifier, e.g. "docx", "html", "svg".
        /// Must match <see cref="IImportPlugin.FormatKey"/> of a registered plugin (case-insensitive).
        /// </param>
        /// <param name="options">Optional format-specific import settings.</param>
        /// <exception cref="NotSupportedException">
        /// Thrown when no <see cref="IImportPlugin"/> for <paramref name="formatKey"/> is registered.
        /// </exception>
        public static void Import(Stream source, Stream pdfOutput, string formatKey, ImportOptions options = null)
        {
            if (source == null)    throw new ArgumentNullException(nameof(source));
            if (pdfOutput == null) throw new ArgumentNullException(nameof(pdfOutput));
            if (formatKey == null) throw new ArgumentNullException(nameof(formatKey));

            var plugin = PdfPluginHost.Instance.FindImportPlugin(formatKey)
                ?? throw new NotSupportedException(
                    $"No import plugin registered for format '{formatKey}'. " +
                    $"Register an IImportPlugin implementation via PdfPluginHost.Instance.Register(...).");

            plugin.Import(source, pdfOutput, options ?? new ImportOptions());
        }

        /// <summary>
        /// Validates a PDF against a conformance level using a registered
        /// <see cref="IValidationPlugin"/>.
        /// </summary>
        /// <param name="input">PDF stream (readable and seekable). Not disposed by this method.</param>
        /// <param name="conformanceLevel">
        /// Conformance level, e.g. "PDF/A-1b", "PDF/UA-1", "PDF/X-1a". Matched against
        /// <see cref="IValidationPlugin.ConformanceLevel"/> case-insensitively.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Thrown when no <see cref="IValidationPlugin"/> for <paramref name="conformanceLevel"/>
        /// is registered. <see cref="Validate(Stream, string)"/> returns <c>null</c> in that case
        /// instead, which is the contract the rest of the library is written against.
        /// </exception>
        /// <remarks>
        /// <para><b>Superseded by <see cref="Validate(Stream, string)"/>.</b> The two are the same
        /// dispatch over <see cref="IValidationPlugin"/> with opposite failure contracts: this one
        /// throws when the validator assembly is not registered — the ordinary state, since the
        /// validators are opt-in packages — while <c>Validate</c> returns <c>null</c> and puts the
        /// reason on <see cref="LastValidationError"/>. Two contracts for one dispatch means no
        /// single wrapper can be correct for both, which is why this pair is obsolete rather than
        /// merely redundant. It delegates, so the two cannot diverge on seek-normalisation (a
        /// mid-position stream reaches the plugin rewound) or on the <c>IsPartial</c> caveat.</para>
        /// </remarks>
        [Obsolete("Use Validate(Stream, string), which reports a missing validator through " +
                  "LastValidationError instead of throwing. This overload will be removed.")]
        public static PluginValidationResult ValidateConformance(Stream input, string conformanceLevel)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (conformanceLevel == null) throw new ArgumentNullException(nameof(conformanceLevel));

            var result = Validate(input, conformanceLevel);
            if (result == null)
                throw new NotSupportedException(
                    LastValidationError ??
                    $"No validation plugin registered for conformance level '{conformanceLevel}'. " +
                    "Register an IValidationPlugin implementation via PdfPluginHost.Instance.Register(...).");

            return result;
        }

        /// <summary>
        /// Conformance levels for which an <see cref="IValidationPlugin"/> is currently
        /// registered. Empty when none are.
        /// </summary>
        /// <remarks>
        /// <b>Superseded by <see cref="GetSupportedConformanceLevels"/>.</b> The only difference
        /// was that this one skipped plugins with a null or empty
        /// <see cref="IValidationPlugin.ConformanceLevel"/>; it now filters the survivor's list so
        /// the two cannot enumerate different sets.
        /// </remarks>
        [Obsolete("Use GetSupportedConformanceLevels(). This overload will be removed.")]
        public static IReadOnlyList<string> GetAvailableConformanceLevels()
        {
            var levels = new List<string>();
            foreach (string level in GetSupportedConformanceLevels())
                if (!string.IsNullOrEmpty(level))
                    levels.Add(level);
            return levels;
        }

        /// <summary>Runs OCR over a page of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. The password sits <i>before</i> the optional tail —
        /// a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">PDF stream (readable and seekable). Not disposed by this method.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rasterization resolution. Higher values improve recognition accuracy.</param>
        public static string RecognizeTextWithOcr(
            Stream input, int pageIndex, string password, int dpi = DefaultOcrDpi)
            => WithDecrypted(input, password, s => RecognizeTextWithOcr(s, pageIndex, dpi));

        /// <summary>
        /// Recognises text in a rendered page using a registered <see cref="IOcrPlugin"/>.
        /// Intended for scanned/image-only pages, where <see cref="ExtractText(Stream,string)"/>
        /// returns nothing because the page carries no text operators.
        /// </summary>
        /// <param name="input">PDF stream (readable and seekable). Not disposed by this method.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rasterization resolution. Higher values improve recognition accuracy.</param>
        /// <returns>
        /// The recognised text, or <c>null</c>. A <c>null</c> has five distinct causes, and
        /// <see cref="LastOcrError"/> says which — "no engine was ever consulted" and "the engine
        /// ran and found nothing" are opposite conclusions about a scan.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>Opt-in by design.</b> The text extraction pipeline does not dispatch
        /// <see cref="IOcrPlugin"/>: rather than silently changing what <see cref="ExtractText(Stream,string)"/> returns
        /// (OCR is lossy, slow, and would alter existing callers' results), recognition is an
        /// explicit call. The interface documentation has been corrected to match.
        /// </para>
        /// </remarks>
        public static string RecognizeTextWithOcr(Stream input, int pageIndex, int dpi = DefaultOcrDpi)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            // Reset at the top, on every path: a slot a caller reads AFTER an operation must be
            // cleared at its START, or the previous page's reason answers this page's question.
            _lastOcrError = null;

            // One byte each of R, G and B per pixel, in Images.OcrSampleBuffer — the same decision
            // RecognizeWordsWithOcr makes, because the constants a plugin is SELECTED by have to be
            // the constants its bytes are WRITTEN under. The engine is found before the page is
            // rendered: without one the render is wasted.
            const int components = Images.OcrSampleBuffer.Components;
            const int bitsPerComponent = Images.OcrSampleBuffer.BitsPerComponent;
            var plugin = PdfPluginHost.Instance.FindOcrPlugin(components, bitsPerComponent);
            if (plugin == null)
            {
                _lastOcrError =
                    $"no registered IOcrPlugin accepts {components} components at " +
                    $"{bitsPerComponent} bits, so no engine was consulted. Reference " +
                    "Tetitulz.Pdf.Plugins.Ocr and register it (directly, or via " +
                    "PdfPluginHost.Instance.RegisterFromLoadedAssemblies()).";
                return null;
            }

            // Render to RGB samples, then hand them to the plugin.
            byte[] png = Rendering.PdfPageRenderer.RenderPage(input, pageIndex, dpi);
            if (png == null || png.Length == 0)
            {
                _lastOcrError = $"page {pageIndex} rendered no image data, so nothing was recognised";
                return null;
            }

            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            if (bitmap == null)
            {
                _lastOcrError = $"the rendered image of page {pageIndex} could not be decoded, " +
                                "so nothing was recognised";
                return null;
            }

            byte[] pixels = Images.OcrSampleBuffer.TryPack(bitmap, out long required);
            if (pixels == null)
            {
                _lastOcrError =
                    $"page {pageIndex} is {bitmap.Width}x{bitmap.Height} at {dpi} dpi, whose " +
                    $"{components}-component buffer needs {required} bytes and cannot be " +
                    "allocated; render at a lower dpi";
                return null;
            }

            string text = plugin.RecognizeText(pixels, bitmap.Width, bitmap.Height, components);
            if (text == null)
                _lastOcrError = $"{plugin.Name} ran over page {pageIndex} and returned no text";
            return text;
        }

        [ThreadStatic] private static string _lastOcrError;

        /// <summary>
        /// Why the most recent <see cref="RecognizeTextWithOcr(Stream,int,int)"/> on <b>this thread</b> returned
        /// <c>null</c>, or <c>null</c> when it returned text.
        /// </summary>
        /// <remarks>
        /// <para>Five different causes would otherwise share one bare <c>null</c>: the page rendered
        /// nothing, the render would not decode, no plugin accepts RGB8, the pixel buffer would
        /// overflow <c>int</c>, and the engine ran and found nothing. A host that forgets
        /// <c>RegisterFromLoadedAssemblies()</c> would see the same value as one whose scans are blank,
        /// and logged "no text on this page" for both — the wrong conclusion for four of the
        /// five.</para>
        /// <para><c>[ThreadStatic]</c> per the Diagnostics Pattern in CLAUDE.md; read it on the
        /// thread that made the call. Modelled on
        /// <see cref="Scripting.PluginJavaScriptRunner.LastError"/>, including the
        /// "reference the package and register it" hint.</para>
        /// </remarks>
        public static string LastOcrError => _lastOcrError;
    }
}
