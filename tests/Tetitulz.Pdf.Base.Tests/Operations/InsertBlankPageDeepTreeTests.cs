// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfPageManipulator.InsertBlankPage"/> puts the page at the index asked for in a page
/// tree of any depth — into the node holding the page now there, every ancestor's <c>/Count</c>
/// raised — and refuses a NaN or infinite side; <see cref="PdfPageManipulator.SetPageBox"/>
/// refuses a NaN or infinite coordinate rather than write a degenerate box.
/// </summary>
public class InsertBlankPageDeepTreeTests
{
    /// <summary>
    /// Four pages under two intermediate nodes. The new 300-point page is found at the index asked
    /// for: the first page, between the nodes, inside the second node, and appended.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InsertBlankPage_InADeepTree_LandsAtTheIndexAskedFor(int index)
    {
        using var input = new MemoryStream(FourPagesUnderTwoNodes());
        using var output = new MemoryStream();

        PdfPageManipulator.InsertBlankPage(input, output, index, 300, 300);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(5, doc.Pages.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal(i == index ? 300 : 200, Width(doc, i));

        // Every node on the way up counts the page: the root says 5, and the node it joined 3.
        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]) as PdfDictionary;
        Assert.Equal(5, Count(doc, root));
        var parent = doc.ResolveObject(doc.Pages[index].Dictionary[PdfName.Parent]) as PdfDictionary;
        Assert.NotNull(parent);
        Assert.Equal(3, Count(doc, parent));
        Assert.NotSame(root, parent);
    }

    [Theory]
    [InlineData(double.NaN, 792)]
    [InlineData(612, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 792)]
    public void InsertBlankPage_RefusesANonFiniteSide(double width, double height)
    {
        using var input = new MemoryStream(FourPagesUnderTwoNodes());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageManipulator.InsertBlankPage(input, output, 0, width, height));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(0, double.PositiveInfinity, 100, 100)]
    [InlineData(0, 0, double.NaN, 100)]
    [InlineData(0, 0, 100, double.NegativeInfinity)]
    public void SetPageBox_RefusesANonFiniteCoordinate(double x, double y, double w, double h)
    {
        using var input = new MemoryStream(FourPagesUnderTwoNodes());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.CropBox, x, y, w, h));
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: a finite box inside the page is written.</summary>
    [Fact]
    public void SetPageBox_WritesAFiniteBox()
    {
        using var input = new MemoryStream(FourPagesUnderTwoNodes());
        using var output = new MemoryStream();

        PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.CropBox, 10, 10, 100, 100);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var crop = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.CropBox]) as PdfArray;
        Assert.NotNull(crop);
        Assert.Equal(110, (crop![2] as PdfNumber)!.DoubleValue);
    }

    // ------------------------------------------------------------------

    private static double Width(PdfDocument doc, int page)
    {
        var box = doc.ResolveObject(doc.Pages[page].Dictionary[PdfName.MediaBox]) as PdfArray;
        return (doc.ResolveObject(box![2]) as PdfNumber)!.DoubleValue;
    }

    private static int Count(PdfDocument doc, PdfDictionary node)
        => (doc.ResolveObject(node![PdfName.Count]) as PdfNumber)?.IntValue ?? -1;

    /// <summary>Root 2 → nodes 3 and 4 → pages 5, 6 and 7, 8, each 200 points wide.</summary>
    private static byte[] FourPagesUnderTwoNodes() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 4 >>",
        "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R 6 0 R] /Count 2 >>",
        "<< /Type /Pages /Parent 2 0 R /Kids [7 0 R 8 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 200 200] >>");

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
