// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Annotations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Annotations;

/// <summary>
/// <see cref="PdfAnnotation"/> and <see cref="PdfWidgetAnnotation"/> answer the same whether an
/// entry is written directly or as an indirect reference (ISO 32000-2 §7.3.10).
/// </summary>
/// <remarks>
/// <para><b>Every entry was read raw.</b> Both classes hold the owning document and used it for
/// nothing: <c>/Subtype</c>, <c>/Rect</c>, <c>/Contents</c>, <c>/F</c>, <c>/FT</c> and <c>/T</c> went
/// through <c>PdfDictionary</c>'s typed getters, which do not follow a reference. An indirect
/// <c>/Subtype</c> made <see cref="PdfAnnotation.Create"/> build a plain annotation for a widget and
/// <see cref="PdfAnnotation.AnnotationType"/> answer <c>Unknown</c>; an indirect <c>/F</c> read as
/// "no flags", which for a hidden annotation is the opposite answer.</para>
///
/// <para>Each case runs both spellings. The direct one always worked, so on its own it could never
/// have gone red; it is here so the indirect one is compared against a known-good answer rather
/// than against a constant.</para>
/// </remarks>
public class IndirectAnnotationEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWidgetIsCreatedAndReadWhicheverWayItsEntriesAreSpelled(bool indirect)
    {
        using var doc = Open(indirect);

        var annotation = PdfAnnotation.Create(Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(5)), doc);

        var widget = Assert.IsType<PdfWidgetAnnotation>(annotation);
        Assert.Equal(PdfAnnotationType.Widget, widget.AnnotationType);
        Assert.Equal("Tx", widget.FieldType?.Value);
        Assert.Equal("name", widget.FieldName);
        Assert.Equal(4, widget.AnnotationFlags);
        Assert.Equal(4, widget.Rect?.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATextAnnotationIsReadWhicheverWayItsEntriesAreSpelled(bool indirect)
    {
        using var doc = Open(indirect);

        var annotation = PdfAnnotation.Create(Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(6)), doc);

        Assert.IsNotType<PdfWidgetAnnotation>(annotation);
        Assert.Equal(PdfAnnotationType.Text, annotation.AnnotationType);
        Assert.Equal("note", annotation.Contents);
        Assert.Equal(2, annotation.AnnotationFlags);
    }

    // ------------------------------------------------------------------

    private static PdfDocument Open(bool indirect)
        => PdfDocument.Open(new MemoryStream(Pdf(indirect), writable: false));

    /// <summary>
    /// Object 5 is a widget and object 6 a text annotation; with <paramref name="indirect"/> every
    /// entry under test is one of objects 7–14 instead of a direct value.
    /// </summary>
    private static byte[] Pdf(bool indirect)
    {
        string V(string direct, int objectNumber)
            => indirect ? objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R" : direct;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> "
                + "/Annots [5 0 R 6 0 R] >>",
            "<< /Length 4 >>\nstream\nq Q\nendstream",
            "<< /Type /Annot /Subtype " + V("/Widget", 7) + " /FT " + V("/Tx", 8) + " /T " + V("(name)", 9)
                + " /Rect " + V("[10 10 50 30]", 10) + " /F " + V("4", 11) + " >>",
            "<< /Type /Annot /Subtype " + V("/Text", 12) + " /Contents " + V("(note)", 13)
                + " /Rect [60 10 80 30] /F " + V("2", 14) + " >>",
            "/Widget",
            "/Tx",
            "(name)",
            "[10 10 50 30]",
            "4",
            "/Text",
            "(note)",
            "2");
    }

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
