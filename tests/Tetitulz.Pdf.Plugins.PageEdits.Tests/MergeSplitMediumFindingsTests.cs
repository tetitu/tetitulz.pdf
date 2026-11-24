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
/// The MEDIUM assembly-scan findings for merge, split and watermark. Assertions are on the parsed
/// output document — the value a key resolves to, the operand a content stream carries — never on
/// "the call returned bytes".
/// </summary>
public class MergeSplitMediumFindingsTests
{
    // ------------------------------------------------------------------
    //  PdfMerger — the outline /Next chain is a graph the file chooses
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_CyclicOutlineNextChain_EmitsEachItemOnceAndNoBackwardPrev()
    {
        // A /Next ring: A → B → A. A counter does not bound it — the cycle is one hop deep — so the
        // walk appended the same two items ~500 000 times, and since later writes win in the
        // re-chain loop the FIRST item came out with a /Prev pointing FORWARD. §12.3.3 says the
        // first item has no /Prev at all, so the output was corrupt in a new way.
        using var merged = Merge(CyclicOutlinePdf(), OnePageWithOutline("B1"));

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);

        var first = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        Assert.NotNull(first);
        Assert.Null(first[PdfName.Prev]);

        // Three distinct items: the cyclic source's two, plus the second document's one.
        Assert.Equal(3, OutlineChainLength(merged, outlines));
    }

    // ------------------------------------------------------------------
    //  PdfMerger — §12.7.2 /DR and /NeedAppearances belong to every source
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_TwoFormsWithDifferentDefaultResources_KeepsBothFontsInTheMergedDr()
    {
        // Source B's fields carry /DA (/Arial 10 Tf 0 g). Cloning only source A's form kept A's /DR,
        // which defines no /Arial, so any appearance regeneration drew B's fields in a fallback.
        using var merged = Merge(FormPdf("Helv", "(A)", needAppearances: false),
                                 FormPdf("Arial", "(B)", needAppearances: false));

        var form = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var dr = merged.ResolveObject(form[PdfName.DR]) as PdfDictionary;
        var fonts = merged.ResolveObject(dr[PdfName.Font]) as PdfDictionary;

        // The exact key set, not two NotNulls: "both survived" and "the merge kept everything it
        // found" are different claims, and only the second rules out a /DR that silently gained a
        // third entry from a source whose fields were dropped.
        var names = fonts.Keys.Select(k => k.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Arial", "Helv" }, names);
        Assert.All(names, n =>
            Assert.NotNull(merged.ResolveObject(fonts[new PdfName(n)]) as PdfDictionary));
    }

    [Fact]
    public void Merge_LaterSourceNeedsAppearances_KeepsTheFlagOn()
    {
        // ORed, not taken from the first source: a form that needed regeneration would otherwise
        // stop asking for it and render its fields blank.
        using var merged = Merge(FormPdf("Helv", "(A)", needAppearances: false),
                                 FormPdf("Arial", "(B)", needAppearances: true));

        var form = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var flag = merged.ResolveObject(form[PdfName.NeedAppearances]) as PdfBoolean;
        Assert.NotNull(flag);
        Assert.True(flag.Value);
    }

    [Fact]
    public void Merge_NoSourceNeedsAppearances_DoesNotInventTheFlag()
    {
        using var merged = Merge(FormPdf("Helv", "(A)", needAppearances: false),
                                 FormPdf("Arial", "(B)", needAppearances: false));

        var form = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.Null(merged.ResolveObject(form[PdfName.NeedAppearances]) as PdfBoolean);
    }

    [Fact]
    public void Merge_CollidingDefaultResourceName_KeepsTheFirstAndReportsIt()
    {
        // Two sources both defining /DR /Font /Helv, differently. A /DR key is named from inside
        // every /DA string that uses it, so it cannot be suffixed the way a field name can — the
        // honest half is saying which name now means something else.
        using var merged = Merge(FormPdf("Helv", "(A)", needAppearances: false),
                                 FormPdf("Helv", "(B)", needAppearances: false));

        Assert.Contains("/Font /Helv", PdfMerger.LastFormWarning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  PdfSplitter
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_DirectAcroFormDictionary_IsCarriedIntoTheExtract()
    {
        // §12.7.2 requires indirection for the FIELD dictionaries, not for the form, so a catalog
        // holding /AcroForm inline is legal — and used to make the whole carry-over vanish.
        using var extracted = Split(DirectAcroFormPdf(), 1, 1);

        var form = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(form);
        var fields = extracted.ResolveObject(form[PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        var field = extracted.ResolveObject(fields[0]) as PdfDictionary;
        Assert.Equal("Name", Encoding.ASCII.GetString(((PdfString)field[PdfName.T]).RawBytes));
    }

    /// <summary>
    /// The extract carries a §14.4 trailer <c>/ID</c>.
    /// </summary>
    /// <remarks>
    /// <para><c>PdfSplitter</c> was the only full-rewrite path in the tree that omitted one — its
    /// sibling <c>PdfMerger</c> and nine other writers have always written one — so every extract
    /// was a document PDF/A rejects and that a reader cannot tell apart from another extract of the
    /// same source. §14.4 makes <c>/ID</c> required with <c>/Encrypt</c> and strongly recommended
    /// otherwise.</para>
    ///
    /// <para>The identifier is <b>fresh</b>, not carried over from the source: an extract is a
    /// different document, and telling one file from another is the key's entire purpose. That is
    /// why the second assertion compares two extracts of the same source rather than checking the
    /// array's shape — a splitter that copied the source's <c>/ID</c>, or emitted a constant, would
    /// satisfy every structural check and defeat the point.</para>
    /// </remarks>
    [Fact]
    public void SplitRange_WritesAFreshFileIdentifier()
    {
        byte[] source = DirectAcroFormPdf();

        using var first = Split(source, 1, 1);

        var id = first.Trailer[PdfName.ID] as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id.Count);
        Assert.Equal(16, ((PdfString)id[0]).RawBytes.Length);

        using var second = Split(source, 1, 1);
        var secondId = second.Trailer[PdfName.ID] as PdfArray;

        Assert.NotEqual(((PdfString)id[0]).RawBytes, ((PdfString)secondId[0]).RawBytes);
    }

    [Fact]
    public void SplitRange_CarriesOpticalContentPropertiesSoAHiddenLayerStaysHidden()
    {
        // With no /OCProperties a reader has no configuration that can turn a group off, so a layer
        // the author set /OFF becomes visible in the extract — the opposite of what extracting
        // fewer pages is for.
        using var extracted = Split(OptionalContentPdf(), 1, 1);

        var ocp = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.OCProperties]) as PdfDictionary;
        Assert.NotNull(ocp);

        var config = extracted.ResolveObject(ocp[PdfName.D]) as PdfDictionary;
        var off = extracted.ResolveObject(config[new PdfName("OFF")]) as PdfArray;
        Assert.NotNull(off);
        Assert.Single(off);

        // The group must be a real dictionary, not a null left by an uncollected reference.
        var group = extracted.ResolveObject(off[0]) as PdfDictionary;
        Assert.NotNull(group);
        Assert.Equal("Draft", Encoding.ASCII.GetString(((PdfString)group[PdfName.Name]).RawBytes));
    }

    [Fact]
    public void SplitRange_CatalogEntriesThatCannotTravel_AreNamedRatherThanDropped()
    {
        SplitToBytes(OptionalContentPdf(), 1, 1);

        Assert.NotNull(PdfSplitter.LastCatalogWarning);
        Assert.Contains("/PageLabels", PdfSplitter.LastCatalogWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_PlainDocument_ReportsNoCatalogWarning()
    {
        // The control: without it, a warning raised unconditionally would pass the test above.
        SplitToBytes(TwoPlainPages(), 1, 1);

        Assert.Null(PdfSplitter.LastCatalogWarning);
    }

    [Fact]
    public void SplitRange_EncryptedInput_ThrowsInsteadOfBlamingTheCallersRange()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfSplitter.SplitRange(input, output, 1, 1));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Split_EncryptedInput_WritesNoFilesAndThrows()
    {
        string inputFile = Path.Combine(Path.GetTempPath(), "enc_" + Path.GetRandomFileName() + ".pdf");
        string outputDir = Path.Combine(Path.GetTempPath(), "split_" + Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(inputFile, EncryptedPdf());

            // The failure this replaces: the directory was created, nothing was written, and the
            // call returned — a batch job recorded success and the pages were simply gone.
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfSplitter.Split(inputFile, outputDir));
            Assert.Empty(Directory.GetFiles(outputDir, "*.pdf"));
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    // ------------------------------------------------------------------
    //  PdfWatermarker
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("סודי")]          // Hebrew — the case that produced "????"
    [InlineData("CONFIDENTIAL—DRAFT")] // an em dash, which WinAnsi maps from a different byte
    public void AddWatermark_TextOutsideLatin1_IsRefusedRatherThanStampedAsQuestionMarks(string text)
    {
        using var input = new MemoryStream(TwoPlainPages());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(
            () => PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = text }));
        Assert.Contains("U+", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AddWatermark_Latin1Text_IsAccepted()
    {
        // The control: the refusal must reject only what the encoder cannot represent. 'É' is
        // U+00C9, inside Latin-1, and must still stamp.
        using var input = new MemoryStream(TwoPlainPages());
        using var output = new MemoryStream();

        PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIÉL" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Contains("CONFIDENTI",
            Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
    }

    [Fact]
    public void AddWatermark_PageWhoseBoxDoesNotStartAtOrigin_StampsInsideThatBox()
    {
        // /MediaBox [0 842 595 1684] — an A4 imposed on the upper half of a sheet. Placing at
        // (Width/2, Height/2) put the text matrix at y = 421, entirely below the page, so nothing
        // rendered and the call still reported success.
        using var input = new MemoryStream(OffsetMediaBoxPdf());
        using var output = new MemoryStream();

        PdfWatermarker.AddWatermark(input, output, new WatermarkOptions { Text = "CONFIDENTIAL" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        (double x, double y) = TextMatrixTranslation(doc.Pages[0].GetContentStreamData());

        Assert.InRange(x, 0, 595);
        Assert.InRange(y, 842, 1684);
        Assert.Equal(297.5, x, 1);
        Assert.Equal(1263, y, 1);
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

    private static int OutlineChainLength(PdfDocument doc, PdfDictionary outlines)
    {
        var seen = new HashSet<int>();
        var cur = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        int count = 0;
        while (cur != null && count < 50)
        {
            if (cur.ObjectNumber > 0 && !seen.Add(cur.ObjectNumber))
                throw new InvalidOperationException("the emitted /Next chain revisits an item");
            count++;
            cur = doc.ResolveObject(cur[PdfName.Next]) as PdfDictionary;
        }
        return count;
    }

    /// <summary>The (e, f) operands of the first <c>Tm</c> in a content stream.</summary>
    private static (double X, double Y) TextMatrixTranslation(byte[] content)
    {
        string text = Encoding.Latin1.GetString(content);
        int at = text.IndexOf(" Tm", StringComparison.Ordinal);
        Assert.True(at > 0, "the appended content carries no Tm operator");

        string[] parts = text.Substring(0, at).Trim()
            .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return (double.Parse(parts[^2], CultureInfo.InvariantCulture),
                double.Parse(parts[^1], CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static byte[] TwoPlainPages() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
    });

    private static byte[] OffsetMediaBoxPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 842 595 1684] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
    });

    private static byte[] OnePageWithOutline(string title) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
        $"<< /Title ({title}) /Parent 5 0 R /Dest [3 0 R /Fit] >>",
    });

    // Outline items A and B whose /Next entries point at each other.
    private static byte[] CyclicOutlinePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Outlines /First 6 0 R /Last 7 0 R /Count 2 >>",
        "<< /Title (A) /Parent 5 0 R /Next 7 0 R /Dest [3 0 R /Fit] >>",
        "<< /Title (B) /Parent 5 0 R /Next 6 0 R /Dest [3 0 R /Fit] >>",
    });

    private static byte[] FormPdf(string drFontName, string value, bool needAppearances) =>
        Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << >> /Annots [6 0 R] >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            $"<< /Fields [6 0 R] /DA (/{drFontName} 10 Tf 0 g) /DR << /Font << /{drFontName} 7 0 R >> >>" +
            (needAppearances ? " /NeedAppearances true" : "") + " >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Field" + drFontName + ") /V " + value +
            $" /DA (/{drFontName} 10 Tf 0 g) /Rect [72 700 200 720] /P 3 0 R >>",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /{drFontName} >>",
        });

    private static byte[] DirectAcroFormPdf() => Assemble(new List<string>
    {
        // /AcroForm held DIRECTLY on the catalog — legal, and read.
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [5 0 R] >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 700 200 720] /P 3 0 R >>",
    });

    // Two pages, an optional-content group turned OFF by the default configuration, and a
    // /PageLabels tree — which is page-index-keyed and therefore cannot travel.
    private static byte[] OptionalContentPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >> " +
        "/PageLabels << /Nums [0 << /S /D >>] >> /Lang (en-GB) >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /OCG /Name (Draft) >>",
    });

    private static byte[] EncryptedPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /U <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /P -1 >>",
    }, trailerExtras: "/Encrypt 5 0 R ");

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
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
