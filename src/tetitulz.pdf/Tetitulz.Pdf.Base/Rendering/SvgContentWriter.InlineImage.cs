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
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
internal sealed partial class SvgContentWriter
    {
        private void OpBi(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1 || op.Operands[0] is not PdfStream stream) return;
            // The SHARED expander (ContentStreamInterpreter). This was a private second copy of
            // Table 93 that had already drifted — it omitted /D -> /Decode — and the drift was
            // invisible only because all six SVG image-decode reads say `Decode ?? D` while the
            // raster ones say `Decode` alone. A duplicate held correct by defensive reads at every
            // one of its consumers is one edit away from being wrong.
            ContentStreamInterpreter.ExpandAbbreviatedImageDict(stream);
            // A colour space named from the resources (§8.9.7), as a colour space — the raster
            // path's rule, one implementation.
            stream = InlineImageColorSpace.Resolve(stream, _resources, _doc);
            // §8.6.5.6: a device space, or a device base, is the resources' default for it.
            stream = DefaultColorSpaces.WithDefaults(stream, _resources, _doc);
            // §8.6.7, as for an image XObject: reported at the operator, before any decode.
            if (_gs.OverprintFill && OverprintCompositor.ImageLeavesColorantsAlone(stream, _doc))
                ReportOverprintSkipped(OverprintReportReason.Image);
            int w = GetStreamInt(stream, PdfName.Width,  PdfName.W, 1);
            int h = GetStreamInt(stream, PdfName.Height, PdfName.H, 1);
            byte[] raw;
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryDecodeImage()) return;
            try { raw = ContentStreamInterpreter.DecodeImageStream(stream, w, Shared?.FormBudget?.ImageWork); }
            catch (Exception ex)
            {
                if (Shared?.FormBudget != null && Shared.FormBudget.IsImageWorkRefusal(ex)) return;
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG inline image decode failed", ex);
                return;
            }

            // An inline image mask (`BI /IM true`) is a 1-bit stencil painted in the fill
            // colour, exactly like an /ImageMask XObject — not BuildImageDataUri, whose 8-bpc paths
            // cannot read a packed 1-bit buffer.
            bool isMask = (stream[PdfName.ImageMask] ?? stream[PdfName.IM]) is PdfBoolean imB && imB.Value;
            // A stencil under a pattern fill takes the image XObject's route — the pattern painted
            // through a mask — so one stencil gets one answer whichever way it is drawn.
            if (isMask && SelectedPatternName(stroke: false) is string stencilPattern)
            {
                EmitStencilThroughPattern(raw, w, h, stream, stencilPattern, _gs.CTM,
                                          ImageRenderingAttribute(stream), SoftMaskAttribute());
                return;
            }
            string dataUri = isMask
                ? BuildImageMaskDataUri(raw, w, h, stream)
                : BuildImageDataUri(raw, w, h, stream);
            if (dataUri == null) return;

            float[] m = _gs.CTM;
            string matrix = $"matrix({F(m[0])},{F(m[1])},{F(m[2])},{F(m[3])},{F(m[4])},{F(m[5])})";
            // The graphics-state soft mask, through the same accessor RenderImageXObject uses.
            // §11.6.4 applies it to every painting operation, and this one was the only image draw
            // in either renderer that dropped it: the XObject path one file away emitted
            // `mask="url(#…)"`, and the raster path masks both routes because both end in
            // PdfRenderContext.DrawBitmap → DrawWithOptionalSoftMask. So a page whose inline image
            // sat under a `gs` soft mask rendered masked and exported UNMASKED — an image the file
            // asked to hide, painted in full.
            _sb.Append($"<image transform=\"{matrix}\" x=\"0\" y=\"0\" width=\"1\" height=\"1\"");
            _sb.Append($" preserveAspectRatio=\"none\"{ImageRenderingAttribute(stream)}" +
                       $"{SoftMaskAttribute()}{ImagePaintAttributes()} xlink:href=\"{dataUri}\"/>\n");
        }


        /// <summary>
        /// An integer image parameter, full key or abbreviated inline spelling (§8.9.7 Table 93).
        /// </summary>
        /// <remarks>
        /// Forwards to <see cref="PdfResolve.Int(PdfDocument,PdfDictionary,PdfName,PdfName,int)"/>, which the raster path also calls. Reading the raw value,
        /// <c>(d[k1] ?? d[k2]) is PdfNumber</c>, would let <c>/Width 12 0 R</c> fall to the default
        /// of <b>1</b> and draw the image as a single pixel stretched over its placement rectangle.
        /// It is an instance method only because resolving needs the document.
        /// </remarks>
        private int GetStreamInt(PdfDictionary d, PdfName k1, PdfName k2, int def)
            => PdfResolve.Int(_doc, d, k1, k2, def);

        private float GetArrFloat(PdfArray arr, int idx)
            => PdfResolve.ArrayFloat(_doc, arr, idx);

        // ---- Coordinate transform ----
    }
}
