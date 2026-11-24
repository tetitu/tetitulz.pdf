// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    internal sealed partial class ContentStreamInterpreter
    {
        /// <summary>
        /// Down-converts a 16-bit sample to 8 bits with round-to-nearest — the
        /// <c>(v·255 + 32767) / 65535</c> rounding every 16-bpc image path uses.
        /// </summary>
        /// <remarks>Delegates to <see cref="PdfColor.Scale16To8"/>, which both renderers share.
        /// See the remarks there for why the shared copy lives in a leaf class rather than being
        /// called sideways out of this one. GAPS.md §2b.</remarks>
        private static byte Scale16To8(int v16) => PdfColor.Scale16To8(v16);

        /// <summary>
        /// Decodes an encoded image (PNG/JPEG/codec) but bounds it by the image-size caps
        /// BEFORE allocating pixels. A PDF can declare a tiny /Width /Height (passing the
        /// pre-decode cap) yet embed a codec whose OWN header is huge; SKBitmap.Decode would
        /// then allocate the codec's real size (e.g. a 1×1-declared PDF embedding a
        /// 16384×12000 PNG → 786 MB). Peeking SKCodec.Info lets us refuse oversized frames
        /// without the allocation; the post-decode re-check is a defensive backstop. Returns
        /// null (and disposes) when the image exceeds <see cref="MaxImageDimension"/> /
        /// <see cref="MaxImagePixelArea"/> or cannot be decoded.
        /// </summary>
        internal static SKBitmap SafeDecodeEncodedImage(byte[] data) =>
            SafeDecodeEncodedImage(data, targetWidth: 0, targetHeight: 0);

        /// <param name="targetWidth">
        /// Device-pixel width the image will be drawn into, or 0 for "decode at native size".
        /// </param>
        /// <param name="targetHeight">Device-pixel height, or 0.</param>
        /// <remarks>
        /// <para>When a target is supplied, the codec is asked for the nearest <b>supported</b>
        /// reduced size (<see cref="SKCodec.GetScaledDimensions"/>) rather than being decoded at
        /// full resolution and shrunk afterwards. For JPEG that is a DCT-domain scale of 1/2, 1/4
        /// or 1/8 and is nearly free; a codec that supports no reduction returns its native size
        /// and nothing changes.</para>
        /// <para>Measured on an 8.4 MP source drawn as a 24 pt thumbnail: decoding it in full cost
        /// <b>85.9 ms and 57 MB</b> against a 14.9 ms floor — 71 ms of that thrown away.</para>
        /// <para><b>This changes output pixels, so it is opt-in and reported.</b> A scaled decode
        /// followed by Skia's resampler is not bit-identical to a full decode followed by the same
        /// resampler. The renderer already reduces DPI silently above
        /// <c>MaxImagePixelArea</c> and GAPS.md §2 files that as a defect for exactly that reason,
        /// so this one announces itself through <see cref="RenderDiagnosticKind.ImageDownsampled"/>.</para>
        /// <para>The safety caps are still applied to the <i>native</i> dimensions first: a
        /// decode bomb must be refused on what it claims to be, not on what it would shrink to.</para>
        /// </remarks>
        /// <param name="data">
        /// The encoded image bytes — a JPEG or other format Skia decodes directly, not raw samples.
        /// </param>
        internal static SKBitmap SafeDecodeEncodedImage(byte[] data, int targetWidth, int targetHeight)
        {
            if (data == null || data.Length == 0) return null;
            SKBitmap bmp = null;
            try
            {
                using var skData = SKData.CreateCopy(data);
                using var codec = SKCodec.Create(skData);
                if (codec != null)
                {
                    int cw = codec.Info.Width, ch = codec.Info.Height;
                    if (cw <= 0 || ch <= 0 || cw > MaxImageDimension || ch > MaxImageDimension
                        || (long)cw * ch > MaxImagePixelArea)
                    {
                        RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                            $"Encoded image codec dimensions {cw}x{ch} exceed safety caps; image skipped.", null);
                        return null;
                    }

                    SKSizeI scaled = ImageDownsampling.CodecSize(codec, cw, ch, targetWidth, targetHeight);
                    if (scaled.Width > 0 && scaled.Width < cw)
                    {
                        var info = codec.Info.WithSize(scaled.Width, scaled.Height);
                        var reduced = new SKBitmap(info);
                        if (codec.GetPixels(info, reduced.GetPixels()) == SKCodecResult.Success)
                        {
                            RenderDiagnostics.Report(RenderDiagnosticKind.ImageDownsampled,
                                $"Image decoded at {scaled.Width}x{scaled.Height} instead of {cw}x{ch} " +
                                $"for a {targetWidth}x{targetHeight} device target.", null);
                            return reduced;
                        }
                        // A codec that advertised the size but would not deliver it: fall back to
                        // the full decode rather than return a partially-filled surface.
                        reduced.Dispose();
                    }

                    bmp = SKBitmap.Decode(codec);
                }
                else
                {
                    using var ms = new MemoryStream(data);
                    bmp = SKBitmap.Decode(ms);
                }
            }
            catch (Exception ex)
            {
                try { bmp?.Dispose(); } catch { }
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed, "Encoded image decode failed", ex);
                return null;
            }
            if (bmp != null && (bmp.Width > MaxImageDimension || bmp.Height > MaxImageDimension
                || (long)bmp.Width * bmp.Height > MaxImagePixelArea))
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }

        /// <summary>
        /// Decimates a **raw sample buffer** in place of decoding it at full resolution, when the
        /// image is being drawn far smaller than it is stored and
        /// <see cref="PdfRenderContext.DownsampleImagesToTarget"/> is on. Returns true when
        /// <paramref name="data"/>, <paramref name="w"/> and <paramref name="h"/> were replaced.
        /// </summary>
        /// <remarks>
        /// <para><b>Why here rather than in each decode branch.</b> The raw path fans out into
        /// Indexed, Separation/DeviceN, Lab, CalGray/CalRGB and four device branches, each
        /// allocating <c>SKColor[w*h]</c>. Editing all of them would be eight chances to diverge —
        /// the defect class this file keeps finding. One decimation pass ahead of the fan-out means
        /// every branch inherits it and none of them knows it happened. A 2 900² image drawn at
        /// 24 pt costs <b>141.9 ms and 57.7 MB</b> undecimated; the same page with a right-sized
        /// source costs 15.7 ms and 0.24 MB.</para>
        ///
        /// <para><b>Nearest-neighbour, deliberately.</b> Averaging would change which sample values
        /// exist, and several things downstream read <i>values</i> rather than colours: colour-key
        /// <c>/Mask</c> ranges (§8.9.6.4), <c>/Decode</c> remapping, an <c>/Indexed</c> palette
        /// lookup and a Separation tint transform. Picking existing samples keeps every one of
        /// those exact. It also means the result is visibly harsher than Skia's filtered downscale,
        /// which is why this is opt-in and off by default.</para>
        ///
        /// <para><b>8 and 16 bpc only.</b> At 1, 2 and 4 bpc a pixel is not byte-addressable, and
        /// those depths are stencil masks and small indexed images — the cases where the saving is
        /// least and the aliasing worst. They decode at full size exactly as before.</para>
        /// </remarks>
        private static bool TryDecimateRawSamples(ref byte[] data, ref int w, ref int h,
                                                  int comps, int bpc, int targetW, int targetH)
        {
            if (data == null) return false;
            if (bpc != 8 && bpc != 16) return false;
            if (comps <= 0) return false;
            // The one policy (ImageDownsampling), which the decimating Flate decode asks too.
            if (!ImageDownsampling.TryRawDecimation(w, h, targetW, targetH, out int step, out int nw, out int nh)) return false;

            int bytesPerPixel = comps * (bpc / 8);
            long stride = (long)w * bytesPerPixel;
            // Only decimate a buffer that really has this shape; anything shorter is a malformed
            // or differently-packed image and is left to the existing branches to handle.
            if (stride <= 0 || (long)h * stride > data.Length) return false;

            var dst = new byte[(long)nw * nh * bytesPerPixel];
            int di = 0;
            for (int y = 0; y < nh; y++)
            {
                long srcRow = (long)y * step * stride;
                for (int x = 0; x < nw; x++)
                {
                    long si = srcRow + (long)x * step * bytesPerPixel;
                    for (int b = 0; b < bytesPerPixel; b++) dst[di++] = data[si + b];
                }
            }

            data = dst;
            w = nw;
            h = nh;
            return true;
        }

        // ----------------------------------------------------------------
        //  Public entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Decodes an image stream's raw bytes through the filter pipeline, augmenting
        /// any CCITTFaxDecode filter's /DecodeParms with /Columns = imageWidth when
        /// /Columns is absent.  Some PDF generators (including DocuSign) omit /Columns
        /// from /DecodeParms and rely on the image /Width — the CCITT decoder defaults
        /// to 1728 columns (fax default) without this, causing decode failure.
        /// </summary>
        /// <remarks>
        /// <c>internal</c> so <see cref="PdfSvgRenderer"/> shares it: the SVG path read image
        /// streams with a plain <c>GetDecodedData()</c> and so lost this repair, giving the two
        /// renderers different pixels for the same CCITT file.
        /// </remarks>
        /// <remarks>
        /// <para><b>These two reads are deliberately NOT resolved, and that is a correction to an
        /// earlier finding rather than an oversight.</b> A raw <c>/Filter</c> would indeed be a
        /// non-null <c>PdfReference</c>, skipping the early return, and <c>FilterPipeline.Decode</c>
        /// would then match neither its name branch nor its array branch and hand back the
        /// compressed bytes. But it cannot arrive that way: <c>PdfParser</c> normalises an indirect
        /// <c>/Filter</c> <i>and</i> <c>/DecodeParms</c> in place when it builds the
        /// <c>PdfStream</c>, and resolves the stream-valued entries inside <c>/DecodeParms</c> as
        /// well — precisely so a filter, which runs in Core with no document, never receives a
        /// reference. The abbreviations <c>/F</c> and <c>/DP</c> are inline-image keys, and an
        /// inline image's values are direct by construction: a content stream has no syntax for a
        /// reference.</para>
        /// </remarks>
        internal static byte[] DecodeImageStream(PdfStream stream, int imageWidth)
            => DecodeImageStream(stream, imageWidth, null);

        /// <summary>
        /// <see cref="DecodeImageStream(PdfStream, int)"/> under the page's image-decoding budget
        /// (<see cref="RenderBudget.ImageWork"/>), which a codec whose own budget is sized by the
        /// stream's parameters (JBIG2) spends too; null when there is no page.
        /// </summary>
        internal static byte[] DecodeImageStream(PdfStream stream, int imageWidth, WorkBudget work)
        {
            var filterObj   = stream[PdfName.Filter]      ?? stream[PdfName.F];
            var decParmsObj = stream[PdfName.DecodeParms] ?? stream[PdfName.DP];

            if (filterObj == null)
                return stream.RawData ?? Array.Empty<byte>();

            decParmsObj = AugmentCcittDecodeParms(filterObj, decParmsObj, imageWidth);
            return FilterPipeline.Decode(stream.RawData ?? Array.Empty<byte>(), filterObj, decParmsObj,
                PdfLimits.MaxDecompressedBytes, work);
        }

        /// <summary>
        /// Returns a /DecodeParms augmented so that every CCITTFaxDecode entry has
        /// /Columns set. If /Columns is already present, the dict is returned as-is.
        /// </summary>
        private static PdfObject AugmentCcittDecodeParms(
            PdfObject filterObj, PdfObject decParmsObj, int columns)
        {
            if (filterObj is PdfName fn)
            {
                if (fn.Value != "CCITTFaxDecode" && fn.Value != "CCF") return decParmsObj;
                return EnsureCcittColumns(decParmsObj as PdfDictionary, columns);
            }

            if (filterObj is PdfArray filterArr)
            {
                var parmsArr = decParmsObj as PdfArray;
                PdfArray newParmsArr = null;

                for (int i = 0; i < filterArr.Count; i++)
                {
                    // Elements are NOT resolved here either, and unlike the top-level keys that is
                    // because resolving would buy nothing: FilterPipeline.Decode drops a /Filter
                    // element that is not a name and reports it through LastUnknownFilterName, so
                    // the stage never runs whatever this method does with its /DecodeParms.
                    string fname = (filterArr[i] as PdfName)?.Value ?? "";
                    if (fname != "CCITTFaxDecode" && fname != "CCF") continue;

                    var existing = (parmsArr != null && i < parmsArr.Count)
                        ? parmsArr[i] as PdfDictionary : null;
                    var augmented = EnsureCcittColumns(existing, columns);
                    if (ReferenceEquals(augmented, existing)) continue;

                    if (newParmsArr == null)
                    {
                        newParmsArr = new PdfArray();
                        int srcLen = parmsArr?.Count ?? 0;
                        for (int j = 0; j < Math.Max(srcLen, filterArr.Count); j++)
                            newParmsArr.Add(parmsArr != null && j < parmsArr.Count ? parmsArr[j] : null);
                    }
                    newParmsArr[i] = augmented;
                }
                return newParmsArr ?? decParmsObj;
            }

            return decParmsObj;
        }

        private static PdfDictionary EnsureCcittColumns(PdfDictionary existing, int columns)
        {
            var colName = PdfName.Columns;
            if (existing != null && existing[colName] != null) return existing;

            var d = new PdfDictionary();
            if (existing != null)
                foreach (var kv in existing)
                    d.Set(kv.Key, kv.Value);
            d[colName] = new PdfNumber(columns);
            return d;
        }

        /// <summary>
        /// Draws a decoded image in the unit square — an image XObject's and an inline image's
        /// alike. A separation plate reads the samples themselves (and decides overprint per
        /// colorant), or converts an sRGB-coloured image through its output profile; anything else
        /// is the ordinary draw.
        /// </summary>
        private void DrawDecodedImage(SKBitmap bitmap, RawImageSamples painted, PdfDictionary imageDict, bool interpolate)
        {
            // §8.9.6.2: a stencil's painted samples take the fill, which may be a pattern — painted
            // through the stencil, as the SVG export paints it through a mask; the stencil was
            // decoded in the solid colour the state last held, and drawn so it was that colour.
            if (_ctx.Plate == null && ImageStencil.IsStencilMask(imageDict, _doc)
                && SelectedPatternName(stroke: false) is string stencilPattern && PatternResourceExists(stencilPattern))
            {
                _ctx.FillStencilWithPattern(bitmap, interpolate, () => RenderTilingPattern(stencilPattern, stroke: false));
                return;
            }

            bool drawnOnPlate = _ctx.Plate != null && (TryDrawImageOnPlate(bitmap, painted, interpolate) || TryDrawImageThroughProfile(bitmap, imageDict, interpolate));
            if (!drawnOnPlate)
                _ctx.DrawBitmap(bitmap, 0, 0, 1, 1, interpolate, plateReady: ImageStencil.IsStencilMask(imageDict, _doc));
        }

        private void RenderImageXObject(PdfDictionary imageDict)
        {
            var stream = imageDict as PdfStream
                      ?? _doc.ResolveObject(imageDict) as PdfStream;
            if (stream == null) return;
            // A decode that failed earlier in this render failed for good; its report stands.
            if (_ctx.DecodeFailedBefore(stream)) return;

            // §8.6.5.6: a device /ColorSpace is the current resources' default for it. The stream
            // stays the data and the cache key; a remapped draw is not cached, since the same image
            // drawn under other resources is another colour.
            var asDrawn = DefaultColorSpaces.WithDefaults(imageDict, _resources, _doc);
            bool remapped = !ReferenceEquals(asDrawn, imageDict);
            imageDict = asDrawn;

            // §8.6.7: non-zero overprint mode "shall not, however, apply to the painting of images",
            // so an overprinting image writes every colorant its colour space names — all four
            // process colorants for DeviceCMYK, grey, RGB and the rest — which on a composite is
            // exactly the ordinary draw. Only a colorant set naming some colorants and not others
            // leaves the rest alone, which an sRGB surface cannot show; that one is reported, at the
            // operator and before any decode, whether or not simulation is on — a gap either way.
            // (A separation plate decides it per colorant, in TryDrawImageOnPlate.)
            if (_ctx.GS.OverprintFill && _ctx.Plate == null && OverprintCompositor.ImageLeavesColorantsAlone(imageDict, _doc))
                PdfRenderContext.ReportOverprintSkipped(OverprintReportReason.Image);

            int w = PdfResolve.Int(_doc, imageDict, PdfName.Width,  PdfName.W, 1);
            int h = PdfResolve.Int(_doc, imageDict, PdfName.Height, PdfName.H, 1);

            // PdfResolve, not the indexer: the indexer does not follow an indirect reference, so a
            // producer writing /Interpolate as "6 0 R" yielded false here — indistinguishable from
            // the key being absent — while the SVG exporter, which already used PdfResolve, read it
            // as true. The same file was smoothed on export and pixelated in the raster render.
            bool interpolate = PdfResolve.Bool(_doc, imageDict, PdfName.Interpolate)?.Value ?? false;

            // Everything below — decode plus the five post-decode passes — depends only on the
            // stream and its dictionary, with ONE exception: ApplyTransferFunction reads the
            // graphics state's /TR. So the decoded result is reusable only while no transfer
            // function is set, which is the overwhelmingly common case. With one active the
            // image is decoded fresh, exactly as before.
            // Two things make a decode reusable across draws of the same XObject:
            //   * no /TR — ApplyTransferFunction reads the graphics state, so the result is not a
            //     function of the stream alone;
            //   * no downsampling — the decode size then depends on the CTM, and the same image
            //     drawn large elsewhere on the page would be served the thumbnail-sized bitmap.
            // The second is why this is checked here rather than left to the cache: the cache is
            // keyed on the stream, and adding a size to that key would trade a correctness bug
            // for a memory one (one entry per distinct draw size).
            // A third: a separation plate draws from the samples, which a cached bitmap has not got.
            // A fourth: a stencil mask is decoded in the fill colour in force, so its bitmap is that
            // draw's colour — a later draw under another colour would be served the first one's.
            bool cacheable = _ctx.GS.TransferFunction == null && !_ctx.DownsampleImagesToTarget && _ctx.Plate == null
                && !remapped && !ImageStencil.IsStencilMask(imageDict, _doc);

            if (cacheable)
            {
                var cached = _ctx.TryGetCachedImage(stream);
                if (cached != null)
                {
                    _ctx.DrawBitmap(cached, 0, 0, 1, 1, interpolate, plateReady: ImageStencil.IsStencilMask(imageDict, _doc));
                    return;
                }
            }

            var bitmap = DecodeImageWithMasks(stream, imageDict, w, h, out RawImageSamples painted);
            if (bitmap == null) { _ctx.NoteDecodeFailed(stream); return; }

            ApplyTransferFunction(bitmap);

            DrawDecodedImage(bitmap, painted, imageDict, interpolate);

            // Hand ownership to the cache when it will take it; otherwise dispose as before.
            if (!cacheable || !_ctx.TryCacheImage(stream, bitmap))
                bitmap.Dispose();
        }

        /// <summary>
        /// The image as this renderer places it, before the transfer function and the draw: decoded
        /// at its own size, Premul when it carries transparency, ICC-transformed, and with its soft
        /// mask, colour key and stencil applied. <c>null</c> when it cannot be decoded, reported.
        /// </summary>
        /// <param name="stream">The image XObject.</param>
        /// <param name="imageDict">Its dictionary, as the caller holds it.</param>
        /// <param name="w">Its resolved <c>/Width</c>.</param>
        /// <param name="h">Its resolved <c>/Height</c>.</param>
        /// <param name="key">The samples the image was painted from, which the overprint path composites and the colour key reads (<see cref="RawImageSamples"/>).</param>
        private SKBitmap DecodeImageWithMasks(PdfStream stream, PdfDictionary imageDict, int w, int h,
            out RawImageSamples key)
        {
            key = default;
            // The page's image-decoding budget: once spent, no further image is decoded, and the
            // refusal was reported once — a page of a hundred hostile JBIG2 streams is not a hundred
            // stream budgets of work (GAPS.md §7c).
            if (FormBudget != null && !FormBudget.TryChargeRepeatedImageDecode(stream, (long)w * h)) return null;

            // Drawn far smaller than it is stored, a FlateDecode image is decimated as it is
            // inflated — the samples the decimation below would keep, without the whole buffer.
            byte[] decimated = TryDecodeDecimatedFlate(stream, imageDict, w, h, out int dw, out int dh);
            if (decimated != null) return DecodeSamplesWithMasks(decimated, imageDict, dw, dh, out key);

            byte[] raw;
            try { raw = DecodeImageStream(stream, w, FormBudget?.ImageWork); }
            catch (Exception ex)
            {
                if (FormBudget != null && FormBudget.IsImageWorkRefusal(ex)) return null;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "RenderImageXObject GetDecodedData failed", ex);
                return null;
            }
            return DecodeSamplesWithMasks(raw, imageDict, w, h, out key);
        }

        /// <summary>
        /// <see cref="DecimatingFlateDecode"/>'s samples for an image the raw path would decimate
        /// (<see cref="TryDecimateRawSamples"/>), and its geometry; <c>null</c> for every other image —
        /// downsampling off, no target, a filter chain other than FlateDecode or LZWDecode,
        /// alone or after ASCIIHexDecode and ASCII85Decode stages (the same day), a decode plugin
        /// the full path would ask, a stencil mask, a depth other than 8 or 16, a size the full path
        /// refuses — and whenever the decoder itself declines.
        /// </summary>
        /// <remarks>
        /// The stages are the filter pipeline's own (<see cref="FilterPipeline.TryGetStages"/>, read
        /// from the dictionary as <see cref="DecodeImageStream(PdfStream, int, WorkBudget)"/> hands it
        /// over), so the parameters paired with the last are the ones the full path applies. The
        /// stages before the last run whole, through the pipeline (<see cref="FilterPipeline.DecodeStages"/>),
        /// and only the last is decoded row by row; they are ASCII stages alone because the decimating
        /// decode can still decline after them — a stream short of its rows — and the full path then
        /// runs the chain again: an ASCII stage is one linear pass whose output is never longer than its
        /// input, where a second inflate or LZW decode of an inner layer could double the image's cost.
        /// </remarks>
        private byte[] TryDecodeDecimatedFlate(PdfStream stream, PdfDictionary imageDict, int w, int h, out int dw, out int dh)
        {
            dw = w; dh = h;
            if (!_ctx.DownsampleImagesToTarget || stream.RawData == null) return null;
            _ctx.GetImageDeviceExtent(out int tw, out int th);
            if (tw <= 0 || th <= 0) return null;
            if (w > MaxImageDimension || h > MaxImageDimension || (long)w * h > MaxImagePixelArea) return null;

            if (!FilterPipeline.TryGetStages(stream[PdfName.Filter] ?? stream[PdfName.F],
                    stream[PdfName.DecodeParms] ?? stream[PdfName.DP], out var filters, out var parameters))
                return null;
            int last = filters.Count - 1;
            bool flate = filters[last] == PdfName.FlateDecode.Value || filters[last] == PdfName.Fl.Value;
            bool lzw = filters[last] == PdfName.LZWDecode.Value || filters[last] == PdfName.LZW.Value;
            if (!flate && !lzw) return null;
            for (int i = 0; i < last; i++)
                if (!IsAsciiFilter(filters[i])) return null;   // any other chain is the filter pipeline's
            if (PluginImageDecoder.HasDecoderFor(imageDict, _doc)) return null;
            if (ImageStencil.IsStencilMask(imageDict, _doc)) return null;

            // An /Indexed space has one component (PdfColor.ResolveSpace): one index a sample.
            int comps = GetColorComponents(imageDict);
            int bpc = GetBitsPerComponent(imageDict);
            if (comps <= 0 || (bpc != 8 && bpc != 16)) return null;
            if (!ImageDownsampling.TryRawDecimation(w, h, tw, th, out int step, out int nw, out int nh)) return null;

            byte[] input = stream.RawData;
            long decodedBefore = 0;
            if (last > 0)
            {
                try
                {
                    input = FilterPipeline.DecodeStages(input, filters, parameters, last,
                        PdfLimits.MaxDecompressedBytes, FormBudget?.ImageWork, ref decodedBefore, out _);
                }
                catch (Exception)
                {
                    return null;   // the full path runs the stages again, and refuses or reports as it does
                }
            }

            PdfDictionary parms = parameters[last];
            byte[] samples = flate
                ? DecimatingFlateDecode.TryDecode(input, parms, w, h, comps, bpc, step, nw, nh, decodedBefore)
                : DecimatingFlateDecode.TryDecodeLzw(input, parms, w, h, comps, bpc, step, nw, nh, decodedBefore);
            if (samples == null) return null;
            dw = nw; dh = nh;
            return samples;
        }

        /// <summary>An ASCIIHexDecode or ASCII85Decode stage, by either spelling (Table 6, Table 92): one whose output is never longer than its input.</summary>
        private static bool IsAsciiFilter(string name)
            => name == PdfName.ASCIIHexDecode.Value || name == PdfName.AHx.Value
            || name == PdfName.ASCII85Decode.Value || name == PdfName.A85.Value;

        /// <summary>
        /// <see cref="DecodeImageWithMasks"/> from the filter chain's output, for a caller that has
        /// already run it — the SVG writer, which reads the samples first to decide whether a JPEG
        /// passes through untouched.
        /// </summary>
        private SKBitmap DecodeSamplesWithMasks(byte[] raw, PdfDictionary imageDict, int w, int h,
            out RawImageSamples key)
        {
            // The colour key is evaluated on the samples the raw path read (§8.9.6.4): the filter
            // output, or a JPEG 2000 image's decoded samples when its /ColorSpace governs — the
            // codestream itself is no sample buffer and cannot be keyed — at the
            // geometry they were painted at, which a reduced decode shrinks below the XObject's.
            var rawBitmap = DecodeToBitmap(raw, w, h, imageDict, out key);
            if (rawBitmap == null) return null;

            // Ensure Premul alpha type so SMask / colour-key transparency is respected.
            // Opaque bitmaps have their alpha channel ignored by Skia when drawing.
            var bitmap = EnsurePremulForTransparency(rawBitmap, imageDict);
            if (!ReferenceEquals(bitmap, rawBitmap)) rawBitmap.Dispose();

            ApplyIccTransform(bitmap, imageDict);
            ApplyImageSoftMask(bitmap, imageDict);
            // A JPEG 2000 opacity channel the raw path could not carry, after the paint and the
            // soft mask. Table 87: /SMaskInData is ignored when the image has an /SMask.
            if (key.Opacity != null && imageDict[PdfName.SMask] == null)
            {
                SKBitmap withOpacity = Images.ImageOpacity.Apply(bitmap, key.Opacity, SKAlphaType.Premul);
                if (!ReferenceEquals(withOpacity, bitmap)) { bitmap.Dispose(); bitmap = withOpacity; }
            }
            ApplyColorKeyMask(bitmap, key.Samples, key.Width, key.Height, key.Dictionary, _doc);
            ApplyStencilMask(bitmap, imageDict);
            return bitmap;
        }

        /// <summary>
        /// An image XObject decoded as this renderer draws it — every bit depth, <c>/Decode</c>,
        /// every colour space and ICC profile, the soft mask, colour key and stencil — at its own
        /// size, for a caller that wants the pixels rather than a page. <c>null</c> when it cannot
        /// be decoded, which <see cref="RenderDiagnostics"/> reports.
        /// </summary>
        /// <remarks>
        /// <para><b>For <c>PdfImageExporter</c>, which had a decoder of its own</b> — 8 bits per
        /// component, five colour spaces, no <c>/Decode</c>, no masks — and refused everything
        /// else. GAPS.md §6 recorded that closing the gap meant sharing this decode, not writing a
        /// fourth.</para>
        /// <para>The graphics state is a fresh page's: a stencil mask paints in black and no
        /// transfer function applies, because both belong to where an image is drawn, not to the
        /// image.</para>
        /// </remarks>
        internal static SKBitmap DecodeImageXObject(PdfDocument doc, PdfStream image)
            => DecodeImageXObject(doc, image, raw: null, IccRenderingIntents.Parse(null));

        /// <summary>
        /// <see cref="DecodeImageXObject(PdfDocument, PdfStream)"/> for the SVG writer: from
        /// <paramref name="raw"/>, the filter chain's output it has already read, under the rendering
        /// <paramref name="intent"/> its graphics state holds. <c>null</c> for <paramref name="raw"/>
        /// runs the filter chain here. The vector renderer decoded its images on its own — 1 100
        /// lines that mirrored this path and drifted from it twice; GAPS.md §6a's last row.
        /// </summary>
        internal static SKBitmap DecodeImageXObject(PdfDocument doc, PdfStream image, byte[] raw, IccRenderingIntent intent)
            => DecodeImageXObject(doc, image, raw, intent, targetWidth: 0, targetHeight: 0);

        /// <summary>
        /// <see cref="DecodeImageXObject(PdfDocument, PdfStream, byte[], IccRenderingIntent)"/> with a
        /// device target: an encoded image is decoded no larger than the raster path would decode it
        /// for an area of <paramref name="targetWidth"/> × <paramref name="targetHeight"/> pixels. Zero
        /// on either axis decodes at the image's own size.
        /// </summary>
        /// <remarks>
        /// The target is handed over as the current transform of the decode's own context, so the
        /// reduction is the raster path's exactly — <see cref="PdfRenderContext.GetImageDeviceExtent"/>
        /// reads it back and <see cref="Images.ImageDownsampling"/> decides — rather than a second rule
        /// for the SVG writer to keep in step. <paramref name="pageBudget"/> is the drawing page's budget
        /// (the SVG writer's <see cref="SvgSharedState.FormBudget"/>), so a decode here spends the page's
        /// image work as the raster path's does; null — a caller decoding one image outside any page —
        /// gives the decode a budget of its own.
        /// </remarks>
        internal static SKBitmap DecodeImageXObject(PdfDocument doc, PdfStream image, byte[] raw, IccRenderingIntent intent,
            int targetWidth, int targetHeight, RenderBudget pageBudget = null)
        {
            if (doc == null || image == null) return null;
            int w = PdfResolve.Int(doc, image, PdfName.Width,  PdfName.W, 1);
            int h = PdfResolve.Int(doc, image, PdfName.Height, PdfName.H, 1);

            using var surface = new SKBitmap(1, 1);
            using var canvas  = new SKCanvas(surface);
            using var ctx     = new PdfRenderContext(canvas, 1f, 1f, 1f);
            if (targetWidth > 0 && targetHeight > 0)
            {
                ctx.DownsampleImagesToTarget = true;
                ctx.GS.CTM = new float[] { targetWidth, 0, 0, targetHeight, 0, 0 };
            }
            using var fonts   = new FontCache();
            var interpreter = new ContentStreamInterpreter(ctx, doc, new PdfDictionary(), fonts)
                { FormBudget = pageBudget ?? new RenderBudget() };
            ctx.GS.RenderingIntent = IccRenderingIntents.Name(intent);
            ctx.GS.BlackPointCompensation = IccRenderingIntents.Compensates(intent);
            return raw == null
                ? interpreter.DecodeImageWithMasks(image, image, w, h, out _)
                : interpreter.DecodeSamplesWithMasks(raw, image, w, h, out _);
        }

        /// <summary>
        /// Applies the ICC colour transform when the image /ColorSpace is
        /// [/ICCBased &lt;stream&gt;].  Delegates to <see cref="IccTransform.Current"/>
        /// so hosts can plug in a full colour engine for prepress-grade colour.  On any error
        /// the bitmap is left untouched (silent fallback is the spec's
        /// alternate-space behaviour when no transform is available).
        /// </summary>
        private void ApplyIccTransform(SKBitmap bitmap, PdfDictionary imageDict)
        {
            if (bitmap == null || imageDict == null || _doc == null) return;
            try
            {
                var csEntry = _doc.ResolveObject(imageDict[PdfName.ColorSpace]);
                if (!(csEntry is PdfArray arr) || arr.Count < 2) return;
                if (!((arr[0] as PdfName)?.Value == PdfColorSpaceNames.ICCBased)) return;

                var iccStream = _doc.ResolveObject(arr[1]) as PdfStream;
                if (iccStream == null) return;

                byte[] profileBytes;
                try { profileBytes = iccStream.GetDecodedData(); }
                catch (Exception innerEx)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                        "ICC profile stream decode failed", innerEx);
                    return;
                }
                if (profileBytes == null || profileBytes.Length == 0) return;

                IccRenderingIntents.TransformImage(profileBytes, bitmap, ImageIntent(imageDict));
            }
            catch (Exception ex)
            {
                // Surface the failure to the diagnostics sink — pixels are left
                // untouched (alternate-space fallback is the spec's behaviour).
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    "ICC transform threw — alternate-space fallback used", ex);
            }
        }

        /// <summary>
        /// Applies the image /SMask (a separate greyscale image whose luma = alpha) to
        /// the decoded main bitmap by writing the mask luminosity into each pixel's alpha channel.
        /// </summary>
        private void ApplyImageSoftMask(SKBitmap bitmap, PdfDictionary imageDict)
        {
            var smaskObj = imageDict[PdfName.SMask];
            if (smaskObj == null) return;

            var smaskDict = _doc?.ResolveObject(smaskObj) as PdfDictionary;
            if (smaskDict == null) return;

            int sw = PdfResolve.Int(_doc, smaskDict, PdfName.Width,  PdfName.W, 0);
            int sh = PdfResolve.Int(_doc, smaskDict, PdfName.Height, PdfName.H, 0);
            if (sw == 0 || sh == 0) return;

            byte[] raw;
            // Through DecodeImageStream, not GetDecodedData: a CCITT soft mask that omits
            // /Columns must get it from the mask's own /Width, exactly as the base image
            // (RenderImageXObject) and the stencil /Mask path already do. Reading it raw
            // decoded at the 1728 fax default and produced garbage alpha.
            try
            {
                var smaskStream = smaskDict as PdfStream ?? _doc?.ResolveObject(smaskObj) as PdfStream;
                raw = smaskStream != null ? DecodeImageStream(smaskStream, sw, FormBudget?.ImageWork) : null;
            }
            catch (Exception ex)
            {
                if (FormBudget != null && FormBudget.IsImageWorkRefusal(ex)) return;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SMask stream decode failed", ex);
                return;
            }
            if (raw == null || raw.Length == 0) return;

            // SMask is always greyscale; decode as 1-component 8bpc.
            using var maskBmp = DecodeToBitmap(raw, sw, sh, smaskDict);
            if (maskBmp == null) return;

            // Scale mask to match main image if sizes differ.
            using var scaledMask = sw == bitmap.Width && sh == bitmap.Height
                ? null
                : maskBmp.Resize(new SKImageInfo(bitmap.Width, bitmap.Height),
                      new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest));
            var effectiveMask = scaledMask ?? maskBmp;

            // /Matte (PDF §11.6.5.2): when present, the base samples were pre-blended against
            // this backdrop colour (c' = m + α·(c − m)).  Recover the straight colour
            // c = m + (c' − m)/α.  Without this, anti-aliased soft-mask edges over a coloured
            // backdrop show a matte-colour halo/fringe.
            SKColor matte = default; bool hasMatte = false;
            // Resolved, elements included. Unresolved, hasMatte stays false and the un-premultiply
            // below is skipped — leaving exactly the matte-colour halo this block exists to remove.
            // SvgContentWriter has the identical read, so BOTH renderers left the fringe: agreeing,
            // and therefore invisible to the parity suite.
            if (PdfResolve.Value(_doc, smaskDict[PdfName.Matte]) is PdfArray matteArr && matteArr.Count >= 1)
            {
                int nc = matteArr.Count;
                string mcs = DeviceSpaceForComponentCount(nc);
                var comps = new float[nc];
                for (int i = 0; i < nc; i++)
                    comps[i] = PdfResolve.Value(_doc, matteArr[i]) is PdfNumber mn ? (float)mn.DoubleValue : 0f;
                matte = ComponentsToColor(comps, mcs);
                hasMatte = true;
            }

            // Write the mask luminosity into the bitmap as the alpha channel. We write a
            // STRAIGHT (non-premultiplied) colour + alpha; the SKBitmap.Pixels setter on a
            // Premul bitmap premultiplies on store, so the stored RGB ends up = colour·α
            // (correct). Writing the already-premultiplied value here would double-multiply.
            Images.BitmapPixels.Map(bitmap, effectiveMask, (p, m) =>
            {
                int a = m.Red; // greyscale: R=G=B=luma
                byte pr = p.Red, pg = p.Green, pb = p.Blue;
                if (hasMatte && a > 0)
                {
                    float an = a / MaxChannelValueF;
                    pr = ClampByte(matte.Red   + (p.Red   - matte.Red)   / an);
                    pg = ClampByte(matte.Green + (p.Green - matte.Green) / an);
                    pb = ClampByte(matte.Blue  + (p.Blue  - matte.Blue)  / an);
                }
                return new SKColor(pr, pg, pb, (byte)a);
            });
        }

        private static byte ClampByte(float v) => PdfColor.ClampChannel(v);

        /// <summary>
        /// Per-channel transfer-function LUTs (R,G,B × 256) for a <c>/TR</c> or <c>/TR2</c> entry —
        /// forwards to <see cref="TransferFunctionLut.Build"/>, the one implementation, which the
        /// SVG writer calls too. Kept as a local name so the existing call sites read unchanged.
        ///
        /// <para>It moved out of here because <b>only this renderer applied it to fill and stroke
        /// colours</b>. Both writers read <c>/TR2 ?? /TR</c> and both remapped images; a page that
        /// set an inverting transfer function and filled a red rectangle rendered cyan and exported
        /// red.</para>
        /// </summary>
        private byte[][] BuildTransferLut(PdfObject tr) => TransferFunctionLut.Build(tr, _doc);

        private byte[] BuildTransferChannelLut(PdfDictionary fn)
            => TransferFunctionLut.BuildChannel(fn, _doc);

        /// <summary>
        /// Applies the graphics state's <c>/TR</c> to a decoded image (§10.4).
        ///
        /// <para>The unpacking, the 256-entry table and the per-pixel loop all live in
        /// <see cref="TransferFunctionLut"/> now, and the SVG writer calls the same three. This
        /// method's own body was the better of the two — the SVG copy evaluated the function
        /// <i>per channel per pixel</i> and with no resolver — which is exactly why it had to move:
        /// the way to stop one renderer being better than the other is to make there be one.</para>
        /// </summary>
        /// <remarks>
        /// Applies the current graphics-state transfer function (/TR or /TR2) to every pixel
        /// of the decoded bitmap.  Each component value in [0,1] is passed through the function
        /// and the result clamped back to [0,255].
        ///
        /// The transfer function may be:
        ///   • A single PdfDictionary — applied uniformly to R, G and B.
        ///   • A PdfArray of 4 dicts — entries [R/C, G/M, B/Y, Gray/K], any entry may be
        ///     the name /Identity meaning no remapping for that component.
        ///
        /// If the GS has no transfer function this method is a no-op.
        /// </remarks>
        private void ApplyTransferFunction(SKBitmap bitmap)
            => TransferFunctionLut.ApplyToBitmap(
                   TransferFunctionLut.Build(_ctx.GS.TransferFunction, _doc), bitmap);

        /// <summary>
        /// Colour-key masking against the DECODED bitmap, for an image whose stream never held raw
        /// samples (a DCT/JPX pass-through). Only defensible at 8 bpc in a 1- or 3-component space,
        /// where the decoded channels are the source components at the same scale.
        /// </summary>
        /// <remarks>
        /// Kept only for the case it is right for. It is an approximation: a <c>/Decode</c> array is already applied to the bitmap by the time this
        /// runs, so a colour key on a <c>/Decode</c>-inverted JPEG compares post-decode values.
        /// </remarks>
        private static void ApplyColorKeyMaskFromDecodedPixels(SKBitmap bitmap, PdfArray maskArr, int comps,
                                                               PdfDocument doc)
        {
            if (!ImageSamples.TryReadColorKeyRanges(maskArr, comps, doc, out int[] lo, out int[] hi)) return;

            // DELIBERATELY ON THE PROPERTY, and the last write in this file that is. Every decode
            // path here now fills through BitmapPixels; this one must not, because the predicate
            // below matches each channel against a RANGE. StraightPixelLayout's read differs from
            // the property's on 621 of 65 536 (colour, alpha) pairs by at most one — SkiaSharp
            // un-premultiplies in float32 where the layout uses Skia's integer table — and a value
            // one off can fall the other side of a bound, masking a pixel here that the property
            // would keep. The SVG twin carries the same exclusion and states the same reason; this
            // file had no note at all, so a conversion pass reading it alone finds a bare
            // get/mutate/set with nothing saying why.
            //
            // It is invisible to BufferWalks/PropertyFallbacks too, because it bypasses
            // BitmapPixels entirely: an accidental conversion would not even register as a fallback.
            var pixels = bitmap.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                SKColor c = pixels[i];
                bool masked = comps == PdfColorSpaceNames.GrayComponents
                    ? c.Red >= lo[0] && c.Red <= hi[0]
                    : c.Red   >= lo[0] && c.Red   <= hi[0]
                   && c.Green >= lo[1] && c.Green <= hi[1]
                   && c.Blue  >= lo[2] && c.Blue  <= hi[2];

                if (masked) pixels[i] = new SKColor(0, 0, 0, 0);
            }
            bitmap.Pixels = pixels;
        }

        // TFByte — the per-pixel transfer-function evaluator — was deleted here, deliberately
        // rather than left unused. It was a *second* implementation of what
        // BuildTransferChannelLut already does, and it differed: it called the evaluator's
        // no-resolver overload, so a Type 3 stitching function with indirect sub-functions
        // silently degraded. Its own comment said that was fine because such functions are rare.
        // ApplyTransferFunction now builds the same 256-entry table the fill/stroke path uses,
        // which is both faster and the correct one. Leaving the dead copy in place would invite
        // exactly the divergence this repo keeps finding.

        /// <summary>
        /// Applies color-key masking: when the image /Mask entry is a PdfArray of the form
        /// [min0 max0 min1 max1 ...], any pixel whose (undecoded) component values all fall
        /// within the specified ranges is made fully transparent (alpha = 0).
        ///
        /// PDF spec §8.9.6.1: "colour key masking".
        /// </summary>
        private static void ApplyColorKeyMask(SKBitmap bitmap, byte[] raw, int w, int h,
                                              PdfDictionary imageDict, PdfDocument doc)
        {
            var maskObj = imageDict[PdfName.Mask];
            if (maskObj == null) return;
            if (doc != null) maskObj = doc.ResolveObject(maskObj) ?? maskObj;

            // /Mask as a stream means a stencil (1-bit image mask) — handled elsewhere.
            // /Mask as a PdfName (e.g. /None) — nothing to do.
            if (maskObj is not PdfArray maskArr || maskArr.Count < 2) return;

            bool[] masked = ImageSamples.TryComputeColorKeyMask(raw, w, h, maskArr, imageDict, doc);

            if (masked == null)
            {
                // The raw path did not apply. Fall back to the decoded bitmap for the one shape
                // where that is still meaningful: 8-bit samples whose decoded channels ARE the
                // source components (an RGB or grey JPEG, whose stream stays compressed).
                int fbBpc   = PdfResolve.Int(doc, imageDict, PdfName.BitsPerComponent, PdfName.BPC, 8);
                int fbComps = ImageSamples.ComponentCount(imageDict, doc);
                if (fbBpc == 8 && (fbComps == PdfColorSpaceNames.GrayComponents
                                   || fbComps == PdfColorSpaceNames.RgbComponents)
                    && maskArr.Count / 2 >= fbComps)
                    ApplyColorKeyMaskFromDecodedPixels(bitmap, maskArr, fbComps, doc);
                return;
            }

            // Written through the bitmap's buffer rather than SKBitmap.Pixels, which allocates a
            // managed SKColor[] over the whole image and copies both ways. Two things make this
            // one simpler than the soft-mask conversion in ContentStreamInterpreter.GraphicsState:
            // the loop never READS a pixel, so the premultiplied-versus-straight question that
            // governs that one does not arise; and the value it writes — premultiplied
            // transparent — is four zero bytes in Rgba8888 and Bgra8888 alike, so there is no
            // channel order to get wrong either.
            //
            // The getter runs below the `masked == null` branch, so an image taking the
            // decoded-pixel fallback allocates no full-size copy of itself.
            // Dimensions rather than `pixels.Length < w * h`, which would admit a
            // bitmap WIDER than w, and a flat pixel index cannot address such a bitmap correctly:
            // `masked` is indexed x + y*w while Pixels[i] is (i % bitmap.Width, i / bitmap.Width),
            // so the two only ever agreed when the widths matched. Declining is the safer of the
            // two wrong answers, and it is unreachable in any case — BuildBitmap constructs this
            // bitmap from the very w and h being compared.
            if (bitmap.Width != w || bitmap.Height != h) return;
            var span = bitmap.GetPixelSpan();
            int stride = bitmap.RowBytes;
            if (span.Length < (long)h * stride || stride < w * 4) return;

            int p = 0;
            for (int y = 0; y < h; y++)
            {
                var row = span.Slice(y * stride, w * 4);
                for (int x = 0; x < w; x++, p++)
                {
                    if (p >= masked.Length) break;
                    if (!masked[p]) continue;
                    int o = x * 4;
                    row[o] = row[o + 1] = row[o + 2] = row[o + 3] = 0;
                }
            }
            bitmap.NotifyPixelsChanged();
        }

        /// <summary>
        /// Applies an explicit stencil <c>/Mask</c> image (PDF §8.9.6.3): when the image's
        /// <c>/Mask</c> entry is a separate 1-bit image stream, its sample value 1 marks
        /// base-image points to be MASKED OUT (transparent); value 0 paints. The mask's own
        /// <c>/Decode [1 0]</c> reverses this. The mask is nearest-mapped to the base image
        /// size and written into the alpha channel. (Color-key array <c>/Mask</c> is handled
        /// by <see cref="ApplyColorKeyMask"/>; this method ignores that case.)
        /// </summary>
        private void ApplyStencilMask(SKBitmap bitmap, PdfDictionary imageDict)
        {
            if (bitmap == null) return;
            var maskObj = _doc?.ResolveObject(imageDict[PdfName.Mask]);
            if (maskObj is not PdfStream maskStream) return;

            int mw = PdfResolve.Int(_doc, maskStream, PdfName.Width,  PdfName.W, 0);
            int mh = PdfResolve.Int(_doc, maskStream, PdfName.Height, PdfName.H, 0);
            if (mw <= 0 || mh <= 0) return;
            // One predicate for both renderers — the SVG twin had no cap at all, so the same file
            // drew opaque here and fully transparent there. Reported now as well as refused: a
            // silent refusal is indistinguishable from a mask the file never declared.
            if (!ImageStencil.MaskGeometryWithinLimits(mw, mh))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"Stencil /Mask geometry {mw}x{mh} exceeds the image limits; the mask was not applied");
                return;
            }

            byte[] data;
            try { data = DecodeImageStream(maskStream, mw, FormBudget?.ImageWork); }   // preserves CCITT /Columns augmentation
            catch (Exception ex)
            {
                if (FormBudget != null && FormBudget.IsImageWorkRefusal(ex)) return;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed, "Stencil /Mask decode failed", ex);
                return;
            }
            if (data == null || data.Length == 0) return;

            // Default stencil /Decode is [0 1]: sample 1 = masked (transparent). [1 0] reverses.
            bool invert = ImageStencil.DecodeInverts(maskStream, _doc);

            int stride = (mw + 7) / 8;        // 1 bpc, MSB-first
            int w = bitmap.Width, h = bitmap.Height;

            // Cleared straight in the bitmap's buffer. This mask WRITES; it never reads a pixel,
            // so the SKColor[] the property route allocated existed only to be copied back --
            // every pixel of the image converted twice to clear the ones the mask names.
            Images.BitmapPixels.Clear(bitmap, (x, y) =>
            {
                int my = (int)((long)y * mh / h);
                int mx = (int)((long)x * mw / w);
                int byteIdx = (my * stride) + (mx >> 3);
                int bit = byteIdx < data.Length ? (data[byteIdx] >> (7 - (mx & 7))) & 1 : 0;
                return invert ? bit == 0 : bit == 1;
            });
        }

        /// <summary>
        /// Returns a copy of <paramref name="bitmap"/> with <see cref="SKAlphaType.Premul"/>
        /// when the image has an /SMask or colour-key /Mask and the bitmap was decoded as
        /// <see cref="SKAlphaType.Opaque"/> (which causes Skia to ignore stored alpha bytes).
        /// Returns the original bitmap unchanged when no transparency is pending.
        /// The caller must dispose the original if a new bitmap is returned.
        /// </summary>
        private SKBitmap EnsurePremulForTransparency(SKBitmap bitmap, PdfDictionary imageDict)
        {
            if (bitmap.AlphaType != SKAlphaType.Opaque) return bitmap;
            bool hasSMask    = imageDict[PdfName.SMask] != null;
            var maskObj      = _doc?.ResolveObject(imageDict[PdfName.Mask]) ?? imageDict[PdfName.Mask];
            bool hasColorKey = maskObj is PdfArray;
            bool hasStencil  = maskObj is PdfStream;   // separate 1-bit stencil mask image
            if (!hasSMask && !hasColorKey && !hasStencil) return bitmap;
            // Copy to a Premul surface.  All source pixels have alpha=255 (Opaque),
            // so premultiplied and straight representations are identical here.
            var premul = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var c = new SKCanvas(premul);
            c.Clear(SKColors.Transparent);
            c.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
            return premul;
        }

        /// <summary>
        /// Whether the image's own <c>/Filter</c> chain names <c>JPXDecode</c> — one of the two
        /// ways an image reaches <see cref="JpxImageDecoder"/>, beside the sniffed framing.
        /// </summary>
        /// <remarks>
        /// Asked through the same enumeration the plugin dispatch uses, so the two cannot disagree
        /// about what the chain says — including for an indirect <c>/Filter</c>, which is why that
        /// enumeration resolves.
        /// </remarks>
        internal static bool DeclaresJpxFilter(PdfDictionary imageDict, PdfDocument doc)
        {
            if (imageDict == null) return false;
            foreach (string name in Images.PluginImageDecoder.GetImageFilterNames(imageDict, doc))
                if (string.Equals(name, JpxImageDecoder.FilterName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// Asks a registered <see cref="Core.Plugins.IImageDecodePlugin"/> to decode
        /// <paramref name="data"/>, dispatching on the image's declared <c>/Filter</c> name(s).
        /// Returns <c>null</c> when no plugin handles the filter, the plugin declines, or the
        /// plugin fails — in every case the caller continues with the built-in decoders.
        /// </summary>
        private SKBitmap TryDecodeViaPlugin(byte[] data, PdfDictionary imageDict)
        {
            var bmp = PluginImageDecoder.DecodeToBitmap(data, imageDict, _doc, SKAlphaType.Premul,
                out ImageSampleLayout layout, CurrentIntent);
            if (bmp == null) return null;

            // The /Decode array's entries describe the source samples; for CMYK they would be
            // applied to the already-converted RGB result, so they are skipped there.
            if (!layout.IsCmyk)
                ApplyDecodeArrayToBitmap(bmp, GetDecodeArray(imageDict));
            return bmp;
        }

        private SKBitmap DecodeToBitmap(byte[] data, int w, int h, PdfDictionary imageDict) =>
            DecodeToBitmap(data, w, h, imageDict, out _);

        /// <summary>
        /// The samples an image was painted from, as the raw sample path read them — the one
        /// description a later pass takes its geometry, depth and colour-key values from
        /// (§8.9.6.4 compares raw samples), and what the overprint arm composites.
        /// </summary>
        internal readonly struct RawImageSamples
        {
            internal RawImageSamples(byte[] samples, PdfDictionary dictionary, int width, int height, bool areSamples, byte[] opacity)
            {
                Samples = samples;
                Dictionary = dictionary;
                Width = width;
                Height = height;
                AreSamples = areSamples;
                Opacity = opacity;
            }

            /// <summary>The filter output as given, or a JPEG 2000 image's decoded 8-bit samples when its <c>/ColorSpace</c> governs; decimated with the bitmap when the render downsamples.</summary>
            internal byte[] Samples { get; }

            /// <summary>The dictionary describing <see cref="Samples"/>: the image's, or one whose <c>/BitsPerComponent</c> and geometry say what the decoded samples are.</summary>
            internal PdfDictionary Dictionary { get; }

            /// <summary>The samples' geometry — the bitmap's, not the XObject's <c>/Width</c> and <c>/Height</c> when a reduced decode or a decimation shrank it.</summary>
            internal int Width { get; }
            internal int Height { get; }

            /// <summary>
            /// True when <see cref="Samples"/> are samples the raw path strode by the dictionary;
            /// false when a codec (JPEG, PNG, a JPEG 2000 file's own colour, a plugin) painted the
            /// bitmap and the bytes are still its codestream — which no pass may read as samples.
            /// </summary>
            internal bool AreSamples { get; }

            /// <summary>A JPEG 2000 image's straight opacity channel the raw path could not carry (<see cref="JpxDecodeOutcome.RawOpacity"/>), to composite after the paint; else null.</summary>
            internal byte[] Opacity { get; }
        }

        /// <summary>
        /// The image decoded through every codec this renderer has, in a fixed order: JPEG 2000
        /// (the in-house decoder, then a registered plugin), any other registered plugin, Skia's
        /// JPEG and PNG codecs, then the raw sample path. <paramref name="key"/> is what the raw
        /// path read — the filter output and the dictionary as given, or, for a JPEG 2000 image
        /// whose <c>/ColorSpace</c> the dictionary defines, the decoded 8-bit samples and a
        /// dictionary describing them, at the geometry they were painted at — so a caller
        /// evaluating the colour key (§8.9.6.4, on raw samples) sees what was drawn. A codec's
        /// bitmap leaves <see cref="RawImageSamples.AreSamples"/> false.
        /// </summary>
        private SKBitmap DecodeToBitmap(byte[] data, int w, int h, PdfDictionary imageDict,
            out RawImageSamples key)
        {
            key = new RawImageSamples(data, imageDict, w, h, areSamples: false, opacity: null);

            // Device extent this image will occupy, for the encoded-image decoders below.
            // Zero disables reduction, which is what every path gets when the option is off.
            int tw = 0, th = 0;
            if (_ctx.DownsampleImagesToTarget) _ctx.GetImageDeviceExtent(out tw, out th);

            // Reject absurd image dimensions before any per-pixel allocation or codec
            // decode.  A crafted /Width /Height (or a decode bomb) would otherwise force a
            // multi-GB SKColor[] and SKBitmap, pinning a core or OOM-ing the host.  Mirrors
            // the page renderer's clamp and PdfOptimizer's image area cap.
            if (w <= 0 || h <= 0) return null;
            if (w > MaxImageDimension || h > MaxImageDimension || (long)w * h > MaxImagePixelArea)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    $"Image {w}x{h} exceeds the render size limit " +
                    $"({MaxImageDimension} px/axis, {MaxImagePixelArea / 1_000_000} MP area); image skipped.");
                return null;
            }

            // JPEG 2000 first. JpxImageDecoder owns the order — the in-house decoder, then a
            // registered plugin — so it runs before the generic plugin dispatch, which skips
            // /JPXDecode for the same reason: a plugin is never asked ahead of the decoder, or
            // twice. Both the declared filter and the sniffed framing (the JP2 signature box or
            // the SOC marker) reach it: an inline image or a chain this reader cannot name still
            // gets decoded, and a declared JPXDecode whose bytes are not a codestream still reaches
            // the plugin, which is the case the sniff alone would miss.
            bool rawFromJpx = false;
            byte[] jpxOpacity = null;
            if (JpxHeader.IsJpx(data) || DeclaresJpxFilter(imageDict, _doc))
            {
                SKBitmap jpxBitmap;
                JpxDecodeOutcome outcome;
                try
                {
                    jpxBitmap = JpxImageDecoder.DecodeToBitmap(data, imageDict, _doc, SKAlphaType.Premul,
                        out outcome, CurrentIntent, tw, th, _ctx.JpxDecodeThreads, FormBudget?.ImageWork);
                }
                catch (Core.Util.WorkBudgetExceededException ex) when (FormBudget != null && FormBudget.IsImageWorkRefusal(ex))
                {
                    // The page's image decoding is spent: reported once by the budget, nothing drawn.
                    return null;
                }
                if (jpxBitmap != null)
                {
                    if (outcome.DecodeArrayApplies)
                        ApplyDecodeArrayToBitmap(jpxBitmap, GetDecodeArray(imageDict));
                    return jpxBitmap;
                }
                if (outcome.RawSamples != null)
                {
                    // The dictionary's own colour space governs (ISO 32000-2 §7.4.9): the decoded
                    // samples go down the raw path below like any other image's, which is what
                    // applies an Indexed palette, a tint transform, /Decode and the colour key to
                    // them. Nothing below sniffs them as JPEG or PNG: the plugin and codec block is
                    // skipped, because samples that happen to open with a codec's signature are
                    // still samples.
                    data = outcome.RawSamples;
                    imageDict = outcome.RawDictionary;
                    // A reduced decode's samples are smaller than the XObject's /Width x /Height;
                    // the dictionary handed over describes the samples (their size as their depth),
                    // and the geometry the raw branches stride by is read from it.
                    w = PdfResolve.Int(_doc, imageDict, PdfName.Width, PdfName.W, w);
                    h = PdfResolve.Int(_doc, imageDict, PdfName.Height, PdfName.H, h);
                    jpxOpacity = outcome.RawOpacity;
                    rawFromJpx = true;
                }
                else
                {
                    // The in-house decoder and the plugin both declined, and the door reported it
                    // (ImageDecodeFailed). Nothing is drawn: the bytes are a codestream nothing
                    // could read, and reading them as raw samples would paint noise in the image's
                    // place.
                    return null;
                }
            }

            if (!rawFromJpx)
            {
                // Give a registered IImageDecodePlugin first refusal, keyed on the image's DECLARED
                // PDF filter name(s) rather than on sniffed magic bytes. This is what makes the
                // interface's documented purpose — "add support for currently unsupported filters
                // such as JBIG2Decode" — reachable, so a plugin for any filter is consulted.
                var pluginBitmap = TryDecodeViaPlugin(data, imageDict);
                if (pluginBitmap != null) return pluginBitmap;

                // A JPEG whose colour space is not the one its codec assumes (an ICC, spot, Lab or
                // CIE space) is coloured from its samples, down the raw path below like any other
                // filter's output — the codec's bitmap would carry the codec's colours, not the
                // image's (JpegSamples). Nothing below sniffs the samples as a JPEG or PNG again.
                bool rawFromJpeg = false;
                // On a separation plate every JPEG goes this way when it can: the plate reads the
                // samples themselves — a DeviceCMYK JPEG's inks, a DeviceGray one's black.
                if (RasterImageInspector.IsJpeg(data)
                    && (_ctx.Plate != null || !JpegSamples.CodecColoursAreTheImages(imageDict, _doc, data)))
                {
                    byte[] samples = JpegSamples.TryDecode(data, MaxImageDimension, MaxImagePixelArea,
                        out int jw, out int jh, out int jn);
                    if (samples != null)
                    {
                        data = samples;
                        // The geometry the raw branches stride by is the dictionary's, which now
                        // says the frame's size — as the JPEG 2000 route reads its own back.
                        imageDict = JpegSamples.Describing(imageDict, jw, jh, jn);
                        w = PdfResolve.Int(_doc, imageDict, PdfName.Width, PdfName.W, w);
                        h = PdfResolve.Int(_doc, imageDict, PdfName.Height, PdfName.H, h);
                        rawFromJpeg = true;
                    }
                }

                // Check for JPEG / PNG / raw pixel data.
                // If we can create an SKBitmap via SKBitmap.Decode, try that first.
                if (!rawFromJpeg && RasterImageInspector.IsJpeg(data))
                {
                    // JPEG — decode then apply /Decode value remapping if non-identity.
                    // Exception: CMYK JPEG images (DeviceCMYK / ICCBased N=4) use the Adobe
                    // CMYK convention where values are stored inverted.  SkiaSharp's JPEG
                    // decoder detects the APP14 "Adobe" marker and handles the inversion
                    // internally before converting to RGB.  The PDF /Decode [1 0 1 0 1 0 1 0]
                    // array describes those raw CMYK-component inversions — applying it to the
                    // already-converted RGB output would invert the channels a second time and
                    // produce wrong colours (inverted image).
                    var bmp = SafeDecodeEncodedImage(data, tw, th);
                    if (bmp == null) return null;
                    if (GetColorComponents(imageDict) != 4)
                        ApplyDecodeArrayToBitmap(bmp, GetDecodeArray(imageDict));
                    return bmp;
                }
                if (!rawFromJpeg && RasterImageInspector.IsPng(data))
                {
                    var bmp = SafeDecodeEncodedImage(data, tw, th);
                    if (bmp == null) return null;
                    ApplyDecodeArrayToBitmap(bmp, GetDecodeArray(imageDict));
                    return bmp;
                }
            }

            // Parse /Decode array for value remapping.
            // Default: [0 1] per component. /Decode [1 0] inverts a grayscale image.
            float[] decodeArray = GetDecodeArray(imageDict);

            // Check /ImageMask — 1-bit stencil masks use fill color, not black/white.
            bool isImageMask = ImageStencil.IsStencilMask(imageDict, _doc);

            // Handle Indexed (palette-mapped) color space.
            var csEntry = imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS];
            if (_doc != null) csEntry = _doc.ResolveObject(csEntry) ?? csEntry;

            // Every raw branch below allocates SKColor[w*h], so reduce the samples once here rather
            // than in each of them. An /Indexed image stores one index per pixel whatever its base
            // space says, so its component count is 1 — asking GetColorComponents would give the
            // base space's arity and stride the buffer wrongly.
            // `/I` as well as `/Indexed`: §8.9.7 Table 93's abbreviation is legal at any depth of
            // an inline image's colour space, and ExpandAbbreviatedImageDict expands only the
            // TOP-LEVEL /CS name — an array head is left as written. This test read /Indexed alone,
            // so `BI /CS [/I /RGB 255 <…>]` was not recognised as indexed at all and its samples
            // were strided as three components per pixel instead of one palette index. The SVG
            // writer accepted both spellings.
            bool indexed = csEntry is PdfArray idxProbe && idxProbe.Count >= 1
                           && IsIndexedFamily((idxProbe[0] as PdfName)?.Value);
            // A JPEG 2000 image's reduced decode has already served the target, and its opacity
            // plane is at the decoded geometry: not decimated again.
            if (tw > 0 && th > 0 && !isImageMask && !rawFromJpx)
            {
                TryDecimateRawSamples(ref data, ref w, ref h,
                    indexed ? 1 : GetColorComponents(imageDict),
                    GetBitsPerComponent(imageDict), tw, th);
            }
            // What the raw branches below stride: the samples, their dictionary and their
            // geometry after every reduction, which is what a later pass must read.
            key = new RawImageSamples(data, imageDict, w, h, areSamples: true, opacity: jpxOpacity);
            if (csEntry is PdfArray csArrIdx && csArrIdx.Count >= 1
                && IsIndexedFamily((csArrIdx[0] as PdfName)?.Value))
            {
                int idxBpc = GetBitsPerComponent(imageDict);
                return DecodeIndexedBitmap(data, w, h, csArrIdx, idxBpc, decodeArray);
            }

            // Try raw pixel bytes.
            int colorComponents = GetColorComponents(imageDict);
            int bitsPerComp     = GetBitsPerComponent(imageDict);

            // Colour-space-aware decode for Separation/DeviceN (tint transform), Lab,
            // CalGray (gamma) and CalRGB images.  The generic device paths below would
            // mis-render these (a spot plate renders gray; a Lab image renders garbled).
            //
            // Offered at EVERY legal bit depth: gated on `bitsPerComp == 8`, the identical colour
            // space at 1, 2, 4 or 16 bpc would fall through to a device path and silently lose its
            // tint transform, /Gamma or white point — the same file rendering
            // two different ways depending only on its sample width.
            if (csEntry is PdfArray csSpecial && csSpecial.Count >= 1
                && (csSpecial[0] as PdfName)?.Value is string csKind)
            {
                var special = TryDecodeSpecialColorSpaceImage(
                    data, w, h, csKind, csSpecial, decodeArray, colorComponents, bitsPerComp,
                    ImageIntent(imageDict));
                if (special != null) return special;
            }

            if (colorComponents == 3 && bitsPerComp == 8 && data.Length >= w * h * 3)
            {
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                BitmapPixels.Fill(bmp, (row, y) =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = ((y * w) + x) * 3;
                        byte r = ApplyDecode(data[o],     decodeArray, 0, MaxChannelValue);
                        byte g = ApplyDecode(data[o + 1], decodeArray, 1, MaxChannelValue);
                        byte b = ApplyDecode(data[o + 2], decodeArray, 2, MaxChannelValue);
                        row[x] = new SKColor(r, g, b);
                    }
                });
                return bmp;
            }

            if (colorComponents == 1 && bitsPerComp == 8 && data.Length >= w * h)
            {
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                BitmapPixels.Fill(bmp, (row, y) =>
                {
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = ApplyDecode(data[o + x], decodeArray, 0, MaxChannelValue);
                        row[x] = new SKColor(v, v, v);
                    }
                });
                return bmp;
            }

            // 16-bit per component (BPC 16) RGB / Gray paths — PDF spec §8.9.5.1 lists 1,2,4,8,16
            // as valid bit depths. Down-convert to 8-bit with correct `(v*255 + 32767) / 65535`
            // rounding.
            if (colorComponents == 3 && bitsPerComp == 16 && data.Length >= w * h * 6)
            {
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                BitmapPixels.Fill(bmp, (row, y) =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = ((y * w) + x) * 6;
                        int r16 = (data[o] << 8) | data[o + 1];
                        int g16 = (data[o + 2] << 8) | data[o + 3];
                        int b16 = (data[o + 4] << 8) | data[o + 5];
                        // Down-convert 16→8, then apply /Decode (the array maps the normalised
                        // sample regardless of bit depth, so remapping the 8-bit value is equivalent).
                        row[x] = new SKColor(
                            ApplyDecode(Scale16To8(r16), decodeArray, 0, MaxChannelValue),
                            ApplyDecode(Scale16To8(g16), decodeArray, 1, MaxChannelValue),
                            ApplyDecode(Scale16To8(b16), decodeArray, 2, MaxChannelValue));
                    }
                });
                return bmp;
            }
            if (colorComponents == 1 && bitsPerComp == 16 && data.Length >= w * h * 2)
            {
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                BitmapPixels.Fill(bmp, (row, y) =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = ((y * w) + x) * 2;
                        int v16 = (data[o] << 8) | data[o + 1];
                        byte v = ApplyDecode(Scale16To8(v16), decodeArray, 0, MaxChannelValue);
                        row[x] = new SKColor(v, v, v);
                    }
                });
                return bmp;
            }

            if (colorComponents == 4 && bitsPerComp == 8 && data.Length >= w * h * 4)
            {
                // Resolved once for the whole image; null means "no evaluable 4-component ICC
                // profile", and then every pixel below is byte-identical to what it was.
                var icc = IccColorTransform.IccImageConverter.TryCreate(_doc, imageDict, 4,
                                                                        CurrentIntent);
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                BitmapPixels.Fill(bmp, (row, y) =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = ((y * w) + x) * 4;
                        float c  = ApplyDecodeF(data[o],     decodeArray, 0);
                        float m  = ApplyDecodeF(data[o + 1], decodeArray, 1);
                        float yc = ApplyDecodeF(data[o + 2], decodeArray, 2);
                        float k  = ApplyDecodeF(data[o + 3], decodeArray, 3);
                        row[x] = icc != null && icc.TryConvert(c, m, yc, k, out SKColor managed)
                            ? managed
                            : PdfColor.CmykToRgb(c, m, yc, k);
                    }
                });
                return bmp;
            }

            // DeviceN / multi-ink with >4 components.  When the image dict carries
            // a [/DeviceN names base tintTransform] colour space we evaluate the
            // tint transform per pixel and convert through the alternate space —
            // colour-correct.  When no transform is recoverable we fall back to
            // grayscale-averaging (and report it via diagnostics).
            if (colorComponents > 4 && bitsPerComp == 8 && data.Length >= (long)w * h * colorComponents)
            {
                int nc = colorComponents;
                var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);

                PdfDictionary tintFn = null;
                string altCs = PdfColorSpaceNames.DeviceRGB;
                var csForN = imageDict[PdfName.ColorSpace] ?? imageDict[PdfName.CS];
                if (_doc != null) csForN = _doc.ResolveObject(csForN) ?? csForN;
                if (csForN is PdfArray nArr && nArr.Count >= 4 &&
                    (nArr[0] as PdfName)?.Value == PdfColorSpaceNames.DeviceN)
                {
                    altCs   = (nArr[2] as PdfName)?.Value ?? PdfColorSpaceNames.DeviceRGB;
                    tintFn  = _doc?.ResolveObject(nArr[3]) as PdfDictionary;
                }

                if (tintFn != null)
                {
                    float[] tints = new float[nc];
                    // The abort is carried out of the fill rather than thrown through it. A
                    // `return null` cannot cross a delegate boundary, and swallowing the budget
                    // exception inside the producer would silently paint a half-decoded image —
                    // which is the opposite of what this catch means. The flag stops
                    // every later row too, so the transform is abandoned exactly where it was.
                    Exception aborted = null;
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        if (aborted != null) return;
                        try
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int o = ((y * w) + x) * nc;
                                for (int c = 0; c < nc; c++)
                                    tints[c] = ApplyDecodeF(data[o + c], decodeArray, c);
                                // _doc is non-null here: tintFn was resolved through it above, so a
                                // null _doc would have left tintFn null and skipped this branch.
                                float[] altVals = PdfFunctionEvaluator.Evaluate(tintFn, tints, _doc.ResolveObject);
                                // Straight to PdfColor: a PdfArray and one PdfNumber per component
                                // per pixel, only for ArrayToColor to unwrap back into the float[]
                                // Evaluate returned, would be ~28 million allocations on a
                                // 2000x2000 six-ink image. A null from Evaluate is guarded here.
                                row[x] = altVals != null
                                    ? PdfColor.ComponentsToColor(altVals, altCs)
                                    : SKColors.Black;
                            }
                        }
                        catch (Exception ex) when (ex is WorkBudgetExceededException || ex is InvalidOperationException)
                        {
                            aborted = ex;
                        }
                    });
                    if (aborted != null)
                    {
                        // Adversarial Type 4 tint transform: skip the whole image rather than
                        // propagate (and abort the page) or re-pay the budget for every pixel.
                        RenderDiagnostics.Report(RenderDiagnosticKind.DeviceNFallback,
                            "DeviceN tint transform aborted (work/recursion budget); image skipped", aborted);
                        bmp.Dispose();   // the array route leaked this to the finaliser
                        return null;
                    }
                }
                else
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.DeviceNFallback,
                        $"DeviceN image with {nc} components has no tint transform; using grayscale-average fallback.");
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int sum = 0;
                            int o = ((y * w) + x) * nc;
                            for (int c = 0; c < nc; c++) sum += data[o + c];
                            byte v = (byte)(MaxChannelValue - (sum / nc)); // invert: ink → darkness
                            row[x] = new SKColor(v, v, v);
                        }
                    });
                }
                return bmp;
            }

            // 1-bit bilevel (common in CCITT-decoded scanned pages).
            //
            // The guard is `>=`, and it must stay `>=`: an /EncodedByteAlign CCITT stream pads
            // every row out to a byte boundary and routinely leaves trailing bytes past the last
            // one, and this branch is the ONLY path a padded /ImageMask can take — see
            // GetColorComponents' remarks for how that was discovered. Tightening it to `==`
            // makes every such stencil vanish from the page.
            if (colorComponents == 1 && bitsPerComp == 1)
            {
                int stride = (w + 7) / 8;
                if (data.Length >= stride * h)
                    return DecodePackedBilevel(data, w, h, stride, decodeArray, isImageMask);
            }

            // 2-bit grayscale
            if (colorComponents == 1 && bitsPerComp == 2)
            {
                int stride = (w * 2 + 7) / 8;
                if (data.Length >= stride * h)
                {
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int bitOffset = y * stride * 8 + x * 2;
                            int byteIdx = bitOffset / 8;
                            int shift = 6 - (bitOffset % 8);
                            byte raw = (byte)((data[byteIdx] >> shift) & 0x03);
                            byte v = (byte)(raw * 85); // 0→0, 1→85, 2→170, 3→255
                            v = ApplyDecode(v, decodeArray, 0, MaxChannelValue);
                            row[x] = new SKColor(v, v, v);
                        }
                    });
                    return bmp;
                }
            }

            // 4-bit grayscale
            if (colorComponents == 1 && bitsPerComp == 4)
            {
                int stride = (w * 4 + 7) / 8;
                if (data.Length >= stride * h)
                {
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int bitOffset = y * stride * 8 + x * 4;
                            int byteIdx = bitOffset / 8;
                            int shift = 4 - (bitOffset % 8);
                            byte raw = (byte)((data[byteIdx] >> shift) & 0x0F);
                            byte v = (byte)(raw * 17); // 0→0, 15→255
                            v = ApplyDecode(v, decodeArray, 0, MaxChannelValue);
                            row[x] = new SKColor(v, v, v);
                        }
                    });
                    return bmp;
                }
            }

            // 4-bit RGB (rare but possible)
            if (colorComponents == 3 && bitsPerComp == 4)
            {
                int bitsPerRow = w * 3 * 4;
                int stride = (bitsPerRow + 7) / 8;
                if (data.Length >= stride * h)
                {
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int pixBit = y * stride * 8 + x * 12;
                            byte r = (byte)(ReadNibble(data, pixBit)     * 17);
                            byte g = (byte)(ReadNibble(data, pixBit + 4) * 17);
                            byte b = (byte)(ReadNibble(data, pixBit + 8) * 17);
                            r = ApplyDecode(r, decodeArray, 0, MaxChannelValue);
                            g = ApplyDecode(g, decodeArray, 1, MaxChannelValue);
                            b = ApplyDecode(b, decodeArray, 2, MaxChannelValue);
                            row[x] = new SKColor(r, g, b);
                        }
                    });
                    return bmp;
                }
            }

            // 16-bit per component images (professional/high-depth PDFs)
            if (bitsPerComp == 16)
            {
                int bytesPerPixel = colorComponents * 2;
                if (data.Length >= (long)w * h * bytesPerPixel)
                {
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    // Hoisted out of the loop for the same reason as the 8-bpc site: one profile
                    // resolution per image, not per sample. Null leaves every pixel exactly as it
                    // was.
                    var icc16 = colorComponents == 4
                        ? IccColorTransform.IccImageConverter.TryCreate(_doc, imageDict, 4,
                                                                        CurrentIntent)
                        : null;
                    // /Decode applies at every bit depth (§8.9.5.2 Table 89), or a CMYK 16-bpc image
                    // with the inverting /Decode [1 0 1 0 1 0 1 0] that Adobe-marked CMYK JPEGs carry
                    // would render as its own negative. Same helper for all three arities here, so
                    // they cannot drift apart.
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int o = ((y * w) + x) * bytesPerPixel;
                            if (colorComponents == 3)
                            {
                                byte r = ApplyDecode(Scale16To8((data[o]     << 8) | data[o + 1]), decodeArray, 0, MaxChannelValue);
                                byte g = ApplyDecode(Scale16To8((data[o + 2] << 8) | data[o + 3]), decodeArray, 1, MaxChannelValue);
                                byte b = ApplyDecode(Scale16To8((data[o + 4] << 8) | data[o + 5]), decodeArray, 2, MaxChannelValue);
                                row[x] = new SKColor(r, g, b);
                            }
                            else if (colorComponents == 1)
                            {
                                byte v = ApplyDecode(Scale16To8((data[o] << 8) | data[o + 1]), decodeArray, 0, MaxChannelValue);
                                row[x] = new SKColor(v, v, v);
                            }
                            else if (colorComponents == 4)
                            {
                                float c  = ApplyDecodeF01(((data[o]     << 8) | data[o + 1]) / MaxChannelValue16F, decodeArray, 0);
                                float m  = ApplyDecodeF01(((data[o + 2] << 8) | data[o + 3]) / MaxChannelValue16F, decodeArray, 1);
                                float yc = ApplyDecodeF01(((data[o + 4] << 8) | data[o + 5]) / MaxChannelValue16F, decodeArray, 2);
                                float k  = ApplyDecodeF01(((data[o + 6] << 8) | data[o + 7]) / MaxChannelValue16F, decodeArray, 3);
                                row[x] = icc16 != null && icc16.TryConvert(c, m, yc, k, out SKColor managed16)
                                    ? managed16
                                    : PdfColor.CmykToRgb(c, m, yc, k);
                            }
                        }
                    });
                    return bmp;
                }
            }

            // Fallback: packed 1-bit bilevel.  CCITTFaxDecode output is packed 1-bpp,
            // MSB-first, with row stride = ceil(w/8) bytes.  PDF image dicts for CCITT
            // images often omit /ColorSpace and /BitsPerComponent (both are implied by
            // the filter), so the explicit bitsPerComp==1 path above won't fire.
            // Detect it by checking whether the decoded length exactly matches the
            // packed-bit stride.  This must come last so it doesn't shadow any
            // explicit-format path that also satisfies the length condition.
            //
            // The guard is `==`, and unlike the branch above it must stay exact. This one is a
            // GUESS about samples whose format the file never stated, so it may only claim a
            // buffer that is exactly the size of the guess. Relaxing it to `>=` — the tempting
            // move once both bodies became one helper — would hand it every short buffer the
            // 2/4/16-bpc branches above declined, painting a truncated greyscale image as
            // bilevel noise instead of refusing it with the diagnostic below.
            {
                int stride1 = (w + 7) / 8;
                if (h > 0 && stride1 > 0 && data.Length == stride1 * h)
                    return DecodePackedBilevel(data, w, h, stride1, decodeArray, isImageMask);
            }

            // Nothing above could interpret the samples. The caller returns without drawing, so
            // say why: the alternative is an image that is simply absent from the page with no
            // trace, which is how several decode gaps stayed invisible.
            //
            // The most common cause is a filter that failed and passed its INPUT through — both
            // CcittFaxDecodeFilter and Jbig2DecodeFilter return the raw compressed bytes on error
            // — so name that possibility rather than only the shape of the data.
            RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                $"Image {w}x{h} ({bitsPerComp} bpc) could not be decoded to pixels: " +
                $"{data?.Length ?? 0} sample byte(s) do not match any supported component layout" +
                DescribeFilterFallback(imageDict) + "; image skipped.");
            return null;
        }

        /// <summary>
        /// Turns a packed 1-bit-per-pixel buffer (MSB-first, row stride
        /// <paramref name="stride"/> bytes) into a bitmap — greyscale, or a §8.9.6.2 stencil
        /// painted in the current fill colour when <paramref name="isImageMask"/> is set.
        ///
        /// <para>Full <c>/Decode</c> remap per §8.9.5.2: a 1-bit sample <i>s</i> becomes
        /// <c>decode[0] + s·(decode[1] − decode[0])</c>, quantised to an 8-bit channel. The
        /// boolean invert (<c>/Decode [1 0]</c>) is the common case; this generalisation also
        /// handles a partial range such as <c>[0 0.5]</c>.</para>
        /// </summary>
        /// <remarks>
        /// <b>One body, two callers, and the two guards stay at the call sites.</b> This was a
        /// pair of near-identical loops in <see cref="DecodeToBitmap(byte[], int, int, PdfDictionary)"/> — the explicit
        /// <c>/BitsPerComponent 1</c> branch and the last-resort fallback for the CCITT/JBIG2
        /// dictionaries that declare neither <c>/BitsPerComponent</c> nor <c>/ColorSpace</c>.
        /// Deduplicating them is a DRY consolidation and nothing more: the copies differed only
        /// in spelling — one tested <c>decodeArray.Length &gt;= 2</c> for both entries, the other
        /// tested <c>&gt;= 1</c> for the first and <c>&gt;= 2</c> for the second — and that
        /// difference is <b>unobservable</b>, because <see cref="GetDecodeArray"/> returns
        /// <c>null</c> for any array with fewer than two entries, so a one-element
        /// <c>/Decode</c> never reaches either. Do not record this as a fixed defect; there was
        /// no defect.
        ///
        /// <para>What the callers keep is their length guard, and the two are genuinely
        /// different: the explicit branch accepts <c>&gt;=</c> because a padded
        /// <c>/EncodedByteAlign</c> stencil has trailing bytes, the fallback demands <c>==</c>
        /// because it is guessing at a format the file never declared. Merging them into one
        /// test here is the careless version of this change and breaks one direction or the
        /// other.</para>
        /// </remarks>
        private SKBitmap DecodePackedBilevel(byte[] data, int w, int h, int stride,
                                             float[] decodeArray, bool isImageMask)
        {
            bool hasDecode = decodeArray != null && decodeArray.Length >= 2;
            // PdfColor.Channel, not an inline cast: it is the single quantisation rule for the
            // render path, and both copies here open-coded clamp-then-scale-then-truncate, which
            // agrees with it on every value a /Decode entry can hold but is a second spelling of
            // a rule this repo has already had to unify three times. GAPS.md §2b.
            byte v0 = PdfColor.Channel(hasDecode ? decodeArray[0] : 0f);
            byte v1 = PdfColor.Channel(hasDecode ? decodeArray[1] : 1f);

            // Stencil per §8.9.6.4: sample 0 paints with the fill colour and 1 is transparent,
            // and /Decode [1 0] swaps the two. That decision is the same for every pixel, so it
            // is made once here rather than re-evaluated inside the loop as both copies did.
            bool paintOnSetBit = hasDecode && decodeArray[0] > decodeArray[1];

            // The loop itself is in ImageStencil, shared with the SVG writer - which had its own
            // copy, handling only the stencil case and carrying no bounds test at all.
            return ImageStencil.DecodePackedBilevel(
                data, w, h, stride, paintOnSetBit, v0, v1, isImageMask, _ctx.StencilColor());
        }

        /// <summary>
        /// Names the filter-level fallback when one has just occurred — both the CCITT and JBIG2
        /// filters report a failure by returning the compressed input unchanged, which reaches the
        /// decoder as nonsense sample data. Their <c>Last*</c> slots had no readers at all before
        /// this.
        /// </summary>
        /// <remarks>
        /// <b>Gated on the image's own <c>/Filter</c>.</b> The slots are <c>[ThreadStatic]</c> and
        /// are cleared by the filter's own <c>Decode</c> — but only when that filter runs at all.
        /// An image with no CCITT filter never clears the CCITT slot, so consulting it
        /// unconditionally would attribute an <i>earlier</i> image's failure to this one. Checking
        /// the filter list first means a reason is only ever quoted for a filter this image
        /// actually used.
        /// </remarks>
        private static string DescribeFilterFallback(PdfDictionary imageDict)
        {
            bool Uses(string filterName)
            {
                var f = imageDict?[PdfName.Filter] ?? imageDict?[PdfName.F];
                if (f is PdfName n) return n.Value == filterName;
                if (f is PdfArray arr)
                {
                    for (int i = 0; i < arr.Count; i++)
                        if (arr[i] is PdfName an && an.Value == filterName) return true;
                }
                return false;
            }

            string ccitt = Core.IO.Filters.CcittFaxDecodeFilter.LastFallbackReason;
            if (!string.IsNullOrEmpty(ccitt) && Uses("CCITTFaxDecode"))
                return " — CCITTFaxDecode fell back to raw bytes: " + ccitt;

            string jbig2 = Core.IO.Filters.Jbig2DecodeFilter.LastFallbackReason;
            if (!string.IsNullOrEmpty(jbig2) && Uses("JBIG2Decode"))
                return " — JBIG2Decode fell back to raw bytes: " + jbig2;

            return string.Empty;
        }

        private static int ReadNibble(byte[] data, int bitOffset)
        {
            int byteIdx = bitOffset / 8;
            int shift = 4 - (bitOffset % 8);
            if (byteIdx >= data.Length) return 0;
            return (data[byteIdx] >> shift) & 0x0F;
        }

        /// <summary>
        /// Reads the /Decode array from an image dictionary.
        /// Returns null if not present (caller should use identity mapping).
        /// </summary>
        private float[] GetDecodeArray(PdfDictionary imageDict)
        {
            // One reader, in PdfColor.DecodeArray — shared with the SVG exporter, which had its
            // own with a different fallback for a non-numeric entry. See its remarks for why a
            // malformed entry now makes the whole array absent rather than guessing a default.
            var decObj = imageDict[PdfName.Decode];
            if (decObj == null) return null;
            return PdfColor.DecodeArray(_doc, PdfResolve.Value(_doc, decObj) as PdfArray);
        }

        /// <summary>
        /// Applies /Decode array remapping for a single component byte value.
        /// Maps input [0, maxVal] through Decode[2*comp] .. Decode[2*comp+1].
        /// </summary>
        private static byte ApplyDecode(byte raw, float[] decode, int comp, int maxVal)
        {
            if (decode == null) return raw;
            int idx = comp * 2;
            if (idx + 1 >= decode.Length) return raw;
            float dMin = decode[idx], dMax = decode[idx + 1];
            // Identity check (common case)
            if (dMin == 0f && dMax == 1f) return raw;
            float normalized = raw / (float)maxVal;
            float mapped = dMin + normalized * (dMax - dMin);
            // PdfColor.Channel, not an inline cast: this truncated while the SVG renderer's
            // /Decode path rounded, so /Decode [0 0.5] over sample 255 rendered 127 and exported
            // 128. GAPS.md §2b.
            return PdfColor.Channel(mapped);
        }

        /// <summary>
        /// Applies /Decode array remapping, returning a float in [0,1].
        /// </summary>
        private static float ApplyDecodeF(byte raw, float[] decode, int comp)
            => ApplyDecodeF01(raw / MaxChannelValueF, decode, comp);

        /// <summary>
        /// Applies /Decode array remapping to an ALREADY-NORMALISED sample, returning a float in
        /// [0,1]. Bit-depth-independent, which is what lets the 16-bpc paths share the mapping
        /// with the 8-bpc ones instead of carrying their own (or, as the 16-bpc CMYK path did,
        /// none at all).
        /// </summary>
        private static float ApplyDecodeF01(float normalized, float[] decode, int comp)
            => PdfColor.ApplyDecode(normalized, decode, comp);

        /// <summary>
        /// Applies the /Decode value-remapping array to an already-decoded SKBitmap (JPEG or PNG).
        /// Each component c is mapped: c' = Decode[2i] + c/255 * (Decode[2i+1] - Decode[2i]).
        /// A null decode array or an identity array ([0 1 0 1 …]) is a no-op.
        /// Handles 1-component (grey), 3-component (RGB), and 4-component (RGBA/CMYK) images.
        /// </summary>
        private static void ApplyDecodeArrayToBitmap(SKBitmap bmp, float[] decode)
        {
            if (bmp == null || decode == null || decode.Length < 2) return;

            // Check if every pair is the identity [0 1] — skip if so.
            bool identity = true;
            for (int i = 0; i + 1 < decode.Length; i += 2)
                if (decode[i] != 0f || decode[i + 1] != 1f) { identity = false; break; }
            if (identity) return;

            Images.BitmapPixels.Map(bmp, c =>
            {
                // Determine number of colour components from pixel (ignore alpha for decode remapping).
                // Grey images decoded by Skia arrive as RGBA with R=G=B.
                byte r = ApplyDecode(c.Red,   decode, 0, MaxChannelValue);
                byte g = decode.Length >= 4 ? ApplyDecode(c.Green, decode, 1, MaxChannelValue) : r;
                byte b = decode.Length >= 6 ? ApplyDecode(c.Blue,  decode, 2, MaxChannelValue) : r;
                // Alpha channel is not remapped by /Decode.
                return new SKColor(r, g, b, c.Alpha);
            });
        }

        /// <summary>
        /// Decodes an image whose colour space needs per-pixel conversion the generic device paths
        /// can't do: Separation/DeviceN (tint transform → alternate space), Lab, CalGray (gamma),
        /// CalRGB.  Returns <c>null</c> when the kind is unhandled (e.g. ICCBased, or
        /// Separation/DeviceN with no tint transform) so the caller falls through.
        /// </summary>
        /// <remarks>
        /// Handles all five legal sample widths (§8.9.5.1: 1, 2, 4, 8, 16). It was restricted to 8
        /// for no reason but the byte-indexed reads below, which meant a CalGray image at 4 bpc lost
        /// its <c>/Gamma</c> and a Separation plate at 1 bpc rendered as bilevel grey — silently,
        /// while the very same colour space at 8 bpc converted correctly.
        /// </remarks>
        /// <remarks>
        /// <c>intent</c> is this image's rendering intent — its own <c>/Intent</c> (§8.9.5.2
        /// Table 89) where it has one, and the graphics state's otherwise. It is passed in rather
        /// than read from the state here because only the caller holds the image dictionary that
        /// can override it.
        /// </remarks>
        private SKBitmap TryDecodeSpecialColorSpaceImage(byte[] data, int w, int h, string csKind,
            PdfArray csa, float[] decodeArray, int nc, int bpc, IccRenderingIntent intent)
        {
            if (data == null || w <= 0 || h <= 0 || nc < 1) return null;
            if (bpc != 1 && bpc != 2 && bpc != 4 && bpc != BitsPerByte && bpc != 16) return null;

            // §8.9.5.1: samples are packed MSB-first and each ROW starts on a byte boundary.
            long stride = ((long)w * nc * bpc + BitsPerByte - 1) / BitsPerByte;
            if (stride <= 0 || stride * h > data.Length) return null;

            int maxRaw = bpc == 16 ? MaxChannelValue16 : (1 << bpc) - 1;
            float maxF = maxRaw;

            // A LUT pays only when it has fewer entries than the image has pixels.
            // SIZED BY BIT DEPTH ALONE IT INVERTS AT 16 BPC: 65 536 entries to colour a 4x4
            // plate is 65 536 tint-transform evaluations for 16 pixels, and a Type 4 tint runs
            // a few hundred PostScript ops each time. The comment below it reasoned only
            // downward -- "a 4-bpc plate costs 16 evaluations rather than 256" -- and every
            // depth above 8 was the case it never asked about.
            //
            // The SVG renderer needs no such bound: ExpandSamplesToBytes normalises to one byte
            // per component before its LUT is built, so its table is always 256 entries. The two
            // renderers differ in COST here rather than in pixels, which no parity test can see.
            bool lutPays = (long)maxRaw + 1 <= (long)w * h;
            // The normalised, /Decode-mapped value of component c of pixel (x,y).
            float Sample(int x, int y, int c)
                => ApplyDecodeF01(RawSample(data, stride, y, x * nc + c, bpc) / maxF, decodeArray, c);

            switch (csKind)
            {
                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                {
                    csa = NamedSpotColors.Substitute(_doc, csa);   // a spot the named-colour profile names
                    var tintFn = csa.Count > 3 ? _doc?.ResolveObject(csa[3]) as PdfDictionary : null;
                    if (tintFn == null) return null; // no transform → fall through (device/gray fallback)

                    // An image's tints vary per sample and the samples become sRGB right here —
                    // the same reason the overprint image path has to carry its colorants as
                    // separate rasters. Measuring this needs the device layer (GAPS §11a), so what
                    // the ink map can honestly do is say the region is unmeasured rather than let
                    // a caller read "no spot ink here".
                    _ctx.ReportInkNotMeasured("a /Separation or /DeviceN image");
                    PdfObject altObj = csa.Count > 2 ? csa[2] : null;
                    string altCs = ResolveAlternateCsName(altObj);
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    if (nc == 1 && lutPays)
                    {
                        // 1-ink Separation: precompute a full-range LUT (O(1) per pixel), but only
                        // when the table is smaller than the image -- otherwise the loop below is
                        // cheaper and computes the identical colour from the identical input.
                        var lut = new SKColor[maxRaw + 1];
                        for (int v = 0; v <= maxRaw; v++)
                        {
                            float t = ApplyDecodeF01(v / maxF, decodeArray, 0);
                            // Through the alternate's ICC profile when it has one — the LUT is built
                            // once per image, so the profile lookup costs nothing per pixel.
                            lut[v] = IccColorTransform.ResolveAlternateColor(_doc, altObj,
                                PdfFunctionEvaluator.Evaluate(tintFn, new[] { t }, _doc.ResolveObject),
                                altCs, intent);
                        }
                        BitmapPixels.Fill(bmp, (row, y) =>
                        {
                            for (int x = 0; x < w; x++)
                                row[x] = lut[RawSample(data, stride, y, x, bpc)];
                        });
                    }
                    else
                    {
                        var tints = new float[nc];
                        BitmapPixels.Fill(bmp, (row, y) =>
                        {
                            for (int x = 0; x < w; x++)
                            {
                                for (int c = 0; c < nc; c++) tints[c] = Sample(x, y, c);
                                row[x] = IccColorTransform.ResolveAlternateColor(_doc, altObj,
                                    PdfFunctionEvaluator.Evaluate(tintFn, tints, _doc.ResolveObject),
                                    altCs, intent);
                            }
                        });
                    }
                    return bmp;
                }
                case PdfColorSpaceNames.Lab:
                {
                    if (nc != PdfColorSpaceNames.RgbComponents) return null;
                    // a*/b* range: /Range in the Lab params dict (default [-100 100]); L is 0..100.
                    // That IS the default /Decode of a Lab image (§8.9.5.2 Table 89), which is why
                    // the raw sample is scaled by it here rather than through ApplyDecodeF01; an
                    // explicit /Decode replaces it below.
                    var lp = ColorSpaceParams(csa);
                    // Parsed ONCE. LabToSRgb's dictionary overload re-reads /Range and /WhitePoint
                    // on every call, which is right for an `sc` fill and wrong for w*h pixels.
                    var labParams = PdfColor.CieParams.ForLab(lp, _doc);

                    // The axis range comes from the SAME struct that will clamp against it. It used
                    // to be re-read here with the NON-resolving PdfDictionary.GetArray, one line
                    // below a ForLab call that resolves — so an indirect /Range scaled the samples
                    // by the ±100 default and then clamped them to the file's real range, inside one
                    // function. The SVG twin resolved and got it right, which is the usual shape:
                    // one renderer ahead, in mirror files. Table 65's default lives in ForLab.
                    float amin = (float)labParams.AMin, amax = (float)labParams.AMax;
                    float bmin = (float)labParams.BMin, bmax = (float)labParams.BMax;
                    // An explicit /Decode (Table 89) replaces those defaults: each component is
                    // Dmin + sample / maxF × (Dmax − Dmin), the same mapping ApplyDecodeF01 makes.
                    float lmin = 0f, lmax = 100f;
                    if (decodeArray != null && decodeArray.Length >= 6)
                    {
                        lmin = decodeArray[0]; lmax = decodeArray[1];
                        amin = decodeArray[2]; amax = decodeArray[3];
                        bmin = decodeArray[4]; bmax = decodeArray[5];
                    }
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            float L  = lmin + RawSample(data, stride, y, x * 3,     bpc) / maxF * (lmax - lmin);
                            float aa = amin + RawSample(data, stride, y, x * 3 + 1, bpc) / maxF * (amax - amin);
                            float bb = bmin + RawSample(data, stride, y, x * 3 + 2, bpc) / maxF * (bmax - bmin);
                            // Params dict passed so the image honours /WhitePoint exactly as an `sc`
                            // fill in the same space does. The /Range re-clamp inside is a no-op here
                            // — the values above were built from it.
                            row[x] = PdfColor.LabToSRgb(L, aa, bb, labParams);
                        }
                    });
                    return bmp;
                }
                case PdfColorSpaceNames.CalGray:
                {
                    if (nc != PdfColorSpaceNames.GrayComponents) return null;
                    var gp = ColorSpaceParams(csa);
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    if (lutPays)
                    {
                        var lut = new SKColor[maxRaw + 1];
                        for (int v = 0; v <= maxRaw; v++)
                            lut[v] = PdfColor.CalGrayToSRgb(ApplyDecodeF01(v / maxF, decodeArray, 0), gp, _doc);
                        BitmapPixels.Fill(bmp, (row, y) =>
                        {
                            for (int x = 0; x < w; x++)
                                row[x] = lut[RawSample(data, stride, y, x, bpc)];
                        });
                    }
                    else
                    {
                        // Sample() is the LUT's own input expression, so this is the same
                        // colour by the same route -- a 16-bpc CalGray page just stops paying
                        // 65 536 gamma conversions to shade a handful of pixels.
                        BitmapPixels.Fill(bmp, (row, y) =>
                        {
                            for (int x = 0; x < w; x++)
                                row[x] = PdfColor.CalGrayToSRgb(Sample(x, y, 0), gp, _doc);
                        });
                    }
                    return bmp;
                }
                case PdfColorSpaceNames.CalRGB:
                {
                    if (nc != PdfColorSpaceNames.RgbComponents) return null;
                    var cp = ColorSpaceParams(csa);
                    // Parsed ONCE - see the Lab branch above. This one cost three indirect-reference
                    // resolutions and fifteen PdfNumber casts PER PIXEL to recompute nine constants.
                    var calParams = PdfColor.CieParams.ForCalRgb(cp, _doc);
                    var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    var comps = new float[PdfColorSpaceNames.RgbComponents];
                    BitmapPixels.Fill(bmp, (row, y) =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            comps[0] = Sample(x, y, 0);
                            comps[1] = Sample(x, y, 1);
                            comps[2] = Sample(x, y, 2);
                            row[x] = PdfColor.CalRgbToSRgb(comps, calParams);
                        }
                    });
                    return bmp;
                }
            }
            return null;
        }

        /// <summary>
        /// One raw image sample: component index <paramref name="idxInRow"/> (= <c>x*nc + c</c>) of
        /// row <paramref name="y"/>, at 1, 2, 4, 8 or 16 bits, MSB-first within each byte and with
        /// every row starting on a byte boundary (§8.9.5.1). Returns 0 past the end of the buffer
        /// rather than throwing — a truncated stream must lose pixels, not the page.
        /// </summary>
        private static int RawSample(byte[] data, long stride, int y, int idxInRow, int bpc)
        {
            if (bpc == 16)
            {
                long o16 = y * stride + (long)idxInRow * 2;
                if (o16 + 1 >= data.Length) return 0;
                return (data[o16] << BitsPerByte) | data[o16 + 1];
            }
            if (bpc == BitsPerByte)
            {
                long o8 = y * stride + idxInRow;
                return o8 < data.Length ? data[o8] : 0;
            }
            int bitPos = idxInRow * bpc;
            long o = y * stride + bitPos / BitsPerByte;
            if (o < 0 || o >= data.Length) return 0;
            int shift = BitsPerByte - bpc - (bitPos % BitsPerByte);
            return (data[o] >> shift) & ((1 << bpc) - 1);
        }

        /// <summary>Resolves a Separation/DeviceN alternate colour space to a name accepted by
        /// <see cref="ComponentsToColor"/> (ICCBased collapses to its device equivalent by /N).</summary>
        private string ResolveAlternateCsName(PdfObject alt)
            => IndexedPalette.AlternateSpaceName(_doc, alt);

        private SKBitmap DecodeIndexedBitmap(byte[] data, int w, int h, PdfArray csArr, int bpc = 8,
            float[] decodeArray = null)
        {
            // csArr = [/Indexed base hival lookup]
            // One reader, in IndexedPalette.TryReadSpace, shared with the other three /Indexed
            // sites.
            IndexedPalette.TryReadSpace(_doc, csArr, out var baseObj, out int hival, out byte[] table);

            // Resolve the base colour space into a flat index→colour palette (handles
            // Device*, ICCBased, CalGray/CalRGB, Lab and Separation/DeviceN bases).
            SKColor[] palette = BuildIndexedPalette(baseObj, table, hival);

            // PDF §8.9.5.2 Table 89: an Indexed image's /BitsPerComponent is 1, 2, 4 or 8.
            // Anything else is malformed, and 16 or 32 made ReadPackedIndex compute
            // pxPerByte = 8 / bpc = 0 and then divide by it — a DivideByZeroException that
            // Execute's per-operator catch turned into a silently dropped image.
            if (!IsValidIndexedBpc(bpc))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    InvalidIndexedBpcMessage(bpc));
                return null;
            }

            // /Decode for an Indexed image remaps the stored sample to an index; the default
            // is identity [0 (2^bpc − 1)].  Apply only when a non-identity array is present.
            int maxVal = (1 << bpc) - 1;
            bool remap = decodeArray != null && decodeArray.Length >= 2
                         && !(Math.Abs(decodeArray[0]) < 1e-4f && Math.Abs(decodeArray[1] - maxVal) < 1e-4f);
            float dMin = remap ? decodeArray[0] : 0f;
            float dMax = remap ? decodeArray[1] : maxVal;

            int stride = (w * bpc + 7) / 8;
            var bmp = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);
            // Bulk pixel access: SKBitmap.SetPixel is a marshalled interop call per pixel, so a
            // full-page indexed scan cost millions of them. This fills the bitmap's own buffer,
            // which also drops the image-sized SKColor[] the array route allocated.
            if ((long)w * h > int.MaxValue) return bmp;

            // The `return` below abandons the REST OF THE ROW and carries on with the next one,
            // and the row arrives cleared, so the abandoned tail stays
            // transparent black exactly as the zero-initialised array left it. This surface is
            // Rgb888x, whose fourth byte is padding rather than alpha — Fill checks CanStore
            // rather than assuming the 8888 offsets, which is the omission that once made a whole
            // conversion inert.
            BitmapPixels.Fill(bmp, (row, y) =>
            {
                for (int x = 0; x < w; x++)
                {
                    if (!ReadPackedIndex(data, stride, y, x, bpc, out int sample)) return;
                    int palIdx = remap && maxVal > 0
                        ? SaturatingCast.ToInt(dMin + sample * (dMax - dMin) / maxVal + 0.5f)
                        : sample;
                    palIdx = Math.Max(0, Math.Min(hival, palIdx));
                    row[x] = palette[palIdx];
                }
            });
            return bmp;
        }

        /// <summary>
        /// Whether a colour-space family name is <c>/Indexed</c> — under either spelling.
        ///
        /// <para><c>internal</c> and shared with the SVG writer, which had the two-spelling test and
        /// the raster path did not. §8.9.7 Table 93's <c>/I</c> is legal wherever an inline image's
        /// colour space appears, including as the head of the <c>[/I base hival lookup]</c> array
        /// that <c>ExpandAbbreviatedImageDict</c> does not reach into.</para>
        /// </summary>
        internal static bool IsIndexedFamily(string family) =>
            family == PdfColorSpaceNames.Indexed || family == PdfColorSpaceNames.IndexedAbbrev;

        /// <summary>
        /// §8.9.5.2 Table 89: an <c>/Indexed</c> image's <c>/BitsPerComponent</c> is 1, 2, 4 or 8.
        ///
        /// <para><b>internal, and the SVG writer calls it</b>, so both report and drop the image when
        /// it is not. Clamping to <c>bpc = 8</c> and carrying on would export a 16-bpc Indexed image
        /// as confidently wrong pixels — the stride computed from the clamped depth against data
        /// with twice as many bytes per row — with no diagnostic. One predicate and one message, so
        /// the answer cannot differ by renderer.</para>
        /// </summary>
        internal static bool IsValidIndexedBpc(int bpc)
            => bpc == 1 || bpc == 2 || bpc == 4 || bpc == 8;

        /// <summary>The shared refusal text, so a parity test can compare the two directly.</summary>
        internal static string InvalidIndexedBpcMessage(int bpc)
            => $"Indexed image has invalid /BitsPerComponent {bpc} (must be 1, 2, 4 or 8); image skipped.";

        /// <summary>
        /// The index -> sRGB palette for <c>[/Indexed base hival lookup]</c>.
        /// </summary>
        /// <remarks>
        /// One implementation, in <see cref="IndexedPalette"/>, reached from here and from the SVG
        /// writer. It was two ~140-line bodies whose summaries claimed to mirror each other, and
        /// they drifted twice in opposite directions (CalGray on this side, CalRGB on the other).
        /// </remarks>
        private SKColor[] BuildIndexedPalette(PdfObject baseObj, byte[] table, int hival)
            => IndexedPalette.Build(_doc, baseObj, table, hival, CurrentIntent);

        // Reads a packed palette index at pixel (row, col) with stride bytes per row.
        // Returns false when data is exhausted (row is incomplete — caller stops that row).
        private static bool ReadPackedIndex(byte[] data, int stride, int row, int col, int bpc, out int palIdx)
        {
            int pxPerByte = BitsPerByte / Math.Max(1, bpc);
            int byteIdx = row * stride + col / pxPerByte;
            if (byteIdx >= data.Length) { palIdx = 0; return false; }
            // One sample per byte needs no shift; sub-byte samples are packed MSB-first.
            int shift = bpc == BitsPerByte ? 0 : (pxPerByte - 1 - col % pxPerByte) * bpc;
            int mask   = (1 << bpc) - 1;
            palIdx = (data[byteIdx] >> shift) & mask;
            return true;
        }

        private int GetColorComponents(PdfDictionary d) => ImageSamples.ComponentCount(d, _doc);

        /// <summary>
        /// The image's <c>/BitsPerComponent</c>. An <c>/ImageMask</c> is <b>always</b> 1 (§8.9.6.2
        /// Table 90: the entry "shall be 1" when present, and is implicit when absent), so the
        /// 8-bpc default must not be applied to one — an image mask that omits the entry would
        /// otherwise be read as an 8-bit greyscale image and never reach the stencil path.
        /// </summary>
        private int GetBitsPerComponent(PdfDictionary d)
        {
            if (ImageStencil.IsStencilMask(d, _doc)) return 1;
            return PdfResolve.Value(_doc, d[PdfName.BitsPerComponent] ?? d[PdfName.BPC]) is PdfNumber n
                   ? n.IntValue : DefaultBitsPerComponent;
        }
    }
}
