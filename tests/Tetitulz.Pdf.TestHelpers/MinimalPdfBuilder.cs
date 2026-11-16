// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Builds minimal valid PDF files in-memory for testing.
/// </summary>
public static class MinimalPdfBuilder
{
    public static byte[] CreateMinimalPdf(string textContent = "Hello World", string? contentStream = null)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        contentStream ??= $"BT /F1 12 Tf 100 700 Td ({textContent}) Tj ET";

        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2Offset = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3Offset = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
        pdf.Append("/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\n");
        pdf.Append("endobj\n");

        int obj4Offset = pdf.Length;
        pdf.Append("4 0 obj\n");
        pdf.AppendFormat("<< /Length {0} >>\n", contentStream.Length);
        pdf.Append("stream\n");
        pdf.Append(contentStream);
        pdf.Append("\nendstream\nendobj\n");

        int obj5Offset = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n0 6\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj5Offset);

        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreateMultiPagePdf(int pageCount)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int fontObjNum = 3;
        int firstPageObj = 4;
        int firstContentObj = firstPageObj + pageCount;
        int totalObjects = firstContentObj + pageCount;

        var offsets = new int[totalObjects + 1];

        offsets[1] = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [");
        for (int i = 0; i < pageCount; i++)
        {
            if (i > 0) pdf.Append(" ");
            pdf.AppendFormat("{0} 0 R", firstPageObj + i);
        }
        pdf.AppendFormat("] /Count {0} >>\nendobj\n", pageCount);

        offsets[3] = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var contentTexts = new string[pageCount];
        for (int i = 0; i < pageCount; i++)
            contentTexts[i] = $"BT /F1 12 Tf 100 700 Td (Page {i + 1}) Tj ET";

        for (int i = 0; i < pageCount; i++)
        {
            offsets[firstPageObj + i] = pdf.Length;
            pdf.AppendFormat("{0} 0 obj\n", firstPageObj + i);
            pdf.Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
            pdf.AppendFormat("/Contents {0} 0 R ", firstContentObj + i);
            pdf.AppendFormat("/Resources << /Font << /F1 {0} 0 R >> >> >>\n", fontObjNum);
            pdf.Append("endobj\n");
        }

        for (int i = 0; i < pageCount; i++)
        {
            offsets[firstContentObj + i] = pdf.Length;
            pdf.AppendFormat("{0} 0 obj\n", firstContentObj + i);
            pdf.AppendFormat("<< /Length {0} >>\n", contentTexts[i].Length);
            pdf.Append("stream\n");
            pdf.Append(contentTexts[i]);
            pdf.Append("\nendstream\nendobj\n");
        }

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n");
        pdf.AppendFormat("0 {0}\n", totalObjects);
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i < totalObjects; i++)
            pdf.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

        pdf.Append("trailer\n");
        pdf.AppendFormat("<< /Size {0} /Root 1 0 R >>\n", totalObjects);
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// A one-page document drawing a <paramref name="imgWidth"/>×<paramref name="imgHeight"/> JPEG.
    ///
    /// <para><b>The drawn size defaults to the pixel count, which pins the image at exactly 72 dpi.</b>
    /// That matters for any test about resolution: §8.9.5.2 maps an image onto the unit square of
    /// user space, so its effective dpi is <c>pixels × 72 / points drawn</c> and nothing else. At the
    /// default there is no dpi to reduce, so <c>ImageOptimizationOptions.TargetDpi</c> — 150, 200 and
    /// 300 in the presets — can never engage. Use the overload taking an explicit drawn size to test
    /// downsampling.</para>
    /// </summary>
    public static byte[] CreatePdfWithImage(int imgWidth = 100, int imgHeight = 100) =>
        CreatePdfWithImage(imgWidth, imgHeight, imgWidth, imgHeight);

    /// <summary>
    /// As above, but drawn at <paramref name="drawWidthPoints"/> × <paramref name="drawHeightPoints"/>
    /// user-space points, so the caller chooses the effective resolution:
    /// <c>dpi = imgWidth × 72 / drawWidthPoints</c>. Drawing a 400-pixel image across 96 points is
    /// 300 dpi.
    /// </summary>
    public static byte[] CreatePdfWithImage(int imgWidth, int imgHeight,
        double drawWidthPoints, double drawHeightPoints)
    {
        byte[] jpegData = CreateMinimalJpeg(imgWidth, imgHeight);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        w.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));

        var offsets = new long[8];

        offsets[1] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));

        offsets[2] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));

        offsets[3] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>\n" +
            "endobj\n"));

        string drawW = drawWidthPoints.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string drawH = drawHeightPoints.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string contentStream = $"q {drawW} 0 0 {drawH} 100 600 cm /Im0 Do Q BT /F1 12 Tf 100 500 Td (Image Test) Tj ET";
        offsets[4] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n"));
        w.Write(Encoding.ASCII.GetBytes(contentStream));
        w.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        offsets[5] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));

        offsets[6] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes(
            $"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgWidth} /Height {imgHeight} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegData.Length} >>\nstream\n"));
        w.Write(jpegData);
        w.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        long xrefOffset = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("xref\n0 7\n"));
        w.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
        for (int i = 1; i <= 6; i++)
            w.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} 00000 n \n"));

        w.Write(Encoding.ASCII.GetBytes("trailer\n<< /Size 7 /Root 1 0 R >>\n"));
        w.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n"));

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] CreateMinimalJpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 255 / Math.Max(1, width - 1));
                byte g = (byte)(y * 255 / Math.Max(1, height - 1));
                byte b = 128;
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor(r, g, b));
            }
        }
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    public static byte[] CreatePdfWithDuplicateObjects()
    {
        const string fontDict = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        string content1 = "BT /F1 12 Tf 100 700 Td (Page 1) Tj ET";
        string content2 = "BT /F1 12 Tf 100 700 Td (Page 2) Tj ET";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content1.Length, content1);

        int off5 = pdf.Length;
        pdf.AppendFormat("5 0 obj\n{0}\nendobj\n", fontDict);

        int off6 = pdf.Length;
        pdf.Append("6 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>\nendobj\n");

        int off7 = pdf.Length;
        pdf.AppendFormat("7 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content2.Length, content2);

        int off8 = pdf.Length;
        pdf.AppendFormat("8 0 obj\n{0}\nendobj\n", fontDict);

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 9\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.AppendFormat("{0:D10} 00000 n \n", off7);
        pdf.AppendFormat("{0:D10} 00000 n \n", off8);
        pdf.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithUnusedObject()
    {
        string contentStream = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", contentStream.Length, contentStream);

        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int off6 = pdf.Length;
        pdf.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>\nendobj\n");

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithMetadata(string title = "Test Document")
    {
        string contentStream = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", contentStream.Length, contentStream);

        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int off6 = pdf.Length;
        pdf.AppendFormat("6 0 obj\n<< /Title ({0}) >>\nendobj\n", title);

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithAnnotations()
    {
        string contentStream = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [6 0 R] /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", contentStream.Length, contentStream);

        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int off6 = pdf.Length;
        pdf.Append("6 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 100 200 200] /Contents (A note) >>\nendobj\n");

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithStructureTree()
    {
        string contentStream = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", contentStream.Length, contentStream);

        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int off6 = pdf.Length;
        pdf.Append("6 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n");

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithRepetitiveContent()
    {
        var repeated = new StringBuilder();
        repeated.Append("BT /F1 12 Tf 100 700 Td ");
        for (int i = 0; i < 60; i++)
            repeated.Append("(AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA) Tj T* ");
        repeated.Append("ET");
        string contentStream = repeated.ToString();

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", contentStream.Length, contentStream);

        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xrefOff = pdf.Length;
        pdf.Append("xref\n0 6\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithRedRectangle() => BuildSinglePagePdf("1 0 0 rg\n0 0 306 792 re\nf");
    public static byte[] CreatePdfWithGrayFill() => BuildSinglePagePdf("0.5 g\n0 0 612 792 re\nf");
    public static byte[] CreateBlankPdf() => BuildSinglePagePdf(string.Empty);

    public static byte[] CreatePdfWithColorStateStack()
    {
        string cs = "q\n1 0 0 rg\n0 0 306 792 re\nf\nQ\n" +
                    "q\n0 0 1 rg\n306 0 306 792 re\nf\nQ";
        return BuildSinglePagePdf(cs);
    }

    public static byte[] CreatePdfWithStrokedRect() => BuildSinglePagePdf("0 0 0 RG\n2 w\n50 50 512 692 re\nS");
    public static byte[] CreatePdfWithCmykFill() => BuildSinglePagePdf("0 1 0 0 k\n0 0 612 792 re\nf");

    public static byte[] CreateRotatedPdf(int degrees = 90)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        string cs = "0.5 g\n0 0 612 792 re\nf";

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.AppendFormat("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Rotate {0} /Contents 4 0 R /Resources << >> >>\nendobj\n", degrees);

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static byte[] CreatePdfWithCircle()
    {
        const float cx = 306, cy = 396, r = 100, k = 55.23f;
        string cs =
            $"0 1 0 rg\n" +
            $"{cx} {cy + r} m\n" +
            $"{cx + k} {cy + r} {cx + r} {cy + k} {cx + r} {cy} c\n" +
            $"{cx + r} {cy - k} {cx + k} {cy - r} {cx} {cy - r} c\n" +
            $"{cx - k} {cy - r} {cx - r} {cy - k} {cx - r} {cy} c\n" +
            $"{cx - r} {cy + k} {cx - k} {cy + r} {cx} {cy + r} c\n" +
            $"f";
        return BuildSinglePagePdf(cs);
    }

    public static byte[] CreatePdfWithJavaScript()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        string contentStream = "BT /F1 12 Tf 100 700 Td (JS Test) Tj ET";

        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 6 0 R >>\nendobj\n");

        int obj2Offset = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3Offset = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
        pdf.Append("/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\n");
        pdf.Append("endobj\n");

        int obj4Offset = pdf.Length;
        pdf.Append("4 0 obj\n");
        pdf.AppendFormat("<< /Length {0} >>\n", contentStream.Length);
        pdf.Append("stream\n");
        pdf.Append(contentStream);
        pdf.Append("\nendstream\nendobj\n");

        int obj5Offset = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int obj6Offset = pdf.Length;
        pdf.Append("6 0 obj\n<< /S /JavaScript /JS (app.alert\\('Hello'\\)) >>\nendobj\n");

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj5Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj6Offset);

        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static byte[] BuildSinglePagePdf(string contentStream)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Contents 4 0 R /Resources << >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
