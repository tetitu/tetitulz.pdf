// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The shape of an extracted link: which annotations count, what rectangle they occupy, and where
/// they point.
/// </summary>
/// <remarks>
/// <para><b>Two directions of harm.</b> A link dropped is a URL that exists on the page and is
/// invisible to a link checker, and an anchor lost outright in conversion. A target wrong is worse:
/// the clickable region is right and the destination is not, which is the failure that looks like a
/// working link.</para>
///
/// <para><b>This file had a previous pass for indirect reads</b> — several sites carry a "Resolved:"
/// note — and a sweep found that two of those fixes shipped without a fixture: an indirect
/// <c>/Rect</c> array and an indirect <c>/URI</c> string. A survivor on a documented fix is the
/// sharpest kind, because the prose asserts the behaviour and nothing else does.</para>
/// </remarks>
public class LinkExtractionShapeTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);
    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    /// <summary>One page carrying the given annotation objects (numbered from 5).</summary>
    private static byte[] PageWithAnnots(string annotsArray, params string[] extra)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots {annotsArray} >>"),
            Obj(4, "<< /Length 0 >>\nstream\n\nendstream"),
        };
        bodies.AddRange(extra);
        return Assemble(bodies);
    }

    private static IReadOnlyList<PdfLink> Links(byte[] pdf)
        => PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf, writable: false));

    // ==================================================================
    //  The hit rectangle
    // ==================================================================

    /// <summary>
    /// <b>A <c>/Rect</c> written with its corners in the other order still yields a positive
    /// rectangle</b> (§12.5.2), because the array states two corners and not an origin and a size.
    /// </summary>
    /// <remarks>
    /// A negative width is not a cosmetic defect: every consumer hit-tests with it, and a region of
    /// negative extent contains no point at all — so the link becomes unclickable while still being
    /// reported as present.
    /// </remarks>
    [Fact]
    public void AReversedRectIsNormalisedToAPositiveRectangle()
    {
        byte[] pdf = PageWithAnnots("[5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [100 200 50 150] " +
                   "/A << /S /URI /URI (https://example.test/) >> >>"));

        PdfLink link = Assert.Single(Links(pdf));

        Assert.Equal(50f, link.X, 3);
        Assert.Equal(150f, link.Y, 3);
        Assert.Equal(50f, link.Width, 3);
        Assert.Equal(50f, link.Height, 3);
    }

    /// <summary>
    /// <b>A <c>/Rect</c> with fewer than four numbers is not a rectangle</b>, and the annotation is
    /// dropped rather than given three coordinates and a zero.
    /// </summary>
    [Fact]
    public void AnUnderlongRectIsRefused()
    {
        byte[] pdf = PageWithAnnots("[5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 30] " +
                   "/A << /S /URI /URI (https://example.test/) >> >>"));

        Assert.Empty(Links(pdf));
    }

    /// <summary>
    /// <b>An indirect <c>/Rect</c> array is followed.</b> This is one of the reads the file's
    /// "Resolved:" notes were added for, and nothing asserted it.
    /// </summary>
    /// <remarks>
    /// A raw read yields null here, which is indistinguishable from "no rectangle" — so the
    /// annotation is dropped, and the URL and its clickable region are lost outright to link
    /// checkers and to conversion.
    /// </remarks>
    [Fact]
    public void AnIndirectRectArrayIsFollowed()
    {
        byte[] pdf = PageWithAnnots("[5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect 6 0 R " +
                   "/A << /S /URI /URI (https://example.test/) >> >>"),
            Obj(6, "[10 20 110 70]"));

        PdfLink link = Assert.Single(Links(pdf));

        Assert.Equal(10f, link.X, 3);
        Assert.Equal(100f, link.Width, 3);
    }

    // ==================================================================
    //  Where the link points
    // ==================================================================

    /// <summary>
    /// <b>An indirect <c>/URI</c> string is followed</b> — the other read the "Resolved:" notes
    /// cover, and the other one nothing asserted.
    /// </summary>
    [Fact]
    public void AnIndirectUriStringIsFollowed()
    {
        byte[] pdf = PageWithAnnots("[5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] " +
                   "/A << /S /URI /URI 6 0 R >> >>"),
            Obj(6, "(https://example.test/deep)"));

        PdfLink link = Assert.Single(Links(pdf));

        Assert.Equal(PdfLinkKind.Uri, link.Kind);
        Assert.Equal("https://example.test/deep", link.Uri);
    }

    /// <summary>
    /// <b>An empty <c>/URI</c> is not a URI link.</b> The region exists but points nowhere, so
    /// reporting it as a URI hands a link checker an empty string to resolve.
    /// </summary>
    [Fact]
    public void AnEmptyUriIsNotReportedAsAUriLink()
    {
        byte[] pdf = PageWithAnnots("[5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] " +
                   "/A << /S /URI /URI () >> >>"));

        PdfLink link = Assert.Single(Links(pdf));

        Assert.NotEqual(PdfLinkKind.Uri, link.Kind);
        Assert.True(string.IsNullOrEmpty(link.Uri), $"empty /URI became '{link.Uri}'");
    }

    /// <summary>
    /// <b>A <c>/GoTo</c> action resolves to the page its destination names.</b>
    /// </summary>
    [Fact]
    public void AGoToActionResolvesItsTargetPage()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>"),
            Obj(4, "<< /Length 0 >>\nstream\n\nendstream"),
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] " +
                   "/A << /S /GoTo /D [6 0 R /XYZ 0 792 0] >> >>"),
            Obj(6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
        };

        PdfLink link = Assert.Single(Links(Assemble(bodies)));

        Assert.Equal(PdfLinkKind.GoTo, link.Kind);
        Assert.Equal(1, link.TargetPageIndex);
    }

    /// <summary>
    /// <b>The first destination of a repeated name wins.</b>
    /// </summary>
    /// <remarks>
    /// A name tree may legally carry one name twice — merged documents do it routinely — and the
    /// two entries point at different pages. Taking the last silently re-aims every link that uses
    /// that name at whichever copy happened to be walked latest.
    /// </remarks>
    [Fact]
    public void TheFirstOfTwoDestinationsSharingANameWins()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 7 0 R >> >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>"),
            Obj(4, "<< /Length 0 >>\nstream\n\nendstream"),
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] /Dest (Target) >>"),
            Obj(6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            // "Target" twice: page 1 first, page 2 second.
            Obj(7, "<< /Names [(Target) [3 0 R /XYZ 0 792 0] (Target) [6 0 R /XYZ 0 792 0]] >>"),
        };

        PdfLink link = Assert.Single(Links(Assemble(bodies)));

        Assert.Equal(PdfLinkKind.GoTo, link.Kind);
        Assert.Equal(0, link.TargetPageIndex);
    }

    /// <summary>
    /// <b>"First wins" counts only destinations that resolve to a page.</b> An earlier copy of the
    /// name that points at no page does not shadow a later one that does.
    /// </summary>
    /// <remarks>
    /// Indexing the unresolvable copy stores -1 under the name, and first-wins then keeps it — so a
    /// link whose destination the document does define reports no target at all. The same shape
    /// arises across stores, since the legacy catalog <c>/Dests</c> is read before the name tree.
    /// </remarks>
    [Fact]
    public void AnUnresolvableFirstCopyOfANameDoesNotShadowAResolvableOne()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 7 0 R >> >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>"),
            Obj(4, "<< /Length 0 >>\nstream\n\nendstream"),
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] /Dest (Target) >>"),
            Obj(6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            // "Target" twice: first at object 4 (a content stream, not a page), then at page 2.
            Obj(7, "<< /Names [(Target) [4 0 R /XYZ 0 792 0] (Target) [6 0 R /XYZ 0 792 0]] >>"),
        };

        PdfLink link = Assert.Single(Links(Assemble(bodies)));

        Assert.Equal(PdfLinkKind.GoTo, link.Kind);
        Assert.Equal(1, link.TargetPageIndex);
    }

    // ==================================================================
    //  Malformed input
    // ==================================================================

    /// <summary>
    /// <b>An <c>/Annots</c> entry that is not a dictionary is skipped</b> rather than dereferenced.
    /// </summary>
    /// <remarks>
    /// The array is file-controlled, so a number or a null among the references is an ordinary
    /// malformed input and not an exotic one. The real link beside it must still be extracted — the
    /// second assertion is what makes this a skip rather than an abort.
    /// </remarks>
    [Fact]
    public void AMalformedAnnotsEntryIsSkippedAndTheRestAreKept()
    {
        byte[] pdf = PageWithAnnots("[42 null 5 0 R]",
            Obj(5, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 70] " +
                   "/A << /S /URI /URI (https://example.test/) >> >>"));

        PdfLink link = Assert.Single(Links(pdf));
        Assert.Equal("https://example.test/", link.Uri);
    }
}
