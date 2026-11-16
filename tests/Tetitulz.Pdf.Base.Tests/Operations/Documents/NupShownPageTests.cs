// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// N-up imposes each page as a reader shows it: the <c>/CropBox</c> clips what lies outside it, a
/// <c>/Rotate 90</c> page is turned onto its slot, and the annotations a reader shows — a filled
/// field's appearance — come along. The sheet of a cropped, turned page with a widget saying FILLED
/// reads FILLED, clips the page's form to the crop box, and turns its placement.
/// </summary>
public sealed class NupShownPageTests
{
    [Fact]
    public void ARotatedCroppedPageWithAWidget_IsImposedAsShown()
    {
        using var output = new MemoryStream();
        PdfNup.Create2Up(new MemoryStream(Pdf()), output);
        byte[] sheet = output.ToArray();

        // The widget's appearance is drawn on the sheet; the page's text, outside its crop box, is
        // clipped by the form's /BBox (asserted below), not removed from the stream.
        Assert.Contains("FILLED", PdfOperations.ExtractText(new MemoryStream(sheet)));

        using var doc = PdfDocument.Open(new MemoryStream(sheet));
        string content = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        // The page's placement turns: its cm has a and d at 0, b and c not.
        Match cm = Regex.Match(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) cm\s+/SrcPg");
        Assert.True(cm.Success, content);
        Assert.Equal(0, double.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture), 3);
        Assert.NotEqual(0, double.Parse(cm.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Contains(" /Ap", content);
        var form = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject])).First(kv => kv.Key.Value.StartsWith("SrcPg", System.StringComparison.Ordinal)).Value);
        var bbox = (PdfArray)form[PdfName.BBox];
        Assert.Equal(new[] { 50.0, 50, 550, 750 }, bbox.Select(e => ((PdfNumber)e).DoubleValue));
    }

    /// <summary>A 600 × 800 page cropped to [50 50 550 750], turned 90°, with text outside the crop and a filled widget inside it.</summary>
    private static byte[] Pdf()
    {
        const string content = "BT /F1 12 Tf 10 10 Td (abc) Tj ET";
        const string ap = "BT /F1 12 Tf 2 4 Td (FILLED) Tj ET";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Rotate 90 /CropBox [50 50 550 750] /Contents 4 0 R " +
            "/Resources << /Font << /F1 6 0 R >> >> /Annots [5 0 R] >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /V (FILLED) /Rect [100 100 300 130] /F 4 /AP << /N 7 0 R >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 30] /Resources << /Font << /F1 6 0 R >> >> /Length " + ap.Length + " >>\nstream\n" + ap + "\nendstream",
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
