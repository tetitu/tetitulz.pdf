// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Tetitulz.Pdf.DocxToPdf.Tests.Helpers
{
    /// <summary>Builds DOCX files that embed an inline image, plus tiny PNG/JPEG byte blobs.</summary>
    internal static class DocxImageBuilder
    {
        public static byte[] CreateDocxWithImage(byte[] imageBytes, string contentType,
            long cxEmu = 914400, long cyEmu = 914400)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var imagePart = mainPart.AddImagePart(contentType);
                using (var s = new MemoryStream(imageBytes))
                    imagePart.FeedData(s);
                string relId = mainPart.GetIdOfPart(imagePart);

                var drawing = BuildInlineDrawing(relId, cxEmu, cyEmu);
                mainPart.Document = new Document(new Body(new Paragraph(new Run(drawing))));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        internal static Drawing BuildInlineDrawing(string relId, long cx, long cy)
        {
            return new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.DocProperties { Id = 1U, Name = "Picture 1" },
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "img.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = cx, Cy = cy }),
                                    new A.PresetGeometry(new A.AdjustValueList())
                                    { Preset = A.ShapeTypeValues.Rectangle })))
                        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
        }

        // ---- Tiny image blobs ----

        /// <summary>A valid w×h 8-bit truecolour (RGB) PNG filled with one colour.</summary>
        public static byte[] MakeTruecolorPng(int w, int h, byte r, byte g, byte b)
        {
            // Raw scanlines: each row is a filter byte (0 = none) + w*RGB.
            var raw = new MemoryStream();
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0);
                for (int x = 0; x < w; x++) { raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); }
            }
            byte[] idat = ZlibCompress(raw.ToArray());

            var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, w); WriteBE32(ihdr, 4, h);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // colour type: truecolour
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", idat);
            WriteChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        /// <summary>
        /// A truecolour-with-alpha PNG (colour type 6) whose alpha varies along x, so a mask that
        /// is uniformly anything — including the all-opaque one a dropped alpha channel produces —
        /// fails against it.
        /// </summary>
        public static byte[] MakeAlphaPng(int w, int h, byte r, byte g, byte b, Func<int, byte> alphaAt)
        {
            var raw = new MemoryStream();
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0);                       // filter: none
                for (int x = 0; x < w; x++)
                {
                    raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b);
                    raw.WriteByte(alphaAt(x));
                }
            }
            byte[] idat = ZlibCompress(raw.ToArray());

            var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, w); WriteBE32(ihdr, 4, h);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // colour type: truecolour + alpha
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", idat);
            WriteChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        /// <summary>A PNG that declares a tiny 2×2 image but whose IDAT inflates to
        /// ~2 MB — a decompression bomb for testing the bounded inflate.</summary>
        public static byte[] MakeBombPng()
        {
            byte[] idat = ZlibCompress(new byte[2_000_000]);
            var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, 2); WriteBE32(ihdr, 4, 2);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // truecolour
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", idat);
            WriteChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        /// <summary>
        /// A minimal JPEG header with a valid SOF0 frame (width×height, <paramref name="components"/>
        /// of them, an Adobe APP14 segment before it when <paramref name="adobe"/>) — enough for the
        /// frame to be read and the file embedded as DCTDecode (not a renderable photo).
        /// </summary>
        public static byte[] MakeJpegHeader(int width, int height, int components = 3, bool adobe = false)
        {
            var bytes = new System.Collections.Generic.List<byte> { 0xFF, 0xD8 };          // SOI
            if (adobe)
                bytes.AddRange(new byte[]
                {
                    0xFF, 0xEE, 0x00, 0x0E,                                                // APP14, length 14
                    (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e',
                    0x00, 0x64, 0x00, 0x00, 0x00, 0x00,                                    // version 100, flags
                    0x00,                                                                  // transform: none (CMYK)
                });
            int length = 8 + 3 * components;
            bytes.AddRange(new byte[]
            {
                0xFF, 0xC0, (byte)(length >> 8), (byte)length, 0x08,                       // SOF0, precision 8
                (byte)(height >> 8), (byte)(height & 0xFF),                                // height
                (byte)(width >> 8), (byte)(width & 0xFF),                                  // width
                (byte)components,
            });
            for (int c = 1; c <= components; c++)
                bytes.AddRange(new byte[] { (byte)c, 0x11, (byte)(c == 1 ? 0 : 1) });     // id, sampling, quantisation table
            bytes.AddRange(new byte[] { 0xFF, 0xD9 });                                     // EOI
            return bytes.ToArray();
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(data, 0, data.Length);
            return output.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBE32(len, 0, data.Length);
            s.Write(len, 0, 4);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, data);
            byte[] crcBytes = new byte[4];
            WriteBE32(crcBytes, 0, (int)crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBE32(byte[] buf, int off, int v)
        {
            buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            void Feed(byte[] b)
            {
                foreach (byte by in b)
                {
                    crc ^= by;
                    for (int k = 0; k < 8; k++)
                        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }
            Feed(type); Feed(data);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
