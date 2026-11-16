// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// An image XObject's pixels at its own size, as straight colour: what the page draws for it,
    /// in a form any program can keep — for a picture taken out of a document to be used again.
    /// </summary>
    /// <remarks>
    /// The pixels are the renderers' own decode of the image
    /// (<see cref="ContentStreamInterpreter.DecodeImageXObject(PdfDocument, PdfStream)"/>): every
    /// depth, <c>/Decode</c>, colour space, ICC profile and mask the library reads, so a picture
    /// read here is the picture a page shows. The colour of a pixel that is wholly transparent is
    /// not kept — it is nobody's to see — and reads as black.
    /// </remarks>
    public sealed class PdfImagePixels
    {
        private PdfImagePixels(int width, int height, byte[] rgb, byte[] alpha)
        {
            Width = width;
            Height = height;
            Rgb = rgb;
            Alpha = alpha;
        }

        /// <summary>Pixels a row.</summary>
        public int Width { get; }

        /// <summary>Rows.</summary>
        public int Height { get; }

        /// <summary>Red, green and blue of each pixel, three bytes a pixel, rows from the top.</summary>
        public byte[] Rgb { get; }

        /// <summary>The opacity of each pixel, a byte a pixel, rows from the top; null when every pixel is opaque.</summary>
        public byte[] Alpha { get; }

        [ThreadStatic] private static string _lastReadError;

        /// <summary>
        /// Why the most recent <see cref="Read"/> on this thread answered null; null when it read
        /// the image. Reset at the start of every call.
        /// </summary>
        public static string LastReadError => _lastReadError;

        /// <summary>
        /// The pixels of <paramref name="image"/>, an image XObject of <paramref name="document"/>;
        /// null when the library cannot decode it, with the reason in <see cref="LastReadError"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static PdfImagePixels Read(PdfDocument document, PdfStream image)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (image == null) throw new ArgumentNullException(nameof(image));
            _lastReadError = null;

            SKBitmap bitmap;
            try
            {
                bitmap = ContentStreamInterpreter.DecodeImageXObject(document, image);
            }
            catch (Exception ex) when (ex is PdfException || ex is System.IO.InvalidDataException || ex is System.IO.IOException
                                       || ex is ArgumentException || ex is NotSupportedException)
            {
                _lastReadError = ex.Message;
                return null;
            }
            if (bitmap == null)
            {
                _lastReadError = "the image could not be decoded.";
                return null;
            }

            using (bitmap)
            {
                int width = bitmap.Width, height = bitmap.Height;
                if (width <= 0 || height <= 0)
                {
                    _lastReadError = "the image has no pixels.";
                    return null;
                }
                var rgb = new byte[(long)width * height * 3];
                var alpha = new byte[(long)width * height];
                bool opaque = true;
                BitmapPixels.Read(bitmap, (row, y) =>
                {
                    int at = y * width;
                    for (int x = 0; x < row.Length; x++)
                    {
                        SKColor colour = row[x];
                        rgb[(at + x) * 3] = colour.Red;
                        rgb[(at + x) * 3 + 1] = colour.Green;
                        rgb[(at + x) * 3 + 2] = colour.Blue;
                        alpha[at + x] = colour.Alpha;
                        opaque &= colour.Alpha == 255;
                    }
                });
                return new PdfImagePixels(width, height, rgb, opaque ? null : alpha);
            }
        }
    }
}
