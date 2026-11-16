// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>Pixel-level comparison of two rendered PDFs.</summary>
    public static class PdfVisualDiff
    {
        /// <summary>
        /// Default rasterization resolution for the comparison, in dots per inch.
        /// Matches <see cref="Tetitulz.Pdf.Core.Constants.PdfUnits.CssPixelsPerInch"/> —
        /// enough detail to catch layout drift without the cost of a print-quality render.
        /// </summary>
        public const int DefaultDiffDpi = 96;

        public sealed class PageDiff
        {
            public int PageIndex { get; }
            public int DifferingPixels { get; }
            public int TotalPixels { get; }
            public float DifferencePct => TotalPixels == 0 ? 0f : (float)DifferingPixels / TotalPixels * 100f;

            public PageDiff(int pageIndex, int differingPixels, int totalPixels)
            {
                PageIndex = pageIndex;
                DifferingPixels = differingPixels;
                TotalPixels = totalPixels;
            }
        }

        public sealed class Result
        {
            public IReadOnlyList<PageDiff> Pages { get; }
            public int? PageCountMismatchLeft { get; }
            public int? PageCountMismatchRight { get; }
            public float Threshold { get; }
            public bool AreEqual { get; }

            public Result(IReadOnlyList<PageDiff> pages, int? left, int? right, float threshold)
            {
                Pages = pages;
                PageCountMismatchLeft = left;
                PageCountMismatchRight = right;
                Threshold = threshold;
                bool allWithin = true;
                if (left.HasValue || right.HasValue) allWithin = false;
                if (allWithin)
                    foreach (var p in pages)
                        if (p.DifferencePct > threshold) { allWithin = false; break; }
                AreEqual = allWithin;
            }
        }

        /// <summary>Refuses an encrypted side rather than rendering blank pages and calling them equal.</summary>
        private static void RefuseEncrypted(Stream side, string which)
        {
            if (!PdfHelpers.IsEncryptedDocument(side)) return;

            throw new Core.PdfException(
                "The " + which + " document is encrypted, so it renders as no pages at all and " +
                "comparing it would report two different documents as identical. Decrypt each " +
                "side through Tetitulz.Pdf.Base.Encryption.PdfDecryptedInput and compare the " +
                "results, or render each with PdfRenderOptions.Password and compare the images.");
        }

        /// <summary>
        /// Renders each page of both PDFs and counts pixels that differ by more than
        /// a per-channel tolerance. Default threshold: 2 % of pixels per page.
        /// </summary>
        public static Result Compare(Stream left, Stream right, int dpi = DefaultDiffDpi, float thresholdPct = 2f, int pixelTolerance = 8)
        {
            if (left == null)  throw new ArgumentNullException(nameof(left));
            if (right == null) throw new ArgumentNullException(nameof(right));
            // A verdict's parameters: a NaN threshold compares false and called every pair unequal,
            // a negative tolerance made every pixel differ, and dpi 0 failed inside the render.
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be positive.");
            NumericArguments.RequireNonNegative(thresholdPct, nameof(thresholdPct));
            if (pixelTolerance < 0) throw new ArgumentOutOfRangeException(nameof(pixelTolerance), pixelTolerance, "pixelTolerance must be at least 0.");

            // An encrypted document has no readable catalog, so it measures zero pages and two
            // different ones compare EQUAL. Same failure and same reason as PdfComparer.CompareText:
            // a verdict method that cannot read its input must refuse, not answer. The rendering
            // stack carries PdfRenderOptions.Password, but there is one options set here and two
            // documents, so it cannot serve a pair with different passwords.
            RefuseEncrypted(left, "left");
            RefuseEncrypted(right, "right");

            int leftPages, rightPages;
            using (var l = CopyToMemory(left))
            using (var pdfL = PdfDocument.Open(l))
                leftPages = pdfL.Pages.Count;
            using (var r = CopyToMemory(right))
            using (var pdfR = PdfDocument.Open(r))
                rightPages = pdfR.Pages.Count;

            int common = Math.Min(leftPages, rightPages);
            var pageDiffs = new List<PageDiff>(common);

            for (int i = 0; i < common; i++)
            {
                left.Position = 0;
                right.Position = 0;
                // Pixels, not PNG: encoding is most of a render (RenderedPage's remarks), and this
                // encoded both pages only to decode them again and read them a GetPixel at a time.
                // A page renders on an opaque background, so its premultiplied bytes are its colours.
                RenderedPage lp = PdfPageRenderer.RenderPageToPixels(left, i, dpi);
                RenderedPage rp = PdfPageRenderer.RenderPageToPixels(right, i, dpi);
                pageDiffs.Add(DiffPages(i, lp, rp, pixelTolerance));
            }

            int? mismatchL = leftPages != rightPages ? leftPages  : (int?)null;
            int? mismatchR = leftPages != rightPages ? rightPages : (int?)null;
            return new Result(pageDiffs, mismatchL, mismatchR, thresholdPct);
        }

        private static MemoryStream CopyToMemory(Stream s)
        {
            var ms = new MemoryStream();
            s.Position = 0;
            s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        private static PageDiff DiffPages(int pageIndex, RenderedPage lb, RenderedPage rb, int tolerance)
        {
            if (lb == null || rb == null)
                return new PageDiff(pageIndex, 0, 0);

            int w = Math.Min(lb.Width, rb.Width);
            int h = Math.Min(lb.Height, rb.Height);
            int differing = 0;
            for (int y = 0; y < h; y++)
            {
                int la = y * lb.Stride, ra = y * rb.Stride;
                for (int x = 0; x < w; x++, la += 4, ra += 4)
                {
                    if (Math.Abs(lb.Pixels[la] - rb.Pixels[ra]) > tolerance ||
                        Math.Abs(lb.Pixels[la + 1] - rb.Pixels[ra + 1]) > tolerance ||
                        Math.Abs(lb.Pixels[la + 2] - rb.Pixels[ra + 2]) > tolerance)
                        differing++;
                }
            }

            // What lies outside the compared rectangle on either side has no counterpart on the
            // other and so differs — all of it, on both sides. A portrait page against a landscape
            // one of the same area has a difference of the two areas of 0 and would otherwise
            // compare equal on the common square alone.
            int leftArea = lb.Width * lb.Height, rightArea = rb.Width * rb.Height, shared = w * h;
            int outside = (leftArea - shared) + (rightArea - shared);
            int total = leftArea + rightArea - shared;
            differing += outside;
            return new PageDiff(pageIndex, differing, total);
        }
    }
}
