// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.IO;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// One stamp placed on a page: its PNG (the bytes written into the PDF, and a bitmap to draw on
    /// screen) and where it sits, in the page's PDF coordinates.
    /// </summary>
    internal sealed class StampItem : IDisposable
    {
        public StampItem(string imagePath, int pageIndex, RectangleF pdfRect)
        {
            ImagePath = imagePath;
            ImageBytes = File.ReadAllBytes(imagePath);
            using (var ms = new MemoryStream(ImageBytes))
            using (var img = Image.FromStream(ms))
                Bitmap = new Bitmap(img);
            PageIndex = pageIndex;
            PdfRect = pdfRect;
        }

        /// <summary>The picture's file; stamps from one file share its samples when written.</summary>
        public string ImagePath { get; }

        public byte[] ImageBytes { get; }
        public Bitmap Bitmap { get; }
        public int PageIndex { get; }

        /// <summary>Where the stamp sits, in the page's PDF coordinates (Top is the smaller y).</summary>
        public RectangleF PdfRect { get; set; }

        /// <summary>The image's width over its height, which a corner drag keeps.</summary>
        public float Aspect => Bitmap.Height == 0 ? 1f : (float)Bitmap.Width / Bitmap.Height;

        public void Dispose() => Bitmap.Dispose();
    }
}
