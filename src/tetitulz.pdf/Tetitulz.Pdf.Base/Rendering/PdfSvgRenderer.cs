// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
// Aliased rather than `using SkiaSharp`, matching SKPath above: this renderer emits vectors and
// pulls in only the Skia types it genuinely needs. SKColor is the currency IccColorTransform and
// PdfColor already speak, so the ICC result crosses into CSS through PdfColor.ToCssRgb.
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Renders PDF pages to SVG markup by walking the content stream and translating
    /// each operator into SVG elements.
    ///
    /// <para>Output characteristics:</para>
    /// <list type="bullet">
    ///   <item>Paths → <c>&lt;path d="..."/&gt;</c> with fill/stroke attributes.</item>
    ///   <item>Text → <c>&lt;text&gt;</c> with <c>transform</c> and font size.</item>
    ///   <item>Images → <c>&lt;image&gt;</c> with inline base-64 PNG data URI.</item>
    ///   <item>CTM changes → <c>&lt;g transform="matrix(...)"&gt;</c> groups.</item>
    /// </list>
    ///
    /// Usage:
    /// <code>
    ///   string svg = PdfSvgRenderer.RenderPageToSvg("doc.pdf", 0);
    ///   File.WriteAllText("page1.svg", svg);
    /// </code>
    /// </summary>
    public static class PdfSvgRenderer
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        // The four entry points take an OPTIONAL PdfRenderOptions, added so the SVG export can
        // honour the same policy switches the raster path does — `SimulateOverprint` above all.
        // Optional, so a caller without options compiles and behaves the same.

        // Cancellation arrives through a SEPARATE, WIDER overload of each — never by appending an
        // optional parameter to the shipped signature. C# bakes an optional-argument default in at
        // the CALL SITE, so widening a shipped member is a binary break: an assembly already
        // compiled against the narrow form emits a call to a member that no longer exists and fails
        // with MissingMethodException at run time, while still compiling from source. This library
        // ships as NuGet packages and multi-targets netstandard2.0 precisely to serve net462 hosts,
        // which is exactly the consumer that break reaches.
        //
        // These four WERE widened in place, and the API-baseline guard recorded it as 8 removed / 8
        // added — the evidence was on screen and was read as "regenerate the baseline". The guard
        // notifies; it does not enforce. Same rule and same reasoning as the password overloads on
        // PdfOperations.DocumentInfo.
        //
        // The token comes BEFORE options because a required parameter cannot follow an optional one,
        // and that placement is also what makes each pair unambiguous at every arity: the wide
        // overload is inapplicable unless a token is actually supplied (CancellationToken is a
        // non-nullable struct, so `null` cannot bind to it), so no existing call site can start
        // resolving to it.
        //
        // So the SVG path can be abandoned, as a hostile raster render can.

        /// <summary>Renders one page of a PDF file to an SVG string.</summary>
        public static string RenderPageToSvg(string inputPath, int pageIndex,
                                             PdfRenderOptions options = null)
            => RenderPageToSvg(inputPath, pageIndex, default, options);

        /// <summary>Renders one page of a PDF file to an SVG string, abandoning it on cancellation.</summary>
        public static string RenderPageToSvg(string inputPath, int pageIndex,
                                             System.Threading.CancellationToken cancellationToken,
                                             PdfRenderOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageToSvg(fs, pageIndex, cancellationToken, options);
        }

        /// <summary>Renders one page of a PDF stream to an SVG string.</summary>
        public static string RenderPageToSvg(Stream input, int pageIndex,
                                             PdfRenderOptions options = null)
            => RenderPageToSvg(input, pageIndex, default, options);

        /// <summary>Renders one page of a PDF stream to an SVG string, abandoning it on cancellation.</summary>
        public static string RenderPageToSvg(Stream input, int pageIndex,
                                             System.Threading.CancellationToken cancellationToken,
                                             PdfRenderOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // The SAME prologue the raster entry points use: seek-normalise, decrypt, open, and
            // dispose whatever this call owns. Doing it by hand here dropped half of the last
            // step — the buffer EnsureSeekable allocates for a non-seekable input was never
            // disposed — and it could not read the page tree of an encrypted document at all
            // before the decryption line was added, reporting a locked file as an empty one.
            return PdfPageRenderer.WithDecryptedPage(input, pageIndex, options?.Password,
                (page, doc) => RenderPage(page, doc, options, cancellationToken));
        }

        /// <summary>Renders all pages of a PDF file to SVG strings.</summary>
        public static string[] RenderAllPagesToSvg(string inputPath, PdfRenderOptions options = null)
            => RenderAllPagesToSvg(inputPath, default, options);

        /// <summary>Renders all pages of a PDF file to SVG strings, abandoning on cancellation.</summary>
        public static string[] RenderAllPagesToSvg(string inputPath,
                                                   System.Threading.CancellationToken cancellationToken,
                                                   PdfRenderOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderAllPagesToSvg(fs, cancellationToken, options);
        }

        /// <summary>Renders all pages of a PDF stream to SVG strings.</summary>
        public static string[] RenderAllPagesToSvg(Stream input, PdfRenderOptions options = null)
            => RenderAllPagesToSvg(input, default, options);

        /// <summary>Renders all pages of a PDF stream to SVG strings, abandoning on cancellation.</summary>
        public static string[] RenderAllPagesToSvg(Stream input,
                                                   System.Threading.CancellationToken cancellationToken,
                                                   PdfRenderOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Same prologue as the single-page path. The raster side learned this the hard way —
            // its JPEG and tile entry points once rendered still-encrypted content streams while
            // the PNG path was correct, so one file produced two different answers.
            return PdfPageRenderer.WithDecryptedDocument(input, options?.Password, doc =>
            {
                var results = new string[doc.Pages.Count];
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    // Between pages as well as within them: a hostile document can be a thousand
                    // cheap pages rather than one expensive one, and the in-stream poll would never
                    // fire on any of them.
                    cancellationToken.ThrowIfCancellationRequested();
                    results[i] = RenderPage(doc.Pages[i], doc, options, cancellationToken);
                }
                return results;
            });
        }

        // ----------------------------------------------------------------
        //  Core render
        // ----------------------------------------------------------------

        private static string RenderPage(PdfPage page, PdfDocument doc, PdfRenderOptions options = null,
                                         System.Threading.CancellationToken cancellationToken = default)
        {
            options ??= PdfRenderOptions.Default;
            NamedSpotColors.Attach(doc, options);
            // The SAME box decision the raster renderer makes. Reading `CropBox ?? MediaBox` here
            // meant UseTrimBox/UseBleedBox/UseArtBox and the document's /ViewerPreferences box
            // choice were honoured when rendering and ignored when exporting, and a /CropBox
            // larger than the /MediaBox was not clipped to it (§14.11.2) — one file, two sizes.
            var box  = PdfPageRenderer.ResolveRenderBox(page, doc, options);
            // §7.9.5's normalisation AND the "no usable page box" fallback, in PageBoxMetrics —
            // shared with the raster renderer, so the two cannot differ on it. Subtracting blindly
            // would give a NEGATIVE width for an inverted box, and an SVG no viewer will render.
            PageBoxMetrics metrics = PageBoxMetrics.Of(box);
            float pdfX = metrics.X;
            float pdfY = metrics.Y;
            float pdfW = metrics.Width;
            float pdfH = metrics.Height;

            // §14.11.1: /UserUnit scales the DEFAULT USER SPACE — one unit is UserUnit points. The
            // raster path folds it into the device scale (PdfPageRenderer: `dpi / 72 * userUnit`);
            // the SVG analogue is to grow the root viewport by the same factor and scale the
            // content group into it, so page geometry (the CropBox origin shift, /Rotate) stays in
            // user units, so a /UserUnit 2 page exports at its size. The guard is shared, the same
            // shape as ResolveRenderBox, so a /UserUnit 0 page is treated alike in both.
            float userUnit = PdfPageRenderer.ResolveUserUnit(page);

            // The one reader — see PdfPageRenderer's note — so an unquantised /Rotate 45 never
            // reaches `rotate(45,…)` while `swap` says portrait.
            int rotate = PdfPageRotation.Normalize(page.Rotate, out bool rotateMalformed);
            if (rotateMalformed)
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"/Rotate {page.Rotate} is not a multiple of 90 (§7.7.3.3); the page is " +
                    "exported upright.");
            bool swap = PdfPageRotation.SwapsAxes(rotate);
            float svgW = (swap ? pdfH : pdfW) * userUnit;
            float svgH = (swap ? pdfW : pdfH) * userUnit;

            var sb = new StringBuilder(8192);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\"");
            sb.Append($" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
            sb.Append($" width=\"{F(svgW)}\" height=\"{F(svgH)}\"");
            sb.Append($" viewBox=\"0 0 {F(svgW)} {F(svgH)}\">\n");

            // Page background, from PdfRenderOptions.BackgroundArgb — the SAME option the raster
            // renderer clears its surface with. This was a hardcoded opaque white <rect>, so
            // BackgroundArgb was honoured when rendering and silently discarded when exporting:
            // the identical call asking for a transparent page returned transparent pixels and an
            // opaque-white SVG. A fully transparent background emits no rect at all, which is how
            // SVG spells "nothing behind the content".
            PdfColor.UnpackArgb(options.BackgroundArgb, out byte bgA, out byte bgR, out byte bgG, out byte bgB);
            if (bgA > 0)
            {
                // The default keeps its historical spelling so an unchanged call still produces
                // byte-identical markup — the same courtesy PngCompression and JpegSubsampling
                // extend to their defaults. Only a caller who asked for a different background
                // sees different bytes.
                string fill = options.BackgroundArgb == PdfRenderOptions.OpaqueWhite
                    ? "white"
                    : $"rgb({bgR},{bgG},{bgB})";
                string opacity = bgA == 255 ? string.Empty : $" fill-opacity=\"{F(bgA / 255f)}\"";
                sb.Append($"<rect width=\"{F(svgW)}\" height=\"{F(svgH)}\" fill=\"{fill}\"{opacity}/>\n");
            }

            // PDF coordinate system: origin bottom-left, Y-up.
            // SVG coordinate system: origin top-left, Y-down.
            // Apply Y-flip so that PDF coordinates work directly.
            // scale(userUnit, -userUnit): the Y-flip and the /UserUnit device scale in one step, so
            // everything after it (the /Rotate, the CropBox origin shift) is expressed in user units.
            // The items apply to a point right to left: the box's corner is moved to the origin,
            // then §7.7.3.3's clockwise turn maps the box onto [0, svgW] × [0, svgH] (still Y-up —
            // a 90° turn takes the box's top-left corner to the view's top-right), then the flip.
            // That is the turn the raster makes about the bitmap's centre; a rotate() about the
            // unturned box's centre left a landscape page's content outside its portrait viewBox.
            sb.Append($"<g transform=\"translate(0,{F(svgH)}) scale({F(userUnit)},{F(-userUnit)})");
            switch (rotate)
            {
                case 90:  sb.Append($" matrix(0,-1,1,0,0,{F(pdfW)})"); break;
                case 180: sb.Append($" matrix(-1,0,0,-1,{F(pdfW)},{F(pdfH)})"); break;
                case 270: sb.Append($" matrix(0,1,-1,0,{F(pdfH)},0)"); break;
            }
            if (pdfX != 0f || pdfY != 0f)
                sb.Append($" translate({F(-pdfX)},{F(-pdfY)})");
            sb.Append("\">\n");

            // Resolved once and shared with the annotation pass: an annotation on a hidden layer
            // must be skipped too (§12.5.2).
            var hiddenOcgs = PdfPageRenderer.BuildHiddenOcgSet(doc);

            // ONE registry for the whole page, including the annotation pass. Every generated id
            // (clip, pattern, gradient, mask, glyph, image) is minted from its counter, so a page
            // whose content and whose annotation appearances both clip cannot emit two id="clip1".
            var shared = new SvgSharedState
            {
                PageX = pdfX,
                PageY = pdfY,
                // One budget for the whole page, so the content stream and every annotation
                // appearance draw against the same allowance rather than each getting a fresh one.
                FormBudget = new RenderBudget(),
                // One font cache for the page, its annotations included, as the raster page has.
                Fonts = new FontCache(),
            };

            try
            {
                byte[] content = page.GetContentStreamData();
                if (content != null && content.Length > 0)
                {
                    var writer = new SvgContentWriter(sb, doc, page.Resources, pdfW, pdfH);
                    writer.SetAmbient(hiddenOcgs, shared, options, cancellationToken);
                    // The page group's /CS (§11.6.6, honoured by the raster renderer) is one more thing
                    // an SVG document composites in sRGB regardless of.
                    writer.ReportGroupSpaceApproximation(PdfColor.TransparencyGroupColorSpace(page.Dictionary, doc));
                    writer.Execute(content);
                }

                // Render annotations after the content stream so they layer on top —
                // matches the SkiaSharp renderer's order and the PDF spec's painting
                // model (annotations are part of the page's visible appearance).
                RenderAnnotationsToSvg(page, doc, sb, hiddenOcgs, pdfW, pdfH, shared, options,
                                       cancellationToken);
            }
            finally
            {
                shared.Fonts.Dispose();
            }

            sb.Append("</g>\n</svg>");
            // Soft proofing, as the raster surface is proofed (PdfRenderOptions.ProofProfile).
            return SvgProof.Apply(sb.ToString(), options);
        }

        /// <summary>
        /// Emits every entry in the page's <c>/Annots</c> array.
        ///
        /// <para><b>The appearance stream comes first.</b> §12.5.5 says an annotation's
        /// <c>/AP</c> <c>/N</c> stream <i>shall</i> be used to render it, selected by <c>/AS</c>
        /// when <c>/N</c> is a sub-state dictionary, and mapped onto <c>/Rect</c> by Algorithm 8.1.
        /// Synthetic primitives from the annotation's styling keys are only the fallback: otherwise
        /// <b>every</b> AcroForm field and every signature — none of which is in the subtype switch
        /// below — would export as a dashed grey box while the raster renderer drew the real
        /// appearance.</para>
        /// </summary>
        private static void RenderAnnotationsToSvg(PdfPage page, PdfDocument doc, StringBuilder sb,
                                                   HashSetInt hiddenOcgs,
                                                   float pageW, float pageH, SvgSharedState shared,
                                                   PdfRenderOptions options,
                                                   System.Threading.CancellationToken cancellationToken)
        {
            var annotsObj = page.Dictionary[PdfName.Annots];
            var annotsArr = doc.ResolveObject(annotsObj) as PdfArray
                            ?? annotsObj as PdfArray;
            if (annotsArr == null || annotsArr.Count == 0) return;

            // §12.7.3.3 — document-scoped, so it is resolved once rather than per annotation, as in
            // PdfPageRenderer.RenderAnnotations.
            bool needAppearances = AnnotationFallbackModel.FormNeedsAppearances(doc);

            sb.Append("<g data-layer=\"annotations\">\n");
            foreach (var annotRef in annotsArr)
            {
                var annot = doc.ResolveObject(annotRef) as PdfDictionary;
                if (annot == null) continue;
                // PDF §12.5.3, plus §12.7.5.5's rule for a signature widget, and which rule applies
                // depends on the intent, not on the output being vector: under RenderIntent.Print
                // an SVG export is a print target. One implementation, AnnotationVisibility, shared
                // with the raster renderer — the block it replaces was written out in both loops,
                // and the signature rule was in neither.
                if (AnnotationVisibility.IsHidden(annot, doc, options)) continue;
                // …and annotations on a hidden optional-content layer, through the same evaluator
                // the raster path uses (PdfPageRenderer's annotation loop).
                if (ContentStreamInterpreter.IsAnnotationOcHidden(annot, hiddenOcgs, doc))
                    continue;

                string subtype = (PdfResolve.Name(doc, annot, PdfName.Subtype))?.Value ?? "";
                var rect = PdfResolve.Array(doc, annot, PdfName.Rect);
                if (rect == null || rect.Count < 4) continue;
                // A /Rect past float's range draws nothing, in both renderers (MatrixGuard).
                if (MatrixGuard.HasNonFiniteNumber(rect, doc))
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        "annotation ignored: a /Rect number past the range of a float");
                    continue;
                }

                // Resolved per element, through the same helper the raster path uses: §7.3.10 lets
                // an array element be indirect, and an unresolved one reads as 0 — a plausible
                // coordinate rather than a detectable failure. See PdfResolve.ArrayFloat.
                float rx0 = PdfResolve.ArrayFloat(doc, rect, 0), ry0 = PdfResolve.ArrayFloat(doc, rect, 1);
                float rx1 = PdfResolve.ArrayFloat(doc, rect, 2), ry1 = PdfResolve.ArrayFloat(doc, rect, 3);
                if (rx0 > rx1) (rx0, rx1) = (rx1, rx0);
                if (ry0 > ry1) (ry0, ry1) = (ry1, ry0);

                // §12.5.5 first: the appearance stream, if the annotation has a usable one. It is
                // rendered into a scratch buffer so that a refusal (no /AP, /N unresolvable, empty
                // stream) can still fall through to the synthetic drawing below without having
                // written a half-formed group into the document.
                var apSb = new StringBuilder();
                bool viaAp = TryRenderAnnotationAppearance(
                    annot, page, doc, apSb, hiddenOcgs, shared, pageW, pageH,
                    rx0, ry0, rx1, ry1, options, cancellationToken, needAppearances,
                    out bool apClaimed);

                if (apClaimed)
                {
                    // The appearance stream owns this annotation, whether or not it produced
                    // markup (a stream that fails to decode draws nothing here, as in the raster
                    // path, rather than being replaced by a synthetic shape the file did not ask
                    // for). The reason is on RenderDiagnostics either way.
                    //
                    // §12.5.5 (ISO 32000-2, 38926-38928): the appearance is composited "using the
                    // values of the BM, ca and CA entries in the annotation dictionary" — all three,
                    // in both renderers: missing them would be a shared deviation, which comparing
                    // the two can never surface. Which alpha a group composite takes (/ca, else /CA) and
                    // which mode it blends with are AnnotationAppearance's decisions, shared with
                    // the raster path, which opens a layer with both; `opacity` and
                    // `mix-blend-mode` on this group are the SVG equivalents — group-level, so
                    // overlapping strokes inside one appearance do not double-darken or
                    // double-blend where they cross.
                    float apOpacity = AnnotationAppearance.CompositeAlpha(annot, doc);
                    string apBlend = SvgContentWriter.PdfBlendModeToSvg(
                        AnnotationAppearance.BlendMode(annot, doc));

                    sb.Append($"<g data-annot-subtype=\"{XmlEsc(subtype)}\"");
                    if (apOpacity < 1f) sb.Append($" opacity=\"{F(apOpacity)}\"");
                    if (apBlend != null) sb.Append($" style=\"mix-blend-mode:{apBlend}\"");
                    sb.Append(">\n");
                    if (viaAp) sb.Append(apSb);
                    sb.Append("</g>\n");
                    continue;
                }

                // Synthetic appearance: /C — border colour, /IC — interior, /CA — opacity,
                // /BS /W (else the legacy /Border) — border width.
                string strokeCol = AnnotColorToSvg(annot, doc, PdfName.C,  "black");
                string fillCol   = AnnotColorToSvg(annot, doc, PdfName.IC, "none");
                // The dash was being discarded with `out _`. The shared model already reads
                // §12.5.4's /BS /D — that is what the out parameter is for — and the raster path
                // strokes with it, so throwing it away here was one renderer's dashed border
                // exported as a solid one.
                float synthBorderW = AnnotationFallbackModel.BorderWidth(annot, doc, out float[] synthDash);
                string synthGeom = SynthBorderAttributes(synthBorderW, synthDash);

                // Resolved, as the raster path does at both of its /CA reads
                // (PdfPageRenderer's PdfResolve.Number calls). Read through the plain indexer an
                // indirect /CA yields no PdfNumber, which is indistinguishable from an absent key,
                // so a half-transparent annotation exported fully opaque while the render honoured
                // it — the same fix landing on one renderer only.
                float opacity = 1f;
                if (PdfResolve.Number(doc, annot, PdfName.CA) is PdfNumber caNum)
                    opacity = Math.Max(0f, Math.Min(1f, (float)caNum.DoubleValue));

                // Table 166's /BM applies to a synthesised appearance as well — the raster path
                // opens a group with it around its fallback drawing; a group attribute is the SVG
                // equivalent. (/CA stays the one opacity here: Table 166 calls an annotation
                // painted without an appearance stream implementation-dependent, and one group
                // carries one opacity — see AnnotationAppearance.CompositeAlpha.)
                string synthBlend = SvgContentWriter.PdfBlendModeToSvg(
                    AnnotationAppearance.BlendMode(annot, doc));
                sb.Append($"<g data-annot-subtype=\"{XmlEsc(subtype)}\" opacity=\"{F(opacity)}\"");
                if (synthBlend != null) sb.Append($" style=\"mix-blend-mode:{synthBlend}\"");
                sb.Append(">\n");

                switch (subtype)
                {
                    // Both carry the border geometry, not `stroke` alone, which would apply SVG's own
                    // default width of 1 however the file had set /BS /W — a 6-point dashed border
                    // exported as a 1-point solid one. Three other subtypes below emit the width too,
                    // which makes this a
                    // divergence inside one method rather than a missing feature.
                    case PdfAnnotationSubtypes.Square:
                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                  $"fill=\"{fillCol}\" stroke=\"{strokeCol}\"{synthGeom}/>\n");
                        break;
                    case PdfAnnotationSubtypes.Circle:
                        sb.Append($"<ellipse cx=\"{F((rx0 + rx1) / 2)}\" cy=\"{F((ry0 + ry1) / 2)}\" " +
                                  $"rx=\"{F((rx1 - rx0) / 2)}\" ry=\"{F((ry1 - ry0) / 2)}\" " +
                                  $"fill=\"{fillCol}\" stroke=\"{strokeCol}\"{synthGeom}/>\n");
                        break;
                    case PdfAnnotationSubtypes.Line:
                    {
                        var lArr = PdfResolve.Array(doc, annot, PdfName.L);
                        if (lArr != null && lArr.Count >= 4)
                        {
                            sb.Append($"<line x1=\"{F(PdfResolve.ArrayFloat(doc, lArr, 0))}\" y1=\"{F(PdfResolve.ArrayFloat(doc, lArr, 1))}\" " +
                                      $"x2=\"{F(PdfResolve.ArrayFloat(doc, lArr, 2))}\" y2=\"{F(PdfResolve.ArrayFloat(doc, lArr, 3))}\" " +
                                      $"stroke=\"{strokeCol}\"/>\n");
                        }
                        break;
                    }
                    case PdfAnnotationSubtypes.Highlight:
                    case PdfAnnotationSubtypes.Underline:
                    case PdfAnnotationSubtypes.StrikeOut:
                    case PdfAnnotationSubtypes.Squiggly:
                    {
                        // Shared with the raster path: the sentinel guard (without which this
                        // exported a page-covering quad the renderer skips) and the extents taken
                        // over ALL FOUR corners rather than the two §12.5.6.10 nominates, which
                        // real producers have written in more than one order for years.
                        var quads = AnnotationQuadPoints.Read(doc, annot, out bool quadsDeclared);

                        // §12.5.6.10 makes /QuadPoints optional, and the raster path fills the
                        // /Rect when a Highlight declares none (PdfPageRenderer's Highlight case).
                        // This side discarded the flag — `out _` — so the same annotation drew a
                        // wash when rendered and VANISHED when exported. It survived because there
                        // were no SVG annotation tests at all: the raster half of the pair is
                        // pinned by AnnotationFallbackRenderingTests and the export half by
                        // nothing. Only Highlight falls back, exactly as in the raster path: a rule
                        // with no quads has no length to draw.
                        //
                        // `quadsDeclared` is deliberately not `quads.Count > 0`. An all-sentinel
                        // array IS declared, and such an annotation routinely carries
                        // /Rect [0 0 612 792], so treating it as absent would paint the
                        // page-covering wash the sentinel guard exists to prevent.
                        if (!quadsDeclared && subtype == PdfAnnotationSubtypes.Highlight)
                        {
                            sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                      $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                      $"fill=\"{strokeCol}\" stroke=\"none\" " +
                                      $"style=\"mix-blend-mode:multiply\"/>\n");
                        }

                        foreach (var q in quads)
                        {
                            if (subtype == PdfAnnotationSubtypes.Highlight)
                            {
                                // §12.5.6.10: a highlight MULTIPLIES at full opacity, which is what
                                // keeps the text under it readable — dark glyphs stay dark, white
                                // paper takes the colour. The raster path composites exactly that
                                // (BlendMode = Multiply, FillAlpha = /CA).
                                //
                                // This emitted a fixed fill-opacity="0.25" with normal blending
                                // instead — on top of the group's own /CA opacity, so the wash was
                                // a quarter strength AND washed the glyphs out rather than leaving
                                // them. mix-blend-mode is the same mechanism SvgContentWriter
                                // already uses for the PDF blend modes.
                                sb.Append($"<path d=\"M{F(q.X0)},{F(q.Y0)} L{F(q.X1)},{F(q.Y1)} " +
                                          $"L{F(q.X3)},{F(q.Y3)} L{F(q.X2)},{F(q.Y2)} Z\" " +
                                          $"fill=\"{strokeCol}\" stroke=\"none\" " +
                                          $"style=\"mix-blend-mode:multiply\"/>\n");
                            }
                            else
                            {
                                // Same factors the raster path passes as lineYFactor.
                                float yLine = q.RuleY(
                                    subtype == PdfAnnotationSubtypes.StrikeOut ? 0.5f : 0f);
                                string dash = subtype == PdfAnnotationSubtypes.Squiggly
                                    ? " stroke-dasharray=\"2 1\"" : "";
                                sb.Append($"<line x1=\"{F(q.Left)}\" y1=\"{F(yLine)}\" " +
                                          $"x2=\"{F(q.Right)}\" y2=\"{F(yLine)}\" " +
                                          $"stroke=\"{strokeCol}\"{dash}/>\n");
                            }
                        }
                        break;
                    }
                    case PdfAnnotationSubtypes.Ink:
                    {
                        // Which strokes are drawable is decided once, in AnnotationPointList, so
                        // the raster renderer cannot disagree about it.
                        foreach (PdfArray pts in AnnotationPointList.InkStrokes(doc, annot))
                        {
                            var pb = new StringBuilder();
                            pb.Append("M").Append(F(PdfResolve.ArrayFloat(doc, pts, 0))).Append(",")
                              .Append(F(PdfResolve.ArrayFloat(doc, pts, 1)));
                            for (int i = 2; i + 1 < pts.Count; i += 2)
                                pb.Append(" L").Append(F(PdfResolve.ArrayFloat(doc, pts, i))).Append(",")
                                  .Append(F(PdfResolve.ArrayFloat(doc, pts, i + 1)));
                            sb.Append($"<path d=\"{pb}\" fill=\"none\" stroke=\"{strokeCol}\"/>\n");
                        }
                        break;
                    }
                    case PdfAnnotationSubtypes.PolyLine:
                    case PdfAnnotationSubtypes.Polygon:
                    {
                        var verts = AnnotationPointList.Vertices(doc, annot);
                        if (verts != null)
                        {
                            var pb = new StringBuilder();
                            pb.Append("M").Append(F(PdfResolve.ArrayFloat(doc, verts, 0))).Append(",")
                              .Append(F(PdfResolve.ArrayFloat(doc, verts, 1)));
                            for (int i = 2; i + 1 < verts.Count; i += 2)
                                pb.Append(" L").Append(F(PdfResolve.ArrayFloat(doc, verts, i))).Append(",")
                                  .Append(F(PdfResolve.ArrayFloat(doc, verts, i + 1)));
                            if (subtype == PdfAnnotationSubtypes.Polygon) pb.Append(" Z");
                            string fill = subtype == PdfAnnotationSubtypes.Polygon ? fillCol : "none";
                            sb.Append($"<path d=\"{pb}\" fill=\"{fill}\" stroke=\"{strokeCol}\"/>\n");
                        }
                        break;
                    }
                    case PdfAnnotationSubtypes.Link:
                    {
                        // §12.5.4: /BS /W, else /Border [Hr Vr W [Dash]], else 1; a width of 0
                        // draws no border. The raster renderer's link reads the same.
                        float borderW = AnnotationFallbackModel.BorderWidth(annot, doc, out _);
                        if (borderW > 0f)
                        {
                            sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                      $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                      $"fill=\"none\" stroke=\"{strokeCol}\" stroke-width=\"{F(borderW)}\"/>\n");
                        }
                        // Emit link metadata as data-* so consumers can post-process.
                        var aObj = doc.ResolveObject(annot[PdfName.A]) as PdfDictionary;
                        // Resolved: /URI is commonly written as an indirect string, and reading it
                        // raw yielded null - indistinguishable from "the link has no URI" - so the
                        // metadata silently vanished from the export. PdfLinkExtractor already
                        // resolved it, so the two readers disagreed on the same file.
                        var uri = PdfResolve.Str(doc, aObj?[PdfName.URI]);
                        if (uri != null)
                            sb.Append($"<metadata data-uri=\"{XmlEsc(uri.GetText())}\"/>\n");
                        break;
                    }

                    // ----------------------------------------------------------------
                    //  The seven subtypes below had a fallback in the raster path and none
                    //  here, so all seven fell into `default` and exported as a dashed grey
                    //  box with an "unsupported subtype" report that was untrue of every one
                    //  of them. Each mirrors PdfPageRenderer.RenderAnnotationFallback's case
                    //  for the same subtype; the decisions behind them (the /DA parse, the
                    //  /Contents-then-/RC choice, the stamp palette, every placement number)
                    //  come from AnnotationFallbackModel so that the two cannot drift.
                    // ----------------------------------------------------------------

                    case PdfAnnotationSubtypes.Text:
                    {
                        // A sticky note draws at a standard icon size — /Rect on a Text
                        // annotation is routinely tiny — and is yellow unless /C says otherwise.
                        string noteCol = AnnotationFallbackModel.DeclaresColor(annot)
                            ? strokeCol
                            : PdfColor.ToCssRgb(AnnotationFallbackModel.NoteDefaultColor);
                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(AnnotationFallbackModel.NoteIconWidth)}\" " +
                                  $"height=\"{F(AnnotationFallbackModel.NoteIconHeight)}\" " +
                                  $"fill=\"{noteCol}\" stroke=\"{noteCol}\"/>\n");
                        break;
                    }

                    case PdfAnnotationSubtypes.PrinterMark:
                        // PDF/X printer marks (crop marks, registration, colour bars) are prepress
                        // artifacts with no screen-rendering semantics of their own. The raster path
                        // draws nothing for them and does NOT report; drawing a dashed box over a
                        // page's crop marks is worse than drawing nothing, so this arm is empty on
                        // purpose and must stay above `default`.
                        break;

                    case PdfAnnotationSubtypes.Caret:
                        // §12.5.6.11: a small inverted-V at the bottom of the /RD-padded /Rect.
                        if (rx1 > rx0 && ry1 > ry0)
                        {
                            float caretX = (rx0 + rx1) * 0.5f;
                            float caretApex = ry0 + (ry1 - ry0) * AnnotationFallbackModel.CaretApexFactor;
                            sb.Append($"<path d=\"M{F(rx0)},{F(ry0)} L{F(caretX)},{F(caretApex)} " +
                                      $"L{F(rx1)},{F(ry0)} Z\" " +
                                      $"fill=\"{fillCol}\" stroke=\"{strokeCol}\"/>\n");
                        }
                        break;

                    case PdfAnnotationSubtypes.FreeText:
                    {
                        if (rx1 <= rx0 || ry1 <= ry0) break;

                        // Interior fill + border, then the text. A width of 0 means no border,
                        // as in the raster path.
                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                  $"fill=\"{fillCol}\" " +
                                  (synthBorderW > 0f
                                      ? $"stroke=\"{strokeCol}\" stroke-width=\"{F(synthBorderW)}\"/>\n"
                                      : "stroke=\"none\"/>\n"));

                        string freeText = AnnotationFallbackModel.FreeTextContents(annot, doc);
                        if (string.IsNullOrEmpty(freeText)) break;

                        var (ftSize, ftColor) = AnnotationFallbackModel.ParseDefaultAppearance(
                            PdfResolve.Str(doc, annot, PdfName.DA)?.GetText());
                        AnnotationFallbackModel.FreeTextBaseline(
                            rx0, ry1, ftSize, out float ftX, out float ftY);
                        AppendFallbackText(sb, freeText, ftSize, ftX, ftY,
                                           PdfColor.ToCssRgb(ftColor), bold: false);
                        break;
                    }

                    case PdfAnnotationSubtypes.Stamp:
                    {
                        if (rx1 <= rx0 || ry1 <= ry0) break;

                        string stampName = AnnotationFallbackModel.StampName(annot, doc);
                        string stampCol  = AnnotationFallbackModel.UsesDefaultColor(strokeCol)
                            ? PdfColor.ToCssRgb(AnnotationFallbackModel.StampPalette(stampName))
                            : strokeCol;

                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                  $"fill=\"none\" stroke=\"{stampCol}\" stroke-width=\"" +
                                  $"{F(Math.Max(synthBorderW, AnnotationFallbackModel.StampMinLineWidth))}\"/>\n");

                        if (AnnotationFallbackModel.StampCaption(stampName, rx0, ry0, rx1, ry1,
                                out float stampSize, out float stampX, out float stampY))
                            AppendFallbackText(sb, stampName, stampSize, stampX, stampY,
                                               stampCol, bold: true);
                        break;
                    }

                    case PdfAnnotationSubtypes.Widget:
                    {
                        if (rx1 <= rx0 || ry1 <= ry0) break;

                        // The frame, if the widget declares one: Table 192's /MK /BC at the /BS
                        // width, decided in AnnotationFallbackModel with the raster path. This
                        // stroked /C — a markup/link colour, defaulting to black — at a width
                        // floored to 1 pt, so every no-/AP widget was boxed in black.
                        if (AnnotationFallbackModel.WidgetBorderColor(annot, doc, synthBorderW) is SKColor frame)
                        {
                            sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                      $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                      $"fill=\"none\" stroke=\"{PdfColor.ToCssRgb(frame)}\"{synthGeom}/>\n");
                        }

                        // /FT is inheritable through the /Parent chain (Table 220). A widget whose
                        // type will not resolve draws its border and nothing else — turning that
                        // into "assume text" typesets a word where a tick belongs.
                        string ft = (AnnotationFallbackModel.ResolveWidgetEntry(
                            annot, doc, PdfName.FT, o => o is PdfName) as PdfName)?.Value;
                        if (ft == null)
                        {
                            RenderDiagnostics.Report(
                                RenderDiagnosticKind.FormFieldTypeUnresolved,
                                "SVG: no /FT was reachable from this widget within "
                                + PdfLimits.MaxFieldInheritanceDepth + " /Parent hops; "
                                + "the field's value was not drawn");
                            break;
                        }

                        // As the widget shows it — a password as asterisks (Table 229 bit 14).
                        string value = AnnotationFallbackModel.DisplayedWidgetValue(annot, doc);
                        if (string.IsNullOrEmpty(value)) break;

                        var (daSize, daColor) = AnnotationFallbackModel.ParseDefaultAppearance(
                            AnnotationFallbackModel.InheritedDefaultAppearance(annot, doc));

                        // Table 192 /MK /R, exactly as the raster fallback: placed in the rectangle
                        // turned back upright, then turned onto it by the coefficients a generated
                        // appearance writes as its /Matrix (Base.Operations.Forms.WidgetRotation).
                        float rw = rx1 - rx0, rh = ry1 - ry0;
                        int rotation = Base.Operations.Forms.WidgetRotation.Read(annot, doc.ResolveObject);
                        var (boxW, boxH) = Base.Operations.Forms.WidgetRotation.LayoutBox(rotation, rw, rh);
                        double[] turn = rotation == 0
                            ? null
                            : Base.Operations.Forms.WidgetRotation.Coefficients(rotation, rw, rh);
                        float valueSize = AnnotationFallbackModel.WidgetFontSize(daSize, 0f, (float)boxH);
                        string valueCol = PdfColor.ToCssRgb(daColor);

                        if (ft == "Btn")
                        {
                            // Checkbox / radio: a tick when /V is any non-Off value.
                            if (value == "Off") break;
                            AnnotationFallbackModel.CheckMarkPlacement(0f, 0f, (float)boxW, (float)boxH,
                                out float markSize, out float markX, out float markY);
                            var (mx, my) = Base.Operations.Forms.WidgetRotation.ToRectangle(rotation, rw, rh, markX, markY);
                            AppendFallbackText(sb, AnnotationFallbackModel.CheckMark,
                                               markSize, rx0 + (float)mx, ry0 + (float)my, valueCol,
                                               bold: false, turn);
                            break;
                        }

                        // /Q is inheritable (§12.7.3.3), with a form-wide default on /AcroForm,
                        // and this draws left-aligned regardless, because the fallback measures no
                        // text width. Reported rather than
                        // dropped, exactly as the raster path reports it; /Q 0 IS left-aligned, so
                        // only 1 and 2 are worth a report.
                        int q = AnnotationFallbackModel.InheritedQuadding(annot, doc) ?? 0;
                        if (q != 0)
                        {
                            RenderDiagnostics.Report(
                                RenderDiagnosticKind.FormFieldQuaddingIgnored,
                                "SVG: /Q " + q + " asked for "
                                + (q == 1 ? "centred" : "right-aligned")
                                + " text; the no-/AP fallback drew it left-aligned");
                        }

                        AnnotationFallbackModel.WidgetValueBaseline(
                            0f, 0f, (float)boxH, valueSize, out float valueX, out float valueY);
                        var (vx, vy) = Base.Operations.Forms.WidgetRotation.ToRectangle(rotation, rw, rh, valueX, valueY);
                        AppendFallbackText(sb, value, valueSize, rx0 + (float)vx, ry0 + (float)vy, valueCol,
                                           bold: false, turn);
                        break;
                    }

                    case PdfAnnotationSubtypes.ThreeD:
                    {
                        // The 3D content (U3D / PRC) needs a full 3D engine, so both renderers draw
                        // a dashed box with a diagonal cross and a "3D" caption — visibly a
                        // placeholder — rather than dropping the annotation silently.
                        if (rx1 <= rx0 || ry1 <= ry0) break;

                        string outline = AnnotationFallbackModel.UsesDefaultColor(strokeCol)
                            ? PdfColor.ToCssRgb(AnnotationFallbackModel.ThreeDDefaultColor)
                            : strokeCol;
                        float[] dash = AnnotationFallbackModel.ThreeDDashPattern();
                        string dashAttr = $" stroke-dasharray=\"{F(dash[0])} {F(dash[1])}\"";
                        float x = rx0, y = ry0, w = rx1 - rx0, h = ry1 - ry0;

                        // The raster path fills at a quarter of the annotation's own alpha; the
                        // group above already carries /CA, so this is that quarter.
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" " +
                                  $"width=\"{F(w)}\" height=\"{F(h)}\" " +
                                  $"fill=\"{outline}\" fill-opacity=\"0.25\" " +
                                  $"stroke=\"{outline}\"{dashAttr}/>\n");
                        sb.Append($"<path d=\"M{F(x)},{F(y)} L{F(x + w)},{F(y + h)} " +
                                  $"M{F(x)},{F(y + h)} L{F(x + w)},{F(y)}\" " +
                                  $"fill=\"none\" stroke=\"{outline}\"{dashAttr}/>\n");

                        if (AnnotationFallbackModel.ThreeDCaptionPlacement(x, y, w, h,
                                out float capSize, out float capX, out float capY))
                            AppendFallbackText(sb, AnnotationFallbackModel.ThreeDCaption,
                                               capSize, capX, capY, outline, bold: true);
                        break;
                    }

                    case PdfAnnotationSubtypes.Popup:
                    case PdfAnnotationSubtypes.Sound:
                    case PdfAnnotationSubtypes.Movie:
                    case PdfAnnotationSubtypes.FileAttachment:
                    case PdfAnnotationSubtypes.Screen:
                        // Interactive annotations whose visible appearance is entirely defined by
                        // /AP; a placeholder rect is the standard substitute and is NOT a gap, so
                        // it is not reported. Split out to mirror the raster switch exactly, so
                        // "reports" and "does not report" name the same set of subtypes on both
                        // sides and cannot drift apart again.
                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                  $"fill=\"none\" stroke=\"{strokeCol}\" stroke-dasharray=\"3 2\"/>\n");
                        break;

                    default:
                        RenderDiagnostics.Report(RenderDiagnosticKind.AnnotationSubtypeUnsupported,
                            $"SVG: annotation subtype '{subtype}' has no per-subtype fallback; drew placeholder rect.");
                        sb.Append($"<rect x=\"{F(rx0)}\" y=\"{F(ry0)}\" " +
                                  $"width=\"{F(rx1 - rx0)}\" height=\"{F(ry1 - ry0)}\" " +
                                  $"fill=\"none\" stroke=\"{strokeCol}\" stroke-dasharray=\"3 2\"/>\n");
                        break;
                }
                sb.Append("</g>\n");
            }
            sb.Append("</g>\n");
        }

        /// <summary>
        /// Renders an annotation's <c>/AP</c> <c>/N</c> appearance stream as a form XObject, mapped
        /// onto <c>/Rect</c> by ISO 32000-1 §12.5.5 Algorithm 8.1. Port of
        /// <c>PdfPageRenderer.RenderAnnotations</c>' appearance branch, so the two renderers agree
        /// on sub-state selection, the fit matrix and the <c>/BBox</c> clip.
        /// </summary>
        /// <param name="annot">The annotation dictionary being drawn.</param>
        /// <param name="page">The page it belongs to.</param>
        /// <param name="doc">The open document, for resolving indirect references.</param>
        /// <param name="dst">Receives the emitted SVG markup.</param>
        /// <param name="hiddenOcgs">
        /// Optional-content groups resolved hidden for this export, so an annotation whose
        /// <c>/OC</c> names one is skipped (§8.11.4).
        /// </param>
        /// <param name="shared">
        /// State shared with the page's other writers — notably the element-id counter, which must
        /// be one sequence across the whole document or two writers mint colliding ids.
        /// </param>
        /// <param name="pageW">Page width in PDF points, for the §12.5.5 fit matrix.</param>
        /// <param name="pageH">Page height in PDF points, for the §12.5.5 fit matrix.</param>
        /// <param name="rx0">Annotation <c>/Rect</c> left edge, normalised.</param>
        /// <param name="ry0">Annotation <c>/Rect</c> bottom edge, normalised.</param>
        /// <param name="rx1">Annotation <c>/Rect</c> right edge, normalised.</param>
        /// <param name="ry1">Annotation <c>/Rect</c> top edge, normalised.</param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <param name="cancellationToken">Observed before the appearance stream is executed.</param>
        /// <param name="needAppearances">
        /// The document-scoped §12.7.3.3 <c>/NeedAppearances</c> answer. When a widget's selected
        /// appearance is stale under it, this method declines the annotation entirely — neither
        /// drawing it nor claiming it — so the synthetic fallback constructs the appearance instead.
        /// </param>
        /// <param name="apClaimed">
        /// True when a usable appearance stream was found — i.e. the annotation is the appearance
        /// stream's to draw and the synthetic fallback must not run. It can be true while the
        /// return value is false: a stream that will not decode draws nothing, which is what the
        /// raster path does, rather than being silently replaced by a shape the file did not ask for.
        /// </param>
        /// <returns>True when markup was written to <paramref name="dst"/>.</returns>
        private static bool TryRenderAnnotationAppearance(
            PdfDictionary annot, PdfPage page, PdfDocument doc, StringBuilder dst,
            HashSetInt hiddenOcgs, SvgSharedState shared,
            float pageW, float pageH,
            float rx0, float ry0, float rx1, float ry1,
            PdfRenderOptions options,
            System.Threading.CancellationToken cancellationToken,
            bool needAppearances,
            out bool apClaimed)
        {
            apClaimed = false;

            var apObj = annot[PdfName.AP];
            if (apObj == null) return false;
            var ap = doc.ResolveObject(apObj) as PdfDictionary ?? apObj as PdfDictionary;
            if (ap == null) return false;

            var nObj = ap[PdfName.N];
            if (nObj == null) return false;
            var nResolved = doc.ResolveObject(nObj);

            // /N is either a stream (one appearance) or a dictionary of named sub-states.
            PdfStream apStream = nResolved as PdfStream;
            if (apStream == null && nResolved is PdfDictionary nd)
            {
                // /AS names the state that is current (§12.5.5) — the key that makes a checkbox
                // render checked rather than unchecked.
                // A state /N does not carry draws nothing (AnnotationAppearance.TryNamedState).
                if (AnnotationAppearance.TryNamedState(doc, annot, nd, out var named))
                {
                    if (named == null) { apClaimed = true; return false; }
                    apStream = doc.ResolveObject(named) as PdfStream;
                }
                // With no /AS, the first entry, as the raster path does.
                else
                    foreach (var kv in nd)
                    {
                        apStream = doc.ResolveObject(kv.Value) as PdfStream;
                        if (apStream != null) break;
                    }
            }
            if (apStream == null) return false;

            // §12.7.3.3: the form may be asking for this appearance to be CONSTRUCTED rather than
            // drawn. Declining without claiming is what routes the widget to the synthetic path
            // below, which is the raster renderer's `continue` into RenderAnnotationFallback. The
            // predicate is the shared one and takes the stream selected above, so no caller re-does
            // the /AS choice to answer it; it returns the reason so each reports to its own sink.
            string staleReason =
                AnnotationFallbackModel.WidgetAppearanceIsStale(annot, doc, apStream, needAppearances);
            if (staleReason != null)
            {
                RenderDiagnostics.Report(
                    RenderDiagnosticKind.FormFieldAppearanceRegenerated, staleReason);
                return false;
            }

            // Algorithm 8.1: the /BBox corners through the stream's /Matrix, their bounding box, and
            // A mapping that box onto /Rect — one reading, AnnotationAppearance.TryFitToRect, shared
            // with the raster pass. A degenerate /Rect with an appearance draws nothing, as the
            // raster loop's `continue` draws nothing: claimed, so the synthetic fallback does not
            // draw what the raster leaves blank.
            if (!AnnotationAppearance.TryFitToRect(doc, rx0, ry0, rx1, ry1, apStream, out float[] ctm, out _, out PdfArray bboxArr))
            { apClaimed = true; return false; }

            apClaimed = true;

            byte[] apData;
            try { apData = apStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "SVG: annotation /AP /N stream could not be decoded; the annotation draws nothing.", ex);
                return false;
            }
            if (apData == null || apData.Length == 0) return false;

            // An appearance is an invocation like a form's, charged to the page's budget before it
            // is parsed, as the raster pass charges it: nothing else counts a page's /Annots entries.
            if (shared?.FormBudget != null && !shared.FormBudget.TryChargeFormInvocation(apData.Length)) return false;

            var apMatrixArr = PdfResolve.Array(doc, apStream, PdfName.Matrix);

            // No rotation step: §12.5.5's algorithm (ISO 32000-2, 38886-38903) is the /BBox through
            // /Matrix, the fit A onto /Rect, then AA = Matrix × A, and nothing else. /MK /R (or a
            // non-standard /Rotate on the annotation) is input to appearance CONSTRUCTION
            // (41198-41200), and the producer's /Matrix already carries the turn; turning the fitted
            // appearance again would draw it sideways and spill it outside /Rect.

            // §12.7.3.3, in AnnotationAppearance — one implementation, shared with the raster path.
            PdfDictionary apResources =
                AnnotationAppearance.ResolveResources(apStream as PdfDictionary, doc, page);

            // The writer is a holder only: RunContentStreamAsForm concatenates the stream's own
            // /Matrix onto this CTM, emits the /BBox clip and runs the body in a child writer — the
            // same machinery a `Do` on a form XObject uses, so the appearance cannot diverge from
            // ordinary form rendering. /Group is deliberately not honoured: the raster path executes
            // an AP stream directly, without transparency-group compositing.
            var writer = new SvgContentWriter(dst, doc, apResources, pageW, pageH);
            // Cancellation rides in with the rest of the ambient context. The raster path wires its
            // annotation interpreter for it and records why: a page carrying one enormous /AP stream
            // otherwise honoured the token for its content and ignored it for its annotations. That
            // is the defect SetAmbient exists to stop recurring — it is one call, so the four fields
            // cannot be set three-quarters of the way again.
            writer.SetAmbient(hiddenOcgs, shared, options, cancellationToken);
            writer.RunAppearanceStream(apData, apResources, apMatrixArr, bboxArr, ctm);

            // Same completion event the raster path emits, so a host correlating rendered
            // appearances with /NM sees one stream of events regardless of output format.
            RenderDiagnostics.Report(RenderDiagnosticKind.AnnotationRendered,
                $"NM={(PdfResolve.Str(doc, annot, PdfName.NM))?.GetText() ?? "(none)"} " +
                $"Subtype={(PdfResolve.Name(doc, annot, PdfName.Subtype))?.Value ?? "?"}");
            return true;
        }

        /// <summary>
        /// One line of text in a synthetic annotation appearance, at the baseline and size
        /// <see cref="AnnotationFallbackModel"/> chose.
        /// </summary>
        /// <remarks>
        /// The matrix is the raster path's text matrix — <c>[size 0 0 size x y]</c> — with the
        /// <c>d</c> component negated to undo the page-level Y-flip, and <c>font-size="1"</c>
        /// because the size is baked into it. Same convention as
        /// <c>SvgContentWriter</c>'s <c>&lt;text&gt;</c> path, so a fallback caption and a caption
        /// drawn by a content stream are placed by the same rule.
        /// </remarks>
        // A synthesised text run at `size`, baseline at (x, y) in the page's own space. `turn` is
        // the rotation a widget's /MK /R asks for, as Base.Operations.Forms.WidgetRotation's coefficients, or
        // null for none. Glyphs run down the page in SVG, so the text is flipped and then turned:
        // [a b c d] after [1 0 0 -1] is [a b -c -d].
        private static void AppendFallbackText(
            StringBuilder sb, string text, float size, float x, float y, string fill, bool bold,
            double[] turn = null)
        {
            if (string.IsNullOrEmpty(text) || !(size > 0f)) return;
            string a = F(size), b = "0", c = "0", d = F(-size);
            if (turn != null)
            {
                a = F((float)(turn[0] * size)); b = F((float)(turn[1] * size));
                c = F((float)(-turn[2] * size)); d = F((float)(-turn[3] * size));
            }
            sb.Append($"<text transform=\"matrix({a},{b},{c},{d},{F(x)},{F(y)})\" " +
                      $"font-size=\"1\" fill=\"{fill}\"" +
                      (bold ? " font-weight=\"bold\"" : "") +
                      $">{XmlEsc(text)}</text>\n");
        }

        private static string AnnotColorToSvg(PdfDictionary annot, PdfDocument doc, PdfName key, string fallback)
        {
            var arr = doc.ResolveObject(annot[key]) as PdfArray
                      ?? annot[key] as PdfArray;
            if (arr == null || arr.Count == 0) return fallback;
            // 1, 3 and 4 operands are DeviceGray / DeviceRGB / DeviceCMYK (§12.5.2, Table 164);
            // a count of 2 is malformed and must fall back rather than be padded to RGB.
            //
            // Routed through the shared converter, which also fixes the defect this branch used to
            // carry: it converted the RAW operands and interpolated the result as `int`, so an
            // out-of-range /C [-0.2 0 0 0] emitted "rgb(306,255,255)" — invalid CSS. The raster
            // twin clamped, so the two rendered the same colour and a pixel-comparison parity test
            // could never see it. GAPS.md §2b.
            int n = arr.Count >= PdfColorSpaceNames.CmykComponents
                        ? PdfColorSpaceNames.CmykComponents
                        : arr.Count;
            if (n != PdfColorSpaceNames.GrayComponents
             && n != PdfColorSpaceNames.RgbComponents
             && n != PdfColorSpaceNames.CmykComponents) return fallback;

            var comps = new float[n];
            for (int i = 0; i < n; i++) comps[i] = PdfResolve.ArrayFloat(doc, arr, i);

            return PdfColor.ToCssRgb(
                PdfColor.ComponentsToColor(comps, PdfColor.DeviceSpaceForComponentCount(n)));
        }

        // ----------------------------------------------------------------
        //  Helpers — shared by the outer renderer and the nested SvgWriter
        // ----------------------------------------------------------------

        // One implementation, in SvgSyntax. These forwarders exist because the call sites are
        // in the hundreds and this repo bans `using static`; an alias is not a second
        // implementation.
        // Same rule as the matching block in SvgContentWriter: a forwarder earns its place by
        // having callers.
        private static string XmlEsc(string s) => SvgSyntax.XmlEsc(s);
        private static string F(float v) => SvgSyntax.F(v);
        private static float StrokeWidthOrHairline(float lineWidth, float ctmScale)
            => SvgSyntax.StrokeWidthOrHairline(lineWidth, ctmScale);

        /// <summary>
        /// The <c>stroke-width</c> and <c>stroke-dasharray</c> a <b>synthesised</b> annotation
        /// border carries, from §12.5.4's <c>/BS /W</c> and <c>/BS /D</c>.
        /// </summary>
        /// <remarks>
        /// <para>One place, because the subtypes that emitted a width each carried their own copy of
        /// the expression while the two that needed it most — Square and Circle — carried none, so a
        /// 6-point dashed border exported at SVG's own default of 1 point, solid. The dash was
        /// available all along: <c>AnnotationFallbackModel.BorderWidth</c> returns it through an out
        /// parameter and this file discarded it with <c>out _</c>.</para>
        /// <para>The width is <b>omitted at zero</b> rather than written as zero. §12.5.4 makes
        /// <c>/W 0</c> mean <i>no border</i>, so the §8.4.3.2 hairline substitution the
        /// content-stream path applies is deliberately not used here — that rule is about a zero
        /// line width meaning "thinnest renderable", which is the opposite of this one.</para>
        /// </remarks>
        private static string SynthBorderAttributes(float width, float[] dash)
        {
            if (!(width > 0f)) return string.Empty;

            string attrs = $" stroke-width=\"{F(width)}\"";
            if (dash == null || dash.Length == 0) return attrs;

            float sum = 0f;
            var parts = new StringBuilder();
            for (int i = 0; i < dash.Length; i++)
            {
                float v = Math.Max(0f, dash[i]);
                sum += v;
                if (i > 0) parts.Append(',');
                parts.Append(F(v));
            }
            // A pattern summing to zero is invalid in PDF and renders solid in SVG, so it is
            // omitted rather than written as zeros — the same rule the content-stream dash uses.
            return sum > 0f ? attrs + $" stroke-dasharray=\"{parts}\"" : attrs;
        }



        // ====================================================================
        //  SvgWriter — interprets content stream ops into SVG elements
        // ====================================================================


        // ====================================================================
        //  Shared per-page <defs>/<use> registry (deduplicates images & glyphs)
        // ====================================================================


        // ====================================================================
        //  Minimal graphics-state bag for SVG rendering
        // ====================================================================

    }
}


