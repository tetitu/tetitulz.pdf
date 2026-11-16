// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// Rasterizes SVG markup to PNG bytes by reusing the pure-managed
    /// <see cref="SvgToPdfConverter"/> (SVG → 1-page PDF) and <see cref="PdfPageRenderer"/>
    /// (PDF page → PNG). Intended to be plugged into <c>HtmlToPdfOptions.SvgRasterizer</c> so the
    /// managed HTML→PDF engine can embed inline <c>&lt;svg&gt;</c> as a raster image — without the
    /// engine itself taking a SkiaSharp dependency (it lives here, in Base).
    /// </summary>
    public static class SvgRasterizer
    {
        /// <summary>Default render resolution (~2× CSS 96dpi) for reasonable crispness.</summary>
        public const int DefaultDpi = 192;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the last <see cref="RasterizeToPng(string,int)"/> call on <b>this thread</b> returned
        /// <c>null</c>, or <c>null</c> when it returned a PNG. Reset at the start of every call, on
        /// every path.
        ///
        /// <para><b>Why this exists.</b> The whole SVG → PDF → PNG chain sat inside
        /// <c>catch { return null; }</c> with no diagnostic of any kind, so a caller could not tell
        /// "no SVG was supplied" from "the SVG used a construct this converter does not support"
        /// from "the converter is broken". That last case is not hypothetical: the converter once
        /// shipped seeding its walk with <c>new Transform()</c> instead of
        /// <c>Transform.Identity</c>, so every conversion produced a blank page — and HtmlToPdf's
        /// managed engine, which reaches this through <c>HtmlToPdfOptions.SvgRasterizer</c>, simply
        /// left a hole where the graphic was. Nothing anywhere recorded that a conversion had been
        /// attempted and failed, which is the condition that let the regression survive sixteen
        /// green tests.</para>
        ///
        /// <para>A non-null result may still carry a warning: it also relays
        /// <see cref="SvgToPdfConverter.LastError"/>, so "the graphic was rasterized but
        /// its text was dropped" is visible too.</para>
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>Rasterizes SVG markup to PNG bytes at <see cref="DefaultDpi"/>; null on failure.</summary>
        public static byte[] RasterizeToPng(string svgXml) => RasterizeToPng(svgXml, DefaultDpi);

        /// <summary>
        /// Rasterizes SVG markup to PNG bytes at the given DPI. The intermediate PDF page is sized
        /// to the SVG's own <c>width</c>/<c>height</c> (else its <c>viewBox</c>, else the SVG 300×150
        /// default) so the PNG is exactly the graphic — not a corner of an A4 page. Returns
        /// <c>null</c> when the SVG is empty / malformed / uses an unsupported construct, with the
        /// reason on <see cref="LastError"/>.
        /// </summary>
        public static byte[] RasterizeToPng(string svgXml, int dpi)
        {
            _lastError = null;

            if (string.IsNullOrWhiteSpace(svgXml))
            {
                _lastError = "no SVG markup was supplied";
                return null;
            }
            if (dpi <= 0) dpi = DefaultDpi;
            try
            {
                // The converter's own reading of the root's size — a copy would take width="100%" for
                // the number 100.
                var (w, h) = SvgToPdfConverter.IntrinsicSize(svgXml);
                byte[] pdf = SvgToPdfConverter.Convert(svgXml,
                    new SvgConversionOptions { PageWidth = w, PageHeight = h });
                using var ms = new MemoryStream(pdf);
                byte[] png = PdfPageRenderer.RenderPage(ms, 0, dpi); // PNG-encoded bytes

                if (png == null)
                {
                    _lastError = $"the {svgXml.Length}-character SVG converted to a PDF that rendered " +
                                 "no page";
                    RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed, _lastError);
                    return null;
                }

                // A conversion that dropped content still produced a PNG; say so rather than
                // letting a partial rasterization look identical to a faithful one.
                _lastError = SvgToPdfConverter.LastError;
                return png;
            }
            catch (Exception ex)
            {
                _lastError = $"SVG rasterization failed for a {svgXml.Length}-character document: " +
                             ex.Message;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed, _lastError, ex);
                return null; // degrade gracefully — caller skips the <svg>
            }
        }
    }
}
