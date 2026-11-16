// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The decisions in Bates numbering, N-up, the portfolio manager, the incremental-save validator and
/// the comparer that the folder's second sweep found nothing asserting.
/// </summary>
public class DocumentsBatch2DecisionTests
{
    // ---------------------------------------------------------------- fixtures

    private static byte[] Objects(IReadOnlyList<string> bodies, string trailerExtras = "")
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R {trailerExtras} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>One page per entry, each with its own box and content, Helvetica as /F1.</summary>
    private static byte[] Pages(params (string Box, string Content)[] pages)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{4 + 2 * i} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (var (box, content) in pages)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox {box} /Contents {bodies.Count + 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        return Objects(bodies);
    }

    private const string Encrypt = "/Encrypt << /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >> /ID [<00><00>]";

    private static byte[] Encrypted() => Objects(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>" }, Encrypt);

    private static List<ContentStreamOperation> PageOps(byte[] pdf, int page = 0)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ContentStreamParser.Parse(doc.Pages[page].GetContentStreamData());
    }

    private static double[] Numbers(ContentStreamOperation op) => op.Operands.Select(o => ((PdfNumber)o).DoubleValue).ToArray();

    // ---------------------------------------------------------------- Bates

    /// <summary>A stamp in the header sits a font size below the top margin, its baseline inside the page.</summary>
    [Fact]
    public void Bates_AHeaderStampSitsAFontSizeBelowTheTopMargin()
    {
        using var output = new MemoryStream();
        PdfBatesNumbering.Apply(new MemoryStream(Pages(("[0 0 612 792]", "q Q"))), output,
            new BatesNumberingOptions { InHeader = true, FontSize = 10, MarginPt = 36 });
        var td = PageOps(output.ToArray()).Last(o => o.Operator == "Td");
        Assert.Equal(792 - 36 - 10, Numbers(td)[1], 3);
    }

    /// <summary>Each document of a bundle carries the prefix, the second continuing the first's numbers.</summary>
    [Fact]
    public void Bates_EveryDocumentOfABundleKeepsThePrefix()
    {
        var outputs = new[] { new MemoryStream(), new MemoryStream() };
        long next = PdfBatesNumbering.Apply(
            new[] { (Stream)new MemoryStream(Pages(("[0 0 200 200]", "q Q"))), new MemoryStream(Pages(("[0 0 200 200]", "q Q"))) },
            outputs, new BatesNumberingOptions { Prefix = "ACME-", Digits = 3 });

        Assert.Equal(3, next);
        string Stamp(MemoryStream s) => Encoding.Latin1.GetString(((PdfString)PageOps(s.ToArray()).Last(o => o.Operator == "Tj").Operands[0]).RawBytes);
        Assert.Equal("ACME-001", Stamp(outputs[0]));
        Assert.Equal("ACME-002", Stamp(outputs[1]));
    }

    // ---------------------------------------------------------------- N-up

    private static byte[] Nup(byte[] pdf, int cols, int rows, NupOptions options = null)
    {
        using var output = new MemoryStream();
        PdfNup.CreateNup(new MemoryStream(pdf), output, cols, rows, options);
        return output.ToArray();
    }

    /// <summary>Encrypted input is refused: the sheets are a new file with no /Encrypt.</summary>
    [Fact]
    public void Nup_AnEncryptedDocument_IsRefused()
        => Assert.Throws<PdfEncryptedDocumentException>(() => Nup(Encrypted(), 2, 1));

    /// <summary>No columns is no layout.</summary>
    [Fact]
    public void Nup_ZeroColumns_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Nup(Pages(("[0 0 100 100]", "q Q")), 0, 1));

    /// <summary>
    /// Two pages of a box away from the origin — [10 10 210 310], 200 × 300 — two up: the sheet is the
    /// page's extent, not its far corner, and each page is placed so its own corner lands in its slot:
    /// a 92 × 292 slot after the 4-point margins, scaled by 0.46, centred vertically.
    /// </summary>
    [Fact]
    public void Nup_ABoxAwayFromTheOrigin_SizesTheSheet_AndPlacesEachCorner()
    {
        byte[] sheet = Nup(Pages(("[10 10 210 310]", "q Q"), ("[10 10 210 310]", "q Q")), 2, 1);
        using (var doc = PdfDocument.Open(new MemoryStream(sheet)))
        {
            Assert.Equal(1, doc.Pages.Count);
            var box = doc.Pages[0].MediaBox.Select(o => ((PdfNumber)o).DoubleValue).ToArray();
            Assert.Equal(new[] { 0.0, 0, 200, 300 }, box);
        }

        var cms = PageOps(sheet).Where(o => o.Operator == "cm").Select(Numbers).ToList();
        Assert.Equal(new[] { 0.46, 0, 0, 0.46, -0.6, 76.4 }, cms[0].Select(v => Math.Round(v, 4)));
        Assert.Equal(new[] { 0.46, 0, 0, 0.46, 99.4, 76.4 }, cms[1].Select(v => Math.Round(v, 4)));
    }

    /// <summary>
    /// A tall page one across and two down: the uniform scale is the smaller of the two (0.48, the
    /// height's), the page is centred across its slot, and the first page fills the top row.
    /// </summary>
    [Fact]
    public void Nup_TheSmallerScaleWins_CentredAcross_TopRowFirst()
    {
        byte[] sheet = Nup(Pages(("[0 0 100 400]", "q Q"), ("[0 0 100 400]", "q Q")), 1, 2);
        var cms = PageOps(sheet).Where(o => o.Operator == "cm").Select(Numbers).ToList();
        Assert.Equal(new[] { 0.48, 0, 0, 0.48, 26, 204 }, cms[0].Select(v => Math.Round(v, 4)));
        Assert.Equal(new[] { 0.48, 0, 0, 0.48, 26, 4 }, cms[1].Select(v => Math.Round(v, 4)));
    }

    /// <summary>
    /// Three pages two up make two sheets, the last half full; no border is drawn unless asked; and
    /// each placed page keeps its resources.
    /// </summary>
    [Fact]
    public void Nup_ALeftoverPageGetsASheet_NoBorderByDefault_ResourcesKept()
    {
        byte[] sheet = Nup(Pages(("[0 0 100 100]", "BT /F1 12 Tf (a) Tj ET"), ("[0 0 100 100]", "q Q"), ("[0 0 100 100]", "q Q")), 2, 1);
        using var doc = PdfDocument.Open(new MemoryStream(sheet));
        Assert.Equal(2, doc.Pages.Count);
        Assert.DoesNotContain(PageOps(sheet), o => o.Operator == "S");

        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
        var form = (PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Resources]));
    }

    /// <summary>
    /// The objects a placed page's resources name are copied into the sheets' file with it: every
    /// form's <c>/F1</c> is the Helvetica font, one object for the three pages that share it, and
    /// the image the first page draws is there with its samples. The forms carried the resource
    /// dictionary alone, its references unchanged, into a file numbered from 1 — where
    /// <c>/F1 3 0 R</c> named the first sheet's first form, and the image was not written at all.
    /// </summary>
    [Fact]
    public void Nup_TheObjectsAPlacedPageNames_AreCopiedWithIt()
    {
        byte[] source = Objects(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 5 0 R" +
            " /Resources << /Font << /F1 3 0 R >> /XObject << /Im1 10 0 R >> >> >>",
            "<< /Length 30 >>\nstream\nBT /F1 12 Tf (a) Tj ET /Im1 Do\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 7 0 R /Resources << /Font << /F1 3 0 R >> >> >>",
            "<< /Length 22 >>\nstream\nBT /F1 12 Tf (b) Tj ET\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 9 0 R /Resources << /Font << /F1 3 0 R >> >> >>",
            "<< /Length 22 >>\nstream\nBT /F1 12 Tf (c) Tj ET\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 2 >>\nstream\n\u0011\u00EE\nendstream",
        });

        byte[] sheet = Nup(source, 2, 1);
        using var doc = PdfDocument.Open(new MemoryStream(sheet));
        Assert.Equal(2, doc.Pages.Count);
        Assert.Equal(2, (doc.Trailer[PdfName.ID] as PdfArray)?.Count);

        var forms = new List<PdfStream>();
        foreach (var page in doc.Pages)
        {
            var xobjects = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.XObject]);
            forms.AddRange(xobjects.Select(x => (PdfStream)doc.ResolveObject(x.Value)));
        }
        Assert.Equal(3, forms.Count);

        var fontRefs = forms.Select(f =>
        {
            var fonts = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(f[PdfName.Resources]))[PdfName.Font]);
            return (PdfReference)fonts[new PdfName("F1")];
        }).ToList();
        foreach (var fontRef in fontRefs)
        {
            var font = (PdfDictionary)doc.ResolveObject(fontRef);
            Assert.Equal(PdfName.Font, font[PdfName.Type]);
            Assert.Equal("Helvetica", ((PdfName)font[PdfName.BaseFont]).Value);
        }
        Assert.Single(fontRefs.Select(r => r.ReferenceObjectNumber).Distinct());

        var images = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(forms[0][PdfName.Resources]))[PdfName.XObject]);
        var image = (PdfStream)doc.ResolveObject(images[new PdfName("Im1")]);
        Assert.Equal(PdfName.Image, image[PdfName.Subtype]);
        Assert.Equal(new byte[] { 0x11, 0xEE }, image.GetDecodedData());
    }

    // ---------------------------------------------------------------- the portfolio

    /// <summary>Encrypted input is refused: the revision would be appended in clear text.</summary>
    [Fact]
    public void Portfolio_AnEncryptedDocument_IsRefused()
        => Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfPortfolioManager.ConvertToPortfolio(new MemoryStream(Encrypted()), new MemoryStream()));

    /// <summary>A view mode the spec does not name becomes the details view; a named one is kept.</summary>
    [Theory]
    [InlineData("T", "T")]
    [InlineData("X", "D")]
    public void Portfolio_TheViewMode(string asked, string written)
    {
        using var output = new MemoryStream();
        PdfPortfolioManager.ConvertToPortfolio(new MemoryStream(Pages(("[0 0 100 100]", "q Q"))), output, asked);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var collection = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Collection]);
        Assert.Equal(written, ((PdfName)collection[new PdfName("View")]).Value);
    }

    /// <summary>
    /// A portfolio whose schema has two fields and whose one item carries a creation date, a size in
    /// its parameters (written as their own object), a description and two field values.
    /// </summary>
    private static byte[] Portfolio(string sizeEntry = "/Size 1234")
        => Objects(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Collection 3 0 R /Names << /EmbeddedFiles << /Names [(report.txt) 4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Collection /Schema << /Type /CollectionSchema"
                + " /Title << /Subtype /S /N (Document title) /O 1 /V false >>"
                + " /Bytes << /Subtype /Size /N (Size) /O 2 >> >> >>",
            "<< /Type /Filespec /F (report.txt) /Desc (The report) /EF << /F 5 0 R >> /CI << /Title (Q3) /Price 1.5 >> >>",
            "<< /Type /EmbeddedFile /Params 6 0 R /Length 5 >>\nstream\nhello\nendstream",
            $"<< {sizeEntry} /CreationDate (D:20260102030405Z) >>",
        });

    /// <summary>
    /// The schema read as ISO 32000-2 Table 155 writes it: the type is /Subtype, the name shown is /N,
    /// the order /O, the visibility /V — and the /Type key is not a field.
    /// </summary>
    [Fact]
    public void Portfolio_TheSchemaIsReadByTable155()
    {
        var fields = PdfPortfolioManager.GetSchema(new MemoryStream(Portfolio())).OrderBy(f => f.Order).ToList();
        Assert.Equal(2, fields.Count);
        Assert.Equal(("Title", "S", "Document title", 1, false), (fields[0].Name, fields[0].FieldType, fields[0].Description, fields[0].Order, fields[0].Visible));
        Assert.Equal(("Bytes", "Size", "Size", 2, true), (fields[1].Name, fields[1].FieldType, fields[1].Description, fields[1].Order, fields[1].Visible));
    }

    /// <summary>
    /// An item's description, size and creation date come from its file specification and its
    /// parameters, resolved; and a number field is written invariantly, whatever the culture.
    /// </summary>
    [Fact]
    public void Portfolio_AnItemsDetails()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var item = Assert.Single(PdfPortfolioManager.ListItems(new MemoryStream(Portfolio())));
            Assert.Equal(("report.txt", "The report", 1234L), (item.FileName, item.Description, item.Size));
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), item.CreationDate);
            Assert.Equal("Q3", item.Fields["Title"]);
            Assert.Equal("1.5", item.Fields["Price"]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>A size that is not an integer is not a size: the stream's own length is measured instead, as with none.</summary>
    [Theory]
    [InlineData("/Size 12.5")]
    [InlineData("")]
    public void Portfolio_ASizeThatIsNotAnInteger_IsMeasured(string sizeEntry)
        => Assert.Equal(5, Assert.Single(PdfPortfolioManager.ListItems(new MemoryStream(Portfolio(sizeEntry)))).Size);

    /// <summary>A /Collection that is not a dictionary does not make a portfolio.</summary>
    [Fact]
    public void Portfolio_ACollectionThatIsNotADictionary_IsNotOne()
    {
        byte[] pdf = Objects(new[] { "<< /Type /Catalog /Pages 2 0 R /Collection 5 >>", "<< /Type /Pages /Kids [] /Count 0 >>" });
        Assert.False(PdfPortfolioManager.IsPortfolio(new MemoryStream(pdf)));
        Assert.True(PdfPortfolioManager.IsPortfolio(new MemoryStream(Portfolio())));
    }

    // ---------------------------------------------------------------- the incremental-save validator

    /// <summary>A one-page document with a signature field whose /ByteRange is the one given.</summary>
    private static byte[] Signed(string byteRange)
        => Objects(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [0 0 1 1] /V 5 0 R >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {byteRange} /Contents <00> >>",
        });

    /// <summary>A range that does not start at 0, one whose first part runs into its second, and one past the file's end are each named.</summary>
    [Theory]
    [InlineData("[5 10 20 30]", "start1=5")]
    [InlineData("[0 50 20 30]", "overlaps")]
    [InlineData("[0 10 20 999999]", "exceeds file length")]
    public void Validator_ABadByteRange_IsNamed(string byteRange, string issue)
    {
        var report = Assert.Single(PdfIncrementalSaveValidator.Inspect(new MemoryStream(Signed(byteRange))).SignatureReports);
        Assert.Contains(issue, report.Issue);
    }

    /// <summary>
    /// Two cross-reference sections of which the second lies before the first revision's end — a
    /// rewrite, not an append — are not a preserved signature; and when the first revision has no
    /// validated end at all, preservation is not claimed either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validator_ASectionBeforeTheFirstRevisionsEnd_IsNotPreserved(bool withEof)
    {
        string head = "%PDF-1.7\n";
        string o1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        string o2 = "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";
        int at1 = head.Length, at2 = at1 + o1.Length;
        int xref1 = at2 + o2.Length;
        string section1 = $"xref\n0 3\n0000000000 65535 f \n{at1:D10} 00000 n \n{at2:D10} 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R >>\n";
        int xref2 = xref1 + section1.Length;
        string section2 = $"xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 3 /Root 1 0 R /Prev {xref1} >>\nstartxref\n{xref2}\n" + (withEof ? "%%EOF\n" : "");
        byte[] pdf = Encoding.ASCII.GetBytes(head + o1 + o2 + section1 + section2);

        var info = PdfIncrementalSaveValidator.Inspect(new MemoryStream(pdf));
        Assert.Equal(2, info.RevisionCount);
        Assert.False(info.SignaturesPreserved);
    }

    // ---------------------------------------------------------------- the comparer

    /// <summary>A page of lines, one Tj each, top down.</summary>
    private static string Lines(params string[] lines)
        => string.Concat(lines.Select((l, i) => $"BT /F1 10 Tf 20 {700 - 14 * i} Td ({l}) Tj ET\n"));

    private static PdfDiffResult Compare(byte[] left, byte[] right)
        => PdfComparer.CompareText(new MemoryStream(left), new MemoryStream(right));

    private static byte[] Doc(params string[] pageContents) => Pages(pageContents.Select(c => ("[0 0 612 792]", c)).ToArray());

    /// <summary>A replaced line reads as the old one deleted, then the new one inserted, between its unchanged neighbours.</summary>
    [Fact]
    public void Comparer_AReplacedLine_IsDeletedThenInserted()
    {
        var diffs = Compare(Doc(Lines("Alpha", "Bravo", "Charlie")), Doc(Lines("Alpha", "Xray", "Charlie"))).TextDiffs;
        Assert.Equal(
            new[] { (PdfDiffType.Unchanged, "Alpha"), (PdfDiffType.Deleted, "Bravo"), (PdfDiffType.Inserted, "Xray"), (PdfDiffType.Unchanged, "Charlie") },
            diffs.Select(d => (d.Type, d.Text)));
    }

    /// <summary>Two documents with the same lines on different numbers of pages are not equal.</summary>
    [Fact]
    public void Comparer_ADifferentPageCount_IsNotEqual()
    {
        var result = Compare(Doc(Lines("Alpha")), Doc(Lines("Alpha"), "q Q"));
        Assert.All(result.TextDiffs, d => Assert.Equal(PdfDiffType.Unchanged, d.Type));
        Assert.False(result.AreTextEqual);
    }

    /// <summary>A whitespace-only line is no line, and space around a line's text is not a difference.</summary>
    [Fact]
    public void Comparer_BlankLinesAndSurroundingSpace_AreNotDifferences()
        => Assert.True(Compare(Doc(Lines("Alpha", "   ", "Bravo")), Doc(Lines("Alpha", "Bravo  "))).AreTextEqual);

    /// <summary>Either side encrypted is refused — the left as surely as the right.</summary>
    [Fact]
    public void Comparer_AnEncryptedLeftSide_IsRefused()
    {
        var ex = Assert.Throws<PdfException>(() => Compare(Encrypted(), Doc(Lines("Alpha"))));
        Assert.Contains("left", ex.Message);
    }

    /// <summary>
    /// Past the table's cap (5 000 lines a side) the comparison is line by line: a page shifted by one
    /// line differs everywhere, where the full diff would find all but one line unchanged; and lines
    /// that do match position for position are still unchanged.
    /// </summary>
    [Fact]
    public void Comparer_PastTheCap_ComparesLineByLine()
    {
        string[] lines = Enumerable.Range(0, 5000).Select(i => "L" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        byte[] Page(IEnumerable<string> ls) => Pages(("[0 0 612 80000]", string.Concat(ls.Select((l, i) => $"BT /F1 10 Tf 20 {79990 - 14 * i} Td ({l}) Tj ET\n"))));

        var shifted = Compare(Page(lines), Page(new[] { "X" }.Concat(lines.Take(4999))));
        Assert.DoesNotContain(shifted.TextDiffs, d => d.Type == PdfDiffType.Unchanged);

        var tail = Compare(Page(lines), Page(lines.Take(4999).Concat(new[] { "Z" })));
        Assert.Equal(4999, tail.TextDiffs.Count(d => d.Type == PdfDiffType.Unchanged));
    }
}
