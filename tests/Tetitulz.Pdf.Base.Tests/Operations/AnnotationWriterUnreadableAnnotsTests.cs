// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfAnnotationWriter"/> refuses a page whose <c>/Annots</c> it cannot read, as the form
/// builder does: read as none, the array written for the page held only the new annotation, and every
/// link and widget the page had left the current revision. A readable one keeps its entry (the control).
/// </summary>
public sealed class AnnotationWriterUnreadableAnnotsTests
{
    private static byte[] Pdf(bool unreadable)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots 4 0 R >>",
            unreadable ? "]" : "[5 0 R]",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 20 20] /A << /S /URI /URI (https://example.com) >> >>",
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
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void AdversarialUnreadableAnnots_IsRefused_AndNothingIsWritten()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfAnnotationWriter.AddTextNote(new MemoryStream(Pdf(unreadable: true)), output, 0, 30, 30, "note"));
        Assert.Contains("Page 1's /Annots could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AReadableAnnots_KeepsItsEntry_BesideTheNewOne()
    {
        var output = new MemoryStream();
        PdfAnnotationWriter.AddTextNote(new MemoryStream(Pdf(unreadable: false)), output, 0, 30, 30, "note");

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        Assert.Equal(2, annots.Count);
        Assert.Equal(5, Assert.IsType<PdfReference>(annots[0]).ReferenceObjectNumber);
    }
}
