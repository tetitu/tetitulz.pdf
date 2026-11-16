// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The arrays that name fields — the form's <c>/Fields</c> and <c>/CO</c>, a field's <c>/Kids</c> —
/// are written from the plan's kept entries rather than copied whole and narrowed afterwards.
/// </summary>
/// <remarks>
/// The copy cost what each array held in every extract, so a split of a form with a field on every
/// page copied pages × fields. And an array held as an indirect object of its own is collected by no
/// walk, so its copy was <c>null</c>: the key was then removed, and every field left the extract
/// with it while <see cref="PdfSplitter.LastFormWarning"/> stayed null.
/// </remarks>
public class SplitFieldArraysTests
{
    private const string Rect = "/Rect [72 700 200 720]";

    // ------------------------------------------------------------------
    //  Indirect arrays
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_AnIndirectFieldsArray_KeepsTheFieldOnTheKeptPage()
    {
        byte[] extracted = SplitPageOne(TwoFieldForm(formIsIndirect: true, fieldsAreIndirect: true));

        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(new[] { "Kept" }, TopLevelTitles(doc));
        Assert.Null(PdfSplitter.LastFormWarning);
    }

    [Fact]
    public void SplitRange_AnIndirectFieldsArrayOnAFormHeldInTheCatalog_KeepsTheFieldOnTheKeptPage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            SplitPageOne(TwoFieldForm(formIsIndirect: false, fieldsAreIndirect: true))));
        Assert.Equal(new[] { "Kept" }, TopLevelTitles(doc));
    }

    [Fact]
    public void SplitRange_DirectFieldsArray_IsWrittenInItsPlaceAmongTheFormsKeys()
    {
        // The control for the two above, and for the placeholder: /Fields is written where the source
        // has it, ahead of /DA, not appended after the form's other keys.
        byte[] extracted = SplitPageOne(TwoFieldForm(formIsIndirect: true, fieldsAreIndirect: false));
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(new[] { "Kept" }, TopLevelTitles(doc));

        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.Equal(new[] { "Fields", "CO", "DA" }, form.Select(kvp => kvp.Key.Value).ToArray());
    }

    [Fact]
    public void SplitRange_AnIndirectKidsArray_KeepsTheKidOnTheKeptPage()
    {
        byte[] extracted = SplitPageOne(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [7 0 R] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /Annots [8 0 R] >>"),
            (7, "<< /Type /Annot /Subtype /Widget /T (Street) /V (OK) /Parent 10 0 R " + Rect + " /P 3 0 R >>"),
            (8, "<< /Type /Annot /Subtype /Widget /T (City) /V (SECRET) /Parent 10 0 R " + Rect + " /P 4 0 R >>"),
            (9, "<< /Fields [10 0 R] >>"),
            (10, "<< /FT /Tx /T (Address) /Kids 11 0 R >>"),
            (11, "[7 0 R 8 0 R]")));

        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var parent = doc.ResolveObject(Fields(doc)[0]) as PdfDictionary;
        var kids = doc.ResolveObject(parent[PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);
        Assert.Equal(new[] { "Street" }, kids.Select(k => Text(doc, (doc.ResolveObject(k) as PdfDictionary)[PdfName.T])).ToArray());
    }

    [Fact]
    public void SplitRange_AnIndirectCalculationOrder_KeepsTheKeptFieldOnly()
    {
        // An indirect /CO was removed outright, so the extract lost the calculation order of the
        // fields it kept.
        byte[] extracted = SplitPageOne(TwoFieldForm(formIsIndirect: true, fieldsAreIndirect: false, orderIsIndirect: true));
        using var doc = PdfDocument.Open(new MemoryStream(extracted));

        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var order = doc.ResolveObject(form[PdfName.CO]) as PdfArray;
        Assert.NotNull(order);
        Assert.Equal(new[] { "Kept" }, order.Select(e => Text(doc, (doc.ResolveObject(e) as PdfDictionary)[PdfName.T])).ToArray());
    }

    // ------------------------------------------------------------------
    //  What a split copies — the evidence
    // ------------------------------------------------------------------

    [Fact]
    public void EachPage_OfAFieldWithAKidOnEveryPage_CopiesOnlyEachPagesKid()
    {
        const int pages = 150;
        using var doc = PdfDocument.Open(new MemoryStream(OneFieldAKidOnEveryPage(pages)));

        long before = PdfObjectHelper.ClonedValues;
        var split = PdfSplitter.EachPage(doc);
        long copied = PdfObjectHelper.ClonedValues - before;

        // About forty values a page. Copying the field's whole /Kids into every page's extract added
        // pages² — 22 500 here — before the kept one was picked out.
        Assert.InRange(copied, 20L * pages, 80L * pages);

        for (int p = 0; p < pages; p += 37)
        {
            using var page = PdfDocument.Open(new MemoryStream(split[p]));
            var field = page.ResolveObject(Fields(page)[0]) as PdfDictionary;
            var kids = page.ResolveObject(field[PdfName.Kids]) as PdfArray;
            Assert.Equal(new[] { "w" + p.ToString(CultureInfo.InvariantCulture) },
                kids.Select(k => Text(page, (page.ResolveObject(k) as PdfDictionary)[PdfName.T])).ToArray());
        }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] SplitPageOne(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);
        return output.ToArray();
    }

    private static PdfArray Fields(PdfDocument doc)
    {
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(form);
        var fields = doc.ResolveObject(form[PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        return fields;
    }

    private static string[] TopLevelTitles(PdfDocument doc) =>
        Fields(doc).Select(f => Text(doc, (doc.ResolveObject(f) as PdfDictionary)?[PdfName.T])).ToArray();

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    // Two pages, a filled field on each; the form, its /Fields and its /CO each held directly or as
    // an indirect object of its own.
    private static byte[] TwoFieldForm(bool formIsIndirect, bool fieldsAreIndirect, bool orderIsIndirect = false)
    {
        string fields = fieldsAreIndirect ? "11 0 R" : "[7 0 R 8 0 R]";
        string order = orderIsIndirect ? "12 0 R" : "[7 0 R 8 0 R]";
        string form = $"<< /Fields {fields} /CO {order} /DA (/Helv 0 Tf 0 g) >>";
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm " + (formIsIndirect ? "9 0 R" : form) + " >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [7 0 R] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /Annots [8 0 R] >>"),
            (7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Kept) /V (OK) " + Rect + " /P 3 0 R >>"),
            (8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Dropped) /V (SECRET) " + Rect + " /P 4 0 R >>"),
            (11, "[7 0 R 8 0 R]"),
            (12, "[7 0 R 8 0 R]"),
        };
        if (formIsIndirect) objects.Add((9, form));
        return Pdf(objects.ToArray());
    }

    // One parent field whose /Kids holds a widget on every page.
    private static byte[] OneFieldAKidOnEveryPage(int pages)
    {
        const int firstPage = 10;
        int firstWidget = firstPage + pages, content = firstWidget + pages;
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>"),
            (2, "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{firstPage + p} 0 R")) +
                $"] /Count {pages} /MediaBox [0 0 612 792] >>"),
            (3, "<< /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) >>"),
            (4, "<< /FT /Tx /T (Root) /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{firstWidget + p} 0 R")) + "] >>"),
            (content, "<< /Length 0 >>\nstream\n\nendstream"),
        };
        for (int p = 0; p < pages; p++)
        {
            objects.Add((firstPage + p, $"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots [{firstWidget + p} 0 R] >>"));
            objects.Add((firstWidget + p,
                $"<< /Type /Annot /Subtype /Widget /T (w{p}) /V (v{p}) /Parent 4 0 R {Rect} /P {firstPage + p} 0 R >>"));
        }
        return Pdf(objects.ToArray());
    }

    private static byte[] Pdf(params (int Number, string Body)[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Number))
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int size = objects.Max(o => o.Number) + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append('\n');
        for (int n = 0; n < size; n++)
            sb.Append(offsets.TryGetValue(n, out int offset)
                ? offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"
                : "0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
