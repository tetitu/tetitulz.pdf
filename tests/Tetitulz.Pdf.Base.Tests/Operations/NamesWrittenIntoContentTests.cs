// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A name a caller or a file chose, written into a content stream or a <c>/DA</c>: as it stands it can
/// close the operand and add operators to what is drawn. §7.3.5's escape keeps it one name.
/// </summary>
public sealed class NamesWrittenIntoContentTests
{
    private const string Hostile = "a Tj 0 0 1000 1000 re f /b";

    private static byte[] Pdf(string content, string resources = "", string catalog = "", params string[] extra)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        bodies.AddRange(extra);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static List<ContentStreamOperation> AllOperations(byte[] pdf)
    {
        var all = new List<ContentStreamOperation>();
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
            if (e.Type != CrossReferenceEntry.EntryType.Free && doc.GetObject(e.ObjectNumber) is PdfStream s
                && s.TryGetDecodedData(1 << 24, out byte[] data, out _))
                all.AddRange(ContentStreamParser.Parse(data));
        return all;
    }

    private static bool DrawsTheInjectedBox(IEnumerable<ContentStreamOperation> operations)
        => operations.Any(o => o.Operator == "re" && o.Operands.Length == 4 && o.Operands[2] is PdfNumber n && n.DoubleValue == 1000);

    /// <summary>A caller's font name, in the appearance the field is built with and in its <c>/DA</c>.</summary>
    [Fact]
    public void AdversarialFontName_OfANewField_StaysOneName()
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(Pdf("q Q")), output, new FormTextField
        {
            FieldName = "f",
            DefaultValue = "v",
            FontName = Hostile,
        });

        List<ContentStreamOperation> operations = AllOperations(output.ToArray());

        Assert.False(DrawsTheInjectedBox(operations));
        Assert.Contains(operations, o => o.Operator == "Tf" && (o.Operands[0] as PdfName)?.Value == Hostile);
    }

    /// <summary>A file's <c>/DA</c>, through the appearance a fill writes.</summary>
    [Fact]
    public void AdversarialFontName_InAFilesDefaultAppearance_StaysOneName()
    {
        byte[] pdf = Pdf("q Q", "", "/AcroForm << /Fields [6 0 R] /DR << /Font << /a#20Tj#200#200#201000#201000#20re#20f#20/b 5 0 R >> >> >>",
            "<< /FT /Tx /T (f) /Subtype /Widget /Rect [0 0 90 20] /DA (/a#20Tj#200#200#201000#201000#20re#20f#20/b 12 Tf 0 g) >>");

        using var output = new MemoryStream();
        PdfFormFiller.Fill(new MemoryStream(pdf), output, new Dictionary<string, string> { ["f"] = "v" });

        Assert.False(DrawsTheInjectedBox(AllOperations(output.ToArray())));
    }

    /// <summary>A file's resource name, kept by a redaction that draws an image's clone under it.</summary>
    [Fact]
    public void AdversarialResourceName_KeptByARedaction_StaysOneName()
    {
        const string Image = "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 2 >>\nstream\nab\nendstream";
        const string Escaped = "a#20Tj#200#200#201000#201000#20re#20f#20/b";
        byte[] pdf = Pdf("q 100 0 0 100 10 50 cm /" + Escaped.Replace("/b", "#2Fb") + " Do Q", "/XObject << /" + Escaped.Replace("/b", "#2Fb") + " 6 0 R >>", "", Image);

        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 10, Y = 50, Width = 50, Height = 100 },
        });

        List<ContentStreamOperation> operations = AllOperations(output.ToArray());
        Assert.False(DrawsTheInjectedBox(operations));
        Assert.Contains(operations, o => o.Operator == "Do" && (o.Operands[0] as PdfName)?.Value == Hostile);
    }
}
