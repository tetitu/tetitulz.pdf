// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A FreeText appearance shows the text the renderers' fallback shows: <c>/Contents</c>, else the
/// <c>/RC</c> rich text stripped of its markup — where an annotation carrying only <c>/RC</c> got an
/// appearance drawing nothing. Each line of the text is drawn on its own baseline, where a line
/// break inside one <c>Tj</c> was an undefined code.
/// </summary>
public class FreeTextAppearanceLinesTests
{
    [Fact]
    public void RichTextAlone_IsDrawn()
    {
        var annot = new PdfDictionary();
        annot.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.FreeText));
        annot.Set(PdfName.RC, new PdfString("<body><p>hi there</p></body>"));

        var form = AnnotationAppearanceBuilder.Build(annot, 200, 40, 0, 0, out string reason);

        Assert.Null(reason);
        Assert.Contains("(hi there) Tj", Content(form), StringComparison.Ordinal);
    }

    [Fact]
    public void LineBreaks_BecomeLines()
    {
        byte[] pdf;
        using (var input = new MemoryStream(OnePage()))
        using (var output = new MemoryStream())
        {
            PdfAnnotationWriter.AddFreeText(input, output, 0, 20, 20, 200, 40, "line one\nline two");
            pdf = output.ToArray();
        }

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].GetAnnotations()[0]));
        var ap = Assert.IsType<PdfDictionary>(doc.ResolveObject(annot[PdfName.AP]));
        string content = Content(Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N])));

        Assert.Contains("(line one) Tj", content, StringComparison.Ordinal);
        Assert.Contains("T*", content, StringComparison.Ordinal);
        Assert.Contains("(line two) Tj", content, StringComparison.Ordinal);
        Assert.Contains(" TL", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\n)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("one\\nline", content, StringComparison.Ordinal);
        Assert.DoesNotContain("one\nline", content, StringComparison.Ordinal);
    }

    private static string Content(PdfStream form) => Encoding.Latin1.GetString(form.GetDecodedData());

    private static byte[] OnePage()
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
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
