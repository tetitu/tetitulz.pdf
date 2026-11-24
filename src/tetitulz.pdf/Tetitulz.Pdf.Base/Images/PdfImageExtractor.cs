// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Extracts images from PDF documents.
    /// </summary>
    public static class PdfImageExtractor
    {
        // The most decoded image data this extractor decodes and writes per image: the decode is refused
        // at it, never inflated further and compared after.
        private const int MaxDecompressedImageBytes = 256 * 1024 * 1024;

        /// <summary>
        /// Eight bits per component: what <c>/BitsPerComponent</c> defaults to when absent
        /// (PDF 32000-1:2008, Table 89), and the only depth the PPM/PGM writers below emit.
        /// </summary>
        private const int EightBitsPerComponent = 8;

        /// <summary>The maximum sample value written into a PPM/PGM header for 8-bit samples.</summary>
        private const int NetpbmMaxSampleValue = 255;

        /// <summary>Reported <c>/ColorSpace</c> when the entry is missing or not a name.</summary>
        private const string UnknownColorSpace = "Unknown";

        [ThreadStatic] private static string _lastExtractionWarning;

        /// <summary>
        /// Which images the last extraction on this thread wrote as their encoded bytes because
        /// their stream could not be decoded, and why — the first twenty named, the rest counted —
        /// or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Such an image's <see cref="PdfImageInfo.Format"/> is <c>raw</c>, as is one whose pixels
        /// simply have no Netpbm form, so the format alone cannot tell a failed decode from a
        /// written one; the failure is reported.
        /// </remarks>
        public static string LastExtractionWarning => _lastExtractionWarning;

        /// <summary>
        /// Extracts all images from a PDF file and saves them to the output directory.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <param name="outputDirectory">Directory where extracted images will be saved.</param>
        /// <returns>List of information about each extracted image.</returns>
        public static List<PdfImageInfo> ExtractImages(string pdfPath, string outputDirectory)
        {
            _lastExtractionWarning = null;
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            if (outputDirectory == null) throw new ArgumentNullException(nameof(outputDirectory));

            using (var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ExtractImages(stream, outputDirectory);
            }
        }

        /// <summary>
        /// Extracts all images from a PDF stream and saves them to the output directory.
        /// </summary>
        public static List<PdfImageInfo> ExtractImages(Stream input, string outputDirectory)
        {
            _lastExtractionWarning = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (outputDirectory == null) throw new ArgumentNullException(nameof(outputDirectory));

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            using (var doc = PdfDocument.Open(input))
            {
                // An encrypted document's pages cannot be read here, and "no images" would be an
                // answer about a document this door never read.
                Operations.EncryptedRead.Refuse(doc, "the images", "PdfOperations.ExtractImages(Stream, string, string)");
                return ExtractImagesFromDocument(doc, outputDirectory);
            }
        }

        /// <summary>
        /// Each image XObject the pages reach, written once, in the order of the first page that
        /// reaches it — <see cref="Operations.Images.DocumentImageIndex"/>'s walk: a page's own
        /// <c>/Resources</c> and the form XObjects, tiling patterns, Type 3 fonts and annotation
        /// appearances under them.
        /// </summary>
        /// <remarks>
        /// This read each page's own <c>/XObject</c> dictionary and wrote every image in it, so an
        /// image the pages share was written once a page, and pages sharing one resource
        /// dictionary wrote pages × images files — 640 000 from a file of a hundred kilobytes — while
        /// an image drawn through a form was not written at all.
        /// </remarks>
        private static List<PdfImageInfo> ExtractImagesFromDocument(PdfDocument doc, string outputDirectory)
        {
            var entries = new List<(int Page, int Object, PdfStream Stream)>();
            foreach (var kvp in Operations.Images.DocumentImageIndex.Build(doc).Entries)
                entries.Add((kvp.Value.Pages.Count > 0 ? MinOf(kvp.Value.Pages) : 0, kvp.Key, kvp.Value.Stream));
            entries.Sort((a, b) => a.Page != b.Page ? a.Page.CompareTo(b.Page) : a.Object.CompareTo(b.Object));

            var results = new List<PdfImageInfo>();
            var undecoded = new Core.Util.DistinctStrings();
            var unwritten = new Core.Util.DistinctStrings();
            // One call's files together, under one total: Deflate's thousand to one makes a one-megabyte PDF of
            // image bombs a gigabyte of files, every one inside the per-image ceiling.
            long room = PdfLimits.MaxExtractedImageBytesPerCall;
            int imageIndex = 0;
            foreach (var (page, number, stream) in entries)
            {
                imageIndex++;
                var imageInfo = ExtractSingleImage(doc, stream, page + 1, imageIndex, outputDirectory, room, out string why, out bool pastTotal);
                if (pastTotal) unwritten.Add($"image {imageIndex} (object {number}, page {page + 1})");
                if (imageInfo != null) room -= imageInfo.FileSize;
                if (why != null)
                    undecoded.Add($"image {imageIndex} (object {number}, page {page + 1}): its stream could not be decoded ({why}); "
                                  + (imageInfo != null ? "its encoded bytes were written" : "nothing was written"));
                if (imageInfo != null)
                    results.Add(imageInfo);
            }
            if (undecoded.Count > 0) _lastExtractionWarning = undecoded.Joined("; ") + ".";
            if (unwritten.Count > 0)
                _lastExtractionWarning = (_lastExtractionWarning == null ? "" : _lastExtractionWarning + " ")
                    + "The images written reached PdfLimits.MaxExtractedImageBytesPerCall ("
                    + PdfLimits.MaxExtractedImageBytesPerCall.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes); not written: " + unwritten.Joined(", ") + ".";
            return results;
        }

        private static int MinOf(List<int> values)
        {
            int min = values[0];
            foreach (int v in values) if (v < min) min = v;
            return min;
        }

        private static PdfImageInfo ExtractSingleImage(PdfDocument doc, PdfStream imageStream,
            int pageNumber, int imageIndex, string outputDirectory, long room, out string undecodedBecause, out bool pastTotal)
        {
            undecodedBecause = null;
            pastTotal = false;
            int width  = (doc.ResolveObject(imageStream[PdfName.Width])  as PdfNumber)?.IntValue ?? 0;
            int height = (doc.ResolveObject(imageStream[PdfName.Height]) as PdfNumber)?.IntValue ?? 0;
            int bpc    = (doc.ResolveObject(imageStream[PdfName.BitsPerComponent]) as PdfNumber)?.IntValue ?? EightBitsPerComponent;

            // Get color space
            string colorSpace = UnknownColorSpace;
            var csObj = imageStream[PdfName.ColorSpace];
            if (csObj is PdfName csName)
                colorSpace = csName.Value;
            else if (csObj is PdfArray csArray && csArray.Count > 0 && csArray[0] is PdfName csFirstName)
                colorSpace = csFirstName.Value;
            else if (csObj is PdfReference csRef)
            {
                var resolved = doc.ResolveObject(csObj);
                if (resolved is PdfName resolvedName)
                    colorSpace = resolvedName.Value;
                else if (resolved is PdfArray resolvedArr && resolvedArr.Count > 0 && resolvedArr[0] is PdfName arrName)
                    colorSpace = arrName.Value;
            }

            // Determine format based on filter
            var filterObj = doc.ResolveObject(imageStream[PdfName.Filter]) ?? imageStream[PdfName.Filter];
            string filterName = GetFilterName(filterObj);
            string format;
            string extension;
            byte[] imageData;

            if (filterName == PdfName.DCTDecode.Value)
            {
                // Raw data IS a JPEG file
                format = "jpeg";
                extension = ".jpg";
                imageData = imageStream.RawData;
            }
            else if (filterName == PdfName.JPXDecode.Value)
            {
                // Raw data is JPEG2000
                format = "jp2";
                extension = ".jp2";
                imageData = imageStream.RawData;
            }
            else if (filterName == PdfName.CCITTFaxDecode.Value)
            {
                // CCITT fax compressed - save as raw TIFF-like data
                format = "ccitt";
                extension = ".raw";
                imageData = imageStream.RawData;
            }
            else
            {
                // FlateDecode or other - decode and save as raw pixel data or try to construct PPM
                try
                {
                    // Refused at the ceiling, never inflated past it; and uncached, so the document holds no quarter
                    // of a gigabyte an image once its file is written. The call's total is held below, on what is written.
                    if (!imageStream.TryGetDecodedDataUncached(MaxDecompressedImageBytes, out byte[] decoded, out string refused))
                        throw new PdfException(refused ?? "it does not decode");

                    // A chain ending in an image codec decodes to the codec's own bitstream — the
                    // pipeline passes DCT and JPX through — which is that format's file, not samples to
                    // wrap in a Netpbm header.
                    string last = LastFilterName(filterObj);
                    if (last == PdfName.DCTDecode.Value)
                    {
                        format = "jpeg";
                        extension = ".jpg";
                        imageData = decoded;
                    }
                    else if (last == PdfName.JPXDecode.Value)
                    {
                        format = "jp2";
                        extension = ".jp2";
                        imageData = decoded;
                    }
                    // Try to save as PPM (Portable Pixmap) for RGB, or PGM for grayscale
                    else if (colorSpace == PdfColorSpaceNames.DeviceRGB && bpc == EightBitsPerComponent && width > 0 && height > 0)
                    {
                        format = "ppm";
                        extension = ".ppm";
                        imageData = CreatePpm(decoded, width, height);
                    }
                    else if (colorSpace == PdfColorSpaceNames.DeviceGray && bpc == EightBitsPerComponent && width > 0 && height > 0)
                    {
                        format = "pgm";
                        extension = ".pgm";
                        imageData = CreatePgm(decoded, width, height);
                    }
                    else
                    {
                        format = "raw";
                        extension = ".raw";
                        imageData = decoded;
                    }
                }
                catch (Exception ex)
                {
                    // The encoded bytes are written instead, and said (LastExtractionWarning).
                    format = "raw";
                    extension = ".raw";
                    imageData = imageStream.RawData ?? Array.Empty<byte>();
                    undecodedBecause = ex.Message;
                }
            }

            if (imageData == null || imageData.Length == 0)
                return null;
            if (imageData.LongLength > room)
            {
                pastTotal = true;
                return null;
            }

            // Write the file
            string fileName = $"image_{imageIndex:D4}{extension}";
            string filePath = Path.Combine(outputDirectory, fileName);
            File.WriteAllBytes(filePath, imageData);

            return new PdfImageInfo
            {
                PageNumber = pageNumber,
                Width = width,
                Height = height,
                BitsPerComponent = bpc,
                ColorSpace = colorSpace,
                FilePath = filePath,
                Format = format,
                FileSize = imageData.Length
            };
        }

        /// <summary>The last filter a stream's <c>/Filter</c> names (a name, or an array's last element), or null.</summary>
        private static string LastFilterName(PdfObject filterObj)
            => filterObj is PdfName name ? name.Value
             : filterObj is PdfArray arr && arr.Count > 0 && arr[arr.Count - 1] is PdfName lastName ? lastName.Value
             : null;

        private static string GetFilterName(PdfObject filterObj)
        {
            if (filterObj is PdfName name)
                return name.Value;

            if (filterObj is PdfArray arr && arr.Count > 0)
            {
                if (arr.Count == 1)
                {
                    // Single-element array — same as a bare name.
                    if (arr[0] is PdfName singleName)
                        return singleName.Value;
                }
                // Multiple filters: the raw data has passed through all of them in order.
                // We cannot determine the final image format from any one filter alone, and
                // saving RawData directly would not be a valid image file. Return null so
                // the caller falls through to the full-decode path (GetDecodedData).
            }
            return null;
        }

        /// <summary>
        /// Creates a PPM (P6 binary) format image from raw RGB pixel data.
        /// </summary>
        private static byte[] CreatePpm(byte[] pixelData, int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                string header = $"P6\n{width} {height}\n{NetpbmMaxSampleValue}\n";
                byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                ms.Write(headerBytes, 0, headerBytes.Length);

                // In long: /Width and /Height are the file's, and 50000 x 50000 x 3 wrapped the int
                // negative, Math.Min handed Write a negative count, and the catch above saved the
                // image as ".raw" with no header.
                long expectedSize = (long)width * height * PdfColorSpaceNames.RgbComponents;
                int writeSize = (int)Math.Min(pixelData.Length, expectedSize);
                ms.Write(pixelData, 0, writeSize);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Creates a PGM (P5 binary) format image from raw grayscale pixel data.
        /// </summary>
        private static byte[] CreatePgm(byte[] pixelData, int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                string header = $"P5\n{width} {height}\n{NetpbmMaxSampleValue}\n";
                byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                ms.Write(headerBytes, 0, headerBytes.Length);

                // One byte per pixel: PGM is single-component and 8-bit. In long, as CreatePpm.
                long expectedSize = (long)width * height;
                int writeSize = (int)Math.Min(pixelData.Length, expectedSize);
                ms.Write(pixelData, 0, writeSize);

                return ms.ToArray();
            }
        }
    }
}
