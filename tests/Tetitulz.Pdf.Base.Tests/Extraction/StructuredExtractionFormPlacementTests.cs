// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// Text inside a form XObject is reported in page space: the CTM at the <c>Do</c>, with the form's
/// <c>/Matrix</c> composed in (ISO 32000 §8.10.1).
/// </summary>
/// <remarks>
/// <b>The composed state is the one the form runs under.</b> Cloning the graphics state at the
/// <c>Do</c>, composing <c>/Matrix</c> into it and calling the interpreter without it would start
/// every form from an identity CTM — and templating, whose replacement is drawn in page space,
/// would land 100 points away from where extraction said the placeholder was.
/// </remarks>
public class StructuredExtractionFormPlacementTests
{
    private static byte[] Pdf(string pageContent, string formMatrix)
        => PdfWithForm(pageContent, formMatrix, "BT /F1 12 Tf 72 700 Td (Placed) Tj ET");

    private static byte[] PdfWithForm(string pageContent, string formMatrix, string form)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            $"<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] {formMatrix} " +
                $"/Resources << /Font << /F1 5 0 R >> >> /Length {form.Length} >>\nstream\n{form}\nendstream",
            "[1 0 0 1 10 20]",   // object 7: a /Matrix a form can reference indirectly
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfTextWord Placed(byte[] pdf)
        => PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Lines
            .SelectMany(l => l.Words).Single(w => w.Text == "Placed");

    /// <summary>The graphics extractor had the same dropped state: a ruling inside a form, in page space.</summary>
    [Theory]
    [InlineData("/Fm0 Do", "", 300)]
    [InlineData("q 1 0 0 1 0 50 cm /Fm0 Do Q", "", 350)]
    [InlineData("/Fm0 Do", "/Matrix [1 0 0 1 0 20]", 320)]
    [InlineData("/Fm0 Do", "/Matrix 7 0 R", 320)]                          // indirect, which GetArray missed
    public void FormRulingsAreReportedInPageSpace(string pageContent, string formMatrix, float y)
    {
        byte[] pdf = PdfWithForm(pageContent, formMatrix, "2 w 100 300 m 400 300 l S");

        var rulings = PdfGraphicsExtractor.Extract(new MemoryStream(pdf))[0].HorizontalRulings;

        HRuling ruling = Assert.Single(rulings);
        Assert.Equal(y, ruling.Y, 1.0);
    }

    [Theory]
    [InlineData("/Fm0 Do", "", 72, 700)]                                  // the control
    [InlineData("q 1 0 0 1 100 50 cm /Fm0 Do Q", "", 172, 750)]           // the CTM at the Do
    [InlineData("/Fm0 Do", "/Matrix [1 0 0 1 10 20]", 82, 720)]           // the form's own matrix
    [InlineData("/Fm0 Do", "/Matrix 7 0 R", 82, 720)]                     // indirect, which GetArray missed
    [InlineData("q 1 0 0 1 100 50 cm /Fm0 Do Q", "/Matrix [1 0 0 1 10 20]", 182, 770)]
    public void FormTextIsReportedInPageSpace(string pageContent, string formMatrix, float x, float baseline)
    {
        PdfTextWord word = Placed(Pdf(pageContent, formMatrix));

        Assert.Equal(x, word.X, 1.0);

        // The control fixes the word's Y relative to its baseline, whatever box convention Y uses.
        PdfTextWord control = Placed(Pdf("/Fm0 Do", ""));
        Assert.Equal(baseline - 700, word.Y - control.Y, 1.0);
    }
}
