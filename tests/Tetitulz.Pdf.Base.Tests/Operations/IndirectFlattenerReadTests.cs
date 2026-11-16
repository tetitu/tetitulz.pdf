// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Flattening must bake the appearance the document actually selected, and place it where the
/// document actually put it — whichever way the keys that say so are spelled.
/// </summary>
/// <remarks>
/// <para><b><c>/AS</c> is the one read in this file that can invert a document's meaning.</b>
/// <c>GetName</c> does not follow an indirect reference, so a null <c>/AS</c> fell through to
/// <c>SingleOnState</c>, which returns the single non-<c>/Off</c> entry — the <b>checked</b>
/// appearance. An <b>unchecked</b> box whose <c>/AS</c> was written as <c>9 0 R</c> was therefore
/// flattened as checked; and <c>RemoveAcroForm</c> then runs unconditionally, so the true value is
/// not merely wrong in the output, it is <b>unrecoverable from it</b>.</para>
///
/// <para><c>/Rect</c>, <c>/BBox</c> and <c>/Matrix</c> are the same read with quieter consequences:
/// a null <c>/Rect</c> skips the widget entirely — leaving an interactive annotation in a document
/// whose <c>/AcroForm</c> has just been removed — while nulls for the other two silently substitute
/// the defaults on the lines above, which assume the appearance box <i>is</i> the widget rectangle
/// and the matrix is the identity. Both are the cases the code's own comments record having been
/// fixed before.</para>
///
/// <para><b>Asserted on decoded pixels.</b> A flattened checkbox is a valid PDF either way, and the
/// page still has content, a stream and a correct length whichever appearance got baked in — so
/// nothing structural separates "checked" from "unchecked". The two states are painted in different
/// colours and the assertion reads the colour back.</para>
/// </remarks>
public class IndirectFlattenerReadTests
{
    /// <summary>
    /// An unchecked box stays unchecked whichever way <c>/AS</c> is spelled. <c>false</c> is the
    /// control: the direct spelling always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /AS /Off
    [InlineData(true)]   // /AS 9 0 R
    public void AnUncheckedBoxFlattensUnchecked_WhicheverWayAsIsSpelled(bool indirect)
    {
        var pixel = FlattenAndSample(CheckBoxPdf(indirect, on: false));

        Assert.True(pixel.Blue > 200 && pixel.Red < 80,
            "expected the /Off appearance (blue), got " + Describe(pixel) +
            " — the checked appearance was baked into an unchecked box.");
    }

    /// <summary>
    /// The floor, and the reason the assertion above is not satisfied by a flattener that bakes
    /// nothing: a box that really is checked must come out checked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACheckedBoxFlattensChecked_WhicheverWayAsIsSpelled(bool indirect)
    {
        var pixel = FlattenAndSample(CheckBoxPdf(indirect, on: true));

        Assert.True(pixel.Red > 200 && pixel.Blue < 80,
            "expected the /Yes appearance (red), got " + Describe(pixel));
    }

    /// <summary>Which geometry key the fixture writes as an indirect reference.</summary>
    public enum Indirectly
    {
        /// <summary>The appearance stream's <c>/BBox</c>, which has a non-zero origin here.</summary>
        BBox,

        /// <summary>The appearance stream's <c>/Matrix</c>, a 90° rotation here.</summary>
        Matrix,

        /// <summary>The widget's own <c>/Rect</c>.</summary>
        Rect,

        /// <summary>One element of the <c>/Rect</c> (§7.3.10 lets any be indirect).</summary>
        RectElement,

        /// <summary>One element of the <c>/BBox</c>.</summary>
        BBoxElement,

        /// <summary>One element of the <c>/Matrix</c>.</summary>
        MatrixElement,
    }

    /// <summary>
    /// The stamp the flattener writes is the same however one element of the geometry arrays is
    /// spelled. The arrays were resolved and their elements read raw, so an indirect element was 0:
    /// a <c>/Rect</c> corner at 0 stamped the appearance in the wrong box (and the widget was still
    /// removed as flattened), a <c>/BBox</c> or <c>/Matrix</c> element at 0 placed it wrongly.
    /// </summary>
    /// <remarks>
    /// The content stream is compared rather than the rendered page: the renderer reads a form's
    /// <c>/BBox</c> and <c>/Matrix</c> elements raw itself (<c>PdfResolve.ArrayFloat</c>'s remarks),
    /// so a rendered comparison would be about it. Both fixtures number every object alike.
    /// </remarks>
    [Theory]
    [InlineData(Indirectly.RectElement)]
    [InlineData(Indirectly.BBoxElement)]
    [InlineData(Indirectly.MatrixElement)]
    public void TheStampIsIdentical_WhicheverWayAnElementIsSpelled(Indirectly key)
    {
        string direct = FlattenedContent(GeometryPdf(null));
        string viaRef = FlattenedContent(GeometryPdf(key));

        Assert.Contains(" cm\n", direct, StringComparison.Ordinal);
        Assert.Equal(direct, viaRef);
    }

    private static string FlattenedContent(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var flat = new MemoryStream();
        PdfFormFlattener.Flatten(input, flat);

        flat.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(flat);
        return Tetitulz.Pdf.Core.Text.Latin1Text.Decode(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>
    /// The flattened page is identical however the geometry keys are spelled.
    /// </summary>
    /// <remarks>
    /// <para>A <i>comparison</i> rather than an absolute-coordinate claim: the two fixtures describe
    /// the same document, so any difference between their rendered pages is the defect and nothing
    /// else. Stating absolute positions would additionally pin the mapping algorithm, which is not
    /// what these rows are about — and the code's own comments record that algorithm having been
    /// corrected twice already.</para>
    ///
    /// <para>The fixture is deliberately non-trivial in all three respects: the <c>/BBox</c> origin
    /// is non-zero, the <c>/Matrix</c> is a rotation, and the <c>/Rect</c> is neither square nor at
    /// the origin. With defaults standing in for any of them the mapping is visibly different, and
    /// for <c>/Rect</c> the widget is not flattened at all.</para>
    /// </remarks>
    [Theory]
    [InlineData(Indirectly.BBox)]
    [InlineData(Indirectly.Matrix)]
    [InlineData(Indirectly.Rect)]
    public void TheFlattenedPageIsIdentical_WhicheverWayTheGeometryIsSpelled(Indirectly key)
    {
        byte[] direct = RenderFlattened(GeometryPdf(null));
        byte[] viaRef = RenderFlattened(GeometryPdf(key));

        using var a = SkiaSharp.SKBitmap.Decode(direct);
        using var b = SkiaSharp.SKBitmap.Decode(viaRef);

        int differing = 0, inkA = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                if (pa != pb) differing++;
                if (pa.Red < 200 || pa.Green < 200 || pa.Blue < 200) inkA++;
            }

        // The control must actually have drawn something, or "identical" is a claim about two
        // blank pages.
        Assert.True(inkA > 50, $"the control page has only {inkA} ink pixels; nothing was flattened");
        Assert.True(differing == 0, $"{differing} pixels differ between the two spellings");
    }

    /// <summary>
    /// After flattening, no interactive widget may remain on the page — whichever way
    /// <c>/Rect</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <para><b>This assertion is structural because the rendered one cannot see the defect, and
    /// finding that out is the useful part.</b> The first version of this test compared the two
    /// spellings' rendered pages, and the <c>/Rect</c> mutation <i>survived</i> it: an unflattened
    /// widget stays in <c>/Annots</c>, and <c>PdfPageRenderer</c> draws annotation appearances — so
    /// the page looks identical whether the appearance was baked into the content stream or is
    /// still being drawn as an annotation. Pixels are the right evidence for <c>/AS</c>, which
    /// changes <i>what</i> is drawn, and the wrong evidence for <c>/Rect</c>, which changes only
    /// <i>who draws it</i>.</para>
    ///
    /// <para>That distinction is exactly the damage the row describes: the widget survives, and
    /// <c>RemoveAcroForm</c> runs unconditionally regardless — so the output carries an interactive
    /// annotation with no <c>/AcroForm</c> to give it meaning, in a document the caller believes is
    /// frozen. A viewer showing the same pixels is what makes it hard to notice.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Rect [40 30 160 70]
    [InlineData(true)]   // /Rect 11 0 R
    public void NoWidgetSurvivesFlattening_WhicheverWayRectIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(GeometryPdf(indirect ? Indirectly.Rect : (Indirectly?)null));
        using var flat = new MemoryStream();
        PdfFormFlattener.Flatten(input, flat);

        flat.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(flat);
        var page = doc.Pages[0];

        var annots = PdfResolve.Array(doc, page.Dictionary, Tetitulz.Pdf.Core.Objects.PdfName.Annots);
        int widgets = 0;
        for (int i = 0; annots != null && i < annots.Count; i++)
            if (PdfResolve.Dict(doc, annots[i]) is { } a &&
                PdfResolve.Name(doc, a, Tetitulz.Pdf.Core.Objects.PdfName.Subtype)?.Value == "Widget")
                widgets++;

        Assert.Equal(0, widgets);
    }

    /// <summary>
    /// The floor for the test above: the <i>unflattened</i> fixture really does carry a widget, so
    /// "no widget survives" is a claim about flattening rather than about a document that never
    /// had one.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesAWidgetBeforeFlattening()
    {
        using var ms = new MemoryStream(GeometryPdf(Indirectly.Rect));
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var page = doc.Pages[0];

        var annots = PdfResolve.Array(doc, page.Dictionary, Tetitulz.Pdf.Core.Objects.PdfName.Annots);
        Assert.NotNull(annots);
        Assert.Single(annots!);
    }

    // ------------------------------------------------------------------

    private static string Describe(SkiaSharp.SKColor c) =>
        "rgb(" + c.Red + "," + c.Green + "," + c.Blue + ")";

    private static SkiaSharp.SKColor FlattenAndSample(byte[] pdf)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(RenderFlattened(pdf));
        Assert.NotNull(bmp);

        // The widget is /Rect [20 20 60 60] on a 100x100 page at 72 dpi; bitmap y is flipped, so
        // its centre (40, 40) in PDF space is bitmap row 60.
        return bmp.GetPixel(40, 60);
    }

    private static byte[] RenderFlattened(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var flat = new MemoryStream();
        PdfFormFlattener.Flatten(input, flat);

        flat.Position = 0;
        return PdfPageRenderer.RenderPage(flat, 0, 72);
    }

    /// <summary>
    /// A check box with two appearance states painted in different colours: <c>/Off</c> blue,
    /// <c>/Yes</c> red. Object 9 holds the state name when <paramref name="indirect"/> — the only
    /// difference between the two fixtures.
    /// </summary>
    private static byte[] CheckBoxPdf(bool indirect, bool on)
    {
        string state = on ? "/Yes" : "/Off";
        string asEntry = indirect ? "9 0 R" : state;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 0 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Annots [5 0 R] /Resources << >> >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Check1) /V " + state
                + " /AS " + asEntry + " /Rect [20 20 60 60] /F 4"
                + " /AP << /N << /Off 6 0 R /Yes 7 0 R >> >> >>",
            AppearanceStream("0 0 1 rg"),   // /Off  — blue
            AppearanceStream("1 0 0 rg"),   // /Yes  — red
            "null",
            state);
    }

    /// <summary>
    /// One widget with a non-zero <c>/BBox</c> origin and a rotated <c>/Matrix</c>, so none of the
    /// three defaults the code falls back to is the right answer. Objects 9, 10 and 11 hold the
    /// three values and 12, 13 and 14 one element of each; <paramref name="key"/> selects which one
    /// the document points at instead of writing inline, and <see langword="null"/> writes all
    /// inline — the control.
    /// </summary>
    private static byte[] GeometryPdf(Indirectly? key)
    {
        const string BBoxValue = "[10 10 110 30]";
        const string MatrixValue = "[0 1 -1 0 0 0]";
        const string RectValue = "[40 30 160 70]";

        string bbox = key == Indirectly.BBox ? "9 0 R"
                    : key == Indirectly.BBoxElement ? "[13 0 R 10 110 30]" : BBoxValue;
        string matrix = key == Indirectly.Matrix ? "10 0 R"
                      : key == Indirectly.MatrixElement ? "[0 1 14 0 R 0 0 0]" : MatrixValue;
        string rect = key == Indirectly.Rect ? "11 0 R"
                    : key == Indirectly.RectElement ? "[12 0 R 30 160 70]" : RectValue;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 0 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R"
                + " /Annots [5 0 R] /Resources << >> >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text1) /Rect " + rect + " /F 4"
                + " /AP << /N 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox " + bbox + " /Matrix " + matrix,
                   "0 0 1 rg 10 10 100 20 re f\n"),
            "null",
            "null",
            BBoxValue,
            MatrixValue,
            RectValue,
            "40",
            "10",
            "-1");
    }

    private static string AppearanceStream(string colour) =>
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 40 40]",
               colour + " 0 0 40 40 re f\n");

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "endstream";

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
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
