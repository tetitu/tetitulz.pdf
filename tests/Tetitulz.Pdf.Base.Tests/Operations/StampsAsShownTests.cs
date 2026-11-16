// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A Bates number and a header or footer are placed on the page as a reader shows it: inside the
/// <c>/CropBox</c>, from a <c>/MediaBox</c> corner that is not the origin, and on a turned page along
/// the shown baseline — where a stamp placed by the extent alone landed below the crop, off the
/// page, or on its side.
/// </summary>
public sealed class StampsAsShownTests
{
    [Theory]
    [InlineData("/MediaBox [0 0 612 792] /CropBox [72 72 540 720]", 108)]
    [InlineData("/MediaBox [100 100 712 892]", 136)]
    [InlineData("/MediaBox [0 0 612 792]", 36)]
    public void ABatesFooter_StandsInsideTheShownBox(string boxes, double expectedY)
    {
        using var output = new MemoryStream();
        PdfBatesNumbering.Apply(new MemoryStream(Pdf(boxes)), output, new BatesNumberingOptions());
        Assert.Equal(expectedY, TdY(StampStream(output.ToArray(), "% Tetitulz.Pdf Bates stamp")), 1);
    }

    [Theory]
    [InlineData("/MediaBox [0 0 612 792] /CropBox [72 72 540 720]", 108)]
    [InlineData("/MediaBox [100 100 712 892]", 136)]
    public void AFooter_StandsInsideTheShownBox(string boxes, double expectedY)
    {
        using var output = new MemoryStream();
        PdfHeaderFooterWriter.AddHeaderFooter(new MemoryStream(Pdf(boxes)), output,
            new HeaderFooterOptions { FooterText = "Page {page}" });
        Assert.Equal(expectedY, TdY(StampStream(output.ToArray(), "/Artifact")), 1);
    }

    /// <summary>
    /// On a <c>/Rotate 90</c> page the footer is written along the page's own +y axis — the shown
    /// baseline — and the render, which turns the page as a reader does, shows it along the bottom.
    /// </summary>
    [Fact]
    public void AFooterOnATurnedPage_ReadsAlongTheShownBottom()
    {
        using var output = new MemoryStream();
        PdfBatesNumbering.Apply(new MemoryStream(Pdf("/MediaBox [0 0 200 100] /Rotate 90")), output,
            new BatesNumberingOptions { FontSize = 10, MarginPt = 10 });
        byte[] stamped = output.ToArray();

        string stream = StampStream(stamped, "% Tetitulz.Pdf Bates stamp");
        Assert.Matches(new Regex(@"\b0 1 -1 0 [-\d.]+ [-\d.]+ Tm"), stream);

        using SKBitmap bmp = PixelAssert.Render(stamped);
        // Shown 100 wide and 200 tall; the number sits along the shown bottom, inside the margin.
        Assert.Equal(100, bmp.Width);
        Assert.Equal(200, bmp.Height);
        var ink = PixelAssert.InkBounds(bmp);
        Assert.NotNull(ink);
        Assert.InRange(ink!.Value.bottom, 200 - 10 - 4, 200 - 10 + 1);
        Assert.True(ink.Value.right - ink.Value.left > ink.Value.bottom - ink.Value.top, "the number runs down the shown page instead of along its bottom");
    }

    private static double TdY(string stream)
    {
        Match m = Regex.Match(stream, @"([-\d.]+) ([-\d.]+) Td");
        Assert.True(m.Success, "the stamp positions its text with Td");
        return double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    private static string StampStream(byte[] pdf, string marker)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]);
        var streams = contents is PdfArray a ? a : new PdfArray { contents };
        foreach (PdfObject entry in streams)
        {
            if (doc.ResolveObject(entry) is not PdfStream stream) continue;
            string text = Encoding.ASCII.GetString(stream.GetDecodedData());
            if (text.Contains(marker, StringComparison.Ordinal)) return text;
        }
        throw new Xunit.Sdk.XunitException("no appended stamp stream");
    }

    private static byte[] Pdf(string boxes)
    {
        const string content = "BT /F1 12 Tf 150 400 Td (Body) Tj ET";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R " + boxes + " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
