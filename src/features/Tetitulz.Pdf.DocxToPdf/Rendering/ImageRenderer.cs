// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Constants;
using WpDrawing = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class ImageRenderer
    {
        /// <summary>
        /// Paints a raster picture (a DrawingML <c>a:blip</c> with an <c>r:embed</c>).
        /// Vector shapes, groups and diagrams are the shape tree's job — see
        /// <c>Drawings.DrawingRenderer</c>, which owns the routing and is this type's only caller.
        /// </summary>
        internal void RenderPicture(DW.Inline inline, DW.Anchor anchor, DocxRenderContext context)
        {
            if (!context.Options.EmbedImages) return;
            if (inline != null) RenderInlineImage(inline, context);
            else if (anchor != null) RenderAnchorImage(anchor, context);
        }

        /// <summary>
        /// Paints a picture into an already-decided box, without touching the flow cursor — the
        /// form a table cell needs, where the position comes from the cell's own layout rather than
        /// from the body's cursor.
        /// </summary>
        internal bool RenderPictureAt(OpenXmlElement container, Drawings.DrawingPlacement.Box box,
            DocxRenderContext context)
        {
            if (!context.Options.EmbedImages) return false;

            if (!TryReadEmbedId(container, out var blip, out var embed)) return false;
            if (!TryDecode(blip, embed, context, out var img)) return false;

            PaintImageAt(context, box.X, box.BottomY, box.Width, box.Height, img);
            return true;
        }
        private void RenderInlineImage(DW.Inline inline, DocxRenderContext context)
        {
            var extent = inline.Extent;
            if (extent == null) return;

            double widthPts = (extent.Cx?.Value ?? 0) / PdfUnits.EmuPerPoint;
            double heightPts = (extent.Cy?.Value ?? 0) / PdfUnits.EmuPerPoint;
            if (widthPts <= 0 || heightPts <= 0) return;

            if (!TryReadEmbedId(inline, out var blip, out var embed)) return;
            if (!TryDecode(blip, embed, context, out var img)) return;

            // Constrain to the printable HEIGHT here; the width constraint, the page break and the
            // descender clearance all belong to DrawingPlacement.PlaceInline, which every other
            // inline drawing already goes through.
            if (heightPts > context.ContentHeight && heightPts > 0)
            {
                double s = context.ContentHeight / heightPts; widthPts *= s; heightPts *= s;
            }

            // Placed through the shared helper rather than by hand. DocxRenderContext.LastLineDescent
            // is consumed in exactly one place — PlaceInline — so its own NeedsNewPage/BreakFlow and a
            // paint at CursorY − height would put an inline picture's top edge exactly on the previous
            // line's baseline, over the descenders of its p's and g's, while an inline chart,
            // autoshape or SmartArt diagram in the same position cleared them: two inline drawings in
            // one document laid out by two rules.
            var box = Drawings.DrawingPlacement.PlaceInline(widthPts, heightPts, context);
            PaintImageAt(context, box.X, box.BottomY, box.Width, box.Height, img);
            context.AdvanceCursor(box.Height);
        }

        // A floating (anchored) image. Unlike an inline image it does not consume vertical
        // flow: it is painted at an absolute position computed from wp:positionH/positionV,
        // and (unless wrapNone / behindDoc) registers an exclusion rectangle that body text
        // wraps around. wrapTopAndBottom reserves the full column band (no text beside it);
        // wrapSquare / wrapTight / wrapThrough reserve only the image's side.
        private void RenderAnchorImage(DW.Anchor anchor, DocxRenderContext context)
        {
            var extent = anchor.Descendants<DW.Extent>().FirstOrDefault();
            if (extent == null) return;

            double w = (extent.Cx?.Value ?? 0) / PdfUnits.EmuPerPoint;
            double h = (extent.Cy?.Value ?? 0) / PdfUnits.EmuPerPoint;
            if (w <= 0 || h <= 0) return;

            if (!TryReadEmbedId(anchor, out var blip, out var embed)) return;
            if (!TryDecode(blip, embed, context, out var img)) return;

            var box = Drawings.DrawingPlacement.PlaceAnchored(anchor, w, h, context);
            PaintImageAt(context, box.X, box.BottomY, box.Width, box.Height, img);
            Drawings.DrawingPlacement.RegisterFloat(anchor, box, context);
            // A float does not advance the cursor; surrounding text flows around it.
        }

        /// <summary>
        /// The operation prefix every picture-drop diagnostic carries. One stable string, because a
        /// host filtering its telemetry for dropped pictures should not have to know which of the
        /// three entry points (inline, anchored, table cell) the picture came through.
        /// </summary>
        private const string DecodeOperation = "ImageRenderer.Decode";

        // Reads the r:embed a picture names. A blip with no r:embed is not a malformed file: it is
        // an image linked with r:link only, whose bytes were never stored in the package. Either
        // way the document showed a picture here and the PDF will not, so it is reported rather
        // than dropped in silence.
        private static bool TryReadEmbedId(OpenXmlElement pictureContainer,
            out WpDrawing.Blip blip, out string embed)
        {
            blip = pictureContainer.Descendants<WpDrawing.Blip>().FirstOrDefault();
            embed = blip?.Embed?.Value;
            if (!string.IsNullOrEmpty(embed)) return true;

            DocxDiagnostics.Report(DecodeOperation, blip == null
                ? "a picture carries no a:blip, so it names no image to paint; nothing was painted"
                : "a picture's a:blip carries no r:embed — an image linked with r:link alone is "
                  + "not stored in the document — so nothing was painted");
            return false;
        }

        // Loads + decodes an image part into PDF-embeddable bytes (JPEG via DCTDecode, PNG
        // decoded to RGB via FlateDecode). Returns false for unsupported / oversized parts.
        //
        // Every false return here drops a picture the DOCX contained, and a dropped picture is
        // indistinguishable in the output from a document that never had one — so each reports its
        // own reason through the feature's sink (CLAUDE.md → Diagnostics Pattern, item 3). The
        // reason matters as much as the fact: "the relationship is stale", "the part is a 300 MB
        // zip bomb" and "the PNG is a format we cannot decode" call for three different fixes by
        // the caller.
        //
        // The relationship belongs to the part the blip lives in — the main document for a body
        // picture, but the header, footer or text-box part for one there. Ids are numbered per
        // part, so resolving against MainDocumentPart alone would throw, drop the picture, or
        // paint whatever different image happened to share the id. See OpenXmlPartLookup.
        //
        // Decoded once a part (context.DecodedImages): a part referenced n times was read, decoded and compressed n
        // times — a 3 KB DOCX naming one 1000 x 1000 PNG two hundred times took 20 s, and a part past the bomb cap
        // was read to the cap again for each reference. A part that failed reports its reason again, verbatim, for
        // each reference, as it did when it was decoded each time.
        private static bool TryDecode(OpenXmlElement blipOwner, string relationshipId,
            DocxRenderContext context, out DecodedImg img)
        {
            img = default;
            var part = OpenXmlPartLookup.Resolve(blipOwner, relationshipId,
                context.WordDocument.MainDocumentPart);
            if (part is ImagePart imagePart && context.DecodedImages.TryGetValue(imagePart, out object cached))
            {
                if (cached is DecodedImg decoded) { img = decoded; return true; }
                DocxDiagnostics.Report(DecodeOperation, (string)cached);
                return false;
            }

            string failure = TryDecodePart(part, relationshipId, out img);
            if (failure != null) DocxDiagnostics.Report(DecodeOperation, failure);
            if (part is ImagePart decodedPart) context.DecodedImages[decodedPart] = failure == null ? img : (object)failure;
            return failure == null;
        }

        /// <summary>The decode itself; null when <paramref name="img"/> holds the picture, else why nothing was painted.</summary>
        private static string TryDecodePart(OpenXmlPart part, string relationshipId, out DecodedImg img)
        {
            img = default;
            if (part is not ImagePart imagePart)
                return string.Format(CultureInfo.InvariantCulture,
                    part == null
                        ? "a picture's r:embed '{0}' resolves to no relationship on its own part or "
                          + "on the main document; nothing was painted"
                        : "a picture's r:embed '{0}' resolves to a {1} rather than an image part; "
                          + "nothing was painted",
                    relationshipId, part?.GetType().Name);

            // A DOCX is a ZIP; an image part can be a tiny deflated blob that expands to
            // gigabytes (zip bomb). Cap the decompressed size and skip the image.
            long MaxImageBytes = PdfLimits.MaxDocxPartBytes;
            byte[] imageData;
            using (var stream = imagePart.GetStream())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxImageBytes)
                        return string.Format(CultureInfo.InvariantCulture,
                            "the image part for r:embed '{0}' expands past the {1} byte cap that "
                                + "bounds a decompression bomb; nothing was painted",
                            relationshipId, MaxImageBytes);
                    ms.Write(buffer, 0, read);
                }
                imageData = ms.ToArray();
            }
            if (imageData.Length == 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "the image part for r:embed '{0}' is empty; nothing was painted", relationshipId);

            string contentType = imagePart.ContentType ?? "";
            bool isJpeg = contentType.Contains("jpeg") || contentType.Contains("jpg") || IsJpeg(imageData);
            if (isJpeg)
            {
                if (!RasterImageInspector.TryInspectJpeg(imageData, out var frame) || frame.Width <= 0 || frame.Height <= 0)
                    return string.Format(CultureInfo.InvariantCulture,
                        "the JPEG for r:embed '{0}' has no readable SOFn frame header, so its pixel "
                            + "size is unknown; nothing was painted",
                        relationshipId);
                // The frame header's size is what the PDF will declare, verbatim, so it is judged
                // first: a JPEG claiming 65 535 x 65 535 pixels must not go into the output as is.
                string tooLarge = AuthoredSizeRefusal(frame.Width, frame.Height, relationshipId);
                if (tooLarge != null) return tooLarge;
                // The frame says its colour space (Core.Imaging.JpegColorSpace): greyscale, YCbCr as
                // RGB, or Adobe's inverted CMYK with its /Decode; a frame it refuses is not embedded,
                // since a dictionary contradicting the codestream draws a negative or nothing.
                string colorSpace = JpegColorSpace.Of(frame, out string refused);
                if (colorSpace == null)
                    return string.Format(CultureInfo.InvariantCulture,
                        "the JPEG for r:embed '{0}' was not embedded: {1}; nothing was painted", relationshipId, refused);
                img = new DecodedImg
                {
                    Data = imageData, Filter = "DCTDecode", ColorSpace = colorSpace, Decode = JpegColorSpace.DecodeFor(frame),
                    Bpc = 8, PixelW = frame.Width, PixelH = frame.Height,
                };
                return null;
            }
            if (IsPng(imageData) &&
                PngImage.TryDecode(imageData, out byte[] rgb, out byte[] alpha, out int w2, out int h2))
            {
                string tooLarge = AuthoredSizeRefusal(w2, h2, relationshipId);
                if (tooLarge != null) return tooLarge;
                // The alpha travels as a real /SMask (§11.6.5.3). Composited over white inside the
                // decoder it would be right only where the image sits on bare page: a
                // transparent-background logo over a coloured shape, a table cell fill or another
                // image would draw a white rectangle around itself.
                img = new DecodedImg
                {
                    Data = PngImage.FlateCompress(rgb),
                    Filter = "FlateDecode",
                    ColorSpace = PdfColorSpaceNames.DeviceRGB,
                    Bpc = 8,
                    PixelW = w2,
                    PixelH = h2,
                    SoftMask = alpha,
                };
                return null;
            }

            // Unsupported / undecodable (GIF, BMP, EMF, a truncated or bombed PNG…). JPEG and PNG
            // are embeddable, so name what arrived instead.
            //
            // Interlaced PNG is not in that list: the decoder this path calls handles Adam7.
            return string.Format(CultureInfo.InvariantCulture,
                "the image for r:embed '{0}' (content type '{1}') is neither a JPEG nor a decodable "
                    + "PNG, so it could not be embedded; nothing was painted",
                relationshipId, contentType.Length == 0 ? "(none)" : contentType);
        }

        // Emits the image XObject at (x, yBottom) with size (w × h) in PDF user space.
        private static void PaintImageAt(DocxRenderContext context, double x, double yBottom,
            double w, double h, DecodedImg img)
        {
            var imgRes = context.RegisterImage(img.Data, img.PixelW, img.PixelH, img.Filter,
                                               img.ColorSpace, img.Bpc, img.SoftMask, img.Decode);
            context.AppendContent(string.Format(CultureInfo.InvariantCulture,
                "q\n{0:F2} 0 0 {1:F2} {2:F2} {3:F2} cm\n/{4} Do\nQ\n", w, h, x, yBottom, imgRes));
        }

        private struct DecodedImg
        {
            public byte[] Data;
            public string Filter;
            public string ColorSpace;
            public int Bpc;
            public int PixelW, PixelH;

            /// <summary>Raw 8-bit alpha, one byte per pixel; null when the image is opaque.</summary>
            public byte[] SoftMask;

            /// <summary>The <c>/Decode</c> array the samples need (an Adobe CMYK JPEG's inversion); null for the default.</summary>
            public double[] Decode;
        }

        // The one size rule every producer's image answers to (Core.Writing.AuthoredImageSize), asked
        // here so the picture is skipped and reported rather than aborting the conversion at the
        // writer, which refuses the same size.
        private static string AuthoredSizeRefusal(int width, int height, string relationshipId)
        {
            string refusal = Core.Writing.AuthoredImageSize.Refusal(width, height);
            return refusal == null ? null : string.Format(CultureInfo.InvariantCulture,
                "the image for r:embed '{0}' was not embedded: {1} Nothing was painted.",
                relationshipId, refusal);
        }
        private static bool IsJpeg(byte[] d)
            => RasterImageInspector.IsJpeg(d);

        private static bool IsPng(byte[] d)
            => RasterImageInspector.IsPng(d);
    }
}
