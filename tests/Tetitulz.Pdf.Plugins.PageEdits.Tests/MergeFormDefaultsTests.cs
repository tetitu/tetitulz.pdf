// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The merged <c>/AcroForm</c> is the first form's dictionary with the union of every source's fields,
/// so what the form itself says for its fields has to come from each source. Every later source lost
/// its <c>/CO</c> — its calculated fields stopped recalculating — and its form-wide <c>/DA</c> and
/// <c>/Q</c>, so its fields without their own regenerated in the first form's font, size and colour.
/// </summary>
public sealed class MergeFormDefaultsTests
{
    [Fact]
    public void TheCalculationOrder_IsEverySourcesInSourceOrder()
    {
        using var merged = Merge(Form("(/Cour 12 Tf 0 g)", 0), Form("(/Cour 12 Tf 0 g)", 0));

        var co = (PdfArray)merged.ResolveObject(AcroForm(merged)[PdfName.CO]);
        Assert.Equal(new[] { "Total", "Total.1" }, co.Select(c => Name(merged, c)).ToArray());
    }

    [Fact]
    public void ALaterSourcesOwnDefaults_TravelOnItsFields()
    {
        using var merged = Merge(Form("(/Cour 12 Tf 0 g)", 0), Form("(/Helv 9 Tf 0 0 1 rg)", 2));

        Assert.Equal("/Cour 12 Tf 0 g", ((PdfString)AcroForm(merged)[PdfName.DA]).GetText());
        PdfDictionary first = Field(merged, "Total"), second = Field(merged, "Total.1");
        Assert.Null(first[PdfName.DA]);                                   // the form's, as it was
        Assert.Null(first[PdfName.Q]);
        Assert.Equal("/Helv 9 Tf 0 0 1 rg", ((PdfString)second[PdfName.DA]).GetText());
        Assert.Equal(2, ((PdfNumber)second[PdfName.Q]).IntValue);
    }

    /// <summary>The control: a later source whose defaults are the first's gets nothing written.</summary>
    [Fact]
    public void ALaterSourceWithTheSameDefaults_IsLeftAsItWas()
    {
        using var merged = Merge(Form("(/Cour 12 Tf 0 g)", 0), Form("(/Cour 12 Tf 0 g)", 0));

        PdfDictionary second = Field(merged, "Total.1");
        Assert.Null(second[PdfName.DA]);
        Assert.Null(second[PdfName.Q]);
    }

    /// <summary>A field's own <c>/DA</c> and <c>/Q</c> decide over its form's, so they are kept.</summary>
    [Fact]
    public void AFieldsOwnDefaults_AreKept()
    {
        using var merged = Merge(Form("(/Cour 12 Tf 0 g)", 0),
                                 Form("(/Helv 9 Tf 0 0 1 rg)", 2, fieldEntries: "/DA (/TiRo 7 Tf 1 0 0 rg) /Q 1"));

        PdfDictionary second = Field(merged, "Total.1");
        Assert.Equal("/TiRo 7 Tf 1 0 0 rg", ((PdfString)second[PdfName.DA]).GetText());
        Assert.Equal(1, ((PdfNumber)second[PdfName.Q]).IntValue);
    }

    /// <summary>
    /// A later form's <c>/Q</c> that is not 0, 1 or 2 is the default, left, and travels nowhere:
    /// <c>(int)LongValue</c> read 4 294 967 297 as 1 and centred the source's fields; 3 is out of
    /// range in the int range too. The control: a real 1 travels.
    /// </summary>
    [Theory]
    [InlineData((1L << 32) + 1)]
    [InlineData(3L)]
    public void ALaterFormsQuaddingOutOfRange_IsTheDefault_AndTravelsNowhere(long q)
    {
        using (var merged = Merge(Form("(/Cour 12 Tf 0 g)", 0), Form("(/Cour 12 Tf 0 g)", q)))
            Assert.Null(Field(merged, "Total.1")[PdfName.Q]);

        using (var control = Merge(Form("(/Cour 12 Tf 0 g)", 0), Form("(/Cour 12 Tf 0 g)", 1)))
            Assert.Equal(1, ((PdfNumber)Field(control, "Total.1")[PdfName.Q]).IntValue);
    }

    // ----------------------------------------------------------------

    private static PdfDictionary AcroForm(PdfDocument doc)
        => (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);

    private static string Name(PdfDocument doc, PdfObject field)
        => ((PdfString)((PdfDictionary)doc.ResolveObject(field))[PdfName.T]).GetText();

    private static PdfDictionary Field(PdfDocument doc, string name)
    {
        var fields = (PdfArray)doc.ResolveObject(AcroForm(doc)[PdfName.Fields]);
        return (PdfDictionary)doc.ResolveObject(fields.Single(f => Name(doc, f) == name));
    }

    // One page with one text field, /T (Total), object 4, which the form's /CO lists; the form's
    // /DA and /Q as given.
    private static byte[] Form(string da, long q, string fieldEntries = "") => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /CO [4 0 R] /DA " + da + " /Q " + q + " >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Total) /Rect [10 10 60 30] /P 3 0 R " + fieldEntries + " >>",
    });

    private static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(a), new MemoryStream(b));
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
