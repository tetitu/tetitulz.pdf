// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A multi-select list box (§12.7.4.4): the filler writes several selections — joined with
/// <see cref="PdfFormFiller.MultiSelectSeparator"/> — as the array <c>/V</c> with the selected
/// options' indices in <c>/I</c>, draws them one under another, and the reader reads every one
/// back; one selection is a string, and a stale <c>/I</c> goes with it.
/// </summary>
public class MultiSelectListBoxFillTests
{
    /// <summary>The exchange fixture's <c>langs</c> is object 9: options en, he, fr, with en and he selected.</summary>
    private const int Langs = 9;

    [Fact]
    public void SeveralSelections_AreWrittenAsTheArrayWithTheirIndices_AndReadBack()
    {
        byte[] filled = Fill(FormPdfFixtures.ExchangeForm(), "en" + PdfFormFiller.MultiSelectSeparator + "fr", out FormFillResult result);

        Assert.Contains("langs", result.FilledFields);
        Assert.Empty(result.FieldsWithoutAppearance);
        PdfFormFieldInfo langs = PdfFormReader.GetFields(new MemoryStream(filled)).Single(f => f.FullyQualifiedName == "langs");
        Assert.Equal(new[] { "en", "fr" }, langs.SelectedValues);
        Assert.Equal("en", langs.Value);

        using var doc = PdfDocument.Open(new MemoryStream(filled));
        var field = (PdfDictionary)doc.GetObject(Langs);
        var v = Assert.IsType<PdfArray>(doc.ResolveObject(field[PdfName.V]));
        Assert.Equal(new[] { "en", "fr" }, v.Cast<PdfString>().Select(s => s.GetText()));
        var i = Assert.IsType<PdfArray>(doc.ResolveObject(field[PdfName.ChoiceSelectedIndices]));
        Assert.Equal(new[] { 0L, 2L }, i.Cast<PdfNumber>().Select(n => n.LongValue));

        // The appearance draws both selections, one under the other.
        string appearance = AppearanceOf(doc, field);
        Assert.Contains("(en)", appearance, System.StringComparison.Ordinal);
        Assert.Contains("(fr)", appearance, System.StringComparison.Ordinal);
        Assert.True(appearance.IndexOf("(en)", System.StringComparison.Ordinal) < appearance.IndexOf("(fr)", System.StringComparison.Ordinal));
    }

    /// <summary>One selection is the string <c>/V</c> every other field has, and the file's <c>/I</c> — stale now — goes.</summary>
    [Fact]
    public void OneSelection_IsAString_AndDropsAStaleIndexArray()
    {
        byte[] form = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (langs) /Rect [100 600 300 640] /DA (/Helv 10 Tf 0 g) " +
            "/Opt [(en) (he) (fr)] /V [(en) (he)] /I [0 1] >>");

        byte[] filled = Fill(form, "he", out _);

        using var doc = PdfDocument.Open(new MemoryStream(filled));
        var field = (PdfDictionary)doc.GetObject(5);
        Assert.Equal("he", Assert.IsType<PdfString>(doc.ResolveObject(field[PdfName.V])).GetText());
        Assert.Null(field[PdfName.ChoiceSelectedIndices]);
        Assert.Equal(new[] { "he" }, PdfFormReader.GetFields(new MemoryStream(filled)).Single().SelectedValues);
    }

    /// <summary>A selection that is no option is still written — the caller asked for it — but without <c>/I</c>, and said so.</summary>
    [Fact]
    public void ASelectionThatIsNoOption_IsWrittenWithoutIndices_AndWarned()
    {
        byte[] filled = Fill(FormPdfFixtures.ExchangeForm(), "en" + PdfFormFiller.MultiSelectSeparator + "xx", out FormFillResult result);

        using var doc = PdfDocument.Open(new MemoryStream(filled));
        var field = (PdfDictionary)doc.GetObject(Langs);
        Assert.Equal(2, Assert.IsType<PdfArray>(doc.ResolveObject(field[PdfName.V])).Count);
        Assert.Null(field[PdfName.ChoiceSelectedIndices]);
        Assert.Contains(result.Warnings, w => w.Contains("xx", System.StringComparison.Ordinal) || w.Contains("/Opt", System.StringComparison.Ordinal));
    }

    /// <summary>An exchange file's several selections reach the document whole: nothing is narrowed to the first.</summary>
    [Fact]
    public void AnImportedMultiValuedField_KeepsEverySelection()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("langs", "he", "fr"));
        using var output = new MemoryStream();
        FormFillResult result = PdfFormDataExchange.Import(new MemoryStream(FormPdfFixtures.ExchangeForm()), data, output);

        Assert.Contains("langs", result.FilledFields);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("only the first", System.StringComparison.Ordinal));
        PdfFormFieldInfo langs = PdfFormReader.GetFields(new MemoryStream(output.ToArray())).Single(f => f.FullyQualifiedName == "langs");
        Assert.Equal(new[] { "he", "fr" }, langs.SelectedValues);
    }

    private static byte[] Fill(byte[] form, string value, out FormFillResult result)
    {
        using var output = new MemoryStream();
        result = PdfFormFiller.Fill(new MemoryStream(form), output, new Dictionary<string, string> { ["langs"] = value }, new FormFillOptions());
        return output.ToArray();
    }

    private static string AppearanceOf(PdfDocument doc, PdfDictionary widget)
    {
        var ap = (PdfDictionary)doc.ResolveObject(widget[PdfName.AP]);
        var n = (PdfStream)doc.ResolveObject(ap[PdfName.N]);
        return Encoding.ASCII.GetString(n.GetDecodedData());
    }
}
