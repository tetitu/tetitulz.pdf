// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

public class PdfImageExtractorTests
{
    [Fact]
    public void ExtractImages_PdfWithNoImages_ReturnsEmpty()
    {
        // Create a simple PDF with no images
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("No Images");
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            using var input = new MemoryStream(pdfBytes);
            var images = PdfImageExtractor.ExtractImages(input, outputDir);
            Assert.Empty(images);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ExtractImages_PdfWithJpegImage_ExtractsJpeg()
    {
        // Build a PDF that contains an inline JPEG image XObject
        byte[] pdfBytes = CreatePdfWithJpegImage();
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            using var input = new MemoryStream(pdfBytes);
            var images = PdfImageExtractor.ExtractImages(input, outputDir);
            Assert.Single(images);
            Assert.Equal("jpeg", images[0].Format);
            Assert.True(images[0].Width > 0);
            Assert.True(images[0].Height > 0);
            Assert.True(File.Exists(images[0].FilePath));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    /// Wave 17, R7.1: a raw image whose declared extent overflows <c>width * height * components</c>
    /// in an <c>int</c> is still written as a PPM or PGM — header from the dictionary, pixels as many
    /// as the stream holds. It used to wrap negative, hand <c>Write</c> a negative count, and the bare
    /// catch then saved the image as <c>.raw</c> with no header and no word said. The small cases pin
    /// the same two writers at an ordinary size.
    /// </summary>
    [Theory]
    [InlineData(50000, 50000, false)]
    [InlineData(2, 2, false)]
    [InlineData(50000, 50000, true)]
    [InlineData(4, 3, true)]
    public void ExtractImages_RawImage_IsWrittenAsNetpbmWhateverItsDeclaredExtent(int width, int height, bool gray)
    {
        byte[] pixels = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30 };
        byte[] pdfBytes = CreatePdfWithRawImage(width, height, pixels, gray ? "DeviceGray" : "DeviceRGB");
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            using var input = new MemoryStream(pdfBytes);
            var images = PdfImageExtractor.ExtractImages(input, outputDir);

            var image = Assert.Single(images);
            Assert.Equal(gray ? "pgm" : "ppm", image.Format);
            byte[] written = File.ReadAllBytes(image.FilePath);
            string magic = gray ? "P5" : "P6";
            byte[] header = Encoding.ASCII.GetBytes(magic + "\n" + width + " " + height + "\n255\n");
            Assert.Equal(header.Length + pixels.Length, written.Length);
            Assert.Equal(header, written.Take(header.Length).ToArray());
            Assert.Equal(pixels, written.Skip(header.Length).ToArray());
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    /// <b>Adversarial: forty pages inheriting one resource dictionary of forty images.</b> Every
    /// image in each page's dictionary was written once a page — pages × images files, 640 000 from a
    /// file of a hundred kilobytes at eight hundred of each; each image object is written once now,
    /// reported at the first page that reaches it.
    /// </summary>
    [Fact]
    public void AdversarialPagesSharingManyImages_WriteEachImageOnce()
    {
        const int pages = 40, images = 40;
        var bodies = new System.Collections.Generic.List<string>();
        var kids = new StringBuilder();
        var xobjects = new StringBuilder();
        int firstImage = 4, firstPage = firstImage + images, content = firstPage + pages;
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        for (int i = 0; i < images; i++) xobjects.Append("/Im").Append(i).Append(' ').Append(firstImage + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>");
        bodies.Add($"<< /XObject << {xobjects}>> >>");
        for (int i = 0; i < images; i++)
            bodies.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nabc\nendstream");
        for (int p = 0; p < pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R >>");
        bodies.Add("<< /Length 10 >>\nstream\n/Im0 Do   \nendstream");
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            using var input = new MemoryStream(Assemble(bodies.ToArray()));
            var extracted = PdfImageExtractor.ExtractImages(input, outputDir);

            Assert.Equal(images, extracted.Count);
            Assert.Equal(images, Directory.GetFiles(outputDir).Length);
            Assert.All(extracted, image => Assert.Equal(1, image.PageNumber));
            Assert.Equal(images, extracted.Select(image => image.FilePath).Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    /// The images the pages reach, not only those each page names itself: an image a page draws
    /// through a form XObject is extracted too, and one only the second page reaches says page 2.
    /// </summary>
    [Fact]
    public void ExtractImages_ReachesAnImageInAForm_AndReportsTheFirstPageThatReachesIt()
    {
        string image = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Resources << /XObject << /Fm0 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Resources << /XObject << /Im9 8 0 R >> >> >>",
            "<< /Length 8 >>\nstream\n/Fm0 Do\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Im0 7 0 R >> >> /Length 7 >>\nstream\n/Im0 Do\nendstream",
            image,
            image,
        };
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            using var input = new MemoryStream(Assemble(bodies));
            var extracted = PdfImageExtractor.ExtractImages(input, outputDir);

            Assert.Equal(new[] { 1, 2 }, extracted.Select(i => i.PageNumber).OrderBy(n => n).ToArray());
            Assert.All(extracted, i => Assert.Equal("pgm", i.Format));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    /// An image whose stream cannot be decoded is written as its encoded bytes and said, naming the
    /// image and the object; the next extraction on the thread starts clean. The control is the
    /// decodable image beside it, written as a PGM and not mentioned.
    /// </summary>
    [Fact]
    public void ExtractImages_AnUndecodableImage_IsWrittenEncoded_AndSaid()
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >> >>",
            "<< /Length 14 >>\nstream\n/Im0 Do /Im1 Do\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length 9 >>\nstream\nnot zlib!\nendstream",
        };
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            var extracted = PdfImageExtractor.ExtractImages(new MemoryStream(Assemble(bodies)), outputDir);

            Assert.Equal(new[] { "pgm", "raw" }, extracted.Select(i => i.Format).ToArray());
            Assert.Equal("not zlib!", File.ReadAllText(extracted[1].FilePath));
            string warning = PdfImageExtractor.LastExtractionWarning;
            Assert.Contains("image 2 (object 6, page 1)", warning, System.StringComparison.Ordinal);
            Assert.Contains("encoded bytes were written", warning, System.StringComparison.Ordinal);
            Assert.DoesNotContain("object 5", warning, System.StringComparison.Ordinal);

            PdfImageExtractor.ExtractImages(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("No Images")), outputDir);
            Assert.Null(PdfImageExtractor.LastExtractionWarning);

            // The path door clears it too, even when the file cannot be opened.
            PdfImageExtractor.ExtractImages(new MemoryStream(Assemble(bodies)), outputDir);
            Assert.NotNull(PdfImageExtractor.LastExtractionWarning);
            Assert.Throws<FileNotFoundException>(() =>
                PdfImageExtractor.ExtractImages(Path.Combine(outputDir, "absent.pdf"), outputDir));
            Assert.Null(PdfImageExtractor.LastExtractionWarning);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    /// <summary>Thirty undecodable images are each said; the slot names twenty and counts the rest.</summary>
    [Fact]
    public void AdversarialManyUndecodableImages_AreEachSaid_AndTheSlotIsCapped()
    {
        const int images = 30;
        var bodies = new System.Collections.Generic.List<string>();
        var xobjects = new StringBuilder();
        for (int i = 0; i < images; i++) xobjects.Append("/Im").Append(i).Append(' ').Append(5 + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>");
        bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /XObject << {xobjects}>> >> >>");
        bodies.Add("<< /Length 7 >>\nstream\n/Im0 Do\nendstream");
        for (int i = 0; i < images; i++)
            bodies.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length 9 >>\nstream\nnot zlib!\nendstream");
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_img_test_" + Path.GetRandomFileName());

        try
        {
            PdfImageExtractor.ExtractImages(new MemoryStream(Assemble(bodies.ToArray())), outputDir);

            string warning = PdfImageExtractor.LastExtractionWarning;
            Assert.Equal(20, System.Text.RegularExpressions.Regex.Matches(warning, "could not be decoded").Count);
            Assert.EndsWith("and 10 more.", warning, System.StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    private static byte[] Assemble(string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A one-page document whose only XObject is an unfiltered 8-bit image of the given declared size and device space.</summary>
    private static byte[] CreatePdfWithRawImage(int width, int height, byte[] pixels, string colorSpace)
    {
        string content = "q 100 0 0 100 50 600 cm /Img1 Do Q";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Img1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width " + width + " /Height " + height + " /ColorSpace /" + colorSpace
                + " /BitsPerComponent 8 /Length " + pixels.Length + " >>\nstream\n"
                + Encoding.Latin1.GetString(pixels) + "\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Creates a minimal PDF with a 2x2 JPEG image XObject.
    /// </summary>
    private static byte[] CreatePdfWithJpegImage()
    {
        // Minimal JPEG: 2x2 red image
        byte[] jpegData = CreateMinimalJpeg();

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2Offset = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3Offset = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
        pdf.Append("/Contents 4 0 R /Resources << /XObject << /Img1 5 0 R >> >> >>\nendobj\n");

        string contentStream = "q 100 0 0 100 50 600 cm /Img1 Do Q";
        int obj4Offset = pdf.Length;
        pdf.Append("4 0 obj\n");
        pdf.AppendFormat("<< /Length {0} >>\n", contentStream.Length);
        pdf.Append("stream\n");
        pdf.Append(contentStream);
        pdf.Append("\nendstream\nendobj\n");

        // We need to write the image object as binary data
        // First, write the header part as ASCII
        string imageHeader = $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegData.Length} >>\nstream\n";

        int obj5Offset = pdf.Length;

        // We'll build the final byte array combining text and binary parts
        byte[] textPart1 = Encoding.ASCII.GetBytes(pdf.ToString());
        byte[] headerBytes = Encoding.ASCII.GetBytes(imageHeader);
        byte[] endStreamBytes = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");

        // Build xref after we know the object 5 content
        int obj5End = textPart1.Length + headerBytes.Length + jpegData.Length + endStreamBytes.Length;

        var xrefPart = new StringBuilder();
        int xrefOffset = obj5End;
        xrefPart.Append("xref\n0 6\n");
        xrefPart.Append("0000000000 65535 f \n");
        xrefPart.AppendFormat("{0:D10} 00000 n \n", obj1Offset);
        xrefPart.AppendFormat("{0:D10} 00000 n \n", obj2Offset);
        xrefPart.AppendFormat("{0:D10} 00000 n \n", obj3Offset);
        xrefPart.AppendFormat("{0:D10} 00000 n \n", obj4Offset);
        xrefPart.AppendFormat("{0:D10} 00000 n \n", obj5Offset);
        xrefPart.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        xrefPart.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        byte[] xrefBytes = Encoding.ASCII.GetBytes(xrefPart.ToString());

        // Combine all parts
        using var ms = new MemoryStream();
        ms.Write(textPart1, 0, textPart1.Length);
        ms.Write(headerBytes, 0, headerBytes.Length);
        ms.Write(jpegData, 0, jpegData.Length);
        ms.Write(endStreamBytes, 0, endStreamBytes.Length);
        ms.Write(xrefBytes, 0, xrefBytes.Length);

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a minimal valid JPEG file (2x2 pixels, RGB).
    /// </summary>
    private static byte[] CreateMinimalJpeg()
    {
        // This is a minimal valid JPEG with SOI, APP0, DQT, SOF0, DHT, SOS, and EOI markers
        // 2x2 pixel red image
        return new byte[]
        {
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, // APP0
            0x00, 0x10, // Length 16
            0x4A, 0x46, 0x49, 0x46, 0x00, // JFIF\0
            0x01, 0x01, // Version 1.1
            0x00, // Aspect ratio units: none
            0x00, 0x01, // X density
            0x00, 0x01, // Y density
            0x00, 0x00, // Thumbnail 0x0
            0xFF, 0xDB, // DQT
            0x00, 0x43, // Length 67
            0x00, // Table 0, precision 8-bit
            // 64 quantization values (all 1 for simplicity)
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0xFF, 0xC0, // SOF0 (Baseline DCT)
            0x00, 0x0B, // Length 11
            0x08, // Precision 8
            0x00, 0x02, // Height 2
            0x00, 0x02, // Width 2
            0x01, // Number of components: 1 (grayscale for simplicity)
            0x01, 0x11, 0x00, // Component 1: ID=1, sampling=1x1, quant table=0
            0xFF, 0xC4, // DHT
            0x00, 0x1F, // Length 31
            0x00, // DC table 0
            0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B,
            0xFF, 0xC4, // DHT
            0x00, 0xB5, // Length 181
            0x10, // AC table 0
            0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03,
            0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D,
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
            0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
            0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
            0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
            0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
            0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
            0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
            0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
            0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
            0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
            0xFF, 0xDA, // SOS
            0x00, 0x08, // Length 8
            0x01, // Number of components
            0x01, 0x00, // Component 1, DC=0, AC=0
            0x00, 0x3F, 0x00, // Spectral selection: 0-63, approx 0
            0xFB, 0xD2, 0x8A, 0x28, 0x03, // Compressed data (minimal)
            0xFF, 0xD9  // EOI
        };
    }
}
