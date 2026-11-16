// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A watermark's angle is measured as the page is displayed: the page's <c>/Rotate</c> is added to
/// the angle before the text matrix is written, so a mark at 0° reads left to right on every page.
/// Without it the mark on a <c>/Rotate 180</c> page reads upside down, and on a <c>/Rotate 90</c>
/// page sideways.
/// </summary>
public class WatermarkRotationTests
{
    private static byte[] PageRotated(int rotate)
    {
        const string content = "BT /F1 12 Tf 10 10 Td (hi) Tj ET";
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[5];
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate " + rotate + " >>",   // inherited, as producers write it
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
        };
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// The baseline direction the text matrix writes, <c>(a, b)</c>, for a 0° mark: along the page's
    /// +x on an unrotated page, up its +y on a <c>/Rotate 90</c> page (shown a quarter turn
    /// clockwise, +y points right), back along −x at 180, down −y at 270 — and <c>/Rotate -90</c>
    /// is 270.
    /// </summary>
    [Theory]
    [InlineData(0, 1.0, 0.0)]
    [InlineData(90, 0.0, 1.0)]
    [InlineData(180, -1.0, 0.0)]
    [InlineData(270, 0.0, -1.0)]
    [InlineData(-90, 0.0, -1.0)]
    public void AMarkAtZeroDegrees_ReadsLeftToRightAsThePageIsDisplayed(int rotate, double a, double b)
    {
        using var output = new MemoryStream();
        PdfWatermarker.AddWatermark(new MemoryStream(PageRotated(rotate)), output,
            new WatermarkOptions { Text = "CONFIDENTIAL", AngleDegrees = 0 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Match tm = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
        Assert.True(tm.Success, "the watermark wrote no text matrix: " + content);

        Assert.Equal(a, double.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture), 4);
        Assert.Equal(b, double.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture), 4);
        // Centred on the page whatever the turn.
        Assert.Equal(306, double.Parse(tm.Groups[5].Value, CultureInfo.InvariantCulture), 2);
        Assert.Equal(396, double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture), 2);
    }

    /// <summary>The caller's angle is added to the turn, not replaced by it.</summary>
    [Fact]
    public void TheCallersAngleIsMeasuredFromTheDisplayedHorizontal()
    {
        using var output = new MemoryStream();
        PdfWatermarker.AddWatermark(new MemoryStream(PageRotated(90)), output,
            new WatermarkOptions { Text = "DRAFT", AngleDegrees = 30 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Match tm = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
        Assert.True(tm.Success, "the watermark wrote no text matrix: " + content);

        double expected = 120 * Math.PI / 180;
        Assert.Equal(Math.Cos(expected), double.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture), 3);
        Assert.Equal(Math.Sin(expected), double.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture), 3);
    }
}
