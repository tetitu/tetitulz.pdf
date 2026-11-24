// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
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
        private void OpBi(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1 || op.Operands[0] is not PdfStream stream) return;

            ExpandAbbreviatedImageDict(stream);
            // A colour space named from the resources (§8.9.7), as a colour space: the decode,
            // the masks and a plate all read the copy.
            stream = InlineImageColorSpace.Resolve(stream, _resources, _doc);
            // §8.6.5.6: a device space, or a device base, is the resources' default for it.
            stream = DefaultColorSpaces.WithDefaults(stream, _resources, _doc);

            // §8.6.7, as for an image XObject: reported at the operator, before any decode.
            if (_ctx.GS.OverprintFill && _ctx.Plate == null && OverprintCompositor.ImageLeavesColorantsAlone(stream, _doc))
                PdfRenderContext.ReportOverprintSkipped(OverprintReportReason.Image);

            int w = (PdfResolve.Number(_doc, stream, PdfName.Width))?.IntValue ?? 1;
            int h = (PdfResolve.Number(_doc, stream, PdfName.Height))?.IntValue ?? 1;

            byte[] raw;

            // Through DecodeImageStream, NOT GetDecodedData. This was the one image-sample decode
            // in either renderer that bypassed the shared rule, and it drifted: DecodeImageStream
            // supplies /Columns = /Width to a CCITTFaxDecode filter whose /DecodeParms omits it,
            // because the CCITT decoder's own default is 1728 (§7.4.6, Table 11) rather than the
            // image width. Measured on the same 8×2 Group 4 image with /Columns absent — the raster
            // INLINE path painted the left half black where the SVG inline path and both XObject
            // paths painted it white: at 1728 columns the decode throws, CcittFaxDecodeFilter hands
            // back its COMPRESSED INPUT, and the 1-bpc path paints those bytes as samples.
            if (FormBudget != null && !FormBudget.TryDecodeImage()) return;
            try { raw = DecodeImageStream(stream, w, FormBudget?.ImageWork); }
            catch (Exception ex)
            {
                if (FormBudget != null && FormBudget.IsImageWorkRefusal(ex)) return;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "Inline image decode failed", ex);
                return;
            }

            // The image XObject's decode and draw, not a copy of them: a copy of the mask passes
            // drifts — a colour key read from the filter output rather than the samples painted (a
            // JPEG's or JPEG 2000's decoded ones, or a decimated buffer), a JPEG 2000 opacity
            // channel dropped, a separation plate never handed the samples.
            var bitmap = DecodeSamplesWithMasks(raw, stream, w, h, out RawImageSamples painted);
            if (bitmap == null) return;
            ApplyTransferFunction(bitmap);
            bool interpolate = stream[PdfName.Interpolate] is PdfBoolean ib && ib.Value;
            using (bitmap)
                DrawDecodedImage(bitmap, painted, stream, interpolate);
        }

        /// <summary>
        /// Expands the inline-image key and value abbreviations (§8.9.7, Table 93).
        ///
        /// <para><b>internal, and the SVG writer calls it</b> — like <see cref="DecodeImageStream(PdfStream, int)"/>
        /// beside it. A second copy in the SVG writer, never renaming <c>/D</c> to <c>/Decode</c>,
        /// could be kept correct only by a defensive <c>Decode ?? D</c> at every one of its call
        /// sites — one new call site away from being wrong. A table of spec abbreviations is exactly
        /// the kind of rule that must have one implementation.</para>
        /// </summary>
        internal static void ExpandAbbreviatedImageDict(PdfStream s)
        {
            RenameKey(s, PdfName.W,   PdfName.Width);
            RenameKey(s, PdfName.H,   PdfName.Height);
            RenameKey(s, PdfName.CS,  PdfName.ColorSpace);
            RenameKey(s, PdfName.BPC, PdfName.BitsPerComponent);
            RenameKey(s, PdfName.F,   PdfName.Filter);
            RenameKey(s, PdfName.DP,  PdfName.DecodeParms);
            RenameKey(s, PdfName.D,   PdfName.Decode);
            RenameKey(s, PdfName.I,   PdfName.Interpolate);
            RenameKey(s, PdfName.IM,  PdfName.ImageMask);

            if (s[PdfName.ColorSpace] is PdfName csn)
            {
                // Only the device-space abbreviations are expanded here; /I (Indexed) always
                // arrives as an array head, which the image decoder handles directly.
                PdfName full = csn.Value switch
                {
                    PdfColorSpaceNames.DeviceGrayAbbrev => PdfName.DeviceGray,
                    PdfColorSpaceNames.DeviceRGBAbbrev  => PdfName.DeviceRGB,
                    PdfColorSpaceNames.DeviceCMYKAbbrev => PdfName.DeviceCMYK,
                    _      => null
                };
                if (full != null) s[PdfName.ColorSpace] = full;
            }

            if (s[PdfName.Filter] is PdfName fn)
            {
                PdfName full = fn.Value switch
                {
                    "AHx" => PdfName.ASCIIHexDecode,
                    "A85" => PdfName.ASCII85Decode,
                    "LZW" => PdfName.LZWDecode,
                    "Fl"  => PdfName.FlateDecode,
                    "RL"  => PdfName.RunLengthDecode,
                    "CCF" => PdfName.CCITTFaxDecode,
                    "DCT" => PdfName.DCTDecode,
                    _     => null
                };
                if (full != null) s[PdfName.Filter] = full;
            }
        }

        // float.IsFinite is unavailable on netstandard2.0; MatrixGuard is the one implementation,
        // shared with the SVG writer.
        private static bool IsFiniteF(float v) => MatrixGuard.IsFinite(v);

        /// <summary>
        /// The half-em fallback for a non-finite advance — one implementation in
        /// <see cref="MatrixGuard.SanitizeAdvance"/>, shared with the SVG writer, which carried
        /// the same rule with the predicate written out.
        /// </summary>
        private static float SanitizeAdvance(float advancePts, float fontSize)
            => MatrixGuard.SanitizeAdvance(advancePts, fontSize);

        /// <summary>
        /// Returns true when every element of <paramref name="m"/> is finite — forwards to
        /// <see cref="MatrixGuard.AllFinite"/>, the one implementation, which the SVG writer calls
        /// too. Kept as a local name so the existing call sites read unchanged.
        /// </summary>
        private static bool AllFinite(float[] m) => MatrixGuard.AllFinite(m);

        private static void RenameKey(PdfDictionary d, PdfName oldKey, PdfName newKey)
        {
            var v = d[oldKey];
            if (v == null) return;
            // ISO 32000-2 §8.9.7: when both abbreviated and full-name forms are present
            // in the same inline image operand, the abbreviated form takes priority.
            d[newKey] = v;
            d.Remove(oldKey);
        }

        // ---- Shading ----
    }
}
