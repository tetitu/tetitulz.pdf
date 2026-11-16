// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What a text-markup annotation writes into <c>/Rect</c> and <c>/QuadPoints</c>.
/// </summary>
/// <remarks>
/// <para><b>Written because a consolidation had nothing to prove itself against.</b> The quad array
/// (§12.5.6.10) was built three times — highlight, underline, strike-out — and the corner
/// normalisation (§12.5.2) five times, and merging each into one helper is a pure move, so the
/// merge itself is the assertion only if something reaches the survivor. Nothing did: mutating the
/// helper to leave the corners unordered, to transpose the quad, and to drop its last pair all left
/// 235 annotation, link and markup tests green.</para>
///
/// <para><b>What the existing tests assert instead.</b> They are about the generated
/// <c>/AP</c> — where the rule sits inside an underline's box, how a highlight composites. The
/// appearance builder re-derives its own geometry from the annotation, so it is satisfied by a
/// <c>/Rect</c> and a <c>/QuadPoints</c> that disagree with each other, and by quad numbers in any
/// order. These assert the dictionary the writer emitted, which is what a consuming viewer reads.</para>
/// </remarks>
public class TextMarkupQuadPointsTests
{
    /// <summary>The three subtypes that carry a quad array, which is the whole cluster.</summary>
    public static TheoryData<string> MarkupWriters => new TheoryData<string>
    {
        PdfAnnotationSubtypes.Highlight,
        PdfAnnotationSubtypes.Underline,
        PdfAnnotationSubtypes.StrikeOut,
    };

    /// <summary>
    /// <c>/Rect</c> is ordered lower-left then upper-right however the caller gave its corners.
    /// </summary>
    /// <remarks>
    /// The corners go in <b>reversed</b> — upper-right first — which is the case normalisation
    /// exists for and the one a caller hits by subtracting a height. §12.5.2 requires the ordered
    /// form, and a viewer handed an inverted rectangle draws nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MarkupWriters))]
    public void ReversedCorners_AreNormalisedIntoTheRect(string subtype)
    {
        byte[] pdf = WriteMarkup(subtype, x1: 200, y1: 320, x2: 40, y2: 300);

        double[] rect = Rect(pdf);

        Assert.Equal(40, rect[0]);
        Assert.Equal(300, rect[1]);
        Assert.Equal(200, rect[2]);
        Assert.Equal(320, rect[3]);
    }

    /// <summary>
    /// <c>/QuadPoints</c> is eight numbers naming the four corners of one quad.
    /// </summary>
    /// <remarks>
    /// Asserted as the four <i>corner pairs</i> rather than as eight loose numbers, because the
    /// order is the part that carries meaning: a transposed pair still has the right count, the
    /// right values and the right bounding box, and describes a bow-tie.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MarkupWriters))]
    public void QuadPoints_NameTheFourCornersInTheWrittenOrder(string subtype)
    {
        byte[] pdf = WriteMarkup(subtype, x1: 40, y1: 300, x2: 200, y2: 320);

        double[] q = QuadPoints(pdf);

        Assert.Equal(8, q.Length);
        Assert.Equal(new double[] {  40, 300 }, new[] { q[0], q[1] });   // lower-left
        Assert.Equal(new double[] { 200, 300 }, new[] { q[2], q[3] });   // lower-right
        Assert.Equal(new double[] {  40, 320 }, new[] { q[4], q[5] });   // upper-left
        Assert.Equal(new double[] { 200, 320 }, new[] { q[6], q[7] });   // upper-right
    }

    /// <summary>
    /// The quad keeps the caller's orientation even where <c>/Rect</c> is normalised.
    /// </summary>
    /// <remarks>
    /// The two are deliberately not derived from one another — the rectangle is ordered because
    /// §12.5.2 requires it, and the quad is not because its order is the caller's statement about
    /// the span. Folding the normalisation into the quad would silently re-orient a marked range,
    /// so this pins that they can disagree.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MarkupWriters))]
    public void AReversedQuad_IsNotSilentlyReordered(string subtype)
    {
        byte[] pdf = WriteMarkup(subtype, x1: 200, y1: 320, x2: 40, y2: 300);

        double[] q = QuadPoints(pdf);

        Assert.Equal(200, q[0]);   // the caller's first corner, as given
        Assert.Equal(320, q[1]);
        Assert.Equal(40, q[6]);    // and its opposite, still opposite
        Assert.Equal(300, q[7]);
    }

    // ------------------------------------------------------------------

    private static byte[] WriteMarkup(string subtype, double x1, double y1, double x2, double y2)
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();

        if (subtype == PdfAnnotationSubtypes.Highlight)
            PdfAnnotationWriter.AddHighlight(input, output, 0, x1, y1, x2, y2);
        else if (subtype == PdfAnnotationSubtypes.Underline)
            PdfAnnotationWriter.AddUnderline(input, output, 0, x1, y1, x2, y2);
        else
            PdfAnnotationWriter.AddStrikeOut(input, output, 0, x1, y1, x2, y2);

        return output.ToArray();
    }

    private static double[] Numbers(PdfObject obj, PdfDocument doc)
    {
        var arr = Assert.IsType<PdfArray>(doc.ResolveObject(obj));
        return Enumerable.Range(0, arr.Count)
            .Select(i => Assert.IsType<PdfNumber>(doc.ResolveObject(arr[i])).DoubleValue)
            .ToArray();
    }

    private static double[] Rect(byte[] pdf) => Entry(pdf, PdfName.Rect);

    private static double[] QuadPoints(byte[] pdf) => Entry(pdf, PdfName.QuadPoints);

    private static double[] Entry(byte[] pdf, PdfName key)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        var annots = doc.Pages[0].GetAnnotations();
        Assert.NotNull(annots);
        Assert.True(annots.Count > 0, "the writer added no annotation");
        var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(annots[0]));
        return Numbers(annot[key], doc);
    }

    /// <summary>A one-page document with nothing on it; the annotation is the subject.</summary>
    private static byte[] OnePage()
    {
        var sb = new StringBuilder();
        var offsets = new int[5];
        sb.Append("%PDF-1.7\n");

        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };

        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
