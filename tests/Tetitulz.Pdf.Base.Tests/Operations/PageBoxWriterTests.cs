// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.11.2 page boundary boxes — <c>PdfPageManipulator.SetPageBox</c> / <c>RemovePageBox</c>.
/// </summary>
/// <remarks>
/// <para><b>Every assertion reads the page DICTIONARY, never <c>PdfPage.TrimBox</c>.</b> Those
/// properties fall back to <c>/CropBox</c> and thence to <c>/MediaBox</c>, so they are never null —
/// asking one "does this page have a trim box?" answers yes for every page in every document, and a
/// test built on them passes with the writer deleted.</para>
///
/// <para><b>Pixels are useless here for the same reason the annotation-<c>/AP</c> work found.</b>
/// <c>PdfPageRenderer</c> intersects a sub-box with the <c>/MediaBox</c> on read and falls back to
/// the whole <c>/MediaBox</c> when they are disjoint — so a page renders identically whether the
/// trim box is present, absent, or nonsense. That is also precisely why the writer refuses an
/// out-of-bounds rectangle instead of storing it: the file would draw as though the call had never
/// happened.</para>
/// </remarks>
public class PageBoxWriterTests
{
    [Theory]
    [InlineData(PdfPageBoxKind.CropBox,  "CropBox")]
    [InlineData(PdfPageBoxKind.BleedBox, "BleedBox")]
    [InlineData(PdfPageBoxKind.TrimBox,  "TrimBox")]
    [InlineData(PdfPageBoxKind.ArtBox,   "ArtBox")]
    public void SetPageBox_WritesTheRectangleUnderTheRightKey(PdfPageBoxKind box, string key)
    {
        byte[] result = Set(Letter(), 0, box, 36, 36, 540, 720);

        Assert.Equal(new double[] { 36, 36, 576, 756 }, Rect(result, 0, key));
    }

    /// <summary>
    /// The floor: writing one box must not invent the others. Without this, an implementation that
    /// wrote all five would pass every test above.
    /// </summary>
    [Fact]
    public void SetPageBox_WritesOnlyTheBoxItWasAsked()
    {
        byte[] result = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 36, 36, 540, 720);

        Assert.NotNull(Rect(result, 0, "TrimBox"));
        Assert.Null(Rect(result, 0, "CropBox"));
        Assert.Null(Rect(result, 0, "BleedBox"));
        Assert.Null(Rect(result, 0, "ArtBox"));
    }

    /// <summary>
    /// §7.9.5 lets either corner pair come first, so a negative extent is the rectangle the caller
    /// meant — not a containment failure and not a stored (max, min) pair a reader must re-normalise.
    /// </summary>
    [Fact]
    public void SetPageBox_NormalisesANegativeExtent()
    {
        byte[] result = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 576, 756, -540, -720);

        Assert.Equal(new double[] { 36, 36, 576, 756 }, Rect(result, 0, "TrimBox"));
    }

    /// <summary>
    /// <b>The constraint that rules out the naive writer.</b> A renderer intersects a sub-box with
    /// the <c>/MediaBox</c> and falls back to the whole page when they are disjoint, so storing an
    /// out-of-bounds rectangle yields a file that draws as though nothing was written. Refusing is
    /// the only outcome a caller can act on — and the output stream must be untouched, not
    /// half-written.
    /// </summary>
    [Fact]
    public void SetPageBox_RefusesASubBoxOutsideTheMediaBox_AndWritesNothing()
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.TrimBox, -50, -50, 800, 900));

        Assert.Contains("14.11.2", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The acceptance control: a rectangle exactly on the MediaBox edge is inside it.</summary>
    [Fact]
    public void SetPageBox_AcceptsARectangleCoincidentWithTheMediaBox()
    {
        byte[] result = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 0, 0, 612, 792);

        Assert.Equal(new double[] { 0, 0, 612, 792 }, Rect(result, 0, "TrimBox"));
    }

    /// <summary>
    /// The containment rule runs the other way too: shrinking the <c>/MediaBox</c> below a sub-box
    /// the page already carries produces the same unrenderable state.
    /// </summary>
    [Fact]
    public void SetPageBox_RefusesAMediaBoxThatWouldNoLongerContainAnExistingSubBox()
    {
        byte[] withTrim = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 36, 36, 540, 720);

        using var input = new MemoryStream(withTrim);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.MediaBox, 0, 0, 200, 200));

        Assert.Contains("TrimBox", ex.Message);
    }

    /// <summary>
    /// …and the control that keeps the rule from being "never write a MediaBox": enlarging it is
    /// exactly what a caller adding bleed room does, and it is allowed.
    /// </summary>
    [Fact]
    public void SetPageBox_AllowsAMediaBoxThatStillContainsTheSubBoxes()
    {
        byte[] withTrim = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 36, 36, 540, 720);
        byte[] enlarged = Set(withTrim, 0, PdfPageBoxKind.MediaBox, -18, -18, 648, 828);

        Assert.Equal(new double[] { -18, -18, 630, 810 }, Rect(enlarged, 0, "MediaBox"));
        Assert.Equal(new double[] { 36, 36, 576, 756 }, Rect(enlarged, 0, "TrimBox"));
    }

    /// <summary>An empty rectangle is refused rather than written as a degenerate box.</summary>
    [Theory]
    [InlineData(0d, 100d)]
    [InlineData(100d, 0d)]
    public void SetPageBox_RefusesAnEmptyRectangle(double width, double height)
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.SetPageBox(input, output, 0, PdfPageBoxKind.TrimBox, 10, 10, width, height));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The multi-page entry point writes <b>one</b> revision, not one per page. Asserted by counting
    /// the revisions in the output — a loop over the single-page call produces the same boxes and a
    /// different file, and only the count tells them apart.
    /// </summary>
    [Fact]
    public void SetPageBoxes_WritesOneRevisionForAllPages()
    {
        byte[] source = ThreePages();

        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageBox(input, output, new[] { 0, 1, 2 },
                                      PdfPageBoxKind.TrimBox, 10, 10, 100, 100);
        byte[] result = output.ToArray();

        for (int page = 0; page < 3; page++)
            Assert.Equal(new double[] { 10, 10, 110, 110 }, Rect(result, page, "TrimBox"));

        Assert.Equal(CountEofMarkers(source) + 1, CountEofMarkers(result));
    }

    /// <summary>
    /// A page named twice is staged once, not emitted twice into the same revision.
    /// </summary>
    /// <remarks>
    /// <b>Asserting the box came out right does not test this</b>, and the first draft did exactly
    /// that: writing the same object number twice leaves the xref pointing at the second copy, so
    /// the parsed result is identical and only the emitted bytes differ. The mutation that deleted
    /// the dedup survived until this counted <c>N 0 obj</c> headers in the appended revision.
    /// </remarks>
    [Fact]
    public void SetPageBoxes_StagesADuplicatePageIndexOnlyOnce()
    {
        byte[] source = ThreePages();
        int pageObjNum = PageObjectNumber(source, 1);

        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageBox(input, output, new[] { 1, 1 },
                                      PdfPageBoxKind.TrimBox, 10, 10, 100, 100);
        byte[] result = output.ToArray();

        Assert.Equal(new double[] { 10, 10, 110, 110 }, Rect(result, 1, "TrimBox"));

        // Once in the original file, once in the appended revision — never twice in the revision.
        Assert.Equal(CountObjectHeaders(source, pageObjNum) + 1,
                     CountObjectHeaders(result, pageObjNum));
    }

    // ------------------------------------------------------------------

    /// <summary>Removal takes the entry away, so the page reverts to its default.</summary>
    [Fact]
    public void RemovePageBox_TakesTheEntryAway()
    {
        byte[] withTrim = Set(Letter(), 0, PdfPageBoxKind.TrimBox, 36, 36, 540, 720);
        Assert.NotNull(Rect(withTrim, 0, "TrimBox"));

        using var input = new MemoryStream(withTrim);
        using var output = new MemoryStream();
        PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.TrimBox);

        Assert.Null(Rect(output.ToArray(), 0, "TrimBox"));
    }

    /// <summary>
    /// §7.7.3.3 makes <c>/MediaBox</c> required and §7.7.3.4 makes it inheritable. Removing the
    /// page's own when no ancestor supplies one would leave a page with no defined size, so it is
    /// refused.
    /// </summary>
    [Fact]
    public void RemovePageBox_RefusesAMediaBoxNoAncestorSupplies()
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.MediaBox));

        Assert.Contains("MediaBox", ex.Message);
    }

    /// <summary>
    /// …and permits it when a <c>/Pages</c> ancestor does — the control, without which the rule
    /// above could just be "never remove a MediaBox".
    /// </summary>
    [Fact]
    public void RemovePageBox_AllowsAMediaBoxTheParentSupplies()
    {
        using var input = new MemoryStream(InheritedMediaBox());
        using var output = new MemoryStream();
        PdfPageManipulator.RemovePageBox(input, output, 0, PdfPageBoxKind.MediaBox);

        byte[] result = output.ToArray();
        Assert.Null(Rect(result, 0, "MediaBox"));

        // Still a laid-out page: the inherited box is what PdfPage reports.
        using var doc = PdfDocument.Open(new MemoryStream(result));
        Assert.NotNull(doc.Pages[0].MediaBox);
    }

    // ------------------------------------------------------------------

    /// <summary><c>CropPage</c> is now one line over the shared writer and must behave identically.</summary>
    [Fact]
    public void CropPage_StillWritesTheCropBox()
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();
        PdfPageManipulator.CropPage(input, output, 0, 36, 36, 540, 720);

        Assert.Equal(new double[] { 36, 36, 576, 756 }, Rect(output.ToArray(), 0, "CropBox"));
    }

    /// <summary>The facade reaches all three, so a caller need not name the engine.</summary>
    [Fact]
    public void TheFacadeReachesTheWriter()
    {
        using var input = new MemoryStream(Letter());
        using var output = new MemoryStream();
        PdfOperations.SetPageBox(input, output, 0, PdfPageBoxKind.ArtBox, 10, 20, 100, 200);

        Assert.Equal(new double[] { 10, 20, 110, 220 }, Rect(output.ToArray(), 0, "ArtBox"));
    }

    // ------------------------------------------------------------------
    //  The name/kind mapping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("TrimBox",  PdfPageBoxKind.TrimBox)]
    [InlineData("/TrimBox", PdfPageBoxKind.TrimBox)]
    [InlineData("MediaBox", PdfPageBoxKind.MediaBox)]
    public void PdfPageBoxes_TryParse_AcceptsBothSpellings(string name, PdfPageBoxKind expected)
    {
        Assert.True(PdfPageBoxes.TryParse(name, out var kind));
        Assert.Equal(expected, kind);
    }

    /// <summary>PDF names are case-sensitive (§7.3.5), so "trimbox" names no box.</summary>
    [Theory]
    [InlineData("trimbox")]
    [InlineData("Trim")]
    [InlineData("")]
    [InlineData(null)]
    public void PdfPageBoxes_TryParse_RejectsWhatIsNotABoxName(string name)
    {
        Assert.False(PdfPageBoxes.TryParse(name, out _));
    }

    /// <summary>
    /// The enum→name mapping returns the <b>cached</b> <c>PdfName</c> instances, not fresh
    /// allocations — the rule <c>NoRedundantPdfNameAllocationTests</c> enforces at the source level
    /// and which a reference comparison pins at runtime.
    /// </summary>
    [Fact]
    public void PdfPageBoxes_ToName_ReturnsTheCachedNames()
    {
        Assert.Same(PdfName.MediaBox, PdfPageBoxes.ToName(PdfPageBoxKind.MediaBox));
        Assert.Same(PdfName.CropBox,  PdfPageBoxes.ToName(PdfPageBoxKind.CropBox));
        Assert.Same(PdfName.BleedBox, PdfPageBoxes.ToName(PdfPageBoxKind.BleedBox));
        Assert.Same(PdfName.TrimBox,  PdfPageBoxes.ToName(PdfPageBoxKind.TrimBox));
        Assert.Same(PdfName.ArtBox,   PdfPageBoxes.ToName(PdfPageBoxKind.ArtBox));
    }

    // ------------------------------------------------------------------

    private static byte[] Set(byte[] pdf, int pageIndex, PdfPageBoxKind box,
                              double x, double y, double w, double h)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPageManipulator.SetPageBox(input, output, pageIndex, box, x, y, w, h);
        return output.ToArray();
    }

    /// <summary>
    /// The four numbers stored under <paramref name="key"/> on the page's own dictionary, or null
    /// when the key is absent. <b>Never <c>PdfPage.TrimBox</c></b> — see the class remarks.
    /// </summary>
    private static double[] Rect(byte[] pdf, int pageIndex, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var arr = doc.ResolveObject(doc.Pages[pageIndex].Dictionary[new PdfName(key)]) as PdfArray;
        if (arr == null) return null;

        var values = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++) values[i] = ((PdfNumber)arr[i]).DoubleValue;
        return values;
    }

    private static int PageObjectNumber(byte[] pdf, int pageIndex)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int num = doc.Pages[pageIndex].Dictionary.ObjectNumber;
        Assert.True(num > 0, "the fixture's page must be an indirect object");
        return num;
    }

    /// <summary>How many <c>N 0 obj</c> headers the file carries for one object number.</summary>
    private static int CountObjectHeaders(byte[] pdf, int objectNumber)
    {
        string text = Encoding.Latin1.GetString(pdf);
        string header = objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 obj";
        int count = 0, at = 0;
        while ((at = text.IndexOf(header, at, StringComparison.Ordinal)) >= 0) { count++; at += header.Length; }
        return count;
    }

    private static int CountEofMarkers(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int count = 0, at = 0;
        while ((at = text.IndexOf("%%EOF", at, StringComparison.Ordinal)) >= 0) { count++; at += 5; }
        return count;
    }

    private static byte[] Letter() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    /// <summary>A page whose <c>/MediaBox</c> comes from its <c>/Pages</c> parent (§7.7.3.4).</summary>
    private static byte[] InheritedMediaBox() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    private static byte[] ThreePages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R 6 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
