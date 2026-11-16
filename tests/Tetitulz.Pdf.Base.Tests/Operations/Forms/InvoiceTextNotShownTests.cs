// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Forms;

/// <summary>
/// An invoice can show one total and carry another in text no reader sees. The text is still read, an
/// OCR layer being text like any other; the finding is what says the two can differ.
/// </summary>
public sealed class InvoiceTextNotShownTests
{
    private const string Shown = "BT /F1 12 Tf 72 700 Td (Total 100.00) Tj ET ";

    private const string LayerOff = "/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>";
    private const string LayerOn = "/OCProperties << /OCGs [6 0 R] /D << >> >>";
    private const string FormOf = "/XObject << /X 7 0 R >>";

    private static byte[] Pdf(string content, string pageExtra = "", string catalog = "", string resources = "/Properties << /L 6 0 R >>", string formDict = "")
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        const string hidden = "BT /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET";
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            font,
            "<< /Type /OCG /Name (L) >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> {formDict} /Length {hidden.Length} >>\nstream\n{hidden}\nendstream",
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
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static List<InvoiceFinding> Findings(byte[] pdf)
    {
        IsraeliInvoice invoice = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf), new IsraeliInvoiceOptions { Ocr = InvoiceOcrMode.Never });
        return invoice.ExtractionFindings.Where(f => f.Code == InvoiceFindingCodes.TextNotShown).ToList();
    }

    [Theory]
    [InlineData("invisible, away from what shows", "BT 3 Tr /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET")]
    [InlineData("clipping only, away from what shows", "BT 7 Tr /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET")]
    [InlineData("off the page to the right", "BT /F1 12 Tf 900 400 Td (Total 999999.00) Tj ET")]
    [InlineData("off the page below", "BT /F1 12 Tf 72 -100 Td (Total 999999.00) Tj ET")]
    [InlineData("too small to read", "BT /F1 0.3 Tf 72 400 Td (Total 999999.00) Tj ET")]
    public void AdversarialTextAReaderDoesNotSee_IsSaid(string name, string hidden)
    {
        Assert.NotNull(name);
        List<InvoiceFinding> findings = Findings(Pdf(Shown + hidden));

        InvoiceFinding finding = Assert.Single(findings);
        Assert.Equal(InvoiceFindingSeverity.Warning, finding.Severity);
        Assert.Contains("999999", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("100.00", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialTextInALayerThatIsOff_IsSaid()
    {
        const string inLayer = "/OC /L BDC BT /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET EMC";
        Assert.Contains("999999", Assert.Single(Findings(Pdf(Shown + inLayer, catalog: LayerOff))).Message, StringComparison.Ordinal);

        // What follows the layer is what the page shows; the section's end is the end of it, a Q or no.
        const string q = "q /OC /L BDC BT /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET Q BT /F1 12 Tf 72 300 Td (hidden2) Tj ET EMC BT /F1 12 Tf 72 200 Td (after) Tj ET";
        string message = Assert.Single(Findings(Pdf(Shown + q, catalog: LayerOff))).Message;
        Assert.Contains("hidden2", message, StringComparison.Ordinal);
        Assert.DoesNotContain("after", message, StringComparison.Ordinal);

        // Control: the same layer on is what a reader sees.
        Assert.Empty(Findings(Pdf(Shown + inLayer, catalog: LayerOn)));

        // Control: a section tagged anything else is no layer, whatever its properties name (§8.11.3.2).
        const string otherTag = "/Span /L BDC BT /F1 12 Tf 72 400 Td (Total 999999.00) Tj ET EMC";
        Assert.Empty(Findings(Pdf(Shown + otherTag, catalog: LayerOff)));
    }

    [Fact]
    public void AdversarialFormInALayerThatIsOff_IsSaid()
    {
        Assert.Single(Findings(Pdf(Shown + "/X Do", catalog: LayerOff, resources: FormOf, formDict: "/OC 6 0 R")));
        Assert.Empty(Findings(Pdf(Shown + "/X Do", catalog: LayerOn, resources: FormOf, formDict: "/OC 6 0 R")));
    }

    [Fact]
    public void OffTheCropBox_ThoughOnTheMediaBox_IsSaid()
    {
        Assert.Single(Findings(Pdf(Shown + "BT /F1 12 Tf 72 100 Td (Total 999999.00) Tj ET", "/CropBox [0 300 612 792]")));
    }

    [Theory]
    [InlineData("what shows, alone", Shown)]
    [InlineData("a recognised layer alone, on a scan", "BT 3 Tr /F1 12 Tf 72 700 Td (Total 100.00) Tj ET")]
    [InlineData("an invisible copy of what shows", Shown + "BT 3 Tr /F1 12 Tf 72 700 Td (Total 100.00) Tj ET")]
    [InlineData("small print", Shown + "BT /F1 5 Tf 72 400 Td (terms apply) Tj ET")]
    public void TextAReaderSees_IsNotSaid(string name, string content)
    {
        Assert.NotNull(name);
        Assert.Empty(Findings(Pdf(content)));
    }
}
