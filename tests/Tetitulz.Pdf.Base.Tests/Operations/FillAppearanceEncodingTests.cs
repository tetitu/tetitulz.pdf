// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A filled value drawn through the form's own <c>/DR</c> font is written as that font's codes
/// (§9.6.6): under Acrobat's <c>/Helv</c>, whose <c>/Differences</c> put the euro sign at 160, a euro
/// is byte A0 and reads back as a euro, and a no-break space — which that encoding has no code for
/// — goes to the WinAnsi font the writer makes, where it is byte A0 too and reads back as itself.
/// Written as Latin-1 bytes through the <c>/DR</c> font, the space was drawn as €.
/// </summary>
public sealed class FillAppearanceEncodingTests
{
    private const string EuroAt160 = "/Encoding << /Type /Encoding /Differences [160 /Euro] >>";

    [Fact]
    public void ANoBreakSpace_IsNotDrawnAsTheDrFontsEuro()
    {
        byte[] filled = Fill("1 000");
        string text = ShownText(filled);
        Assert.DoesNotContain("€", text);
        Assert.Contains("1 000", text.Replace(' ', ' '));
    }

    [Fact]
    public void AEuro_IsWrittenAsTheDrFontsCodeForIt()
    {
        byte[] filled = Fill("A€B");
        // The appearance names the /DR font and writes code 160 for the euro.
        using var doc = PdfDocument.Open(new MemoryStream(filled));
        var widget = (PdfDictionary)doc.GetObject(6);
        var ap = (PdfDictionary)doc.ResolveObject(widget[PdfName.AP]);
        var stream = (PdfStream)doc.ResolveObject(ap[PdfName.N]);
        Assert.Contains("A€B", ShownText(filled));
        var shows = ContentStreamParser.Parse(stream.GetDecodedData()).Where(op => op.Operator == "Tj").ToList();
        Assert.NotEmpty(shows);
        Assert.Contains(shows, op => op.Operands[0] is PdfString s && s.RawBytes.SequenceEqual(new byte[] { 0x41, 0xA0, 0x42 }));
        var fonts = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(stream[PdfName.Resources]))[PdfName.Font]);
        Assert.NotNull(fonts[new PdfName("Helv")]);
    }

    /// <summary>The text the page shows, the widget's appearance included.</summary>
    private static string ShownText(byte[] pdf)
    {
        var layouts = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf),
            new WordAssemblyOptions { IncludeAnnotationAppearances = true });
        return string.Join("\n", layouts.SelectMany(p => p.Lines).Select(l => l.Text));
    }

    private static byte[] Fill(string value)
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Fields [6 0 R] /DR << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " + EuroAt160 + " >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (amount) /Rect [100 700 300 720] /DA (/Helv 12 Tf 0 g) >>");
        using var output = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["amount"] = value }, new FormFillOptions());
        Assert.Contains("amount", result.FilledFields);
        Assert.True(result.FieldsWithoutAppearance.Count == 0, string.Join("; ", result.Warnings));
        return output.ToArray();
    }
}
