// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>A stamp picture's straight RGB samples and its alpha, row by row from the top.</summary>
    internal sealed class StampPixels
    {
        public StampPixels(int width, int height, byte[] rgb, byte[] alpha)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A stamp picture has at least one pixel.");
            if (rgb == null || rgb.Length != (long)width * height * 3) throw new ArgumentException("Three samples a pixel.", nameof(rgb));
            if (alpha == null || alpha.Length != (long)width * height) throw new ArgumentException("One alpha sample a pixel.", nameof(alpha));
            Width = width;
            Height = height;
            Rgb = rgb;
            Alpha = alpha;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Rgb { get; }
        public byte[] Alpha { get; }
    }

    /// <summary>One stamp to write: the page, where on it (PDF coordinates, Top the smaller y), and its picture.</summary>
    internal sealed class StampPlacement
    {
        public StampPlacement(int pageIndex, RectangleF pdfRect, StampPixels pixels)
        {
            PageIndex = pageIndex;
            PdfRect = pdfRect;
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        }

        public int PageIndex { get; }
        public RectangleF PdfRect { get; }
        public StampPixels Pixels { get; }
    }

    /// <summary>
    /// Draws stamps into a document's pages: every stamped page in one appended revision, each picture
    /// upright as the page is shown, with its transparency.
    /// </summary>
    /// <remarks>
    /// One revision, not one a page: each revision is written over the whole file before it, and a
    /// signature applied afterwards covers it either way. A picture several stamps show is written
    /// into the file once.
    /// </remarks>
    internal static class StampWriter
    {
        /// <summary>
        /// The document with <paramref name="stamps"/> drawn in. <paramref name="rotationOf"/> gives a
        /// page's <c>/Rotate</c> (0, 90, 180 or 270), which decides which way up a picture is drawn.
        /// No stamps returns <paramref name="pdf"/> itself.
        /// </summary>
        public static byte[] Apply(byte[] pdf, IReadOnlyList<StampPlacement> stamps, Func<int, int> rotationOf)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (stamps == null) throw new ArgumentNullException(nameof(stamps));
            if (rotationOf == null) throw new ArgumentNullException(nameof(rotationOf));
            if (stamps.Count == 0) return pdf;

            var builders = new Dictionary<int, Action<Tetitulz.Pdf.Core.Writing.PdfContentStreamBuilder>>();
            foreach (var group in stamps.GroupBy(s => s.PageIndex))
            {
                int rotation = rotationOf(group.Key);
                var onPage = group.ToList();
                builders[group.Key] = b =>
                {
                    foreach (StampPlacement stamp in onPage)
                    {
                        StampPixels p = stamp.Pixels;
                        string name = b.AddImage(p.Rgb, p.Width, p.Height, null, "DeviceRGB", 8, null, p.Alpha);
                        double[] m = Placement.UprightImageMatrix(stamp.PdfRect, rotation);
                        b.SaveState();
                        b.Transform(m[0], m[1], m[2], m[3], m[4], m[5]);
                        b.DrawImage(name, 0, 0, 1, 1);
                        b.RestoreState();
                    }
                };
            }

            using (var input = new MemoryStream(pdf, writable: false))
            using (var output = new MemoryStream())
            {
                PdfPageContentAppender.AppendContentToPages(input, output, builders);
                return output.ToArray();
            }
        }
    }
}
