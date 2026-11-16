// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page with nothing to export has no images — not an exception. <c>PdfImageExporter</c>'s image
/// enumeration is the definition of "image N on page P" for extraction and replacement alike, and
/// a page with no <c>/Resources</c>, or resources with no <c>/XObject</c>, is an ordinary page.
/// </summary>
public class ImageExporterPageResourcesTests
{
    [Fact]
    public void APageWithNoResources_HasNoImages()
        => Assert.Equal(0, PdfImageExporter.CountImages(new MemoryStream(Pdf(resources: null)), 0));

    [Fact]
    public void APageWhoseResourcesHoldNoXObjects_HasNoImages()
        => Assert.Equal(0, PdfImageExporter.CountImages(
            new MemoryStream(Pdf(resources: "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>")), 0));

    /// <summary>The control: the same page with one image XObject has one.</summary>
    [Fact]
    public void APageWithAnImageXObject_HasOne()
        => Assert.Equal(1, PdfImageExporter.CountImages(new MemoryStream(Pdf(resources: "<< /XObject << /Im1 4 0 R >> >>")), 0));

    private static byte[] Pdf(string resources)
    {
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]"
                    + (resources == null ? "" : " /Resources " + resources) + " >>";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            page,
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u0080\nendstream",
        };

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
