// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A watermark leaves the page's own resources alone.
/// </summary>
/// <remarks>
/// The watermarker registers a font and an opacity state through the shared content builder, whose
/// first names are <c>F1</c> and <c>GS1</c>, the names ordinary PDFs use most. Merged into a page that
/// already used them, the watermark's replaced the page's. Every line of the page's own text re-rendered
/// in the watermark's font, and its own content took the watermark's opacity. The fix is in Base
/// (<c>PdfPageContentAppender</c> reserves the page's names); this is the consumer's view of it.
/// </remarks>
public class WatermarkResourceNameTests
{
    private static byte[] PageUsingF1AndGS1()
    {
        const string content = "/GS1 gs BT /F1 12 Tf 10 10 Td (hi) Tj ET";
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[5];
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 << /Type /ExtGState /ca 1 >> >> >> >>",
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
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public void AWatermark_KeepsThePagesOwnF1AndGS1()
    {
        using var output = new MemoryStream();
        PdfWatermarker.AddWatermark(new MemoryStream(PageUsingF1AndGS1()), output,
            new WatermarkOptions { Text = "CONFIDENTIAL", Opacity = 0.3 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var resources = doc.Pages[0].Resources;
        var fonts = (PdfDictionary)doc.ResolveObject(resources[PdfName.Font]);
        var states = (PdfDictionary)doc.ResolveObject(resources[PdfName.ExtGState]);

        var f1 = (PdfDictionary)doc.ResolveObject(fonts[new PdfName("F1")]);
        Assert.Equal("Times-Roman", ((PdfName)f1[PdfName.BaseFont]).Value);
        var gs1 = (PdfDictionary)doc.ResolveObject(states[new PdfName("GS1")]);
        Assert.Equal(1, ((PdfNumber)gs1[PdfName.ca]).DoubleValue);

        // Potency: the watermark's own font and state are there too, under other names.
        Assert.Equal(2, fonts.Count);
        Assert.Contains(states, kv => kv.Key.Value != "GS1"
            && ((PdfDictionary)doc.ResolveObject(kv.Value))[PdfName.ca] is PdfNumber ca
            && System.Math.Abs(ca.DoubleValue - 0.3) < 1e-6);
    }
}
