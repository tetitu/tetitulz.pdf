// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// An extract carries, of a resource dictionary several pages share, only the entries its kept
/// pages draw (<c>SplitResourceUse</c>): a producer that writes one <c>/Resources</c> for every page
/// otherwise hands a one-page extract the other pages' pictures and forms.
/// </summary>
/// <remarks>
/// Every leak assertion reads the whole output, each object decoded: an object written but drawn by
/// nothing still reads perfectly well with any object dump. Every refusal to carry is paired with a
/// control in which the same entry is drawn and travels.
/// </remarks>
public class SplitSharedResourcesTests
{
    private const string Box = "/BBox [0 0 300 300]";

    [Fact]
    public void SplitRange_OnePageOfASharedResourceDictionary_CarriesNoFormOrImageOnlyTheDroppedPageDraws()
    {
        byte[] extracted = Split(SharedResources(), 1, 1);

        string all = AllText(extracted);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-IMAGE", all, StringComparison.Ordinal);
        // Potency: what page 1 draws is there, through the name it draws it by.
        Assert.Contains("KEEP-FORM", all, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.IsType<PdfStream>(XObject(doc, doc.Pages[0], "X1"));
        Assert.Null(PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_BothPagesOfASharedResourceDictionary_CarryEveryFormAndImageTheyDraw()
    {
        byte[] extracted = Split(SharedResources(), 1, 2);

        string all = AllText(extracted);
        Assert.Contains("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.Contains("SECRET-IMAGE", all, StringComparison.Ordinal);
        Assert.Contains("KEEP-FORM", all, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPages_TheSecondPageAlone_CarriesOnlyWhatTheSecondPageDraws()
    {
        using var input = new MemoryStream(SharedResources());
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(input, output, new[] { 2 });

        string all = AllText(output.ToArray());
        Assert.DoesNotContain("KEEP-FORM", all, StringComparison.Ordinal);
        Assert.Contains("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.Contains("SECRET-IMAGE", all, StringComparison.Ordinal);
    }

    [Fact]
    public void EachPage_OfASharedResourceDictionary_GivesEachPageOnlyItsOwnForms()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SharedResources()));
        List<byte[]> pages = PdfSplitter.EachPage(doc);

        Assert.Equal(2, pages.Count);
        string first = AllText(pages[0]), second = AllText(pages[1]);
        Assert.Contains("KEEP-FORM", first, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", first, StringComparison.Ordinal);
        Assert.Contains("SECRET-FORM", second, StringComparison.Ordinal);
        Assert.DoesNotContain("KEEP-FORM", second, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_AFormWithNoResourcesOfItsOwn_KeepsWhatItDrawsFromThePages()
    {
        // X1 has no /Resources, so it draws through the page's (§7.8.3): its image and its font are
        // the page's entries, which nothing on the page names directly.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("/X1 Do")),
            (6, Stream("/X2 Do")),
            (10, "<< /Font << /F1 11 0 R /F2 16 0 R >> /XObject << /X1 12 0 R /X2 13 0 R /IM1 14 0 R >> >>"),
            (11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            (12, Stream("BT /F1 10 Tf (KEEP-FORM) Tj ET /IM1 Do", "/Type /XObject /Subtype /Form " + Box)),
            (13, Stream("BT /F2 10 Tf (SECRET-FORM) Tj ET", "/Type /XObject /Subtype /Form " + Box)),
            (14, Image("KEEP-IMAGE")),
            (16, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Name /SECRET-FONT >>")), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
        Assert.Contains("Helvetica", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FONT", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_APatternSpaceNamingItsUnderlyingSpace_KeepsBothEntries()
    {
        // /CS0 is [/Pattern /CS1]: a reader looks /CS1 up in the same dictionary, so drawing /CS0
        // keeps /CS1 although no operator names it. The tiling pattern has no resources of its own
        // and draws the page's /IMP.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("/CS0 cs 0.5 0.5 0.5 /P0 scn 0 0 10 10 re f")),
            (6, Stream("/CS9 cs 1 sc 0 0 10 10 re f")),
            (10, "<< /ColorSpace << /CS0 [/Pattern /CS1] /CS1 17 0 R /CS9 18 0 R >> /Pattern << /P0 19 0 R >> " +
                 "/XObject << /IMP 14 0 R >> >>"),
            (14, Image("KEEP-PATTERN-IMAGE")),
            (17, "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Name (KEEP-SPACE) >>]"),
            (18, "[/CalGray << /WhitePoint [0.9505 1 1.089] /Name (SECRET-SPACE) >>]"),
            (19, Stream("/IMP Do", "/PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5"))), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-SPACE", all, StringComparison.Ordinal);
        Assert.Contains("KEEP-PATTERN-IMAGE", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SPACE", all, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        var spaces = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ColorSpace]));
        Assert.IsType<PdfArray>(doc.ResolveObject(spaces[new PdfName("CS1")]));
        var patterns = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.Pattern]));
        Assert.IsType<PdfStream>(doc.ResolveObject(patterns[new PdfName("P0")]));
    }

    [Fact]
    public void SplitRange_ADefaultColourSpace_IsKeptThoughNoOperatorNamesIt()
    {
        // The page paints in DeviceRGB, which §8.6.5.6 draws through /DefaultRGB when the resources
        // hold one: no operator names it, and left out it would change every colour on the page.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("1 0 0 rg 0 0 10 10 re f")),
            (6, Stream("/CS9 cs 1 sc 0 0 10 10 re f")),
            (10, "<< /ColorSpace << /DefaultRGB 17 0 R /CS9 18 0 R >> >>"),
            (17, "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Name (KEEP-DEFAULT) >>]"),
            (18, "[/CalGray << /WhitePoint [0.9505 1 1.089] /Name (SECRET-SPACE) >>]")), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-DEFAULT", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SPACE", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_AnAppearanceWithNoResourcesOfItsOwn_KeepsTheFontItDrawsFromThePage()
    {
        // The note's appearance names /F2, which only the page's resources hold.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5, " /Annots [20 0 R]")),
            (4, Page(6)),
            (5, Stream("BT /F1 10 Tf (page one) Tj ET")),
            (6, Stream("/X2 Do")),
            (10, "<< /Font << /F1 11 0 R /F2 16 0 R >> /XObject << /X2 13 0 R >> >>"),
            (11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            (13, Stream("(SECRET-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
            (16, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Name /KEEP-FONT >>"),
            (20, "<< /Type /Annot /Subtype /FreeText /Rect [10 10 100 30] /DA (/F2 9 Tf) /AP << /N 21 0 R >> >>"),
            (21, Stream("BT /F2 9 Tf (note) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 90 20]"))), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-FONT", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_AType3GlyphDrawingFromThePage_KeepsWhatTheGlyphDraws()
    {
        // The Type 3 font has no /Resources, so its glyph's /IM1 is the page's.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("BT /T3 12 Tf (a) Tj ET")),
            (6, Stream("/X2 Do")),
            (10, "<< /Font << /T3 22 0 R >> /XObject << /IM1 14 0 R /X2 13 0 R >> >>"),
            (13, Stream("(SECRET-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
            (14, Image("KEEP-IMAGE")),
            (22, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [0.001 0 0 0.001 0 0] " +
                 "/CharProcs << /a 23 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>"),
            (23, Stream("1000 0 d0 /IM1 Do"))), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Table 57's <c>/Font</c> in a graphics state parameter dictionary selects a font as <c>Tf</c>
    /// does, so a Type 3 font chosen through <c>gs</c>, with no <c>/Resources</c> of its own, draws its
    /// glyph procedures from the page's (Table 112): what they draw travels, and nothing is said.
    /// </summary>
    [Fact]
    public void SplitRange_AType3FontSelectedThroughAnExtGState_KeepsWhatItsGlyphDraws()
    {
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("/GS1 gs BT (a) Tj ET")),
            (6, Stream("/X2 Do")),
            (10, "<< /ExtGState << /GS1 << /Font [22 0 R 10] >> >> /XObject << /IM1 14 0 R /X2 13 0 R >> >>"),
            (13, Stream("(SECRET-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
            (14, Image("KEEP-IMAGE")),
            (22, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [0.001 0 0 0.001 0 0] " +
                 "/CharProcs << /a 23 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>"),
            (23, Stream("1000 0 d0 q 10 0 0 10 0 0 cm /IM1 Do Q"))), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.Null(PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_AFontDictionaryTheFormsDefaultResourcesShare_IsCarriedWholeAndSaid()
    {
        // The form's /DR holds the page's /Font dictionary itself: a field's appearance is drawn
        // from it by name, which no content of the page says, so every font in it travels.
        byte[] extracted = Split(FontDictionaryTheFormShares(), 1, 1);

        string all = AllText(extracted);
        Assert.Contains("KEEP-FONT", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.Contains("names it without its drawing having been read", PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void ReleaseWhatOthersName_SearchesEachCarriedObjectOnce_OverEveryCall()
    {
        // Asked until it releases nothing, the search would cost the extract's size once a round
        // were each round to search everything again: a chain of releases made it quadratic.
        using var doc = PdfDocument.Open(new MemoryStream(FontDictionaryTheFormShares()));
        var use = SplitResourceUse.Read(doc, new[] { doc.Pages[0] });
        // What an extract of page 1 carries: page 2, its content and its form are not among them.
        var carried = new List<int> { 1, 2, 3, 5, 10, 11, 15, 16, 20 };

        List<PdfDictionary> first = use.ReleaseWhatOthersName(carried, Array.Empty<PdfObject>());
        int searched = use.ObjectsSearchedForRelease;
        // The /XObject dictionary is still left out, so a second call has something to search for.
        List<PdfDictionary> second = use.ReleaseWhatOthersName(carried, Array.Empty<PdfObject>());

        Assert.Same(doc.GetObject(15), Assert.Single(first));
        Assert.Equal(carried.Count, searched);
        Assert.Empty(second);
        Assert.Equal(searched, use.ObjectsSearchedForRelease);
    }

    /// <summary>
    /// A form whose /DR holds the /Font dictionary the pages share; page 1 draws /F1 alone, and the
    /// pages' /XObject dictionary holds a form only page 2 draws, so something stays left out after
    /// the fonts are released.
    /// </summary>
    private static byte[] FontDictionaryTheFormShares() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [20 0 R] /DR << /Font 15 0 R >> /DA (/F2 0 Tf 0 g) >> >>"),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
        (3, Page(5, " /Annots [20 0 R]")),
        (4, Page(6)),
        (5, Stream("BT /F1 10 Tf (page one) Tj ET")),
        (6, Stream("BT /F1 10 Tf (page two) Tj ET /X9 Do")),
        (10, "<< /Font 15 0 R /XObject << /X9 13 0 R >> >>"),
        (11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        (13, Stream("(SECRET-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
        (15, "<< /F1 11 0 R /F2 16 0 R >>"),
        (16, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Name /KEEP-FONT >>"),
        (20, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (x) /Rect [10 10 100 30] /P 3 0 R >>"));

    [Fact]
    public void SplitRange_ContentThatWillNotDecode_KeepsTheResourcesWholeAndSaysWhy()
    {
        // Page 1's content is a Flate stream of garbage: what it draws cannot be read, so the shared
        // dictionary travels whole, as before this read, and the extract says so.
        byte[] input = SharedResources(firstPageContent: "<< /Filter /FlateDecode /Length 12 >>\nstream\nnot deflated\nendstream");
        byte[] extracted = Split(input, 1, 1);

        Assert.Contains("SECRET-FORM", AllText(extracted), StringComparison.Ordinal);
        Assert.Contains("could not all be read", PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_EveryOtherCategoryAPageNames_IsKeptAndTheOtherPagesAreNot()
    {
        // Page 1 names a graphics state whose soft mask's group draws /IM1 from the page's
        // resources, a shading, a marked-content property list and an inline image's colour space;
        // page 2 names a second of each.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, Page(5)),
            (4, Page(6)),
            (5, Stream("/GS0 gs /Sh0 sh /OC /MC0 BDC EMC BI /W 1 /H 1 /CS /CS0 /BPC 8 ID \u0080 EI")),
            (6, Stream("/GS9 gs /Sh9 sh /OC /MC9 BDC EMC BI /W 1 /H 1 /CS /CS9 /BPC 8 ID \u0080 EI")),
            (10, "<< /ExtGState << /GS0 30 0 R /GS9 31 0 R >> /Shading << /Sh0 32 0 R /Sh9 33 0 R >> " +
                 "/Properties << /MC0 34 0 R /MC9 35 0 R >> /ColorSpace << /CS0 36 0 R /CS9 37 0 R >> " +
                 "/XObject << /IM1 14 0 R >> >>"),
            (14, Image("KEEP-MASKED")),
            (30, "<< /Type /ExtGState /SMask << /S /Luminosity /G 38 0 R >> >>"),
            (31, "<< /Type /ExtGState /CA 0.5 /SECRET-GS true >>"),
            (32, "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> /KEEP-SHADING true >>"),
            (33, "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> /SECRET-SHADING true >>"),
            (34, "<< /Type /OCG /Name (KEEP-LAYER) >>"),
            (35, "<< /Type /OCG /Name (SECRET-LAYER) >>"),
            (36, "[/CalGray << /WhitePoint [0.9505 1 1.089] /Name (KEEP-INLINE-SPACE) >>]"),
            (37, "[/CalGray << /WhitePoint [0.9505 1 1.089] /Name (SECRET-INLINE-SPACE) >>]"),
            (38, Stream("/IM1 Do", "/Type /XObject /Subtype /Form " + Box + " /Group << /S /Transparency /CS /DeviceGray >>"))), 1, 1);

        string all = AllText(extracted);
        foreach (string kept in new[] { "KEEP-MASKED", "KEEP-SHADING", "KEEP-LAYER", "KEEP-INLINE-SPACE" })
            Assert.Contains(kept, all, StringComparison.Ordinal);
        foreach (string dropped in new[] { "SECRET-GS", "SECRET-SHADING", "SECRET-LAYER", "SECRET-INLINE-SPACE" })
            Assert.DoesNotContain(dropped, all, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_ACategoryDictionaryAnUnreadablePageShares_IsCarriedWhole()
    {
        // Pages 1 and 2 have resource dictionaries of their own that hold one /XObject dictionary.
        // Page 1's content will not decode, so what it draws from that dictionary is unknown: the
        // dictionary travels whole, although page 2 — kept too, and readable — draws only /X2.
        byte[] extracted = Split(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 7 0 R] /Count 3 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /XObject 9 0 R >> /Contents 5 0 R >>"),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /XObject 9 0 R >> /Contents 6 0 R >>"),
            (5, "<< /Filter /FlateDecode /Length 12 >>\nstream\nnot deflated\nendstream"),
            (6, Stream("/X2 Do")),
            (7, Page(8)),
            (8, Stream("0 0 1 1 re f")),
            (9, "<< /X1 12 0 R /X2 13 0 R >>"),
            (12, Stream("(UNKNOWN-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
            (13, Stream("(KEEP-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
            (10, "<< >>")), 1, 2);

        string all = AllText(extracted);
        Assert.Contains("UNKNOWN-FORM", all, StringComparison.Ordinal);
        Assert.Contains("KEEP-FORM", all, StringComparison.Ordinal);
        Assert.Contains("could not all be read", PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_AfterAnExtractThatWarned_ACleanOneResetsTheWarning()
    {
        Split(SharedResources(firstPageContent: "<< /Filter /FlateDecode /Length 12 >>\nstream\nnot deflated\nendstream"), 1, 1);
        Assert.NotNull(PdfSplitter.LastResourceWarning);

        Split(SharedResources(), 1, 1);
        Assert.Null(PdfSplitter.LastResourceWarning);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// Page 1 draws /X1, which has no resources of its own and so draws /IM1 through the page's;
    /// page 2 draws /X2 (SECRET-FORM). For the budget tests: what X1 draws is known only by reading it.
    /// </summary>
    internal static byte[] FormDrawingThroughThePage(string? firstForm = null) => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
        (3, Page(5)),
        (4, Page(6)),
        (5, Stream("/X1 Do")),
        (6, Stream("/X2 Do")),
        (10, "<< /XObject << /X1 12 0 R /X2 13 0 R /IM1 14 0 R >> >>"),
        (12, firstForm ?? Stream("/IM1 Do 0 0 1 1 re f 0 0 1 1 re f 0 0 1 1 re f 0 0 1 1 re f", "/Type /XObject /Subtype /Form " + Box)),
        (13, Stream("(SECRET-FORM) Tj", "/Type /XObject /Subtype /Form " + Box)),
        (14, Image("KEEP-IMAGE")));

    /// <summary>
    /// Two pages over one resource dictionary: page 1 draws /X1 (KEEP-FORM), page 2 draws /X2
    /// (SECRET-FORM) and the image /IM2 (SECRET-IMAGE).
    /// </summary>
    internal static byte[] SharedResources(string? firstPageContent = null) => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
        (3, Page(5)),
        (4, Page(6)),
        (5, firstPageContent ?? Stream("BT /F1 12 Tf 20 250 Td (page one) Tj ET /X1 Do")),
        (6, Stream("BT /F1 12 Tf 20 250 Td (page two) Tj ET /X2 Do /IM2 Do")),
        (10, "<< /Font << /F1 11 0 R >> /XObject << /X1 12 0 R /X2 13 0 R /IM2 15 0 R >> >>"),
        (11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        (12, Stream("BT /F1 10 Tf 20 100 Td (KEEP-FORM) Tj ET", "/Type /XObject /Subtype /Form " + Box + " /Resources << /Font << /F1 11 0 R >> >>")),
        (13, Stream("BT /F1 10 Tf 20 100 Td (SECRET-FORM) Tj ET", "/Type /XObject /Subtype /Form " + Box + " /Resources << /Font << /F1 11 0 R >> >>")),
        (15, Image("SECRET-IMAGE")));

    private static string Page(int contents, string extra = "")
        => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources 10 0 R /Contents " + contents + " 0 R" + extra + " >>";

    private static string Image(string marker)
        => "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Alt (" + marker + ") /Length 1 >>\nstream\n\u0080\nendstream";

    internal static string Stream(string content, string extra = "")
        => "<< " + extra + " /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

    private static PdfObject XObject(PdfDocument doc, PdfPage page, string name)
        => doc.ResolveObject((doc.ResolveObject(page.Resources[PdfName.XObject]) as PdfDictionary)?[new PdfName(name)]);

    internal static byte[] Split(byte[] pdf, int first, int last)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, first, last);
        return output.ToArray();
    }

    /// <summary>The whole file as text: its raw bytes, then every object it lists, each stream decoded.</summary>
    internal static string AllText(byte[] pdf)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(pdf));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (var entry in doc.CrossReferenceTable.AllEntries)
            if (entry.Type != Tetitulz.Pdf.Core.Parsing.CrossReferenceEntry.EntryType.Free)
                Dump(doc.GetObject(entry.ObjectNumber), text, 0);
        return text.ToString();
    }

    private static void Dump(PdfObject obj, StringBuilder text, int depth)
    {
        if (obj == null || depth > 16) return;
        switch (obj)
        {
            case PdfStream s:
                foreach (var kvp in s) { text.Append(' ').Append(kvp.Key.Value).Append(' '); Dump(kvp.Value, text, depth + 1); }
                if (s.TryGetDecodedData(1 << 24, out byte[] data, out _) && data != null) text.Append(Encoding.Latin1.GetString(data));
                break;
            case PdfDictionary d:
                foreach (var kvp in d) { text.Append(' ').Append(kvp.Key.Value).Append(' '); Dump(kvp.Value, text, depth + 1); }
                break;
            case PdfArray a:
                foreach (PdfObject item in a) Dump(item, text, depth + 1);
                break;
            case PdfString str:
                text.Append(' ').Append(str.GetText()).Append(' ');
                break;
            case PdfName n:
                text.Append(' ').Append(n.Value).Append(' ');
                break;
        }
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
        int max = objects.Max(o => o.Number);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(max + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int n = 1; n <= max; n++)
            sb.Append(offsets.TryGetValue(n, out int at) ? at.ToString("D10") + " 00000 n \n" : "0000000000 00000 f \n");
        sb.Append("trailer\n<< /Size ").Append(max + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

/// <summary>
/// The read of what the kept pages draw has two ceilings — the streams it reads and the bytes they
/// decode to — and past either it reads no further and carries the resources it could not decide
/// on whole, saying so. Each ceiling is lowered here, so the class joins the limits collection.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class SplitSharedResourcesBudgetTests
{
    [Fact]
    public void SplitRange_WithinBothCeilings_ReadsTheFormAndLeavesOutWhatOnlyTheOtherPageDraws()
    {
        byte[] extracted = SplitSharedResourcesTests.Split(SplitSharedResourcesTests.FormDrawingThroughThePage(), 1, 1);

        string all = SplitSharedResourcesTests.AllText(extracted);
        Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FORM", all, StringComparison.Ordinal);
        Assert.Null(PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_PastTheStreamCeiling_CarriesTheResourcesWholeAndSaysSo()
    {
        PdfLimits.MaxFormXObjectInvocationsPerPage = 0;
        try
        {
            byte[] extracted = SplitSharedResourcesTests.Split(SplitSharedResourcesTests.FormDrawingThroughThePage(), 1, 1);

            string all = SplitSharedResourcesTests.AllText(extracted);
            Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
            Assert.Contains("SECRET-FORM", all, StringComparison.Ordinal);
            Assert.Contains("content streams are drawn from the kept pages", PdfSplitter.LastResourceWarning);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void SplitRange_AFormThatInflatesPastTheCeiling_IsRefusedAtItsDecodeNotAfterIt()
    {
        // A few hundred bytes of Flate that inflate to 200 000: the decode is handed what is left
        // of the ceiling and refuses there, so the bomb is never inflated whole and then measured.
        byte[] inflated = Encoding.ASCII.GetBytes("/IM1 Do " + new string(' ', 200_000));
        byte[] deflated;
        using (var packed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(inflated, 0, inflated.Length);
            deflated = packed.ToArray();
        }
        byte[] pdf = SplitSharedResourcesTests.FormDrawingThroughThePage(
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] /Filter /FlateDecode /Length " + deflated.Length +
            " >>\nstream\n" + Encoding.Latin1.GetString(deflated) + "\nendstream");

        PdfLimits.MaxDecompressedBytes = 50_000;
        try
        {
            using var input = new MemoryStream(pdf);
            using var output = new MemoryStream();
            PdfSplitter.SplitRange(input, output, 1, 1);

            Assert.Contains("a stream they draw could not be read", PdfSplitter.LastResourceWarning);
            Assert.Contains("SECRET-FORM", SplitSharedResourcesTests.AllText(output.ToArray()), StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void SplitRange_APageWhoseOwnContentPassesTheCeiling_CarriesItsResourcesWholeAndSaysSo()
    {
        // The page's content alone is past the ceiling: it is not read, so nothing is left out.
        byte[] pdf = SplitSharedResourcesTests.SharedResources(
            firstPageContent: SplitSharedResourcesTests.Stream("/X1 Do" + new string(' ', 200)));

        PdfLimits.MaxDecompressedBytes = 100;
        try
        {
            byte[] extracted = SplitSharedResourcesTests.Split(pdf, 1, 1);

            Assert.Contains("SECRET-FORM", SplitSharedResourcesTests.AllText(extracted), StringComparison.Ordinal);
            Assert.Contains("bytes of content", PdfSplitter.LastResourceWarning);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void EachPage_WhenOnePageCouldNotBeRead_SaysWhichAfterTheWholeSplit()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SplitSharedResourcesTests.SharedResources(
            firstPageContent: "<< /Filter /FlateDecode /Length 12 >>\nstream\nnot deflated\nendstream")));

        PdfSplitter.EachPage(doc);

        Assert.StartsWith("Page 1: ", PdfSplitter.LastResourceWarning);
        Assert.Contains("could not all be read", PdfSplitter.LastResourceWarning);
    }

    [Fact]
    public void SplitRange_PastTheByteCeiling_CarriesTheResourcesWholeAndSaysSo()
    {
        // The page's six bytes fit; the form's sixty do not.
        PdfLimits.MaxDecompressedBytes = 20;
        try
        {
            byte[] extracted = SplitSharedResourcesTests.Split(SplitSharedResourcesTests.FormDrawingThroughThePage(), 1, 1);

            string all = SplitSharedResourcesTests.AllText(extracted);
            Assert.Contains("KEEP-IMAGE", all, StringComparison.Ordinal);
            Assert.Contains("SECRET-FORM", all, StringComparison.Ordinal);
            Assert.Contains("could not all be read", PdfSplitter.LastResourceWarning);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
