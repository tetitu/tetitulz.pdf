// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Regressions for the assembly-scan findings against <see cref="PdfMerger"/>,
/// <see cref="PdfSplitter"/> and <see cref="PdfWatermarker"/>.
///
/// <para>Every assertion here is on the <b>parsed output document</b> — the object a key resolves
/// to, the set of field names, the presence of a page's bytes — rather than on "the call produced
/// output". A split that emits <c>/Font &lt;&lt; /F1 null &gt;&gt;</c> and a split that carries the
/// font both produce a perfectly valid, non-empty PDF.</para>
/// </summary>
public class MergeSplitScanFindingsTests
{
    // ------------------------------------------------------------------
    //  PdfMerger — /Outlines /Count is a value the file chooses
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_AdversarialOutlineCount_CompletesAndWritesAFiniteCount()
    {
        // long.MinValue: the unchecked (long) conversion of this double yields long.MinValue on
        // x64, and Math.Abs(long.MinValue) throws OverflowException — the whole merge died on one
        // number in one input. The guard's own evidence is that the merge returns AND that the
        // emitted /Count is a sane, finite number.
        byte[] hostile = OnePageWithOutline("-9223372036854775808");
        byte[] plain = OnePageWithOutline("1");

        using var merged = Merge(hostile, plain);

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);
        var count = merged.ResolveObject(outlines[PdfName.Count]) as PdfNumber;
        Assert.NotNull(count);
        Assert.True(double.IsFinite(count.DoubleValue), "the /Count written must be a finite number");
        Assert.InRange(count.LongValue, 1, int.MaxValue);
    }

    /// <remarks>
    /// The values are <b>reals</b>, with a decimal point, and that is not incidental: an integer
    /// token that will not fit a <c>long</c> is read as <c>0</c> by <c>PdfToken.LongValue</c>, so
    /// <c>/Count 99999999999999999999999999</c> never reaches this code at all — a fixture that
    /// cannot fail. §7.3.3 makes a real a legal <c>/Count</c>, and its magnitude is unbounded.
    /// </remarks>
    [Theory]
    [InlineData("1000000000000000000000.0")]
    [InlineData("-1000000000000000000000.0")]
    [InlineData("-9223372036854775808.0")]
    public void Merge_OutOfRangeOutlineCount_IsClampedNotPropagated(string count)
    {
        using var merged = Merge(OnePageWithOutline(count), OnePageWithOutline("2"));

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var emitted = merged.ResolveObject(outlines[PdfName.Count]) as PdfNumber;
        Assert.InRange(emitted.LongValue, 1, int.MaxValue);
    }

    // ------------------------------------------------------------------
    //  PdfMerger — §12.7.3.2 fully-qualified field names must stay unique
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_TwoFilledCopiesOfOneTemplate_KeepsBothValuesUnderDistinctFieldNames()
    {
        // Two filled copies of one invoice template: same field /T (Total), values 111 and 222.
        // §12.7.3.2 makes same-named fields ONE field that "shall have the same value", so keeping
        // both names would leave a viewer showing one number on both pages.
        using var merged = Merge(OnePageWithTextField("(Total)", "(111)"),
                                 OnePageWithTextField("(Total)", "(222)"));

        var names = FieldNames(merged);
        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, new HashSet<string>(names, StringComparer.Ordinal).Count);
        Assert.Contains("Total", names);
        Assert.Contains("Total.1", names);

        // Both values must survive, each under its own name — a rename that dropped a /V would be
        // the same data loss by another route.
        Assert.Equal("111", FieldValue(merged, "Total"));
        Assert.Equal("222", FieldValue(merged, "Total.1"));

        Assert.Contains("suffixed", PdfMerger.LastFormWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_DistinctFieldNames_AreLeftAloneAndReportNoWarning()
    {
        // The control: without it, a fix that suffixed unconditionally would pass the test above.
        using var merged = Merge(OnePageWithTextField("(Buyer)", "(A)"),
                                 OnePageWithTextField("(Seller)", "(B)"));

        var names = FieldNames(merged);
        Assert.Equal(new List<string> { "Buyer", "Seller" }, names);
        Assert.Null(PdfMerger.LastFormWarning);
    }

    // ------------------------------------------------------------------
    //  PdfSplitter — §7.7.3.4 inherited /Resources
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_PageWithInheritedResources_ExtractsAFontDictionaryNotANullReference()
    {
        // The page carries no /Resources of its own; the root /Pages node does. The extracted page
        // used to get `/Font << /F1 null >>` — every Tj drew nothing — because the font object was
        // never collected and DeepClone maps an unmapped reference to PdfNull.
        using var extracted = Split(TwoPagesWithInheritedResources(), 1, 1);

        var page = extracted.Pages[0].Dictionary;
        var resources = extracted.ResolveObject(page[PdfName.Resources]) as PdfDictionary;
        Assert.NotNull(resources);
        var fonts = extracted.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);

        var font = extracted.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;
        Assert.NotNull(font);
        Assert.Equal("Font", (extracted.ResolveObject(font[PdfName.Type]) as PdfName)?.Value);
        Assert.Equal("Helvetica", (extracted.ResolveObject(font[PdfName.BaseFont]) as PdfName)?.Value);
    }

    [Fact]
    public void SplitRange_PageWithInheritedXObject_ExtractsTheXObjectStream()
    {
        using var extracted = Split(TwoPagesWithInheritedResources(), 2, 2);

        var resources = extracted.ResolveObject(
            extracted.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = extracted.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
        var form = extracted.ResolveObject(xobjects[new PdfName("Fm0")]) as PdfStream;
        Assert.NotNull(form);
        Assert.Equal("Form", (extracted.ResolveObject(form[PdfName.Subtype]) as PdfName)?.Value);
    }

    // ------------------------------------------------------------------
    //  PdfSplitter — a page outside the range must not travel with the extract
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_LinkAnnotationToADroppedPage_LeavesThatPagesContentOutOfTheFile()
    {
        // Page 1 carries a link whose /Dest names page 2. Extracting page 1 alone used to write
        // page 2's whole content stream into the output as an orphan object: invisible in a viewer
        // and recoverable with any text extractor.
        byte[] extracted = SplitToBytes(LinkToSecondPagePdf(), 1, 1);

        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Single(doc.Pages);
        Assert.Equal(1, CountPageObjects(doc));

        // The dangling destination is dropped rather than left as [null /Fit], which is the same
        // judgement RechainOutline already makes for an outline item.
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        Assert.Null(annot[PdfName.Dest]);
    }

    [Fact]
    public void SplitRange_WidgetOnADroppedPage_DoesNotDragThatPageIn()
    {
        // A form widget on page 2 names its page with /P. The AcroForm carry-over walks /Fields,
        // so /P was an ordinary reference into a page the caller did not ask for.
        byte[] extracted = SplitToBytes(WidgetOnSecondPagePdf(), 1, 1);

        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(extracted), StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(1, CountPageObjects(doc));
    }

    [Fact]
    public void SplitRange_LinkAnnotationToAKeptPage_KeepsItsDestination()
    {
        // The control: the filter must reject only pages outside the range. Without this, a fix
        // that dropped every /Dest would pass the two tests above.
        using var doc = Split(LinkToSecondPagePdf(), 1, 2);

        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        var dest = doc.ResolveObject(annot[PdfName.Dest]) as PdfArray;
        Assert.NotNull(dest);
        var target = dest[0] as PdfReference;
        Assert.NotNull(target);
        Assert.Equal(doc.Pages[1].Dictionary.ObjectNumber, target.ReferenceObjectNumber);
    }

    // ------------------------------------------------------------------
    //  PdfWatermarker — an encrypted document cannot be stamped
    // ------------------------------------------------------------------

    [Fact]
    public void AddWatermark_EncryptedInput_ThrowsInsteadOfReturningAnUnmarkedCopy()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIAL" }));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Nothing may be handed back: an output the size of the input is exactly what made the old
        // behaviour look like success.
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AddWatermark_PlainInput_StampsEveryPage()
    {
        // The control for the refusal above, and it asserts the watermark text reaches the page's
        // content rather than that bytes came back.
        byte[] source = TwoPagesWithInheritedResources();
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();

        PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIAL" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(2, doc.Pages.Count);
        for (int i = 0; i < doc.Pages.Count; i++)
            Assert.Contains("CONFIDENTIAL",
                Encoding.ASCII.GetString(doc.Pages[i].GetContentStreamData()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var ma = new MemoryStream(a);
        using var mb = new MemoryStream(b);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, ma, mb);
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] SplitToBytes(byte[] source, int startPage, int endPage)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, startPage, endPage);
        return output.ToArray();
    }

    private static PdfDocument Split(byte[] source, int startPage, int endPage) =>
        PdfDocument.Open(new MemoryStream(SplitToBytes(source, startPage, endPage)));

    // The /T of every top-level field of the merged form, in order.
    private static List<string> FieldNames(PdfDocument doc)
    {
        var result = new List<string>();
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        if (!(doc.ResolveObject(form?[PdfName.Fields]) is PdfArray fields)) return result;
        for (int i = 0; i < fields.Count; i++)
            if (doc.ResolveObject(fields[i]) is PdfDictionary field &&
                doc.ResolveObject(field[PdfName.T]) is PdfString t)
                result.Add(Encoding.ASCII.GetString(t.RawBytes));
        return result;
    }

    private static string FieldValue(PdfDocument doc, string fieldName)
    {
        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var fields = doc.ResolveObject(form[PdfName.Fields]) as PdfArray;
        for (int i = 0; i < fields.Count; i++)
        {
            if (!(doc.ResolveObject(fields[i]) is PdfDictionary field)) continue;
            if (!(doc.ResolveObject(field[PdfName.T]) is PdfString t)) continue;
            if (Encoding.ASCII.GetString(t.RawBytes) != fieldName) continue;
            return doc.ResolveObject(field[PdfName.V]) is PdfString v
                ? Encoding.ASCII.GetString(v.RawBytes) : null;
        }
        return null;
    }

    // How many /Type /Page objects the file actually contains — not how many the page tree lists.
    // An orphan page written outside /Kids is exactly the leak these tests are about.
    private static int CountPageObjects(PdfDocument doc)
    {
        int count = 0;
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
            if (kvp.Value is PdfDictionary dict &&
                (dict[PdfName.Type] as PdfName)?.Value == "Page")
                count++;
        return count;
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static byte[] OnePageWithOutline(string count) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        $"<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count {count} >>",
        "<< /Title (B) /Parent 5 0 R /Dest [3 0 R /Fit] >>",
    });

    private static byte[] OnePageWithTextField(string title, string value) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [6 0 R] >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Fields [6 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T " + title + " /V " + value +
        " /Rect [72 700 200 720] /P 3 0 R >>",
    });

    // Two pages that carry NO /Resources of their own: the fonts and XObjects they name are on the
    // root /Pages node, which §7.7.3.4 makes them inherit.
    private static byte[] TwoPagesWithInheritedResources() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 /Resources " +
        "<< /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        $"<< /Length {PageContentDrawingTheForm.Length} >>\nstream\n{PageContentDrawingTheForm}\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>",
        $"<< /Length {PageContentDrawingTheForm.Length} >>\nstream\n{PageContentDrawingTheForm}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream",
    });

    // The pages draw their form: an extract carries the resource entries its pages draw.
    private const string PageContentDrawingTheForm = PageContent + " /Fm0 Do";

    // Page 1 links to page 2, whose content stream carries a word no extract of page 1 may contain.
    private static byte[] LinkToSecondPagePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        $"<< /Length {SecretContent.Length} >>\nstream\n{SecretContent}\nendstream",
        "<< /Type /Annot /Subtype /Link /Rect [72 700 200 720] /Dest [5 0 R /Fit] >>",
    });

    // A widget annotation whose /P names page 2, reached through the catalog /AcroForm.
    private static byte[] WidgetOnSecondPagePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        $"<< /Length {SecretContent.Length} >>\nstream\n{SecretContent}\nendstream",
        "<< /Fields [8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Sig) /Rect [72 700 200 720] /P 5 0 R >>",
    });

    private const string SecretContent = "BT /F1 12 Tf 72 700 Td (SECRET) Tj ET";

    // An /Encrypt dictionary is all this needs: PdfDocument reports IsEncrypted from the trailer,
    // and loads neither catalog nor page tree — which is precisely why the watermarker used to
    // stamp zero pages and report success.
    private static byte[] EncryptedPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718191A1B1C1D1E1F20> /U <0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718191A1B1C1D1E1F20> /P -1 >>",
        };
        return Assemble(bodies, trailerExtras: "/Encrypt 5 0 R ");
    }

    private static byte[] Assemble(List<string> bodies, string trailerExtras = "")
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
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtras}>>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
