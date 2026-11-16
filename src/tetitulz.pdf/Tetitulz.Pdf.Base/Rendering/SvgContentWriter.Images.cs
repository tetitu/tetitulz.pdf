// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Imaging.Jpx;
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
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream into SVG elements — the vector counterpart of
    /// <see cref="ContentStreamInterpreter"/>, which does the same job onto a Skia canvas.
    ///
    /// <para><b>A type of its own, split as the raster interpreter is</b> — .Path, .Text,
    /// .Shading, .Images and the rest — so a change to one renderer and not the other is a
    /// two-file diff. A silently wrong output is, as a rule, one renderer lagging the other.</para>
    ///
    /// <para>It stays <c>internal</c>. Base's three <c>InternalsVisibleTo</c> friends are all
    /// in-repo — the test project, FontDiag and the benchmarks — so no CONSUMER could see it
    /// before and none can now; promoting it changed its nesting, not its visibility.</para>
    /// </summary>
internal sealed partial class SvgContentWriter
    {
        private void RenderImageXObject(PdfStream stream, long objNum = -1)
        {
            // §8.6.5.6: a device /ColorSpace is the current resources' default for it — and an image
            // so remapped is not shared as a definition, since under other resources it is another
            // colour.
            PdfStream memoKey = stream;   // the document's stream, whatever this draw remaps it to
            if (Shared != null && Shared.FailedDecodes.Contains(memoKey)) return;   // failed earlier on this page, reported then
            var asDrawn = DefaultColorSpaces.WithDefaults(stream, _resources, _doc);
            if (!ReferenceEquals(asDrawn, stream)) { stream = asDrawn; objNum = -1; }

            // An overprinting image changes a composite only when its colour space is a colorant set
            // naming some colorants and not others — §8.6.7 keeps non-zero overprint mode off images,
            // so every other image writes all four process colorants. That case is reported here, at
            // the operator and before any decode, by the one rule both renderers ask
            // (OverprintCompositor.ImageLeavesColorantsAlone). Gated on what the FILE asked, not on
            // the caller's opt-in.
            if (_gs.OverprintFill && OverprintCompositor.ImageLeavesColorantsAlone(stream, _doc))
                ReportOverprintSkipped(OverprintReportReason.Image);

            // Emit a <image> element in the unit square, transformed by CTM.
            int w = GetStreamInt(stream, PdfName.Width,  PdfName.W, 1);
            int h = GetStreamInt(stream, PdfName.Height, PdfName.H, 1);

            // 1-bit ImageMask stencil: render as a bitmap coloured with the current fill.
            bool isImageMask = ImageStencil.IsStencilMask(stream, _doc);

            // The CTM maps unit-square → page region (shared by the direct and <use> paths).
            float[] m = _gs.CTM;
            string matrix = $"matrix({F(m[0])},{F(m[1])},{F(m[2])},{F(m[3])},{F(m[4])},{F(m[5])})";
            string imgMask = SoftMaskAttribute();
            string rendering = ImageRenderingAttribute(stream);

            // Dedup is safe only when the encoded pixels depend solely on the XObject itself.
            // ImageMask colour comes from the current fill, and a /TR transfer function rewrites
            // the samples — both vary per draw — so those keep their own inline <image>.
            bool dedupable = Shared != null && objNum >= 0 && !isImageMask
                             && _gs.TransferFunction == null
                             && !Options.SvgDownsamplesImages;   // each draw decodes for its own size

            if (dedupable && Shared.ImageDefs.TryGetValue(objNum, out string existingId))
            {
                // Already defined — reference it (no decode / re-encode / re-base64).
                _sb.Append($"<use transform=\"{matrix}\" xlink:href=\"#{existingId}\"{imgMask}{ImagePaintAttributes()}/>\n");
                return;
            }

            byte[] raw;
            // Shared with the raster path so a CCITT image that omits /Columns takes it from
            // /Width here too; a plain GetDecodedData() decoded at the 1728 fax default.
            // The page's image-decoding budget, as the raster path keeps it: once spent, no further
            // image is decoded and the refusal was reported once.
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryDecodeImage()) return;
            try { raw = ContentStreamInterpreter.DecodeImageStream(stream, w, Shared?.FormBudget?.ImageWork); }
            catch (Exception ex)
            {
                if (Shared?.FormBudget != null && Shared.FormBudget.IsImageWorkRefusal(ex)) return;
                Shared?.FailedDecodes.Add(memoKey);
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG image XObject decode failed", ex);
                return;
            }

            // A stencil takes its colour from the current fill, and a PATTERN fill has no single
            // colour to bake into the PNG: its value is `url(#patN)`, which the colour parser
            // cannot read, so the stencil exported as solid BLACK — a confidently wrong result
            // of exactly the kind PatternFillUnresolved was introduced to stop. Emit the stencil
            // as a luminance <mask> and paint the pattern through it instead, which is what the
            // raster path does by stencilling with the pattern's shader.
            //
            // Gated on the Pattern-space flag as well as the name (SelectedPatternName): a name
            // survives the `g`/`rg`/`k` that leaves the Pattern space, so reading it alone
            // stencilled a stale pattern where the current fill colour is what the file asked for.
            string stencilPattern = SelectedPatternName(stroke: false);
            if (isImageMask && stencilPattern != null)
            {
                if (!EmitStencilThroughPattern(raw, w, h, stream, stencilPattern, m, rendering, imgMask))
                    Shared?.FailedDecodes.Add(memoKey);
                return;
            }

            string dataUri;
            if (isImageMask)
                dataUri = BuildImageMaskDataUri(raw, w, h, stream);
            else
                dataUri = BuildImageDataUri(raw, w, h, stream);
            // A decode that produced nothing was reported in the decode; the next draw of this
            // stream on the page skips it (the raster path's FailedDecodes).
            if (dataUri == null) { Shared?.FailedDecodes.Add(memoKey); return; }

            EmitImageElement(dataUri, matrix, rendering, imgMask, dedupable, objNum);
        }

        /// <summary>
        /// A stencil under a pattern fill (§8.9.6.2: the painted samples take the fill), as a
        /// luminance mask the pattern is painted through — the image XObject's and the inline
        /// image's alike, as the raster paints both through the stencil. A pattern fill has no
        /// single colour to bake into the PNG: its value is <c>url(#patN)</c>, which the colour
        /// parser cannot read, so a stencil baked with it exported as solid black. Returns false
        /// when the stencil could not be built.
        /// </summary>
        /// <param name="raw">The decoded samples.</param>
        /// <param name="w">The stencil's width in samples.</param>
        /// <param name="h">The stencil's height in samples.</param>
        /// <param name="stream">The stencil's dictionary.</param>
        /// <param name="patternName">The fill pattern's resource name.</param>
        /// <param name="m">The matrix placing the unit square: the CTM.</param>
        /// <param name="rendering">The <c>image-rendering</c> attribute.</param>
        /// <param name="imgMask">The graphics-state soft mask's attribute, or empty.</param>
        private bool EmitStencilThroughPattern(byte[] raw, int w, int h, PdfStream stream, string patternName,
                                               float[] m, string rendering, string imgMask)
        {
            // The rect carries the CTM, so the pattern — a userSpaceOnUse paint server, resolved in
            // the painted element's own space — takes the CTM's inverse to land in page space.
            string patternPaint = ResolveAnyPatternFill(patternName, PdfMatrix.Invert(m));
            if (patternPaint == null)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternFillUnresolved,
                    $"SVG: /ImageMask filled with pattern /{patternName}, which could " +
                    "not be resolved to a paint; nothing was drawn for it.", null);
                return true;
            }

            // White, because an SVG mask is a LUMINANCE mask: the painted bits must be at
            // full luminance to pass the pattern through, and the clear bits are already
            // transparent (luminance 0).
            string stencil = BuildImageMaskDataUri(raw, w, h, stream, SkiaSharp.SKColors.White);
            if (stencil == null) return false;

            // Both units are objectBoundingBox so the mask content lands on the unit square
            // the rect occupies before `matrix` maps it to the page — the same unit square
            // every other image here is emitted into. userSpaceOnUse would resolve against
            // the transformed space and silently misplace the stencil.
            string matrix = $"matrix({F(m[0])},{F(m[1])},{F(m[2])},{F(m[3])},{F(m[4])},{F(m[5])})";
            string maskId = "stencil" + NextId();
            _sb.Append($"<defs><mask id=\"{maskId}\" maskUnits=\"objectBoundingBox\"" +
                       " maskContentUnits=\"objectBoundingBox\"" +
                       " x=\"0\" y=\"0\" width=\"1\" height=\"1\">");
            _sb.Append("<image x=\"0\" y=\"0\" width=\"1\" height=\"1\"" +
                       $" preserveAspectRatio=\"none\"{rendering} xlink:href=\"{stencil}\"/>");
            _sb.Append("</mask></defs>\n");

            // An active soft mask needs its own element: SVG allows one `mask` attribute per
            // element, and the stencil has claimed the rect's.
            if (imgMask.Length > 0) _sb.Append($"<g{imgMask}>");
            _sb.Append($"<rect transform=\"{matrix}\" x=\"0\" y=\"0\" width=\"1\" height=\"1\"" +
                       $" fill=\"{patternPaint}\" mask=\"url(#{maskId})\"{ImagePaintAttributes()}/>\n");
            if (imgMask.Length > 0) _sb.Append("</g>\n");
            return true;
        }

        /// <summary>The image's element, defined once and used when it may be shared, else inline.</summary>
        private void EmitImageElement(string dataUri, string matrix, string rendering, string imgMask, bool dedupable, long objNum)
        {
            if (dedupable)
            {
                // Define the image once (in the unit square) and reference it via <use>.
                string id = "img" + (++Shared.IdCounter);
                Shared.ImageDefs[objNum] = id;
                _sb.Append($"<defs><image id=\"{id}\" x=\"0\" y=\"0\" width=\"1\" height=\"1\"" +
                           $" preserveAspectRatio=\"none\"{rendering} xlink:href=\"{dataUri}\"/></defs>\n");
                _sb.Append($"<use transform=\"{matrix}\" xlink:href=\"#{id}\"{imgMask}{ImagePaintAttributes()}/>\n");
                return;
            }

            _sb.Append($"<image transform=\"{matrix}\"");
            _sb.Append($" x=\"0\" y=\"0\" width=\"1\" height=\"1\"");
            _sb.Append($" preserveAspectRatio=\"none\"");
            _sb.Append($"{rendering}{imgMask}{ImagePaintAttributes()}");
            _sb.Append($" xlink:href=\"{dataUri}\"/>\n");
        }

        /// <summary>
        /// The pixel size an encoded image drawn now should be decoded for: its drawn size in points
        /// — the lengths the CTM gives the unit square — at <see cref="PdfRenderOptions.SvgImageDpi"/>.
        /// Zero on both axes, the image's own size, unless <see cref="PdfRenderOptions.SvgDownsamplesImages"/>.
        /// </summary>
        private void ImageTarget(out int width, out int height)
        {
            width = height = 0;
            if (!Options.SvgDownsamplesImages) return;

            float[] m = _gs.CTM;
            double scale = Options.SvgImageDpi / 72.0;
            double sx = Math.Sqrt((double)m[0] * m[0] + (double)m[1] * m[1]) * scale;
            double sy = Math.Sqrt((double)m[2] * m[2] + (double)m[3] * m[3]) * scale;
            if (!(sx > 0 && sx < int.MaxValue) || !(sy > 0 && sy < int.MaxValue)) return;
            width  = SaturatingCast.ToInt(Math.Ceiling(sx));
            height = SaturatingCast.ToInt(Math.Ceiling(sy));
        }

        /// <summary>
        /// Whether a <paramref name="w"/> × <paramref name="h"/> image decoded for this target comes
        /// out smaller than it is — the raster path's rule: a reducible source larger than the
        /// target with its headroom.
        /// </summary>
        private static bool WouldReduce(int w, int h, int targetWidth, int targetHeight)
            => targetWidth > 0 && targetHeight > 0
               && ImageDownsampling.IsReducible(w, h)
               && (w > ImageDownsampling.Wanted(targetWidth) || h > ImageDownsampling.Wanted(targetHeight));

        /// <summary>
        /// The <c>mask</c> attribute for the graphics-state soft mask in force (§11.6.4), including
        /// the leading space, or the empty string when none is set — ready to concatenate into
        /// whichever element places an image.
        ///
        /// <para><b>Why it is a method.</b> §11.6.4 applies
        /// the soft mask to <i>every</i> painting operation, and this writer draws an image from two
        /// places: <see cref="RenderImageXObject"/> for <c>Do</c> and <c>OpBi</c> for
        /// <c>BI … ID … EI</c>. The XObject one emitted the attribute and the inline one did not, so
        /// a page whose inline image sat under a <c>gs</c> soft mask <b>rendered masked and exported
        /// unmasked</b> — the raster path has no such split, because both routes reach
        /// <c>PdfRenderContext.DrawBitmap</c> and it goes through
        /// <c>DrawWithOptionalSoftMask</c>. Both image routes now read the state through this one
        /// accessor, so neither can be fixed alone again.</para>
        ///
        /// <para>The attribute lands on the element that carries the placement matrix — the
        /// <c>&lt;image&gt;</c> for a direct or inline draw, the <c>&lt;use&gt;</c> for a
        /// de-duplicated XObject — because SVG allows one <c>mask</c> per element and that is the
        /// element being painted. The stencil-under-a-pattern branch is the one exception and says
        /// so at its own site: its <c>&lt;rect&gt;</c> has already spent its <c>mask</c> attribute
        /// on the stencil, so the soft mask goes on a wrapping <c>&lt;g&gt;</c> instead.</para>
        ///
        /// <para><b>Every site in this writer goes through here</b> — <c>.Path</c>, <c>.Shading</c>,
        /// <c>.Text</c>, <c>.XObject</c> and the inline image. Copies spelled out inline agree until
        /// one more is written and leaves the mask off. A graphics-state read copied seven times has no mechanism keeping the copies in
        /// step; this accessor is that mechanism, so <b>a new element must call it rather than
        /// re-spell it.</b></para>
        /// </summary>
        private string SoftMaskAttribute()
            => _gs.SvgMaskId != null ? $" mask=\"url(#{_gs.SvgMaskId})\"" : "";

        /// <summary>
        /// The alpha constant and blend mode an image is painted with, each with its leading space:
        /// <c>opacity</c> when <c>ca</c> is below 1 and <c>mix-blend-mode</c> when one is set, or the
        /// empty string. An image is a non-stroking paint (§11.6.4.4, §11.3.5), so it takes <c>ca</c>,
        /// as a shading does. Every image route dropped both — <c>Do</c>, the inline image, the stencil
        /// — and so did the raster path's <c>PdfRenderContext.DrawBitmap</c>, so a half-transparent
        /// image exported and rendered opaque alike.
        /// </summary>
        private string ImagePaintAttributes()
        {
            string opacity = Math.Abs(_gs.FillAlpha - 1f) > OpaqueAlphaEpsilon ? $" opacity=\"{F(_gs.FillAlpha)}\"" : "";
            string blend = _gs.BlendMode != null ? $" style=\"mix-blend-mode:{_gs.BlendMode}\"" : "";
            return opacity + blend;
        }

        /// <summary>
        /// The <c>image-rendering</c> presentation attribute implied by the image's
        /// <c>/Interpolate</c> flag (§8.9.5.1, Table 89), including the leading space, ready to
        /// concatenate into an <c>&lt;image&gt;</c> element.
        ///
        /// <para><b>The flag was ignored entirely.</b> The raster path reads it and picks a
        /// resampler from it — <c>ContentStreamInterpreter</c>: <c>bool interpolate =
        /// stream[/Interpolate] is PdfBoolean ib &amp;&amp; ib.Value;</c>, then Mitchell cubic
        /// when true and nearest-neighbour when false or absent
        /// (<see cref="PdfRenderContext.DrawBitmap"/>). SVG emitted nothing, and every viewer's
        /// default is smoothing, so a 2×2 image blown up to 200×200 exported as a gradient
        /// where it rendered as four hard squares.</para>
        ///
        /// <para>The PDF default is <c>false</c>, so the common case is the explicit
        /// <c>pixelated</c>; <c>auto</c> is written out for the true case rather than left
        /// implicit, so the emitted document states which branch was taken.</para>
        /// </summary>
        private string ImageRenderingAttribute(PdfDictionary imageDict)
        {
            bool interpolate = PdfResolve.Bool(_doc, imageDict, PdfName.Interpolate)?.Value ?? false;
            return interpolate ? " image-rendering=\"auto\"" : " image-rendering=\"pixelated\"";
        }

        /// <param name="paint">
        /// Colour to paint the set bits in. <c>null</c> recovers it from <c>_gs.FillColor</c>.
        /// The stencil-under-a-pattern path passes opaque <b>white</b> so the result can be used
        /// as an SVG luminance <c>&lt;mask&gt;</c> rather than as the final ink.
        /// </param>
        /// <summary>
        /// Renders a 1-bit ImageMask stencil into a PNG RGBA bitmap using the current fill color.
        /// Bit=0 (painted) → fill color + full opacity; Bit=1 (unmasked) → transparent.
        /// </summary>
        /// <param name="raw">The stencil's packed 1-bit samples, one row per scanline.</param>
        /// <param name="w">Stencil width in samples (<c>/Width</c>).</param>
        /// <param name="h">Stencil height in samples (<c>/Height</c>).</param>
        /// <param name="maskDict">
        /// The image-mask dictionary, read for <c>/Decode</c> — which inverts the sense of the bits.
        /// </param>
        private string BuildImageMaskDataUri(byte[] raw, int w, int h, PdfDictionary maskDict,
                                             SkiaSharp.SKColor? paint = null)
        {
            // Bound the stencil allocation: a 1-bit /ImageMask with a malformed huge
            // /Width /Height needs only a small packed stream yet would allocate a
            // multi-GB RGBA bitmap (w·h·4). Through ImageStencil rather than inline: the same
            // predicate is written at the two /Mask sites, and this was the fourth copy.
            if (w <= 0 || h <= 0 || !ImageStencil.MaskGeometryWithinLimits(w, h))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"SVG image-mask dimensions {w}x{h} exceed safety caps; mask skipped.", null);
                return null;
            }

            // The dimensions being sane says nothing about the BUFFER, and this is where the two
            // renderers disagreed. Both raster callers of DecodePackedBilevel test the packed
            // length before indexing — deliberately, because CcittFaxDecodeFilter and
            // Jbig2DecodeFilter report failure by returning their COMPRESSED input unchanged, so a
            // stencil whose filter failed arrives here far shorter than its declared geometry.
            // With no test the sampling loop below indexed raw[bitIdx / 8] straight off the end.
            // Measured rather than assumed: the IndexOutOfRangeException did NOT escape — the
            // per-operator fault isolation caught it and reported `ContentStreamError: operator
            // 'Do' threw and was skipped`. So the cost was ATTRIBUTION, and it was a real parity
            // defect: raster refuses the same file as ImageDecodeFailed naming the reason, so a
            // host filtering on that kind — the kind every other image refusal in both renderers
            // uses — saw one refusal and missed the other.
            //
            // `>=`, not `==`, and for the raster branch's reason: an /EncodedByteAlign CCITT
            // stencil pads every row to a byte boundary and routinely carries trailing bytes past
            // the last one. Tightening this to `==` makes every such mask vanish from the export.
            int packedStride = (w + 7) / 8;
            if (raw == null || raw.Length < (long)packedStride * h)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"SVG image-mask {w}x{h} needs {(long)packedStride * h} packed bytes but has "
                    + $"{raw?.Length ?? 0}; mask skipped. The usual cause is a CCITT or JBIG2 "
                    + "filter that failed and passed its compressed input through unchanged.",
                    null);
                return null;
            }

            if (paint is SkiaSharp.SKColor given)
                return EncodeStencil(raw, w, h, maskDict, given.Red, given.Green, given.Blue);

            // Parse the current fill colour back to components, through the one parser
            // (PdfColor.TryParseCssColor). Anything that is not a solid colour defaults to opaque
            // black so the stencil is visible.
            //
            // The round trip is safe for every SOLID colour: PdfColor.ToCssRgb is the only
            // producer and emits `rgb(r,g,b)` exclusively. It was NOT safe for a PATTERN fill,
            // whose value is `url(#patN)` — unparseable, so the stencil silently painted black.
            // That case no longer reaches here; see the caller.
            //
            // FillPaint, not _gs.FillColor: §10.4's transfer function applies to the ink a stencil
            // paints, and the raster path gets there another way — it builds the bitmap from the
            // raw fill colour and then runs ApplyTransferFunction over the finished pixels.
            byte fr = 0, fg = 0, fb = 0;
            if (PdfColor.TryParseCssColor(FillPaint, out SkiaSharp.SKColor solid))
            {
                fr = solid.Red; fg = solid.Green; fb = solid.Blue;
            }

            return EncodeStencil(raw, w, h, maskDict, fr, fg, fb);
        }

        /// <summary>
        /// Encodes a 1-bpc stencil as a PNG data URI: set bits take <paramref name="fr"/>/
        /// <paramref name="fg"/>/<paramref name="fb"/> at full alpha, clear bits are fully
        /// transparent. Shared by the ordinary fill path and the pattern-mask path so the two
        /// cannot disagree about which bit paints.
        /// </summary>
        private string EncodeStencil(byte[] raw, int w, int h, PdfDictionary maskDict,
                                     byte fr, byte fg, byte fb)
        {
            // /Decode [1 0] inverts which bit paints (§8.9.6.2). The default is [0 1], where a
            // 0 bit paints. This was hard-coded to the default, so a stencil with an inverted
            // /Decode came out as its own negative — inverted against the raster render.
            //
            // ImageStencil.DecodeInverts, not a local copy: this file carried a third reading of
            // the same rule, and the leaf's version additionally resolves each array ELEMENT
            // through PdfResolve (§7.3.10 makes an array element a value like any other), which
            // the local one did not.
            // paintOnSetBit IS DecodeInverts: with the default /Decode [0 1] a 0 bit paints, which
            // is `paintOnSetBit == false`.
            bool paintOnSetBit = ImageStencil.DecodeInverts(maskDict, _doc);

            // One loop, shared with the raster path. The alpha stays opaque here on purpose: this
            // stencil becomes a PNG whose transparency IS the mask, where the raster path paints
            // with the graphics state's fill colour and its alpha participates.
            using var bmp = ImageStencil.DecodePackedBilevel(
                raw, w, h, (w + 7) / 8, paintOnSetBit,
                v0: 0, v1: 0, isImageMask: true,
                fillColor: new SkiaSharp.SKColor(fr, fg, fb, 255));

            using var img  = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            return SvgSyntax.DataUri("image/png", data.ToArray());
        }

        /// <summary>
        /// The image as a data URI: a JPEG that nothing changes passes through as itself; every
        /// other image is decoded as the raster renderer decodes it — one decode for both renderers
        /// and the exporter (<see cref="ContentStreamInterpreter.DecodeImageXObject(PdfDocument, PdfStream, byte[], IccRenderingIntent)"/>:
        /// every depth, <c>/Decode</c>, colour space, ICC profile, the soft mask, the colour key and
        /// the stencil) — then the graphics state's transfer function, then PNG.
        /// </summary>
        /// <remarks>
        /// This writer decodes no image itself — sample expansion, Indexed and special colour spaces,
        /// ICC, <c>/Decode</c>, <c>/SMask</c> with <c>/Matte</c>, <c>/Mask</c> are the raster path's,
        /// since a copy saying it mirrors that path drifts (the <c>/Decode</c> rounding, the 16-bit
        /// narrowing; GAPS.md §2b). The shared decode hands back the raster path's 8888, Premul where the image carries transparency, which PNG encoding
        /// un-premultiplies. Without a document there is nothing to decode against, as the raster
        /// path has none either.
        /// </remarks>
        private string BuildImageDataUri(byte[] raw, int w, int h, PdfStream stream)
        {
            PdfDictionary dict = stream;
            bool needsDecodeRemap = HasNonIdentityDecode(dict);
            // An /SMask (soft-mask image) or /Mask (color-key array or stencil stream) carries
            // transparency the opaque JPEG bytes can't, so the fast pass-through must be
            // skipped to bake the alpha into a PNG.
            var maskEntry = _doc?.ResolveObject(dict[PdfName.Mask]);
            bool hasSMask = _doc != null && _doc.ResolveObject(dict[PdfName.SMask]) is PdfStream;
            bool hasHardMask = maskEntry is PdfArray || maskEntry is PdfStream;
            bool isJpeg = RasterImageInspector.IsJpeg(raw);

            // The raster renderer's rule, which this path did not have: a FOUR-component JPEG's
            // /Decode array describes the Adobe APP14 CMYK inversion, and the JPEG decoder has
            // already performed it before handing back RGB. Re-applying the array to those RGB
            // channels inverts them a second time, so every Adobe-marked CMYK JPEG exported as a
            // photographic negative while the same file rendered correctly to a bitmap
            // (ContentStreamInterpreter.cs, `if (GetColorComponents(imageDict) != 4)`). Sharing
            // GetColorComponents rather than re-deriving the arity is what keeps the two sides
            // deciding this the same way.
            bool isFourComponentJpeg = isJpeg &&
                ImageSamples.ComponentCount(dict, _doc)
                    == PdfColorSpaceNames.CmykComponents;
            // Fast path: pass JPEG through untouched ONLY when no transfer function is active, no
            // /Decode remap is needed, no mask needs baking and no codec but the JPEG's is asked
            // for — a JPEG 2000 codestream or a plugin-decoded filter takes the decode below, as
            // the raster path's DecodeToBitmap orders them. A CMYK JPEG is deliberately NOT sent
            // down it even though its /Decode is known to be inapplicable: the decode-to-PNG round
            // trip is what performs the APP14 conversion, and SVG viewers do not agree on CMYK
            // JPEG support. The arity is tested, so a CMYK JPEG with no /Decode, or an identity one,
            // does not go out as raw CMYK bytes while the raster path converts it.
            bool otherCodec = JpxHeader.IsJpx(raw) || ContentStreamInterpreter.DeclaresJpxFilter(dict, _doc)
                || PluginImageDecoder.HasDecoderFor(dict, _doc);
            // Nor a JPEG whose colour space is not the codec's (an ICC, spot, Lab or CIE space): a
            // viewer would show the codec's colours; the decode below colours its samples.
            // Nor one the reduction below would shrink: passing it through embeds the full-size
            // file, which is what the option exists to stop.
            ImageTarget(out int targetWidth, out int targetHeight);
            if (isJpeg && !otherCodec && !isFourComponentJpeg
                && _gs.TransferFunction == null && !needsDecodeRemap && !hasSMask && !hasHardMask
                && !WouldReduce(w, h, targetWidth, targetHeight)
                && JpegSamples.CodecColoursAreTheImages(dict, _doc, raw))
                return SvgSyntax.DataUri("image/jpeg", raw);

            if (_doc == null)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG image XObject not decoded: the writer has no document to resolve it against", null);
                return null;
            }

            // The raster path's decode, from the samples already in hand and under this state's
            // intent; null is reported there, as the raster side draws nothing for it.
            SkiaSharp.SKBitmap bmp = ContentStreamInterpreter.DecodeImageXObject(
                _doc, stream, raw, ImageIntent(dict), targetWidth, targetHeight);
            if (bmp == null) return null;

            try
            {
                // §10.4's transfer function belongs to where the image is drawn, so the shared decode
                // leaves it to the drawer — the raster path applies it after its decode as well.
                ApplyTransferFunctionToBitmap(bmp);
                using var img  = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                return SvgSyntax.DataUri("image/png", data.ToArray());
            }
            finally { bmp.Dispose(); }
        }

        /// <summary>
        /// Reads /Decode and returns true if it differs from the identity mapping
        /// (component count × [0 1]). Used to decide whether to short-circuit the JPEG fast path.
        /// </summary>
        /// <remarks>
        /// <para><b>This must agree with whichever body actually applies the array, because it is
        /// what decides that the array is never applied at all.</b> It is the third read of this
        /// entry; the other two were consolidated onto <see cref="PdfColor.DecodeArray"/> and this
        /// one kept an open-coded copy with <c>dec[i] as PdfNumber</c> — which does <b>not</b>
        /// follow an indirect reference.</para>
        ///
        /// <para>So for <c>/Decode [0 9 0 R]</c>, or the inverting <c>/Decode [9 0 R 10 0 R]</c>
        /// resolving to <c>[1 0]</c>, the cast yielded null, the positional defaults made the pair
        /// read as the identity, and this returned false — at which point the JPEG pass-through
        /// emitted the original codestream untouched. The same file rendered with <c>/Decode</c>
        /// applied and exported with it silently dropped: an inverted grayscale scan came out
        /// un-inverted.</para>
        /// </remarks>
        private bool HasNonIdentityDecode(PdfDictionary dict)
        {
            var dec = PdfResolve.Array(_doc, dict, PdfName.Decode) ?? PdfResolve.Array(_doc, dict, PdfName.D);
            if (dec == null) return false;

            // Resolves every element, and returns null when any is non-numeric. A /Decode we cannot
            // read is emphatically NOT the identity — treating it as such is what silently drops it.
            float[] values = PdfColor.DecodeArray(_doc, dec);
            if (values == null) return true;

            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                if (Math.Abs(values[i])          > 1e-4f) return true;
                if (Math.Abs(values[i + 1] - 1f) > 1e-4f) return true;
            }
            return false;
        }

        /// <summary>
        /// Applies the ExtGState transfer function (<c>/TR2 ?? /TR</c>, §10.4) to a decoded image.
        /// </summary>
        /// <remarks>
        /// <para>Through <see cref="TransferFunctionLut"/>, which the raster path also calls. The
        /// comment that stood here said "Parity with
        /// <c>ContentStreamInterpreter.ApplyTransferFunction</c>" — the sentence this project treats
        /// as a defect marker — and it was not parity in two ways that mattered. This copy called
        /// <c>PdfFunctionEvaluator.Evaluate</c> <b>per channel per pixel</b> where the raster path
        /// built a 256-entry table, so a large image cost 3·w·h evaluations against 768; and it
        /// evaluated with <b>no resolver</b>, so a Type 3 stitching function with indirect
        /// sub-functions degraded here and not there — a difference in the exported pixels, not just
        /// in the time.</para>
        /// </remarks>
        private void ApplyTransferFunctionToBitmap(SkiaSharp.SKBitmap bmp)
            => TransferFunctionLut.ApplyToBitmap(
                   TransferFunctionLut.Build(_gs.TransferFunction, _doc), bmp);

        /// <summary>Builds the index -> sRGB palette for an Indexed image.</summary>
        /// <remarks>
        /// One implementation, in <see cref="IndexedPalette"/>, reached from here and from the
        /// raster interpreter: two builders each claiming to mirror the other would drift.
        /// </remarks>
        private SkiaSharp.SKColor[] BuildSvgIndexedPalette(PdfObject baseObj, byte[] table, int hival)
            => IndexedPalette.Build(_doc, baseObj, table, hival, CurrentIntent);

    }
}
