// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// FDF and XFDF carry annotations, not only fields — reading them, writing them, and importing
/// them into a document.
/// </summary>
/// <remarks>
/// <para><b>The vocabulary came from another implementation, not from recollection.</b> Apache
/// an independent implementation was asked to export the same six annotation kinds to FDF, and this reader was
/// pointed at that file; then an independent implementation was asked to read back the XFDF this writes. That two-way check
/// settled the facts a specification reading would have had to guess — <c>/Page</c> is
/// <b>zero-based</b>, a <c>Line</c> carries <c>/L</c> as well as its <c>/Rect</c>, and
/// <c>/InkList</c> is an array of arrays.</para>
///
/// <para><b>It also found two bugs that no self-round-trip could.</b> XFDF writes a polygon's
/// vertices as a <i>child element</i> and not an attribute — an independent implementation rejects the whole annotation
/// with "missing element 'vertices'" otherwise — and the reader was taking the lower-case element
/// name as the subtype, so a <c>&lt;highlight&gt;</c> became <c>/Subtype /highlight</c>: a name no
/// reader matches, producing an annotation that is present, well-formed and invisible. A round trip
/// through this library alone was green for both.</para>
///
/// <para><b>What is checked in is the conclusion.</b> The external comparison needs an independent implementation; these
/// tests pin the shapes it confirmed, and the round trip below is bounded by the same
/// caveat — it is the breadth check, not the correctness one.</para>
/// </remarks>
public class AnnotationExchangeTests
{
    // ------------------------------------------------------------------ reading

    /// <summary>
    /// An FDF carrying only annotations reads back as annotations, not as an empty file.
    /// </summary>
    /// <remarks>
    /// An FDF exported from a comment review has no fields at all; read as fields alone, it would
    /// come back as an empty <c>FormData</c> with nothing to distinguish it from a file that really
    /// was empty.
    /// </remarks>
    [Fact]
    public void AnFdfCarryingOnlyAnnotationsReadsThemRatherThanNothing()
    {
        using var input = new MemoryStream(SixAnnotationFdf());

        FormData data = PdfFormDataExchange.ReadFdf(input);

        Assert.Empty(data.Fields);
        Assert.Equal(6, data.Annotations.Count);
    }

    /// <summary>
    /// Each subtype's own geometry survives the read — the keys that differ between them.
    /// </summary>
    /// <remarks>
    /// Asserted per subtype rather than as a count, because "six annotations" is satisfied by six
    /// empty ones. Each row names the field that only that subtype has, so a mapping that dropped
    /// <c>/InkList</c> while keeping <c>/Rect</c> fails here rather than passing as "six".
    /// </remarks>
    [Fact]
    public void EachSubtypeKeepsTheGeometryOnlyItCarries()
    {
        using var input = new MemoryStream(SixAnnotationFdf());
        FormData data = PdfFormDataExchange.ReadFdf(input);

        FormDataAnnotation highlight = Only(data, PdfAnnotationSubtypes.Highlight);
        Assert.Equal(8, highlight.QuadPoints.Length);
        Assert.Equal(new double[] { 1, 1, 0 }, highlight.Color);
        Assert.Equal("Jane Smith", highlight.Title);
        Assert.Equal(0.5, highlight.Opacity);

        FormDataAnnotation line = Only(data, PdfAnnotationSubtypes.Line);
        Assert.Equal(new double[] { 10, 300, 110, 350 }, line.Line);

        FormDataAnnotation ink = Only(data, PdfAnnotationSubtypes.Ink);
        Assert.Equal(2, ink.InkList.Count);
        Assert.Equal(6, ink.InkList[0].Length);
        Assert.Equal(4, ink.InkList[1].Length);

        FormDataAnnotation polygon = Only(data, PdfAnnotationSubtypes.Polygon);
        Assert.Equal(6, polygon.Vertices.Length);

        FormDataAnnotation square = Only(data, PdfAnnotationSubtypes.Square);
        Assert.Equal(new double[] { 0, 0, 1 }, square.InteriorColor);

        // The page index is the format's, and it is zero-based.
        Assert.Equal(1, square.PageIndex);
        Assert.Equal(0, highlight.PageIndex);
    }

    // ------------------------------------------------------------------ XFDF vocabulary

    /// <summary>
    /// <b>A polygon's vertices are written as a child element</b>, which is not symmetric with
    /// <c>coords</c>.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the format's: text-markup quad points ride on a <c>coords</c> attribute, a
    /// polygon's vertices do not. Written as an attribute, an independent implementation refuses the entire annotation.
    /// Asserted through <c>XDocument</c> rather than as a substring, so "the text appears
    /// somewhere" cannot pass for "it is in the right place".
    /// </remarks>
    [Fact]
    public void XfdfWritesPolygonVerticesAsAChildElementAndQuadPointsAsAnAttribute()
    {
        XDocument xfdf = WriteXfdf(SixAnnotationFdf());
        XNamespace ns = "http://ns.adobe.com/xfdf/";

        XElement polygon = xfdf.Descendants(ns + "polygon").Single();
        Assert.Null(polygon.Attribute("vertices"));
        Assert.Equal("10,500,110,500,60,600", polygon.Element(ns + "vertices")!.Value);

        XElement highlight = xfdf.Descendants(ns + "highlight").Single();
        Assert.Equal("10,32,100,32,10,20,100,20", highlight.Attribute("coords")!.Value);
    }

    /// <summary>The element name is the subtype in lower case, and there is no subtype attribute.</summary>
    [Fact]
    public void XfdfNamesEachAnnotationElementForItsSubtypeInLowerCase()
    {
        XDocument xfdf = WriteXfdf(SixAnnotationFdf());
        XNamespace ns = "http://ns.adobe.com/xfdf/";

        var names = xfdf.Descendants(ns + "annots").Single()
                        .Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(new[] { "highlight", "text", "square", "line", "ink", "polygon" }, names);
        Assert.All(xfdf.Descendants(ns + "annots").Single().Elements(),
                   e => Assert.Null(e.Attribute("subtype")));
    }

    /// <summary>Ink strokes are one <c>gesture</c> element each.</summary>
    [Fact]
    public void XfdfWritesOneGestureElementPerInkStroke()
    {
        XDocument xfdf = WriteXfdf(SixAnnotationFdf());
        XNamespace ns = "http://ns.adobe.com/xfdf/";

        var gestures = xfdf.Descendants(ns + "gesture").Select(g => g.Value).ToList();

        Assert.Equal(new[] { "200,400,220,430,250,410", "260,420,280,460" }, gestures);
    }

    /// <summary>Colour is <c>#RRGGBB</c>, which is what XFDF can express.</summary>
    /// <remarks>
    /// The quantisation is the format's limit and is why FDF remains the lossless option: a CMYK
    /// <c>/C</c> has nowhere to put its fourth component, and even an RGB one is rounded to eight
    /// bits per channel here.
    /// </remarks>
    [Fact]
    public void XfdfWritesColourAsSixHexDigits()
    {
        XDocument xfdf = WriteXfdf(SixAnnotationFdf());
        XNamespace ns = "http://ns.adobe.com/xfdf/";

        Assert.Equal("#FFFF00", xfdf.Descendants(ns + "highlight").Single().Attribute("color")!.Value);

        XElement square = xfdf.Descendants(ns + "square").Single();
        Assert.Equal("#FF0000", square.Attribute("color")!.Value);
        Assert.Equal("#0000FF", square.Attribute("interior-color")!.Value);
    }

    // ------------------------------------------------------------------ round trip

    /// <summary>
    /// <b>The subtype comes back capitalised as §12.5.6 spells it</b>, not as the element name.
    /// </summary>
    /// <remarks>
    /// <para>The bug this pins wrote <c>/Subtype /highlight</c> into the PDF — well-formed, present,
    /// and matched by no reader, so the annotation simply does not appear. It survived a round trip
    /// through this library because both halves agreed on the lower-case spelling.</para>
    /// <para><c>FreeText</c>, <c>PolyLine</c>, <c>StrikeOut</c> and <c>FileAttachment</c> are in the
    /// theory because their interior capital is what no case rule recovers: a
    /// <c>ToTitleCase</c> would produce <c>Freetext</c>.</para>
    /// </remarks>
    [Theory]
    [InlineData("highlight", "Highlight")]
    [InlineData("freetext", "FreeText")]
    [InlineData("polyline", "PolyLine")]
    [InlineData("strikeout", "StrikeOut")]
    [InlineData("fileattachment", "FileAttachment")]
    [InlineData("squiggly", "Squiggly")]
    public void ReadingXfdfRestoresTheSpecsCapitalisationOfTheSubtype(string element, string subtype)
    {
        string xfdf =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields /><annots>" +
            $"<{element} page=\"0\" rect=\"1,2,3,4\" />" +
            "</annots></xfdf>";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        FormData data = PdfFormDataExchange.ReadXfdf(input);

        Assert.Equal(subtype, Assert.Single(data.Annotations).Subtype);
    }

    /// <summary>
    /// A number written as <c>NaN</c> or <c>Infinity</c> is not read as one: the rectangle, the
    /// opacity and the border width are left unset, as for any other text that is not a number.
    /// <c>NumberStyles.Float</c> reads both symbols, and the value reached the annotation's
    /// dictionary, which the writer puts as 0 — a degenerate <c>/Rect</c>, a <c>/CA</c> of 0. The
    /// finite spelling of the same annotation is the control.
    /// </summary>
    [Theory]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("-Infinity", null)]
    [InlineData("0.5", 0.5)]
    public void AdversarialNonFiniteNumbers_AreNotReadAsNumbers(string number, double? read)
    {
        string xfdf =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields /><annots>" +
            $"<square page=\"0\" rect=\"{number},2,3,4\" opacity=\"{number}\" width=\"{number}\" />" +
            "</annots></xfdf>";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        var annotation = Assert.Single(PdfFormDataExchange.ReadXfdf(input).Annotations);

        Assert.Equal(read, annotation.Opacity);
        Assert.Equal(read, annotation.BorderWidth);
        if (read == null) Assert.Null(annotation.Rect);
        else Assert.Equal(new[] { 0.5, 2, 3, 4 }, annotation.Rect);
    }

    /// <summary>
    /// A subtype no version of the specification names survives unchanged rather than being dropped.
    /// </summary>
    /// <remarks>
    /// The alternative to a lookup table is guessing a capitalisation, and the alternative to
    /// guessing is discarding — both worse than carrying it through as it arrived.
    /// </remarks>
    [Fact]
    public void AnUnknownSubtypeIsCarriedThroughUnchanged()
    {
        string xfdf =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields /><annots>" +
            "<somethingnew page=\"0\" rect=\"1,2,3,4\" />" +
            "</annots></xfdf>";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        FormData data = PdfFormDataExchange.ReadXfdf(input);

        Assert.Equal("somethingnew", Assert.Single(data.Annotations).Subtype);
    }

    /// <summary>Every annotation survives FDF → XFDF → FDF with its geometry intact.</summary>
    /// <remarks>
    /// <para>The breadth check. It is a relative assertion and cannot see a rule this library reads
    /// the same wrong way on both sides — which is exactly how the subtype-case bug survived until
    /// the output was compared against an independent implementation's. The theories above pin the two facts that
    /// comparison established.</para>
    /// <para><b>Colour is excluded from the comparison on purpose</b>: XFDF quantises to eight bits
    /// per channel, so a round trip through it legitimately changes a colour and asserting equality
    /// would be asserting the format is lossless.</para>
    /// </remarks>
    [Fact]
    public void AnnotationsSurviveTheFdfToXfdfToFdfRoundTrip()
    {
        using var first = new MemoryStream(SixAnnotationFdf());
        FormData original = PdfFormDataExchange.ReadFdf(first);

        using var xfdf = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(original, xfdf);
        xfdf.Position = 0;
        FormData viaXfdf = PdfFormDataExchange.ReadXfdf(xfdf);

        using var fdf = new MemoryStream();
        PdfFormDataExchange.WriteFdf(viaXfdf, fdf);
        fdf.Position = 0;
        FormData back = PdfFormDataExchange.ReadFdf(fdf);

        Assert.Equal(original.Annotations.Count, back.Annotations.Count);

        for (int i = 0; i < original.Annotations.Count; i++)
        {
            FormDataAnnotation a = original.Annotations[i];
            FormDataAnnotation b = back.Annotations[i];

            Assert.Equal(a.Subtype, b.Subtype);
            Assert.Equal(a.PageIndex, b.PageIndex);
            Assert.Equal(a.Rect, b.Rect);
            Assert.Equal(a.QuadPoints, b.QuadPoints);
            Assert.Equal(a.Vertices, b.Vertices);
            Assert.Equal(a.Line, b.Line);
            Assert.Equal(a.Contents, b.Contents);
            Assert.Equal(a.Title, b.Title);
            Assert.Equal(a.InkList.Count, b.InkList.Count);
            for (int s = 0; s < a.InkList.Count; s++) Assert.Equal(a.InkList[s], b.InkList[s]);
        }
    }

    // ------------------------------------------------------------------ importing

    /// <summary>
    /// Importing writes every annotation onto the page the file names, in <b>one</b> revision.
    /// </summary>
    /// <remarks>
    /// <para>The revision count is the point of the batched writer, and it is asserted rather than
    /// described: a review of two hundred comments through the per-annotation writers is two hundred
    /// appended revisions, a file several times its original size, and two hundred chances for a
    /// reader to disagree about which is current.</para>
    /// <para><c>PdfRevisions</c> finds boundaries physically, so it counts what a reader would.</para>
    /// </remarks>
    [Fact]
    public void ImportingWritesEveryAnnotationInOneAppendedRevision()
    {
        byte[] annotated = Import(TwoPagePdf(), SixAnnotationFdf(), out int written);

        Assert.Equal(6, written);

        using var output = new MemoryStream(annotated);
        using var doc = PdfDocument.Open(output);

        Assert.Equal(4, AnnotationsOn(doc, 0));   // highlight, text, line, ink
        Assert.Equal(2, AnnotationsOn(doc, 1));   // square, polygon

        // One revision appended: the original file plus exactly one more %%EOF boundary.
        Assert.Equal(2, Occurrences(annotated, "%%EOF"));
    }

    /// <summary>
    /// The subtypes land as §12.5.6 names them, which is what a reader matches on.
    /// </summary>
    [Fact]
    public void TheImportedAnnotationsCarryTheirSpecSubtypes()
    {
        byte[] annotated = Import(TwoPagePdf(), SixAnnotationFdf(), out _);

        using var output = new MemoryStream(annotated);
        using var doc = PdfDocument.Open(output);

        var subtypes = new List<string>();
        for (int page = 0; page < doc.Pages.Count; page++)
            foreach (PdfDictionary annot in AnnotationDictionaries(doc, page))
                subtypes.Add((doc.ResolveObject(annot[PdfName.Subtype]) as PdfName)?.Value);

        Assert.Contains(PdfAnnotationSubtypes.Highlight, subtypes);
        Assert.Contains(PdfAnnotationSubtypes.Polygon, subtypes);
        Assert.DoesNotContain("highlight", subtypes);
    }

    /// <summary>
    /// An annotation naming a page the document does not have is skipped and named, not thrown on.
    /// </summary>
    /// <remarks>
    /// A review file routinely outlives the pages it was written against, and losing five good
    /// comments to one stale page number is the wrong trade. The count returned says how many
    /// landed; only the diagnostic says which did not.
    /// </remarks>
    [Fact]
    public void AnAnnotationOnAMissingPageIsSkippedAndReported()
    {
        var data = new FormData();
        data.Annotations.Add(new FormDataAnnotation(PdfAnnotationSubtypes.Square)
        {
            PageIndex = 0,
            Rect = new double[] { 1, 2, 3, 4 },
        });
        data.Annotations.Add(new FormDataAnnotation(PdfAnnotationSubtypes.Square)
        {
            PageIndex = 97,
            Rect = new double[] { 1, 2, 3, 4 },
        });

        using var input = new MemoryStream(TwoPagePdf());
        using var output = new MemoryStream();
        int written = PdfFormDataExchange.ImportAnnotations(input, data, output);

        Assert.Equal(1, written);
        Assert.NotNull(PdfAnnotationWriter.LastAnnotationsSkipped);
        Assert.Contains("97", PdfAnnotationWriter.LastAnnotationsSkipped!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Importing nothing appends nothing — and clears the diagnostic a previous call set.
    /// </summary>
    /// <remarks>
    /// <para>A revision that changes nothing still moves every byte offset a signature covers, so
    /// "no annotations" must not mean "one empty revision".</para>
    /// <para>The second half is the slot reset: a diagnostic read after the operation must be
    /// cleared at the start of it, or a caller sees the previous document's verdict.</para>
    /// </remarks>
    [Fact]
    public void ImportingAnEmptySetRewritesTheFileUnchangedAndClearsTheDiagnostic()
    {
        var withMissing = new FormData();
        withMissing.Annotations.Add(new FormDataAnnotation(PdfAnnotationSubtypes.Square)
        {
            PageIndex = 97,
            Rect = new double[] { 1, 2, 3, 4 },
        });

        using (var input = new MemoryStream(TwoPagePdf()))
        using (var output = new MemoryStream())
            PdfFormDataExchange.ImportAnnotations(input, withMissing, output);

        Assert.NotNull(PdfAnnotationWriter.LastAnnotationsSkipped);

        byte[] source = TwoPagePdf();
        using var plainIn = new MemoryStream(source);
        using var plainOut = new MemoryStream();
        int written = PdfFormDataExchange.ImportAnnotations(plainIn, new FormData(), plainOut);

        Assert.Equal(0, written);
        Assert.Equal(source, plainOut.ToArray());
        Assert.Null(PdfAnnotationWriter.LastAnnotationsSkipped);
    }

    /// <summary>Importing twice adds to the first import rather than replacing it.</summary>
    [Fact]
    public void ASecondImportAddsToTheAnnotationsTheFirstWrote()
    {
        byte[] once = Import(TwoPagePdf(), SixAnnotationFdf(), out _);
        byte[] twice = Import(once, SixAnnotationFdf(), out _);

        using var output = new MemoryStream(twice);
        using var doc = PdfDocument.Open(output);

        Assert.Equal(8, AnnotationsOn(doc, 0));
        Assert.Equal(4, AnnotationsOn(doc, 1));
    }

    // ==================================================================

    private static FormDataAnnotation Only(FormData data, string subtype)
        => Assert.Single(data.Annotations.Where(a => a.Subtype == subtype));

    private static XDocument WriteXfdf(byte[] fdf)
    {
        using var input = new MemoryStream(fdf);
        FormData data = PdfFormDataExchange.ReadFdf(input);

        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(data, output);
        output.Position = 0;

        return XDocument.Load(output);
    }

    private static byte[] Import(byte[] pdf, byte[] fdf, out int written)
    {
        using var dataStream = new MemoryStream(fdf);
        FormData data = PdfFormDataExchange.ReadFdf(dataStream);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        written = PdfFormDataExchange.ImportAnnotations(input, data, output);
        return output.ToArray();
    }

    private static IEnumerable<PdfDictionary> AnnotationDictionaries(PdfDocument doc, int page)
    {
        if (!(doc.ResolveObject(doc.Pages[page].Dictionary[PdfName.Annots]) is PdfArray annots))
            yield break;

        for (int i = 0; i < annots.Count; i++)
            if (doc.ResolveObject(annots[i]) is PdfDictionary d)
                yield return d;
    }

    private static int AnnotationsOn(PdfDocument doc, int page)
        => AnnotationDictionaries(doc, page).Count();

    private static int Occurrences(byte[] data, string needle)
    {
        string text = Encoding.GetEncoding("iso-8859-1").GetString(data);
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// An FDF carrying six annotations and no fields — the shape a comment review exports.
    /// </summary>
    /// <remarks>
    /// <b>Byte-for-byte what an independent implementation wrote</b> for six annotations of different kinds,
    /// which is what makes the reader's key mapping a checked fact rather than a reading of the
    /// specification. The <c>/Page</c> values are zero-based because an independent implementation wrote them that way.
    /// </remarks>
    private static byte[] SixAnnotationFdf()
    {
        var objects = new List<string>
        {
            "<<\n/FDF 2 0 R\n>>",
            "<<\n/Annots [3 0 R 4 0 R 5 0 R 6 0 R 7 0 R 8 0 R]\n>>",

            "<<\n/Type /Annot\n/Subtype /Highlight\n/Page 0\n/Rect [10.0 20.0 100.0 32.0]\n" +
            "/C [1.0 1.0 0.0]\n/Contents (a highlight)\n/T (Jane Smith)\n/NM (annot-1)\n/CA 0.5\n" +
            "/CreationDate (D:20260906225736+03'00')\n" +
            "/QuadPoints [10.0 32.0 100.0 32.0 10.0 20.0 100.0 20.0]\n>>",

            "<<\n/Type /Annot\n/Subtype /Text\n/Page 0\n/Rect [120.0 200.0 140.0 220.0]\n" +
            "/Contents (a sticky note)\n/T (Bob)\n/Subj (review)\n/NM (annot-2)\n>>",

            "<<\n/Type /Annot\n/Subtype /Square\n/Page 1\n/Rect [30.0 30.0 90.0 70.0]\n" +
            "/C [1.0 0.0 0.0]\n/IC [0.0 0.0 1.0]\n/Contents (a box)\n>>",

            "<<\n/Type /Annot\n/Subtype /Line\n/Page 0\n/Rect [10.0 300.0 110.0 350.0]\n" +
            "/L [10.0 300.0 110.0 350.0]\n/C [0.0 0.5 0.0]\n>>",

            "<<\n/Type /Annot\n/Subtype /Ink\n/Page 0\n/Rect [200.0 400.0 280.0 460.0]\n" +
            "/InkList [[200.0 400.0 220.0 430.0 250.0 410.0]\n [260.0 420.0 280.0 460.0]\n]\n>>",

            "<<\n/Type /Annot\n/Subtype /Polygon\n/Page 1\n/Rect [10.0 500.0 110.0 600.0]\n" +
            "/Vertices [10.0 500.0 110.0 500.0 60.0 600.0]\n>>",
        };

        var sb = new StringBuilder("%FDF-1.2\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Count + 1;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<<\n/Root 1 0 R\n/Size {size}\n>>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Two blank pages, so a zero-based page index of 1 has somewhere to land.</summary>
    private static byte[] TwoPagePdf()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Count + 1;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
