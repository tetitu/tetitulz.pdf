// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// N-up imposition: places multiple logical pages onto a single output sheet.
    ///
    /// Supported layouts:
    ///   2-up  — 2 pages side-by-side on one landscape sheet.
    ///   4-up  — 4 pages (2×2 grid) on one sheet.
    ///   6-up  — 6 pages (2×3 grid) on one sheet.
    ///   9-up  — 9 pages (3×3 grid) on one sheet.
    ///
    /// Each source page is placed as an XObject (Form XObject referencing its content
    /// stream) into the output sheet using a transformation matrix — the box a reader shows
    /// (the <c>/CropBox</c> inside the <c>/MediaBox</c>), turned by the page's <c>/Rotate</c>, with
    /// the annotations a reader shows drawn as their normal appearances.
    /// </summary>
    public static class PdfNup
    {
        /// <summary>The most cells (<c>cols × rows</c>) one sheet may hold.</summary>
        internal const int MaxCellsPerSheet = 10_000;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Produces a 2-up PDF (2 source pages per sheet, landscape).</summary>
        public static void Create2Up(Stream input, Stream output, NupOptions options = null)
            => CreateNup(input, output, 2, 1, options);

        /// <summary>Produces a 4-up PDF (2×2 grid per sheet).</summary>
        public static void Create4Up(Stream input, Stream output, NupOptions options = null)
            => CreateNup(input, output, 2, 2, options);

        /// <summary>Produces a 6-up PDF (3×2 grid: 3 columns, 2 rows per sheet).</summary>
        public static void Create6Up(Stream input, Stream output, NupOptions options = null)
            => CreateNup(input, output, 3, 2, options);

        /// <summary>Produces a 9-up PDF (3×3 grid per sheet).</summary>
        public static void Create9Up(Stream input, Stream output, NupOptions options = null)
            => CreateNup(input, output, 3, 3, options);

        /// <summary>
        /// Produces an N-up PDF with a custom grid.
        /// </summary>
        /// <param name="input">Source PDF.</param>
        /// <param name="output">Output PDF.</param>
        /// <param name="cols">Number of columns per sheet.</param>
        /// <param name="rows">Number of rows per sheet.</param>
        /// <param name="options">Layout options.</param>
        public static void CreateNup(
            Stream input, Stream output,
            int cols, int rows,
            NupOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (cols < 1) throw new ArgumentOutOfRangeException(nameof(cols));
            if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));
            // The product in long: 65536 × 65536 wrapped an int to 0, and the sheet count divided by it.
            if ((long)cols * rows > MaxCellsPerSheet)
                throw new ArgumentOutOfRangeException(nameof(rows),
                    $"{cols} × {rows} cells is past the {MaxCellsPerSheet} a sheet may hold.");
            if (options != null)
            {
                // A sheet side at or below 0 means "from the cells"; NaN is neither, and made a
                // sheet with no /MediaBox size. The margin is subtracted from every slot.
                NumericArguments.RequireFinite(options.SheetWidthPt, nameof(options), nameof(options.SheetWidthPt));
                NumericArguments.RequireFinite(options.SheetHeightPt, nameof(options), nameof(options.SheetHeightPt));
                NumericArguments.RequireNonNegative(options.MarginPt, nameof(options), nameof(options.MarginPt));
            }

            options = options ?? new NupOptions();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Fail closed, as every whole-file writer here does: the sheets are a new file with no
            // /Encrypt, so an encrypted source's pages would be copied out as ciphertext — or, with
            // the encryption module registered, decrypted into a file that is no longer protected.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Encrypted PDFs are not supported by PdfNup: the imposed sheets are written as a new, " +
                    "unencrypted file. Decrypt the document first (Tetitulz.Pdf.Encryption.PdfDecryptor), " +
                    "impose it, then re-encrypt if required.");

            int sourcePagesCount = doc.Pages.Count;
            int perSheet = cols * rows;

            // Collect source page dimensions (first page drives sheet size when Auto): the box a
            // reader shows (ResolveRenderBox: the /CropBox inside the /MediaBox), as it is shown —
            // its sides swapped by a /Rotate of a quarter turn.
            double srcW = PdfPageSizes.A4Width, srcH = PdfPageSizes.A4Height; // A4 default
            if (sourcePagesCount > 0)
            {
                ShownPage first = ShownPage.Of(doc.Pages[0], doc);
                srcW = first.ShownWidth;
                srcH = first.ShownHeight;
            }
            ISet<int> hiddenLayers = Rendering.PdfPageRenderer.BuildHiddenOcgSet(doc);

            // Sheet dimensions.
            double cellW = srcW / cols;
            double cellH = srcH / rows;
            // For 2-up row=1 we want landscape: sheet = srcW*cols × srcH
            double sheetW = options.SheetWidthPt > 0 ? options.SheetWidthPt : cellW * cols;
            double sheetH = options.SheetHeightPt > 0 ? options.SheetHeightPt : cellH * rows;
            cellW = sheetW / cols;
            cellH = sheetH / rows;

            double margin = options.MarginPt;
            double slotW = cellW - margin * 2;
            double slotH = cellH - margin * 2;

            int sheetCount = (sourcePagesCount + perSheet - 1) / perSheet;

            // The sheets are new objects numbered above the source's, and the file is written by what
            // they reach (PdfFullRewriter.WriteReachable): each form names its page's /Resources, so
            // the fonts, images and graphics states those name are copied and renumbered with it. A
            // resource dictionary carried alone, its references written as they were, would name the
            // wrong objects in a file numbered from 1.
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            int catalogObjNum = nextObjNum++;
            int pagesRootObjNum = nextObjNum++;

            var allObjects = new Dictionary<int, PdfObject>();
            var pageObjNums = new List<int>();

            for (int sheetIdx = 0; sheetIdx < sheetCount; sheetIdx++)
            {
                // Collect source pages for this sheet.
                var slotObjs = new List<(int slot, PdfPage page)>();
                for (int s = 0; s < perSheet; s++)
                {
                    int srcIdx = sheetIdx * perSheet + s;
                    if (srcIdx < sourcePagesCount)
                        slotObjs.Add((s, doc.Pages[srcIdx]));
                }

                // Build a content stream that places each source page as a Form XObject.
                var contentSb = new StringBuilder();
                var xobjResDict = new PdfDictionary();

                foreach (var (slot, page) in slotObjs)
                {
                    // Cell position (row-major, top-to-bottom).
                    int col = slot % cols;
                    int row = slot / cols;

                    // PDF Y=0 is bottom, so flip row ordering.
                    double cellX = col * cellW + margin;
                    double cellY = sheetH - (row + 1) * cellH + margin;

                    // The page as a reader shows it: the box it displays (the /CropBox inside the
                    // /MediaBox, §7.9.5-normalised), turned by its /Rotate. The form's /BBox is that
                    // box, so what the box hides stays hidden, and the placement turns the box onto
                    // the slot, where a /Rotate 90 page was imposed on its side.
                    ShownPage shown = ShownPage.Of(page, doc);
                    double pageW = shown.ShownWidth, pageH = shown.ShownHeight;
                    double scaleX = slotW / pageW;
                    double scaleY = slotH / pageH;
                    double scale = options.UniformScale ? Math.Min(scaleX, scaleY) : scaleX;
                    // Centre within slot when uniform scaling.
                    double offX = options.UniformScale ? (slotW - pageW * scale) / 2.0 : 0;
                    double offY = options.UniformScale ? (slotH - pageH * scale) / 2.0 : 0;

                    // Build content stream for this source page.
                    byte[] srcContent = page.GetContentStreamData() ?? Array.Empty<byte>();

                    // Write source page content as Form XObject.
                    int srcObjNum = nextObjNum++;
                    var srcStream = new PdfStream();
                    srcStream.RawData = srcContent;
                    srcStream.Set(PdfName.Length, new PdfNumber(srcContent.Length));
                    srcStream.Set(PdfName.Type, PdfName.XObject);
                    srcStream.Set(PdfName.Subtype, PdfName.Form);
                    srcStream.Set(PdfName.FormType, new PdfNumber(1));

                    var bbox = new PdfArray();
                    bbox.Add(new PdfNumber(shown.X)); bbox.Add(new PdfNumber(shown.Y));
                    bbox.Add(new PdfNumber(shown.X + shown.Width)); bbox.Add(new PdfNumber(shown.Y + shown.Height));
                    srcStream.Set(PdfName.BBox, bbox);

                    // The page's resources, inherited ones included; what they name is copied by the
                    // write below.
                    var pageRes = page.Resources;
                    if (pageRes != null)
                        srcStream.Set(PdfName.Resources, pageRes);

                    allObjects.Add(srcObjNum, srcStream);

                    string xobjName = $"SrcPg{srcObjNum}";
                    xobjResDict.Set(new PdfName(xobjName), new PdfReference(srcObjNum, 0));

                    // The page's space onto the slot: the turn that shows it, then the scale and the
                    // slot's corner.
                    double[] placement = TransformMatrix.Multiply(shown.ToShown,
                        new[] { scale, 0, 0, scale, cellX + offX, cellY + offY });

                    // Add cm (CTM) operator and Do to place the XObject.
                    contentSb.Append("q\n");
                    if (options.DrawBorder)
                    {
                        contentSb.Append(Inv("{0:F4} {1:F4} {2:F4} {3:F4} re S\n",
                            cellX - margin, cellY - margin, cellW, cellH));
                    }
                    contentSb.Append(Inv("{0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} cm\n",
                        placement[0], placement[1], placement[2], placement[3], placement[4], placement[5]));
                    contentSb.AppendFormat("/{0} Do\n", xobjName);
                    contentSb.Append("Q\n");

                    // The annotations a reader shows on the page — a filled field's value, a note, a
                    // stamp — drawn as their normal appearances, each fitted onto its /Rect as a
                    // reader fits it (§12.5.5) and placed with the page; an annotation that is hidden,
                    // on a layer that is off or a popup draws nothing, as in a render.
                    AppendAnnotationAppearances(page, doc, hiddenLayers, placement, contentSb, xobjResDict);
                }

                byte[] contentBytes = Core.Text.Latin1Text.Encode(contentSb.ToString());

                // Content stream object.
                int contentObjNum = nextObjNum++;
                var contentStream = new PdfStream();
                contentStream.RawData = contentBytes;
                contentStream.Set(PdfName.Length, new PdfNumber(contentBytes.Length));
                allObjects.Add(contentObjNum, contentStream);

                // Resources for this sheet page.
                var sheetResources = new PdfDictionary();
                sheetResources.Set(PdfName.XObject, xobjResDict);

                // Sheet page dictionary.
                int pageObjNum = nextObjNum++;
                var mediaBox = new PdfArray();
                mediaBox.Add(new PdfNumber(0)); mediaBox.Add(new PdfNumber(0));
                mediaBox.Add(new PdfNumber(sheetW)); mediaBox.Add(new PdfNumber(sheetH));

                var pageDict = new PdfDictionary();
                pageDict.Set(PdfName.Type, PdfName.Page);
                pageDict.Set(PdfName.Parent, new PdfReference(pagesRootObjNum, 0));
                pageDict.Set(PdfName.MediaBox, mediaBox);
                pageDict.Set(PdfName.Contents, new PdfReference(contentObjNum, 0));
                pageDict.Set(PdfName.Resources, sheetResources);
                allObjects.Add(pageObjNum, pageDict);
                pageObjNums.Add(pageObjNum);
            }

            // Pages tree root — one implementation, in Core.Writing.PdfPageTreeWriter.
            allObjects.Add(pagesRootObjNum, PdfPageTreeWriter.BuildRoot(pageObjNums, pagesRootObjNum));

            var catalogDict = new PdfDictionary();
            catalogDict.Set(PdfName.Type, PdfName.Catalog);
            catalogDict.Set(PdfName.Pages, new PdfReference(pagesRootObjNum, 0));
            allObjects.Add(catalogObjNum, catalogDict);

            PdfFullRewriter.WriteReachable(doc, output, catalogObjNum, 0, allObjects,
                                           PdfTrailerBuilder.NewFileId(), "PdfNup.CreateNup");
        }

        /// <summary>
        /// Invokes each presented annotation's normal appearance stream in the sheet's content,
        /// under its §12.5.5 fit and the page's <paramref name="placement"/>: the stream is a form
        /// XObject already, named in <paramref name="xobjects"/> by reference, so the write copies it
        /// with what it reaches. A dropped appearance — a filled field's value, a comment — would
        /// leave the sheet saying less than the page.
        /// </summary>
        private static void AppendAnnotationAppearances(PdfPage page, PdfDocument doc, ISet<int> hiddenLayers,
                                                        double[] placement, StringBuilder content, PdfDictionary xobjects)
        {
            if (doc.ResolveObject(page.Dictionary[PdfName.Annots]) is not PdfArray annots) return;
            int walked = 0;
            foreach (PdfObject entry in annots)
            {
                if (++walked > PdfLimits.MaxAnnotationsPerPage) break;
                if (doc.ResolveObject(entry) is not PdfDictionary annot) continue;
                if (Rendering.PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value == PdfAnnotationSubtypes.Popup) continue;
                if (Rendering.AnnotationVisibility.IsHidden(annot, doc, null)) continue;
                if (Rendering.ContentStreamInterpreter.IsAnnotationOcHidden(annot, hiddenLayers, doc)) continue;

                PdfStream appearance = Rendering.AnnotationAppearance.NormalAppearance(doc, annot);
                if (appearance == null || appearance.ObjectNumber <= 0) continue;
                var rect = doc.ResolveObject(annot[PdfName.Rect]) as PdfArray;
                if (!Rendering.AnnotationAppearance.TryFitToRect(doc, rect, appearance, out float[] fit, out _, out _)) continue;

                string name = $"Ap{appearance.ObjectNumber}";
                xobjects.Set(new PdfName(name), new PdfReference(appearance.ObjectNumber, appearance.GenerationNumber));
                // The fit, then the page's placement; the stream's own /Matrix is Do's (§8.10.1).
                double[] m = TransformMatrix.Multiply(
                    new[] { (double)fit[0], fit[1], fit[2], fit[3], fit[4], fit[5] }, placement);
                content.Append(Inv("q {0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} cm /{6} Do Q\n",
                    m[0], m[1], m[2], m[3], m[4], m[5], name));
            }
        }

        /// <summary>
        /// A source page as a reader shows it: the box it displays, with its origin and extent, the
        /// extent as shown (the sides swapped by a quarter turn), and the matrix taking the box's
        /// space onto <c>[0, ShownWidth] × [0, ShownHeight]</c> — §7.7.3.3's clockwise turn.
        /// </summary>
        private readonly struct ShownPage
        {
            internal readonly double X, Y, Width, Height, ShownWidth, ShownHeight;
            internal readonly double[] ToShown;

            private ShownPage(double x, double y, double width, double height, double shownWidth, double shownHeight, double[] toShown)
            {
                X = x; Y = y; Width = width; Height = height; ShownWidth = shownWidth; ShownHeight = shownHeight; ToShown = toShown;
            }

            internal static ShownPage Of(PdfPage page, PdfDocument doc)
            {
                var box = Rendering.PageBoxMetrics.Of(Rendering.PdfPageRenderer.ResolveRenderBox(page, doc, Rendering.PdfRenderOptions.Default));
                int rotation = PdfPageRotation.Normalize(page.Rotate, out _);
                double x = box.X, y = box.Y, w = box.Width, h = box.Height;
                // The box's corner to the origin, then the turn: a 90° turn takes the box's top-left
                // corner to the shown page's top-right (the same matrices the SVG export turns by).
                double[] toShown = rotation switch
                {
                    90  => new[] { 0.0, -1, 1, 0, -y, w + x },
                    180 => new[] { -1.0, 0, 0, -1, w + x, h + y },
                    270 => new[] { 0.0, 1, -1, 0, h + y, -x },
                    _   => new[] { 1.0, 0, 0, 1, -x, -y },
                };
                bool swap = PdfPageRotation.SwapsAxes(rotation);
                return new ShownPage(x, y, w, h, swap ? h : w, swap ? w : h, toShown);
            }
        }

        // Formats with InvariantCulture so {0:F4} etc. always emit '.' as the
        // decimal separator; CurrentCulture would produce commas in cm/re operands —
        // corrupting the imposed sheet — on comma-decimal locales.
        private static string Inv(string fmt, params object[] args) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args);
    }

    /// <summary>Options for N-up imposition.</summary>
    public sealed class NupOptions
    {
        /// <summary>
        /// Output sheet width in points. 0 = auto (derived from source page size × columns).
        /// </summary>
        public double SheetWidthPt { get; set; } = 0;

        /// <summary>
        /// Output sheet height in points. 0 = auto (derived from source page size × rows).
        /// </summary>
        public double SheetHeightPt { get; set; } = 0;

        /// <summary>Margin between adjacent cells and from sheet edges, in points (default 4).</summary>
        public double MarginPt { get; set; } = 4;

        /// <summary>
        /// When true, scales each source page uniformly (preserving aspect ratio).
        /// Default: true.
        /// </summary>
        public bool UniformScale { get; set; } = true;

        /// <summary>
        /// When true, draws a thin border around each cell slot.
        /// Default: false.
        /// </summary>
        public bool DrawBorder { get; set; } = false;
    }
}
