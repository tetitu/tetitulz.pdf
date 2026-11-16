// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A radio field's appearance states are gathered once each, a set deciding — a search of the list
/// was kids² over a field whose kids the file chooses — and a value none of them names is refused
/// with twenty of them named and the rest counted.
/// </summary>
public class FormButtonStatesTests
{
    private const int Kids = 30;

    [Fact]
    public void AdversarialManyStates_AValueNoneNames_IsRefusedNamingTwentyOnce()
    {
        var result = Fill(RadioField(), "nope", out _);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("matches none of the widget's appearance states", warning, System.StringComparison.Ordinal);
        Assert.Single(Regex.Matches(warning, @"\bS0\b"));
        Assert.Contains("S19, and 10 more)", warning, System.StringComparison.Ordinal);
        Assert.DoesNotContain("S20", warning, System.StringComparison.Ordinal);
    }

    /// <summary>The control: a state one kid holds is selected on that kid and no other.</summary>
    [Fact]
    public void AStateOneKidHolds_IsSelectedOnThatKid()
    {
        var result = Fill(RadioField(), "S25", out byte[] output);

        Assert.Contains("r", result.FilledFields);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        var kids = Assert.IsType<PdfArray>(doc.ResolveObject(Assert.IsType<PdfDictionary>(doc.GetObject(4))[PdfName.Kids]));
        var states = kids.Select(k => (Assert.IsType<PdfDictionary>(doc.ResolveObject(k))[PdfName.AS] as PdfName)?.Value).ToList();
        Assert.Equal("S25", states[25]);
        Assert.Equal(Kids, states.Count(s => s == "Off"));
    }

    /// <summary>
    /// A check box whose <c>/AP /N</c> is one appearance stream has one appearance and no states —
    /// what <see cref="PdfFormReader"/> reports, and now what the filler reads too: "Yes" is stored
    /// as <c>/Yes</c> with the no-states warning, and "BBox" is no value at all. The filler read the
    /// stream as a dictionary of states, so "Yes" was refused as ambiguous among <c>/Type</c>,
    /// <c>/Subtype</c>, <c>/BBox</c> and <c>/Length</c>, and "BBox" was written as the field's value.
    /// </summary>
    [Fact]
    public void ACheckBoxWithOneAppearanceStream_HasNoStates_ForTheFillerAsForTheReader()
    {
        byte[] pdf = OneAppearanceCheckBox();

        var info = Assert.Single(PdfFormReader.GetFields(new MemoryStream(pdf)));
        Assert.Empty(info.OnStates);

        var yes = Fill(pdf, "Yes", out byte[] output, field: "c");
        Assert.Contains("c", yes.FilledFields);
        Assert.Contains("c", yes.FieldsWithoutAppearance);
        using (var doc = PdfDocument.Open(new MemoryStream(output)))
            Assert.Equal(PdfName.Yes, Assert.IsType<PdfDictionary>(doc.GetObject(4))[PdfName.V]);

        var bbox = Fill(pdf, "BBox", out _, field: "c");
        Assert.DoesNotContain("c", bbox.FilledFields);
        Assert.Contains(bbox.Warnings, w => w.Contains("declares no appearance states", System.StringComparison.Ordinal));
    }

    private static FormFillResult Fill(byte[] pdf, string value, out byte[] output, string field = "r")
    {
        using var ms = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), ms, new Dictionary<string, string> { [field] = value }, new FormFillOptions());
        output = ms.ToArray();
        return result;
    }

    /// <summary>A check box field (object 4) whose widget's <c>/AP /N</c> is one stream, object 5.</summary>
    private static byte[] OneAppearanceCheckBox() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c) /P 3 0 R /Rect [10 10 20 20] /V /Off /AS /Off /AP << /N 5 0 R >> >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream",
    });

    /// <summary>A radio field whose 31 kids hold states S0 … S29, the last kid S0 again.</summary>
    private static byte[] RadioField()
    {
        int firstKid = 5, appearance = firstKid + Kids + 1;
        var bodies = new List<string>();
        var kids = new StringBuilder();
        for (int k = 0; k <= Kids; k++) kids.Append(firstKid + k).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>");
        bodies.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{kids}] >>");
        bodies.Add($"<< /FT /Btn /Ff 49152 /T (r) /Kids [{kids}] >>");
        for (int k = 0; k <= Kids; k++)
            bodies.Add($"<< /Type /Annot /Subtype /Widget /Parent 4 0 R /P 3 0 R /Rect [{10 + k * 12} 10 {20 + k * 12} 20] /AS /Off "
                       + $"/AP << /N << /S{k % Kids} {appearance} 0 R /Off {appearance} 0 R >> >> >>");
        bodies.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream");
        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
