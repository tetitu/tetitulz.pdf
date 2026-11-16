// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An annotation's <c>/Subtype</c> is the gate every annotation walk in <c>Base.Operations</c>
/// opens with, and six of them read it. Four followed an indirect reference
/// (<c>PdfAttachmentManager</c>, <c>PdfLinkExtractor</c>, <c>PdfRichMediaExtractor</c>,
/// <c>PdfSanitizer</c>); two did not, and this file covers those two.
/// </summary>
/// <remarks>
/// <para><b>The separating input is <c>/Subtype 9 0 R</c></b> — legal under §7.3.10 for any
/// dictionary value, and the exact spelling a producer emits when it shares one name object across
/// a thousand annotations. <see cref="PdfDictionary.GetName"/> does not follow a reference, so the
/// gate read the subtype as <i>absent</i>, which every one of these loops treats as "not my kind of
/// annotation" and skips.</para>
///
/// <para><b><c>Pdf3DExtractor</c> is the sharper case, because its near-identical twin sits one
/// file away and had already been fixed.</b> <c>PdfRichMediaExtractor</c> walks the same
/// <c>/Annots</c> arrays, gates on the same key and extracts the same shape of payload stream, and
/// it resolves. The 3D copy read the annotation subtype twice and the <c>/3DD</c> stream's own
/// subtype once, all three raw.</para>
///
/// <para><b>Every assertion here is on a value the file states.</b> "The list came back" is
/// satisfied by the empty list the defect produces, and for the flattener "no exception" is
/// satisfied by a run that flattened nothing at all.</para>
/// </remarks>
public class AnnotationSubtypeResolutionTests
{
    private const string Payload = "U3D-fake-mesh-payload";

    // ==================================================================
    //  Pdf3DExtractor — annotation gate, both call sites
    // ==================================================================

    /// <summary>
    /// The 3D annotation is listed, with its page and its stream's exact decoded length, whichever
    /// way the annotation's <c>/Subtype</c> is spelled. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Subtype /3D
    [InlineData(true)]   // /Subtype 9 0 R
    public void The3DAnnotationIsListed_WhicheverWayItsSubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(ThreeDPdf(annotIndirect: indirect, streamIndirect: false));

        IReadOnlyList<Pdf3DInfo> listed = Pdf3DExtractor.List3DAnnotations(ms);

        Pdf3DInfo only = Assert.Single(listed);
        Assert.Equal(0, only.PageIndex);
        Assert.Equal("U3D", only.StreamType);
        Assert.Equal(Payload.Length, only.DataLength);
        Assert.Null(only.DataLengthUnavailableReason);
    }

    /// <summary>
    /// The <c>/3DD</c> stream's own <c>/Subtype</c> — the U3D-or-PRC answer <c>Pdf3DInfo</c> exists
    /// to report — survives being written indirectly.
    /// </summary>
    /// <remarks>
    /// This read is the one that does <b>not</b> drop the annotation: it reported
    /// <see cref="Pdf3DInfo.StreamType"/> as <c>null</c>, so the annotation was listed as 3D content
    /// of unknown kind while the file said U3D plainly. A caller choosing a viewer off that property
    /// gets no answer rather than a wrong one, which is why it outlived the two gates beside it.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Subtype /U3D
    [InlineData(true)]   // /Subtype 10 0 R
    public void The3DStreamTypeIsReported_WhicheverWayTheStreamSubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(ThreeDPdf(annotIndirect: false, streamIndirect: indirect));

        Pdf3DInfo only = Assert.Single(Pdf3DExtractor.List3DAnnotations(ms));

        Assert.Equal("U3D", only.StreamType);
    }

    /// <summary>
    /// The second gate: <c>Extract3DStream</c> hands back the payload bytes whichever way the
    /// annotation's <c>/Subtype</c> is spelled. Its failure is louder than the listing's — the
    /// unresolved read walked past the annotation and the method threw
    /// <see cref="System.ArgumentOutOfRangeException"/>, telling the caller no such 3D annotation
    /// exists in a file that carries one.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Subtype /3D
    [InlineData(true)]   // /Subtype 9 0 R
    public void The3DStreamBytesAreExtracted_WhicheverWayTheSubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(ThreeDPdf(annotIndirect: indirect, streamIndirect: false));

        byte[] data = Pdf3DExtractor.Extract3DStream(ms, 0);

        Assert.Equal(Payload, Encoding.ASCII.GetString(data));
    }

    /// <summary>
    /// The floor: a <c>/Square</c> annotation carrying a <c>/3DD</c> stream is not 3D content, so
    /// the listing must stay empty. Without this, "the annotation is listed" above is also
    /// satisfied by a walk that gave up classifying and reported every annotation it saw.
    /// </summary>
    [Fact]
    public void ANonThreeDAnnotation_IsNotListedAs3DContent()
    {
        using var ms = new MemoryStream(ThreeDPdf(
            annotIndirect: false, streamIndirect: false, annotSubtype: "/Square"));

        Assert.Empty(Pdf3DExtractor.List3DAnnotations(ms));
    }

    // ==================================================================
    //  PdfFormFlattener — the widget gate
    // ==================================================================

    /// <summary>
    /// A widget is baked into the page content and dropped from <c>/Annots</c> whichever way its
    /// <c>/Subtype</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves are asserted because either alone is passable by the defect.</b> When
    /// the gate read the subtype as absent the widget never reached <c>widgets</c>, so
    /// <c>StampWidgetsOnPage</c> never ran for the page and <c>/Annots</c> was left exactly as it
    /// was — while <c>RemoveAcroForm</c> ran anyway. The output is then a document with a live
    /// interactive widget and no <c>/AcroForm</c> to give it meaning, which the caller believes is
    /// frozen. Asserting only that a <c>FlatXO</c> resource appeared would miss a widget that was
    /// stamped and then left in <c>/Annots</c>; asserting only that no widget survived would be met
    /// by a flattener that deleted it without drawing it.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Subtype /Widget
    [InlineData(true)]   // /Subtype 9 0 R
    public void TheWidgetIsStampedAndRemoved_WhicheverWayItsSubtypeIsSpelled(bool indirect)
    {
        using var doc = Flatten(FlattenPdf(indirect));
        PdfPage page = doc.Pages[0];

        Assert.Equal(0, CountAnnotations(doc, page, "Widget"));
        Assert.True(HasStampedXObject(doc, page),
            "the flattened page carries no /FlatXO* XObject, so nothing was stamped onto it");
    }

    /// <summary>
    /// The floor for the classification: a <c>/Text</c> annotation with its own appearance stream
    /// survives flattening. It is what stops "resolve the subtype" from being satisfied by a gate
    /// that stopped discriminating and stamped every annotation on the page.
    /// </summary>
    [Fact]
    public void ANonWidgetAnnotationSurvivesFlattening()
    {
        using var doc = Flatten(FlattenPdf(indirect: true));
        PdfPage page = doc.Pages[0];

        Assert.Equal(1, CountAnnotations(doc, page, subtype: null));
        Assert.Equal(1, CountAnnotations(doc, page, "Text"));
    }

    /// <summary>
    /// The floor for both flattener tests: the fixture really does carry a widget and a non-widget
    /// annotation before flattening, so "no widget survives" is a claim about flattening rather
    /// than about a document that never had one.
    /// </summary>
    [Fact]
    public void TheFlattenerFixtureCarriesAWidgetAndANonWidgetBeforeFlattening()
    {
        using var ms = new MemoryStream(FlattenPdf(indirect: true));
        using var doc = PdfDocument.Open(ms);
        PdfPage page = doc.Pages[0];

        Assert.Equal(2, CountAnnotations(doc, page, subtype: null));
        Assert.Equal(1, CountAnnotations(doc, page, "Widget"));
        Assert.Equal(1, CountAnnotations(doc, page, "Text"));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static PdfDocument Flatten(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        var flat = new MemoryStream();
        PdfFormFlattener.Flatten(input, flat);
        flat.Position = 0;
        return PdfDocument.Open(flat);
    }

    /// <summary>
    /// Counts the page's annotations, optionally only those of one subtype. Reads through
    /// <see cref="PdfResolve"/> so the counter itself cannot be fooled by the spelling under test.
    /// </summary>
    private static int CountAnnotations(PdfDocument doc, PdfPage page, string subtype)
    {
        var annots = PdfResolve.Array(doc, page.Dictionary, PdfName.Annots);
        int n = 0;
        for (int i = 0; annots != null && i < annots.Count; i++)
        {
            var annot = PdfResolve.Dict(doc, annots[i]);
            if (annot == null) continue;
            if (subtype == null ||
                PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value == subtype) n++;
        }
        return n;
    }

    /// <summary>True when the page's resources name an appearance the flattener stamped.</summary>
    private static bool HasStampedXObject(PdfDocument doc, PdfPage page)
    {
        var resources = PdfResolve.Dict(doc, page.Dictionary, PdfName.Resources);
        var xobjects = PdfResolve.Dict(doc, resources, PdfName.XObject);
        if (xobjects == null) return false;
        foreach (var kvp in xobjects)
            if (kvp.Key.Value.StartsWith("FlatXO", System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// One page carrying one 3D annotation whose <c>/3DD</c> is a U3D stream. Object 9 holds the
    /// annotation's <c>/Subtype</c> and object 10 the stream's, when the corresponding flag is set.
    /// </summary>
    private static byte[] ThreeDPdf(bool annotIndirect, bool streamIndirect,
        string annotSubtype = "/3D")
    {
        string annot  = annotIndirect  ? "9 0 R"  : annotSubtype;
        string stream = streamIndirect ? "10 0 R" : "/U3D";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Annots [5 0 R] >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype " + annot + " /Rect [0 0 10 10] /3DD 6 0 R >>",
            Stream("/Type /3D /Subtype " + stream, Payload),
            "null", "null",
            annotSubtype,
            "/U3D");
    }

    /// <summary>
    /// One page carrying a text-field widget and a <c>/Text</c> annotation, each with its own
    /// appearance stream. Object 9 holds the widget's <c>/Subtype</c> when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] FlattenPdf(bool indirect)
    {
        string subtype = indirect ? "9 0 R" : "/Widget";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 0 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R"
                + " /Annots [5 0 R 7 0 R] /Resources << >> >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype " + subtype + " /FT /Tx /T (Text1)"
                + " /Rect [20 20 60 60] /F 4 /AP << /N 6 0 R >> >>",
            Appearance("0 0 1 rg"),
            "<< /Type /Annot /Subtype /Text /Rect [100 20 140 60] /F 4"
                + " /AP << /N 8 0 R >> >>",
            Appearance("1 0 0 rg"),
            "/Widget");
    }

    private static string Appearance(string colour) =>
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
