// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The other half of the "split embeds out-of-range pages" finding: a field whose widgets all landed
/// on dropped pages no longer travels with the extract.
///
/// <para>Dropping the widget's dangling <c>/P</c> stopped the field <i>dragging a page in</i>; the
/// field itself stayed, <c>/V</c> and all. Splitting page 1 out of a filled form to share it
/// therefore handed the recipient the answers written on pages 2 onward — invisible in a viewer, one
/// object dump away.</para>
///
/// <para>Every leak assertion here is against the <b>whole output byte array</b>, not against the
/// parsed <c>/Fields</c>: an object that is written but unreferenced parses as nothing and reads
/// perfectly well with any extractor, which is exactly the shape this defect had.</para>
/// </summary>
public class SplitAcroFormFieldScopeTests
{
    private const string KeptAnswer = "KEPT-ANSWER";
    private const string SecretAnswer = "SECRET-ANSWER";

    // ------------------------------------------------------------------
    //  The leak, and its mirror image
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_FieldWhoseWidgetIsOnADroppedPage_LeavesItsValueOutOfTheFileEntirely()
    {
        byte[] extracted = SplitToBytes(TwoPageFormPdf(), 1, 1);

        Assert.DoesNotContain(SecretAnswer, Ascii(extracted), StringComparison.Ordinal);
        Assert.DoesNotContain("Dropped", Ascii(extracted), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_FieldWhoseWidgetIsOnAKeptPage_SurvivesWithItsValue()
    {
        // The honest negative. Without it, a "fix" that dropped every field would pass the test
        // above — and would also be a total loss of the form the caller asked to extract.
        using var doc = Split(TwoPageFormPdf(), 1, 1);

        var fields = FieldsOf(doc);
        Assert.Single(fields);

        var field = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.NotNull(field);
        Assert.Equal("Kept", Text(doc, field[PdfName.T]));
        Assert.Equal(KeptAnswer, Text(doc, field[PdfName.V]));
    }

    [Fact]
    public void SplitRange_TheOtherPage_KeepsTheOtherFieldAndDropsTheFirst()
    {
        // The mirror: the filter must key on which pages were kept, not on field order or on the
        // word "Dropped". A fix that kept only the first entry passes both tests above.
        byte[] extracted = SplitToBytes(TwoPageFormPdf(), 2, 2);
        Assert.DoesNotContain(KeptAnswer, Ascii(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var fields = FieldsOf(doc);
        Assert.Single(fields);
        var field = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.Equal("Dropped", Text(doc, field[PdfName.T]));
        Assert.Equal(SecretAnswer, Text(doc, field[PdfName.V]));
    }

    [Fact]
    public void SplitRange_BothPages_CarriesBothFieldsUntouched()
    {
        // The widest control: nothing is filtered when nothing is dropped.
        using var doc = Split(TwoPageFormPdf(), 1, 2);

        var fields = FieldsOf(doc);
        Assert.Equal(2, fields.Count);
        Assert.Equal(
            new List<string> { "Kept", "Dropped" },
            new List<string>
            {
                Text(doc, (doc.ResolveObject(fields[0]) as PdfDictionary)[PdfName.T]),
                Text(doc, (doc.ResolveObject(fields[1]) as PdfDictionary)[PdfName.T]),
            });
    }

    // ------------------------------------------------------------------
    //  /AcroForm coherence
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_NoFieldSurvives_WritesNoAcroFormAtAll()
    {
        // Page 1 carries no widget, so the only field belongs to page 2. A form shell with /DR and
        // /DA and no fields describes a document that no longer exists.
        using var doc = Split(OneFieldOnSecondPagePdf(), 1, 1);

        Assert.Null(doc.Catalog.Dictionary[PdfName.AcroForm]);
    }

    [Fact]
    public void SplitRange_SurvivingForm_KeepsItsDefaultResourcesAppearanceAndNeedAppearances()
    {
        // /DR, /DA and /NeedAppearances are page-independent defaults the surviving fields still
        // inherit, so filtering /Fields must not take them with it — a field with a /V, no /AP and
        // no /DA renders as nothing.
        using var doc = Split(TwoPageFormPdf(), 1, 1);

        var form = FormOf(doc);
        Assert.Equal("/Helv 10 Tf 0 g", Text(doc, form[PdfName.DA]));
        Assert.True((doc.ResolveObject(form[PdfName.NeedAppearances]) as PdfBoolean)?.Value);

        var dr = doc.ResolveObject(form[PdfName.DR]) as PdfDictionary;
        var drFonts = doc.ResolveObject(dr[PdfName.Font]) as PdfDictionary;
        var helv = doc.ResolveObject(drFonts[new PdfName("Helv")]) as PdfDictionary;
        Assert.NotNull(helv);
        Assert.Equal("Helvetica", (doc.ResolveObject(helv[PdfName.BaseFont]) as PdfName)?.Value);
    }

    [Fact]
    public void SplitRange_CalculationOrder_LosesOnlyTheDroppedField()
    {
        // §12.7.2 Table 218: /CO names terminal fields in calculation order. An entry naming a field
        // the extract dropped clones to null, and [null 4 0 R] asks a viewer to calculate nothing.
        using var doc = Split(TwoPageFormPdf(), 1, 1);

        var co = doc.ResolveObject(FormOf(doc)[new PdfName("CO")]) as PdfArray;
        Assert.NotNull(co);
        Assert.Single(co);
        Assert.Equal("Kept", Text(doc, (doc.ResolveObject(co[0]) as PdfDictionary)[PdfName.T]));
    }

    // ------------------------------------------------------------------
    //  Parents, kids, and empty parent nodes
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_ParentWithKidsOnTwoPages_KeepsTheParentAndOnlyTheKidItCanStillShow()
    {
        byte[] extracted = SplitToBytes(ParentWithKidsOnTwoPagesPdf(), 1, 1);
        Assert.DoesNotContain("SECRET-CITY", Ascii(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var fields = FieldsOf(doc);
        Assert.Single(fields);

        var parent = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.Equal("Address", Text(doc, parent[PdfName.T]));

        var kids = doc.ResolveObject(parent[PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);
        Assert.Single(kids);
        Assert.Equal("STREET-OK",
            Text(doc, (doc.ResolveObject(kids[0]) as PdfDictionary)[PdfName.V]));
    }

    [Fact]
    public void SplitRange_ParentWhoseEveryKidIsOnADroppedPage_IsRemovedFromFields()
    {
        // "Removing empty parent nodes": the parent itself is on no page — it is a naming node — so
        // once both kids go there is nothing left of it to show.
        byte[] extracted = SplitToBytes(ParentWithKidsOnTwoPagesPdf(), 3, 3);

        Assert.DoesNotContain("STREET-OK", Ascii(extracted), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-CITY", Ascii(extracted), StringComparison.Ordinal);
        Assert.DoesNotContain("Address", Ascii(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Null(doc.Catalog.Dictionary[PdfName.AcroForm]);
    }

    [Fact]
    public void SplitRange_WidgetListedByAKeptPageWithNoSlashP_StillSurvives()
    {
        // §12.5.6.19 makes /P optional and §12.5.2 makes the page's /Annots the definitive statement
        // of what appears on it. A filter that trusted /P alone would silently strip every field of
        // a producer that omits it — the whole form, from a file that is perfectly conforming.
        using var doc = Split(WidgetWithoutPageBackPointerPdf(), 1, 1);

        var fields = FieldsOf(doc);
        Assert.Single(fields);
        Assert.Equal("NoBackPointer",
            Text(doc, (doc.ResolveObject(fields[0]) as PdfDictionary)[PdfName.T]));
    }

    [Fact]
    public void SplitRange_WidgetNamingAKeptPageWithSlashPButAbsentFromItsAnnots_StillSurvives()
    {
        // The other half of the placement test, and the one that keeps the filter conservative: a
        // producer that forgets to list a widget in /Annots has still said which page it belongs to,
        // and §12.5.6.19's /P is a statement about this document. Dropping the field on the strength
        // of a missing /Annots entry would delete a value the source plainly attached to a page the
        // caller asked for.
        using var doc = Split(WidgetNotListedInAnnotsPdf(), 1, 1);

        var fields = FieldsOf(doc);
        Assert.Single(fields);
        var field = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.Equal("OrphanWidget", Text(doc, field[PdfName.T]));
        Assert.Equal(KeptAnswer, Text(doc, field[PdfName.V]));
    }

    /// <summary>
    /// §12.5.2 makes a page's <c>/Annots</c> the statement of what is on it and §12.5.6.19's <c>/P</c>
    /// a back-pointer: a widget the dropped page lists is on the dropped page whatever its stale
    /// <c>/P</c> says, so its value leaves the extract. The orphan case above — a widget no page lists —
    /// is the only one the back-pointer decides. The control extracts the page that lists it.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void SplitRange_WidgetListedByADroppedPageWhoseStaleSlashPNamesAKeptPage_IsDropped(int page, bool kept)
    {
        byte[] extracted = SplitToBytes(WidgetListedByOtherPageWithStalePointerPdf(), page, page);

        Assert.Equal(kept, Ascii(extracted).Contains(SecretAnswer, StringComparison.Ordinal));
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        if (kept)
        {
            var fields = FieldsOf(doc);
            Assert.Single(fields);
            Assert.Equal("StalePointer", Text(doc, (doc.ResolveObject(fields[0]) as PdfDictionary)[PdfName.T]));
        }
        else
        {
            Assert.Null(doc.Catalog.Dictionary[PdfName.AcroForm]);
        }
    }

    // ------------------------------------------------------------------
    //  Adversarial input — the guards' own evidence
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_FieldKidsCycle_ReportsTheCycleAndDropsWhatItCannotProve()
    {
        // /Kids names its own parent. Without the in-progress set the walk recurses for ever; with
        // only a "visited" set and no memo it answers the second visit wrongly. The evidence is the
        // reported reason and the emitted field set — never how long the call took.
        byte[] extracted = SplitToBytes(FieldKidsCyclePdf(), 1, 1);

        Assert.Contains("/Kids cycle", PdfSplitter.LastFormWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-LOOP", Ascii(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var fields = FieldsOf(doc);
        Assert.Single(fields);

        // The parent survives exactly ONCE — a cycle that expanded would repeat it — and keeps only
        // the kid that is actually on page 1.
        var parent = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.Equal("Parent", Text(doc, parent[PdfName.T]));
        var kids = doc.ResolveObject(parent[PdfName.Kids]) as PdfArray;
        Assert.Single(kids);
        Assert.Equal("OnPageOne",
            Text(doc, (doc.ResolveObject(kids[0]) as PdfDictionary)[PdfName.T]));
    }

    [Fact]
    public void SplitRange_AcyclicForm_ReportsNoFormWarning()
    {
        // The control for the cycle report: a warning that fired on every document would be one a
        // caller learns to ignore.
        SplitToBytes(TwoPageFormPdf(), 1, 1);
        Assert.Null(PdfSplitter.LastFormWarning);
    }

    [Fact]
    public void AcroFormPageFilter_ExhaustedNodeBudget_RefusesInsteadOfCarryingUnprovenFields()
    {
        // The budget is not reachable through SplitRange, so the guard is exercised where it lives.
        // A budget too small to prove anything must yield NOTHING plus a reason — carrying the
        // fields it never examined is exactly the leak, and reporting nothing would make a partial
        // walk indistinguishable from a complete one.
        using var doc = PdfDocument.Open(new MemoryStream(TwoPageFormPdf()));
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var keptPageObjNums = new HashSet<int> { doc.Pages[0].Dictionary.ObjectNumber };

        var plan = AcroFormPageFilter.Filter(
            form, doc, keptPageObjNums, new[] { doc.Pages[0] }, nodeBudget: 1);

        Assert.False(plan.AnyFieldSurvives);
        Assert.Empty(plan.KeptNodes);
        Assert.Contains("budget", plan.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AcroFormPageFilter_AmpleBudget_ProvesTheKeptFieldAndOnlyThat()
    {
        // The floor under the budget test: with the default budget the same fixture must produce a
        // survivor, or the refusal above is indistinguishable from a filter that never works.
        using var doc = PdfDocument.Open(new MemoryStream(TwoPageFormPdf()));
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var keptPageObjNums = new HashSet<int> { doc.Pages[0].Dictionary.ObjectNumber };

        var plan = AcroFormPageFilter.Filter(form, doc, keptPageObjNums, new[] { doc.Pages[0] });

        Assert.True(plan.AnyFieldSurvives);
        Assert.Single(plan.KeptNodes);
        Assert.Equal("Kept", Text(doc, plan.KeptNodes[0][PdfName.T]));
        Assert.Null(plan.Warning);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] SplitToBytes(byte[] source, int startPage, int endPage)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, startPage, endPage);
        return output.ToArray();
    }

    private static PdfDocument Split(byte[] source, int startPage, int endPage) =>
        PdfDocument.Open(new MemoryStream(SplitToBytes(source, startPage, endPage)));

    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static PdfDictionary FormOf(PdfDocument doc)
    {
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(form);
        return form;
    }

    private static PdfArray FieldsOf(PdfDocument doc)
    {
        var fields = doc.ResolveObject(FormOf(doc)[PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        return fields;
    }

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static string Contents(string text) =>
        $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    // Two pages, one filled text field on each, both reached from the catalog /AcroForm. Extracting
    // one page must carry that page's answer and leave the other page's behind.
    private static byte[] TwoPageFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Kept) /V (" + KeptAnswer + ") " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Dropped) /V (" + SecretAnswer + ") " +
        "/Rect [72 700 200 720] /P 5 0 R >>",
        "<< /Fields [7 0 R 8 0 R] /CO [7 0 R 8 0 R] /DA (/Helv 10 Tf 0 g) " +
        "/DR << /Font << /Helv 10 0 R >> >> /NeedAppearances true >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    });

    // The form's only field lives on page 2, so extracting page 1 leaves nothing for /AcroForm to
    // describe.
    private static byte[] OneFieldOnSecondPagePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Fields [8 0 R] /DA (/Helv 10 Tf 0 g) >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Only) /V (" + SecretAnswer + ") " +
        "/Rect [72 700 200 720] /P 5 0 R >>",
    });

    // §12.7.3.1's other shape: a non-terminal naming node whose two kid FIELDS sit on different
    // pages, each carrying its own /V. Page 3 exists so the whole parent can be dropped too.
    private static byte[] ParentWithKidsOnTwoPagesPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R 11 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /T (Street) /V (STREET-OK) /Parent 10 0 R " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /T (City) /V (SECRET-CITY) /Parent 10 0 R " +
        "/Rect [72 660 200 680] /P 5 0 R >>",
        "<< /Fields [10 0 R] >>",
        "<< /FT /Tx /T (Address) /Kids [7 0 R 8 0 R] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 12 0 R /Resources << >> >>",
        Contents(PageContent),
    });

    // A merged field/widget listed by page 1's /Annots and carrying NO /P back-pointer.
    private static byte[] WidgetWithoutPageBackPointerPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Fields [8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (NoBackPointer) /V (" + KeptAnswer + ") " +
        "/Rect [72 700 200 720] >>",
    });

    // A widget whose /P names page 1 but which page 1's /Annots does not list. Only the /P branch
    // of the placement test can see it.
    private static byte[] WidgetNotListedInAnnotsPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Fields [8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (OrphanWidget) /V (" + KeptAnswer + ") " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
    });

    // A widget page 2's /Annots lists whose /P, stale, names page 1. Page 1 lists no annotation.
    private static byte[] WidgetListedByOtherPageWithStalePointerPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Fields [8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (StalePointer) /V (" + SecretAnswer + ") " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
    });

    // Object 10's /Kids names object 9, which is its own parent: a two-node ring reached from
    // /Fields. Object 10 also carries a value no extract of page 1 may contain.
    private static byte[] FieldKidsCyclePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /T (OnPageOne) /V (" + KeptAnswer + ") /Parent 9 0 R " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Fields [9 0 R] >>",
        "<< /FT /Tx /T (Parent) /Kids [7 0 R 10 0 R] >>",
        "<< /T (Loop) /V (SECRET-LOOP) /Kids [9 0 R] /Parent 9 0 R >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
