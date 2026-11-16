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
/// The collection walk refuses a reference into a field the form plan dropped as it refuses one into
/// a page the range dropped — and a page is what Core's page list holds, not only a
/// <c>/Type /Page</c>.
/// </summary>
/// <remarks>
/// Every leak assertion is against the whole output byte array: an object written but referenced by
/// nothing still reads perfectly well with any object dump, which is the shape both leaks had.
/// </remarks>
public class SplitCollectionGuardTests
{
    private const string Rect = "/Rect [72 700 200 720]";
    private const string Ssn = "123-45-6789";

    // ------------------------------------------------------------------
    //  A form action naming a dropped field
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_AKeptButtonsSubmitAction_DoesNotCarryTheDroppedFieldItNames()
    {
        // Page 1's push button submits field 8, whose widget is on page 2. The plan drops field 8,
        // but the walk went page 1 → button → /A → /Fields → 8, and wrote 8's /V and the stream
        // that draws it into the page-1 extract.
        byte[] extracted = Split(ButtonNamingAFieldOnPageTwo("<< /S /SubmitForm /F (https://example.com) /Fields [8 0 R] >>"), 1, 1);

        Assert.DoesNotContain(Ssn, Ascii(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var action = doc.ResolveObject(Button(doc)[PdfName.A]) as PdfDictionary;
        Assert.Empty(doc.ResolveObject(action[PdfName.Fields]) as PdfArray);
    }

    [Fact]
    public void SplitRange_BothPages_KeepTheButtonsSubmitTarget()
    {
        // The control: with the field's page kept, the action still names it and its value travels.
        byte[] extracted = Split(ButtonNamingAFieldOnPageTwo("<< /S /SubmitForm /F (https://example.com) /Fields [8 0 R] >>"), 1, 2);

        Assert.Contains(Ssn, Ascii(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var action = doc.ResolveObject(Button(doc)[PdfName.A]) as PdfDictionary;
        var target = doc.ResolveObject((doc.ResolveObject(action[PdfName.Fields]) as PdfArray)[0]) as PdfDictionary;
        Assert.Equal("SSN", Text(doc, target[PdfName.T]));
    }

    [Fact]
    public void SplitRange_AKeptFieldsAdditionalAction_DoesNotCarryTheDroppedFieldItResets()
    {
        // The same leak through the kept-node loop: the button is itself a kept field, and its /AA
        // reset action names field 8.
        byte[] extracted = Split(ButtonNamingAFieldOnPageTwo(null,
            "/AA << /U << /S /ResetForm /Fields [8 0 R] >> >>"), 1, 1);

        Assert.DoesNotContain(Ssn, Ascii(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var triggers = doc.ResolveObject(Button(doc)[PdfName.AA]) as PdfDictionary;
        var reset = doc.ResolveObject(triggers[new PdfName("U")]) as PdfDictionary;
        Assert.Empty(doc.ResolveObject(reset[PdfName.Fields]) as PdfArray);
    }

    [Fact]
    public void SplitRange_AKeptButtonsHideAction_HidesNothingRatherThanCarryingTheDroppedField()
    {
        byte[] extracted = Split(ButtonNamingAFieldOnPageTwo("<< /S /Hide /T 8 0 R >>"), 1, 1);

        Assert.DoesNotContain(Ssn, Ascii(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var action = doc.ResolveObject(Button(doc)[PdfName.A]) as PdfDictionary;
        Assert.Empty(doc.ResolveObject(action[PdfName.T]) as PdfArray);
    }

    [Fact]
    public void SplitRange_AnActionNamingADroppedParentField_CarriesNoneOfItsKids()
    {
        // The action names the dropped field's parent: refusing the parent keeps the walk out of
        // its /Kids, and so out of the kid's value.
        byte[] extracted = Split(ButtonNamingAFieldOnPageTwo("<< /S /SubmitForm /F (https://example.com) /Fields [10 0 R] >>",
            parentOfTheSecretField: true), 1, 1);

        Assert.DoesNotContain(Ssn, Ascii(extracted), StringComparison.Ordinal);
        Assert.DoesNotContain("Identity", Ascii(extracted), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_AWidgetTheFieldWalkCouldNotReach_StaysOnItsKeptPage()
    {
        // Its field is a chain deeper than the walk's depth cap, so the plan cannot prove it and
        // drops it — but the page lists it, and refusing it would take a visible annotation off the
        // very page the caller kept.
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [90 0 R] >>"),
            (4, "<< /Fields [10 0 R] >>"),
        };
        for (int i = 0; i < 80; i++) objects.Add((10 + i, $"<< /T (n{i}) /Kids [{11 + i} 0 R] >>"));
        objects.Add((90, "<< /Type /Annot /Subtype /Widget /T (deep) /V (V) " + Rect + " /P 3 0 R >>"));
        byte[] extracted = Split(Pdf(objects.ToArray()), 1, 1);

        Assert.Contains("nesting deeper", PdfSplitter.LastFormWarning, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var widget = doc.ResolveObject(doc.Pages[0].GetAnnotations()[0]) as PdfDictionary;
        Assert.NotNull(widget);
        Assert.Equal("deep", Text(doc, widget[PdfName.T]));
    }

    [Fact]
    public void CollectionGuard_AFieldTreeTooLargeToList_RefusesWhatReadsAsAFieldUnlessKept()
    {
        // When the listing ran out of budget the guard cannot know every field node, so a dictionary
        // that reads as one is refused unless kept or on a kept page; any other is followed.
        using var doc = PdfDocument.Open(new MemoryStream(ButtonNamingAFieldOnPageTwo(null)));
        var kept = new HashSet<int> { doc.Pages[0].Dictionary.ObjectNumber };
        var incomplete = new PdfSplitter.CollectionGuard(doc, kept, new[] { doc.Pages[0] },
            (new HashSet<int>(), false), plan: null);
        var complete = new PdfSplitter.CollectionGuard(doc, kept, new[] { doc.Pages[0] },
            (new HashSet<int>(), true), plan: null);

        Assert.True(incomplete.Refuses(8, doc.GetObject(8)));      // the SSN widget, on page 2
        Assert.False(incomplete.Refuses(7, doc.GetObject(7)));     // the button page 1 lists
        Assert.False(incomplete.Refuses(9, doc.GetObject(9)));     // an appearance stream
        Assert.False(complete.Refuses(8, doc.GetObject(8)));       // listed nowhere as a field
    }

    [Fact]
    public void FieldNodeNumbers_ListsEveryNode_AndSaysWhenItsBudgetRanOut()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ButtonNamingAFieldOnPageTwo(null, parentOfTheSecretField: true)));
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;

        var all = AcroFormPageFilter.FieldNodeNumbers(form, doc, out bool complete);
        Assert.True(complete);
        Assert.Equal(new[] { 7, 8, 10 }, all.OrderBy(n => n).ToArray());

        var part = AcroFormPageFilter.FieldNodeNumbers(form, doc, out bool partial, nodeBudget: 2);
        Assert.False(partial);
        Assert.Equal(new[] { 7, 10 }, part.OrderBy(n => n).ToArray());
    }

    // ------------------------------------------------------------------
    //  A page Core lists without /Type /Page
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_ALinkToADroppedPageWithNoType_DoesNotCarryThatPage()
    {
        byte[] extracted = Split(UntypedPages(), 1, 1);

        Assert.DoesNotContain("SECRET", Ascii(extracted), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(1, doc.Pages.Count);
        var link = doc.ResolveObject((doc.Pages[0].GetAnnotations())[0]) as PdfDictionary;
        Assert.Null(link[PdfName.Dest]);
    }

    [Fact]
    public void SplitRange_APageWithNoType_HangsUnderTheNewPageTree()
    {
        // DeepClone pointed the copied page's /Parent at the new tree only for /Type /Page, so an
        // untyped page kept the source's /Parent, which no extract carries, and read as null.
        byte[] extracted = Split(UntypedPages(), 1, 2);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));

        Assert.Equal(2, doc.Pages.Count);
        var pages = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]) as PdfDictionary;
        foreach (var page in doc.Pages)
            Assert.Same(pages, doc.ResolveObject(page.Dictionary[PdfName.Parent]));
    }

    [Fact]
    public void Merge_APageWithNoType_HangsUnderTheMergedPageTree()
    {
        using var first = new MemoryStream(UntypedPages());
        using var second = new MemoryStream(UntypedPages());
        using var output = new MemoryStream();
        PdfMerger.Merge(output, first, second);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(4, doc.Pages.Count);
        var pages = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]) as PdfDictionary;
        foreach (var page in doc.Pages)
            Assert.Same(pages, doc.ResolveObject(page.Dictionary[PdfName.Parent]));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] Split(byte[] pdf, int first, int last)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, first, last);
        return output.ToArray();
    }

    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    private static PdfDictionary Button(PdfDocument doc) =>
        doc.Pages[0].GetAnnotations().Select(a => doc.ResolveObject(a) as PdfDictionary)
            .Single(a => Text(doc, a[PdfName.T]) == "Send");

    /// <summary>
    /// Page 1 holds a push button (a field of its own) carrying <paramref name="action"/> and
    /// <paramref name="extra"/>; field 8, on page 2, holds a value and the stream that draws it.
    /// </summary>
    private static byte[] ButtonNamingAFieldOnPageTwo(string action, string extra = "", bool parentOfTheSecretField = false)
    {
        string fields = parentOfTheSecretField ? "[7 0 R 10 0 R]" : "[7 0 R 8 0 R]";
        string ssnParent = parentOfTheSecretField ? " /Parent 10 0 R" : "";
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 11 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 /MediaBox [0 0 612 792] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [7 0 R] >>"),
            (5, "<< /Type /Page /Parent 2 0 R /Annots [8 0 R] >>"),
            (7, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (Send) " + Rect + " /P 3 0 R" +
                (action == null ? "" : " /A " + action) + " " + extra + " >>"),
            (8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (SSN) /V (" + Ssn + ") " + Rect +
                " /AP << /N 9 0 R >> /P 5 0 R" + ssnParent + " >>"),
            (9, "<< /Type /XObject /Subtype /Form /BBox [0 0 128 20] /Length 22 >>\nstream\nBT (" + Ssn + ") Tj ET\nendstream"),
            (11, "<< /Fields " + fields + " /DA (/Helv 0 Tf 0 g) >>"),
        };
        if (parentOfTheSecretField)
            objects.Add((10, "<< /FT /Tx /T (Identity) /Kids [8 0 R] >>"));
        return Pdf(objects.ToArray());
    }

    // Two pages with no /Type, both in Core's page list; page 1's link names page 2, whose content
    // says SECRET.
    private static byte[] UntypedPages() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        (3, "<< /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [7 0 R] >>"),
        (4, "<< /Length 17 >>\nstream\nBT (PUBLIC) Tj ET\nendstream"),
        (5, "<< /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>"),
        (6, "<< /Length 17 >>\nstream\nBT (SECRET) Tj ET\nendstream"),
        (7, "<< /Type /Annot /Subtype /Link " + Rect + " /Dest [5 0 R /Fit] >>"));

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
