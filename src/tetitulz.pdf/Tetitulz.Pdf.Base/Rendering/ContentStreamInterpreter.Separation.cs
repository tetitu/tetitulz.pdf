// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A separation plate's image path: the plate's ink read from the image's own samples.
    /// </summary>
    /// <remarks>
    /// Raster only, and no SVG twin: the vector writer has no plates (<see cref="SeparationPlate"/>).
    /// </remarks>
    internal sealed partial class ContentStreamInterpreter
    {
        /// <summary>
        /// Draws an image on the current <see cref="PdfRenderContext.Plate"/> from its samples —
        /// DeviceCMYK, DeviceGray, <c>/Separation</c> or <c>/DeviceN</c> at 8 or 16 bits, an
        /// <c>/ICCBased</c> space as the device space its <c>/N</c> names — with the opacity the
        /// decode already gave it (soft mask, explicit mask, colour key). False when the samples
        /// cannot be read so; the caller then draws the decoded bitmap through the plate's sRGB
        /// filter, which reports the approximation.
        /// </summary>
        /// <param name="decoded">The decoded bitmap: its alpha is the image's opacity.</param>
        /// <param name="painted">The samples it was decoded from.</param>
        /// <param name="interpolate">The image's <c>/Interpolate</c>.</param>
        /// <remarks>
        /// <para><b>Why the samples.</b> By the time the bitmap exists a DeviceCMYK image is sRGB,
        /// and a rich black (60/50/40/100) is indistinguishable from plain black; a spot image is
        /// its alternate's colour. A separation must keep both, and this does.</para>
        ///
        /// <para>A JPEG or a codec-decoded JPEG 2000 image has no samples here
        /// (<see cref="RawImageSamples.AreSamples"/>), nor does a decode reduced below the samples'
        /// size or one whose opacity is carried beside the bitmap; each stays approximated.</para>
        /// </remarks>
        private bool TryDrawImageOnPlate(SKBitmap decoded, RawImageSamples painted, bool interpolate)
        {
            SeparationPlate plate = _ctx.Plate;
            if (plate == null || decoded == null || !painted.AreSamples || painted.Samples == null || painted.Opacity != null) return false;

            PdfDictionary dict = painted.Dictionary;
            int w = painted.Width, h = painted.Height;
            if (dict == null || decoded.Width != w || decoded.Height != h) return false;
            // A stencil mask's ink is the fill colour (StencilColor), never its samples — and one
            // that omits /BitsPerComponent (§8.9.6.2: implied 1) was read below as 8-bit samples.
            if (ImageStencil.IsStencilMask(painted.Dictionary, _doc)) return false;
            if (decoded.ColorType != SKColorType.Rgba8888 && decoded.ColorType != SKColorType.Bgra8888) return false;

            int bpc = PdfResolve.Int(_doc, dict, PdfName.BitsPerComponent, PdfName.BPC, 8);
            if (bpc != 8 && bpc != 16) return false;

            var space = PdfColor.ResolveSpace(dict[PdfName.ColorSpace] ?? dict[PdfName.CS], _doc);
            string family = space.DeviceFamily;
            ImageRoute? found = plate.RouteImage(family, Colorants(space), _ctx.GS.OverprintFill, space.Array);
            if (found == null) return false;
            ImageRoute route = found.Value;
            if (route.Preserves) return true;

            int comps = space.Components, bytesPerSample = bpc / 8;
            if (comps <= 0 || route.ComponentIndex >= comps) return false;
            long rowBytes = (long)w * comps * bytesPerSample;
            if (painted.Samples.Length < rowBytes * h) return false;

            if (_ctx.GS.TransferFunction != null) plate.NoteApproximation("a transfer function");

            byte[] raw = painted.Samples;
            float[] decode = GetDecodeArray(dict);
            int alphaOffset = 3;
            int stride = decoded.RowBytes;
            ReadOnlySpan<byte> px = decoded.GetPixelSpan();
            byte[] alpha = new byte[checked(w * h)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    alpha[y * w + x] = px[y * stride + 4 * x + alphaOffset];

            using var plateBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            BitmapPixels.Fill(plateBitmap, (row, y) =>
            {
                for (int x = 0; x < w; x++)
                {
                    byte a = alpha[y * w + x];
                    float tint = 0f;
                    if (!route.KnocksOut)
                    {
                        long o = y * rowBytes + ((long)x * comps + route.ComponentIndex) * bytesPerSample;
                        float v = bytesPerSample == 1 ? raw[o] / 255f : ((raw[o] << 8) | raw[o + 1]) / 65535f;
                        float c = ApplyDecodeF01(v, decode, route.ComponentIndex);
                        tint = route.Invert ? 1f - c : c;
                    }
                    row[x] = SeparationPlate.PlateColor(tint, a);
                }
            });

            _ctx.DrawBitmap(plateBitmap, 0, 0, 1, 1, interpolate, plateReady: true);
            return true;
        }

        /// <summary>
        /// Draws an image whose colour lives in sRGB — RGB, a CIE-based or <c>/ICCBased</c> space,
        /// <c>/Indexed</c> over one, a JPEG painted by its codec, an inline image — on the plate
        /// through the separation's output profile, at the image's intent. False when there is no
        /// output profile, the plate is a spot ink, or the image's space has colorants of its own
        /// (DeviceCMYK, DeviceGray, <c>/Separation</c>, <c>/DeviceN</c>: their routes are exact
        /// without a profile).
        /// </summary>
        /// <param name="decoded">The decoded bitmap, sRGB, its alpha the image's opacity.</param>
        /// <param name="dict">The image dictionary (its <c>/ColorSpace</c> and <c>/Intent</c>).</param>
        /// <param name="interpolate">The image's <c>/Interpolate</c>.</param>
        private bool TryDrawImageThroughProfile(SKBitmap decoded, PdfDictionary dict, bool interpolate)
        {
            SeparationPlate plate = _ctx.Plate;
            if (plate?.Output == null || decoded == null || dict == null || plate.ProcessIndex == SeparationPlate.SpotColorant) return false;
            if (decoded.ColorType != SKColorType.Rgba8888 && decoded.ColorType != SKColorType.Bgra8888) return false;
            if (ImageStencil.IsStencilMask(dict, _doc)) return false;

            var space = PdfColor.ResolveSpace(dict[PdfName.ColorSpace] ?? dict[PdfName.CS], _doc);
            string family = space.Family == PdfColorSpaceNames.Indexed && space.Array != null && space.Array.Count > 1
                ? PdfColor.ResolveSpace(space.Array[1], _doc).Family
                : space.Family;
            if (family == PdfColorSpaceNames.DeviceCMYK || family == PdfColorSpaceNames.DeviceGray
                || family == PdfColorSpaceNames.Separation || family == PdfColorSpaceNames.DeviceN) return false;
            if (family == PdfColorSpaceNames.ICCBased && space.Components == PdfColorSpaceNames.CmykComponents) return false;

            if (_ctx.GS.TransferFunction != null) plate.NoteApproximation("a transfer function");

            IccRenderingIntent intent = ImageIntent(dict);
            // Straight colour for the profile, read through the one layout reader (a premultiplied
            // surface is not straight; StraightPixelLayout knows which this is).
            if (!StraightPixelLayout.TryRead(decoded, out var layout)) return false;
            int w = decoded.Width, h = decoded.Height;
            var straight = new SKColor[checked(w * h)];
            ReadOnlySpan<byte> px = decoded.GetPixelSpan();
            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<byte> srcRow = px.Slice(y * decoded.RowBytes, decoded.RowBytes);
                for (int x = 0; x < w; x++) straight[y * w + x] = layout.Get(srcRow, x);
            }
            int ink = plate.ProcessIndex;

            using var plateBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            BitmapPixels.Fill(plateBitmap, (row, y) =>
            {
                for (int x = 0; x < w; x++)
                {
                    SKColor c = straight[y * w + x];
                    if (c.Alpha == 0) { row[x] = SKColors.Transparent; continue; }
                    float tint = plate.Output.TryConvert(c, intent, out float[] cmyk) ? cmyk[ink] : 0f;
                    row[x] = SeparationPlate.PlateColor(tint, c.Alpha);
                }
            });

            _ctx.DrawBitmap(plateBitmap, 0, 0, 1, 1, interpolate, plateReady: true);
            return true;
        }

        /// <summary>The colorant names of a <c>/Separation</c> (one) or <c>/DeviceN</c> (its array) space, in component order; null for any other.</summary>
        private IReadOnlyList<string> Colorants(PdfColor.ResolvedColorSpace space)
        {
            if (space.Array == null || space.Array.Count < 2) return null;
            if (space.Family != PdfColorSpaceNames.Separation && space.Family != PdfColorSpaceNames.DeviceN) return null;
            PdfObject names = _doc?.ResolveObject(space.Array[1]) ?? space.Array[1];
            if (names is PdfName single) return new[] { single.Value };
            if (names is not PdfArray arr) return null;
            var list = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
                list.Add((_doc?.ResolveObject(arr[i]) ?? arr[i]) is PdfName n ? n.Value : string.Empty);
            return list;
        }
    }
}
