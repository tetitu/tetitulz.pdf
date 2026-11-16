// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The five page boxes (§14.11.2) written as indirect references — <c>/MediaBox 4 0 R</c> — and the
/// defaulting chain that runs when one is absent.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <see cref="IndirectPageAttributeTests"/> cites the five boxes
/// as evidence that producers really do write page attributes indirectly — and tested none of them.
/// A mutation sweep removed each box's indirect branch in turn and <b>nothing reddened</b>, not in
/// Core's suite and not in Base's, where both renderers read these through
/// <c>PdfPageRenderer.ResolveRenderBox</c>.</para>
///
/// <para><b>What an unresolved box costs.</b> <see cref="PdfDictionary.GetArray"/> tests
/// <c>v is PdfArray</c> and never follows a reference, so the entry reads as <i>absent</i> — which
/// is a legal state with a defined meaning. An indirect <c>/MediaBox</c> therefore silently becomes
/// the inherited one or nothing at all, and an indirect <c>/CropBox</c> silently becomes the
/// MediaBox: the page is rendered, measured and printed at the wrong size, and no error is
/// available to report because nothing went wrong as far as the reader can tell. Sharing one box
/// object across every page of a batch is exactly what a producer does to save space.</para>
///
/// <para><b>Each box gets its own width</b> so a test cannot pass by reading a neighbour — the
/// defaulting chain below means four of the five legitimately return another box's value when
/// absent, and a fixture that gave them equal sizes could not tell that from a resolution failure.</para>
/// </remarks>
public class IndirectPageBoxTests
{
    private static double Width(PdfArray box) => ((PdfNumber)box[2]).DoubleValue;

    // ── all five resolved ─────────────────────────────────────────────────────────────

    [Fact]
    public void MediaBox_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));

        Assert.Equal(400d, Width(doc.Pages[0].MediaBox), 6);
    }

    [Fact]
    public void CropBox_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));

        Assert.Equal(401d, Width(doc.Pages[0].CropBox), 6);
    }

    [Fact]
    public void BleedBox_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));

        Assert.Equal(402d, Width(doc.Pages[0].BleedBox), 6);
    }

    [Fact]
    public void TrimBox_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));

        Assert.Equal(403d, Width(doc.Pages[0].TrimBox), 6);
    }

    [Fact]
    public void ArtBox_WrittenAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));

        Assert.Equal(404d, Width(doc.Pages[0].ArtBox), 6);
    }

    // ── MediaBox and CropBox are reached by a different route ────────────────────────

    /// <summary>
    /// <b><c>/MediaBox</c> and <c>/CropBox</c> are resolved twice over, and only this route reaches
    /// <see cref="PdfPage"/>'s own branch.</b>
    /// </summary>
    /// <remarks>
    /// <para>§7.7.3.4 makes <c>/Resources</c>, <c>/MediaBox</c>, <c>/CropBox</c> and <c>/Rotate</c>
    /// inheritable, so <c>PdfPageTree</c> reads each node's own copy — resolving the indirect case
    /// there — and hands the result to the page as its <i>inherited</i> value. A page obtained from
    /// <c>doc.Pages</c> therefore never reaches <c>PdfPage.MediaBox</c>'s indirect branch: the
    /// walk has already produced the array, and the branch returns the same answer the fallback
    /// would. Mutating it out is invisible from <c>doc.Pages</c> for exactly that reason, and the
    /// three boxes above behave differently because Bleed, Trim and Art are <b>not</b> inheritable,
    /// so their branch is the only resolution there is.</para>
    ///
    /// <para>The branch is not dead, though: <see cref="PdfPage"/>'s public constructor takes the
    /// inherited values separately, so a caller building a page from a dictionary it already holds
    /// — which is what that constructor is for — passes none, and this is what resolves the box.
    /// These two tests drive that constructor, which is the only way to reach the decision.</para>
    /// </remarks>
    [Fact]
    public void MediaBox_OnAPageBuiltFromItsDictionary_ResolvesItsOwnIndirectReference()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));
        var dictionary = (PdfDictionary)doc.GetObject(3);

        var page = new PdfPage(dictionary, doc, inheritedResources: null, pageIndex: 0);

        Assert.Equal(400d, Width(page.MediaBox), 6);
    }

    [Fact]
    public void CropBox_OnAPageBuiltFromItsDictionary_ResolvesItsOwnIndirectReference()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));
        var dictionary = (PdfDictionary)doc.GetObject(3);

        var page = new PdfPage(dictionary, doc, inheritedResources: null, pageIndex: 0);

        Assert.Equal(401d, Width(page.CropBox), 6);
    }

    /// <summary>
    /// And the page-tree route agrees with it — the two resolutions must not be two answers.
    /// </summary>
    [Fact]
    public void BothRoutesAgreeOnTheResolvedBoxes()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(AllBoxesIndirect)));
        var dictionary = (PdfDictionary)doc.GetObject(3);

        var direct = new PdfPage(dictionary, doc, inheritedResources: null, pageIndex: 0);
        PdfPage fromTree = doc.Pages[0];

        Assert.Equal(Width(fromTree.MediaBox), Width(direct.MediaBox), 6);
        Assert.Equal(Width(fromTree.CropBox), Width(direct.CropBox), 6);
    }

    // ── the defaulting chain still runs over a resolved box ──────────────────────────

    /// <summary>
    /// §14.11.2: an absent CropBox defaults to the MediaBox, and Bleed/Trim/Art default to the
    /// CropBox. With only an indirect MediaBox present, all five must report <b>its</b> width —
    /// which is also what proves the resolution happened, since the alternative is four boxes
    /// defaulting off a MediaBox that read as absent.
    /// </summary>
    [Fact]
    public void WithOnlyAnIndirectMediaBox_EveryOtherBoxDefaultsToIt()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage("/MediaBox 4 0 R")));
        PdfPage page = doc.Pages[0];

        Assert.Equal(400d, Width(page.MediaBox), 6);
        Assert.Equal(400d, Width(page.CropBox), 6);
        Assert.Equal(400d, Width(page.BleedBox), 6);
        Assert.Equal(400d, Width(page.TrimBox), 6);
        Assert.Equal(400d, Width(page.ArtBox), 6);
    }

    /// <summary>
    /// An indirect CropBox is what Bleed, Trim and Art default <i>to</i>, so failing to resolve it
    /// would silently substitute the MediaBox for three more boxes.
    /// </summary>
    [Fact]
    public void WithAnIndirectCropBox_BleedTrimAndArtDefaultToIt()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            BuildPage("/MediaBox 4 0 R /CropBox 5 0 R")));
        PdfPage page = doc.Pages[0];

        Assert.Equal(401d, Width(page.CropBox), 6);
        Assert.Equal(401d, Width(page.BleedBox), 6);
        Assert.Equal(401d, Width(page.TrimBox), 6);
        Assert.Equal(401d, Width(page.ArtBox), 6);
    }

    // ── the controls ────────────────────────────────────────────────────────────────

    /// <summary>The direct spelling keeps working — the indirect path is a fallback, not a swap.</summary>
    [Fact]
    public void BoxesWrittenDirectly_AreStillRead()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPage(
            "/MediaBox [0 0 300 300] /CropBox [0 0 301 301] /BleedBox [0 0 302 302] " +
            "/TrimBox [0 0 303 303] /ArtBox [0 0 304 304]")));
        PdfPage page = doc.Pages[0];

        Assert.Equal(300d, Width(page.MediaBox), 6);
        Assert.Equal(301d, Width(page.CropBox), 6);
        Assert.Equal(302d, Width(page.BleedBox), 6);
        Assert.Equal(303d, Width(page.TrimBox), 6);
        Assert.Equal(304d, Width(page.ArtBox), 6);
    }

    /// <summary>
    /// The honest negative: a reference pointing at an object that is not an array reads as absent,
    /// so the box falls back rather than throwing. Without this, returning the first array in the
    /// file would satisfy every test above.
    /// </summary>
    [Fact]
    public void AReferenceToSomethingThatIsNotAnArray_FallsBack()
    {
        // Object 9 is a dictionary, not an array.
        using var doc = PdfDocument.Open(new MemoryStream(
            BuildPage("/MediaBox 4 0 R /CropBox 9 0 R")));

        Assert.Equal(400d, Width(doc.Pages[0].CropBox), 6);
    }

    // ---------------------------------------------------------------- fixture

    private const string AllBoxesIndirect =
        "/MediaBox 4 0 R /CropBox 5 0 R /BleedBox 6 0 R /TrimBox 7 0 R /ArtBox 8 0 R";

    /// <summary>
    /// A one-page file whose page dictionary carries <paramref name="pageExtras"/>. Objects 4–8
    /// hold the five boxes, each one point wider than the last, and object 9 is a dictionary — the
    /// shape a producer makes when every page shares one box object.
    /// </summary>
    private static byte[] BuildPage(string pageExtras)
    {
        const int Last = 9;
        var offsets = new long[Last + 1];
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(int n, string body)
        {
            offsets[n] = file.Position;
            Write(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + body + "\nendobj\n");
        }

        Write("%PDF-1.6\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R " + pageExtras + " >>");
        Obj(4, "[0 0 400 400]");
        Obj(5, "[0 0 401 401]");
        Obj(6, "[0 0 402 402]");
        Obj(7, "[0 0 403 403]");
        Obj(8, "[0 0 404 404]");
        Obj(9, "<< /NotABox true >>");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 ").Append(Last + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= Last; i++)
            xs.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size ").Append(Last + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
