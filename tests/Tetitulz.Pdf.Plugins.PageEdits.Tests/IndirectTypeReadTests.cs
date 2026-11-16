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
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The two structural predicates in <c>PdfObjectHelper</c> must survive an indirect <c>/Type</c> —
/// and they fail in <b>opposite</b> directions, which is why both are pinned here.
/// </summary>
/// <remarks>
/// <para><b><c>IsPagesTreeNode</c>'s resolve is fixed but DEFENSIVE, and the mutation says so.</b>
/// Both the splitter and the merger skip catalog and <c>/Pages</c> objects when collecting what to
/// copy, because the output builds its own tree — but with this fixture the guard never decides
/// anything: nothing references the source <c>/Pages</c> node except each page's <c>/Parent</c>,
/// and the collector skips <c>/Parent</c> by design, so the node is never a candidate. Reverting
/// that resolve leaves both cases green. It is left in for consistency with its sibling rather than
/// claimed as covered; a fixture that reached it would need something outside the page tree
/// referencing the tree itself.</para>
///
/// <para><b><c>IsCatalogObject</c> is defensive in the splitter for the same reason, and live in the
/// merger.</b> The splitter collects only what the extracted pages reach, and nothing a page reaches
/// references the catalog, so reverting the splitter's resolve leaves everything green. The merger
/// walks <i>every</i> object of every input, so there the predicate decides whether a second catalog
/// is written — <c>AMergeWritesOneCatalog_WhicheverWayTheSourceCatalogTypeIsSpelled</c> pins that.</para>
///
/// <para><b><c>IsPageObject</c> reading false means the splitter over-collects.</b> It exists so the
/// splitter refuses to follow a reference into a page it is not extracting — the references that
/// reach a page from outside the page tree (an annotation's <c>/Dest</c>, a widget's <c>/P</c>, a
/// named destination) look like any other reference. Read as "not a page", an unextracted page's
/// objects are pulled into the output.</para>
///
/// <para>Asserted on the extracted document's page count and on the objects it contains, not on the
/// split succeeding — a split that carried the whole source tree still produces a valid PDF.</para>
/// </remarks>
public class IndirectTypeReadTests
{
    /// <summary>
    /// Extracting page 1 of a two-page document yields exactly one page, whichever way the page
    /// tree's <c>/Type</c> entries are spelled. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Type /Pages and /Type /Page written inline
    [InlineData(true)]   // both written as indirect references
    public void ExtractingOnePageYieldsOnePage_WhicheverWayTypeIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(TwoPagePdf(indirect));
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        Assert.Single(doc.Pages);

        // …and it is the page that was asked for. The two pages differ by /MediaBox width, so this
        // separates "extracted the right one" from "extracted one of them".
        var box = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.MediaBox]) as PdfArray;
        Assert.Equal(200, (int)((doc.ResolveObject(box![2]) as PdfNumber)!.DoubleValue));

        // IsPageObject: page 1 carries a link whose /Dest names page 2. The predicate is what stops
        // the collector following that reference into a page it is not extracting, so the
        // unextracted page must not have been dragged in. Its /MediaBox is 400 wide and page 1's is
        // 200, so a surviving copy is unambiguous.
        Assert.DoesNotContain(400, EveryMediaBoxWidth(doc));

        // The output builds exactly one page tree. This does NOT pin IsPagesTreeNode — reverting
        // that resolve leaves it green, for the reason given in the class remarks — but it is worth
        // asserting on its own terms: two /Pages nodes in a split output would be a real defect
        // whatever caused it.
        Assert.Equal(1, CountObjects(doc, PdfName.Pages));
    }

    /// <summary>
    /// A copied page's <c>/Parent</c> is rewritten to the output's page tree, whichever way its
    /// <c>/Type</c> is spelled — through a split and through a merge.
    /// </summary>
    /// <remarks>
    /// <c>DeepClone</c> recognised a page by reading <c>/Type</c> raw, one method below the
    /// <c>IsPageObject</c> predicate that resolves. A page whose <c>/Type</c> was indirect was
    /// cloned as an ordinary dictionary, so its <c>/Parent</c> — a reference to the source tree
    /// node, which is never copied — became <c>null</c>. Opening the output still finds the page,
    /// because the tree is walked down from the catalog, which is why no page-count assertion saw
    /// it; §7.7.3.3 makes <c>/Parent</c> required.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ACopiedPageKeepsItsParent_WhicheverWayTypeIsSpelled(bool indirect, bool merge)
    {
        using var output = new MemoryStream();
        if (merge)
            PdfMerger.Merge(output, new MemoryStream(TwoPagePdf(indirect)), new MemoryStream(TwoPagePdf(indirect)));
        else
            PdfSplitter.SplitRange(new MemoryStream(TwoPagePdf(indirect)), output, 1, 1);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        Assert.NotEmpty(doc.Pages);
        foreach (var page in doc.Pages)
        {
            var parent = doc.ResolveObject(page.Dictionary[PdfName.Parent]) as PdfDictionary;
            Assert.True(parent != null, "a copied page has no /Parent");
            Assert.Equal(PdfName.Pages, doc.ResolveObject(parent![PdfName.Type]) as PdfName);
        }
    }

    /// <summary>
    /// A merge writes one catalog, whichever way the source catalogs' <c>/Type</c> is spelled.
    /// </summary>
    /// <remarks>
    /// The merger walks every object of every input and skips the catalogs, because it writes its
    /// own. <c>IsCatalogObject</c> read <c>/Type</c> raw, so a source catalog whose <c>/Type</c>
    /// was indirect was copied into the output as an ordinary object — a second, unreachable
    /// <c>/Type /Catalog</c> whose <c>/Pages</c> pointed at nothing.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMergeWritesOneCatalog_WhicheverWayTheSourceCatalogTypeIsSpelled(bool indirect)
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(TwoPagePdf(false, indirectCatalogType: indirect)),
                                new MemoryStream(TwoPagePdf(false, indirectCatalogType: indirect)));

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        Assert.Equal(4, doc.Pages.Count);
        Assert.Equal(1, CountObjects(doc, PdfName.Catalog));
    }

    /// <summary>Every <c>/MediaBox</c> width reachable as an indirect object in the document.</summary>
    private static List<int> EveryMediaBoxWidth(PdfDocument doc)
    {
        var widths = new List<int>();
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
        {
            if (!(kvp.Value is PdfDictionary d)) continue;
            if (!(doc.ResolveObject(d[PdfName.MediaBox]) is PdfArray mb) || mb.Count < 4) continue;
            if (doc.ResolveObject(mb[2]) is PdfNumber n) widths.Add((int)n.DoubleValue);
        }
        return widths;
    }

    /// <summary>How many indirect objects declare the given <c>/Type</c>.</summary>
    private static int CountObjects(PdfDocument doc, PdfName type)
    {
        int count = 0;
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
            if (kvp.Value is PdfDictionary d &&
                (doc.ResolveObject(d[PdfName.Type]) as PdfName) == type)
                count++;
        return count;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Two pages of different widths. Objects 10 and 11 hold the <c>/Pages</c> and <c>/Page</c>
    /// type names when <paramref name="indirect"/> — the only difference between the fixtures.
    /// </summary>
    private static byte[] TwoPagePdf(bool indirect, bool indirectCatalogType = false)
    {
        string pagesType = indirect ? "10 0 R" : "/Pages";
        string pageType = indirect ? "11 0 R" : "/Page";
        string catalogType = indirectCatalogType ? "12 0 R" : "/Catalog";

        return Build(
            "<< /Type " + catalogType + " /Pages 2 0 R >>",
            "<< /Type " + pagesType + " /Kids [3 0 R 5 0 R] /Count 2 >>",
            // Page 1 links to page 2. That reference is what IsPageObject exists to refuse to
            // follow, and without it the collector has no route into the unextracted page at all.
            "<< /Type " + pageType + " /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Annots [7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type " + pageType + " /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 6 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [5 0 R /Fit] >>",
            "null", "null",
            "/Pages",
            "/Page",
            "/Catalog");
    }

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
