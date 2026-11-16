// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Every annotation this library wrote was created without an <c>/AP</c>, and nothing here noticed:
/// the renderer synthesises a fallback appearance at draw time, so the annotations looked right in
/// this library's own viewer and its SVG exporter while carrying nothing a different reader could
/// use.
///
/// <para><b>Which is exactly why these tests do not render.</b> A rendered page proves only that
/// the fallback still works — it is identical with the <c>/AP</c> present and absent. Every
/// assertion below reads the <b>parsed appearance stream</b> out of the written file.</para>
/// </summary>
public sealed class AnnotationAppearanceTests
{
    private static byte[] OnePage()
    {
        var sb = new StringBuilder();
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 3 0 R >>",
        };

        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
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

    private static byte[] Write(Action<Stream, Stream> op, byte[] pdf = null)
    {
        using var input = new MemoryStream(pdf ?? OnePage(), writable: false);
        using var output = new MemoryStream();
        op(input, output);
        return output.ToArray();
    }

    /// <summary>The first annotation on page 0 of a written document.</summary>
    private static PdfDictionary Annotation(byte[] pdf, PdfDocument doc, int index = 0)
    {
        var annots = doc.Pages[0].GetAnnotations();
        Assert.NotNull(annots);
        Assert.True(annots.Count > index, $"expected at least {index + 1} annotation(s)");
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(annots[index]));
    }

    /// <summary>The annotation's <c>/AP /N</c> form, resolved.</summary>
    private static PdfStream Appearance(PdfDocument doc, PdfDictionary annot)
    {
        var ap = doc.ResolveObject(annot[PdfName.AP]) as PdfDictionary;
        Assert.NotNull(ap);
        return Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
    }

    private static string Content(PdfStream form) =>
        Encoding.GetEncoding("iso-8859-1").GetString(form.GetDecodedData());

    // ================================================================ /AP is written

    /// <summary>
    /// A square annotation now carries a real appearance form: the right type, a <c>/BBox</c>
    /// matching its rectangle, and a stroked path in the content.
    /// </summary>
    [Fact]
    public void AddRectangle_WritesAnAppearanceFormSizedToItsRectangle()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddRectangle(i, o, 0, 50, 60, 150, 110));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream form = Appearance(doc, Annotation(pdf, doc));

        Assert.Equal("XObject", form.GetName(PdfName.Type)?.Value);
        Assert.Equal("Form", form.GetName(PdfName.Subtype)?.Value);

        var bbox = Assert.IsType<PdfArray>(doc.ResolveObject(form[PdfName.BBox]));
        Assert.Equal(0d, (bbox[0] as PdfNumber).DoubleValue, 3);
        Assert.Equal(0d, (bbox[1] as PdfNumber).DoubleValue, 3);
        Assert.Equal(100d, (bbox[2] as PdfNumber).DoubleValue, 3);   // 150 - 50
        Assert.Equal(50d, (bbox[3] as PdfNumber).DoubleValue, 3);    // 110 - 60

        Assert.Contains(" re", Content(form));
        Assert.Contains("S", Content(form));
    }

    /// <summary>
    /// A highlight must composite through <b>multiply</b>, or the fill is opaque and the highlight
    /// hides the words it marks. The blend lives in the form's own <c>/ExtGState</c>, so it cannot
    /// leak onto the page.
    /// </summary>
    [Fact]
    public void AddHighlight_CompositesThroughMultiplySoTheTextStaysLegible()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddHighlight(i, o, 0, 40, 100, 200, 120));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream form = Appearance(doc, Annotation(pdf, doc));

        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Resources]));
        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.ExtGState]));
        var gs = Assert.IsType<PdfDictionary>(doc.ResolveObject(states[states.Keys.First()]));

        Assert.Equal(PdfBlendModes.Multiply, gs.GetName(PdfName.BM)?.Value);
        Assert.Contains("/" + states.Keys.First().Value + " gs", Content(form));
        Assert.Contains("\nf\n", Content(form));      // the quads are filled, not stroked
    }

    /// <summary>
    /// <c>/QuadPoints</c> are in <b>page</b> space and the appearance box has its origin at (0,0),
    /// so the quads must be translated by the rectangle's origin.
    /// </summary>
    /// <remarks>
    /// Emitting them untranslated puts the marks at the page's own coordinates inside a box that is
    /// then placed at the rectangle — so a highlight halfway down the page lands entirely outside
    /// its own appearance and is clipped away to nothing. The rendered page looks identical either
    /// way, because the renderer's fallback ignores <c>/AP</c> geometry and re-derives it.
    /// </remarks>
    [Fact]
    public void AddHighlight_TranslatesQuadPointsIntoTheAppearanceBox()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddHighlight(i, o, 0, 40, 300, 200, 320));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        string content = Content(Appearance(doc, Annotation(pdf, doc)));

        // The quad is the whole rect, so in appearance space it is at the origin.
        Assert.Contains("0.0000 0.0000 160.0000 20.0000 re", content);
        // ...and NOT at the page coordinates it was declared in.
        Assert.DoesNotContain("40.0000 300.0000", content);
    }

    /// <summary>An underline's rule sits at the bottom of its quad; a strike-out's across the middle.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextMarkup_PutsTheRuleWhereTheSubtypeSaysItGoes(bool underline)
    {
        byte[] pdf = Write((i, o) =>
        {
            if (underline) PdfAnnotationWriter.AddUnderline(i, o, 0, 40, 100, 200, 140);
            else PdfAnnotationWriter.AddStrikeOut(i, o, 0, 40, 100, 200, 140);
        });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        string content = Content(Appearance(doc, Annotation(pdf, doc)));

        // The quad is 40 points tall, so its middle is y=20 and its bottom is near y=0.
        double y = RuleY(content);
        if (underline) Assert.InRange(y, 0.1, 8);
        else Assert.InRange(y, 18, 22);
    }

    /// <summary>The y of the first <c>m … l S</c> rule in a content stream.</summary>
    private static double RuleY(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            string[] t = line.Split(' ');
            if (t.Length >= 6 && t[2] == "m" && t[5] == "l")
                return double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Fail("the appearance draws no rule: " + content);
        return 0;
    }

    /// <summary>A free-text annotation draws its text, with a font in the form's own resources.</summary>
    [Fact]
    public void AddFreeText_DrawsTheTextWithItsOwnFontResource()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddFreeText(i, o, 0, 20, 20, 200, 40, "Reviewed"));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream form = Appearance(doc, Annotation(pdf, doc));

        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Resources]));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.Font]));
        Assert.NotEmpty(fonts.Keys);

        string content = Content(form);
        Assert.Contains("BT", content);
        Assert.Contains("(Reviewed) Tj", content);
        Assert.Contains("ET", content);
    }

    /// <summary>A stamp draws its name.</summary>
    [Fact]
    public void AddStamp_DrawsItsLabel()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddStamp(i, o, 0, 20, 20, 120, 40, "Approved"));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        Assert.Contains("(Approved) Tj", Content(Appearance(doc, Annotation(pdf, doc))));
    }

    // ================================================================ deliberate absences

    /// <summary>
    /// A sticky note gets <b>no</b> appearance, and that is the correct output: §12.5.6.4 leaves the
    /// icon to the viewer, chosen from <c>/Name</c>, so writing one would replace a familiar icon
    /// with ours in every reader at once. The reason is reported rather than left to be guessed.
    /// </summary>
    [Fact]
    public void AddTextNote_WritesNoAppearanceAndSaysWhy()
    {
        byte[] pdf = Write((i, o) => PdfAnnotationWriter.AddTextNote(i, o, 0, 100, 100, "note"));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.ResolveObject(Annotation(pdf, doc)[PdfName.AP]));
        Assert.Contains("viewer", PdfAnnotationWriter.LastAppearanceSkipped);
    }

    /// <summary>A link draws nothing of its own, so it too gets no appearance.</summary>
    [Fact]
    public void AddUriLink_WritesNoAppearanceAndSaysWhy()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddUriLink(i, o, 0, 10, 10, 100, 30, "https://example.com"));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.ResolveObject(Annotation(pdf, doc)[PdfName.AP]));
        Assert.Contains("nothing of its own", PdfAnnotationWriter.LastAppearanceSkipped);
    }

    /// <summary>
    /// Free text needing a composite font is refused rather than drawn as question marks — the
    /// glyphs here are <i>seen</i>, unlike the invisible OCR layer, so a non-embedded font would
    /// draw blank boxes and a WinAnsi one would draw <c>?</c>.
    /// </summary>
    [Fact]
    public void AddFreeText_WithHebrew_WritesNoAppearanceRatherThanQuestionMarks()
    {
        byte[] pdf = Write((i, o) =>
            PdfAnnotationWriter.AddFreeText(i, o, 0, 20, 20, 200, 40, "שלום"));

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.ResolveObject(Annotation(pdf, doc)[PdfName.AP]));
        Assert.Contains("composite font", PdfAnnotationWriter.LastAppearanceSkipped);
    }

    // ================================================================ removal

    [Fact]
    public void RemoveAnnotations_TakesOutOnlyTheNamedSubtypes()
    {
        byte[] withHighlight = Write((i, o) => PdfAnnotationWriter.AddHighlight(i, o, 0, 40, 100, 200, 120));
        byte[] both = Write((i, o) => PdfAnnotationWriter.AddStamp(i, o, 0, 20, 20, 120, 40), withHighlight);

        byte[] cleaned = Write((i, o) => PdfOperations.RemoveAnnotations(i, o, PdfAnnotationSubtypes.Highlight), both);

        using var ms = new MemoryStream(cleaned, writable: false);
        using var doc = PdfDocument.Open(ms);
        var annots = doc.Pages[0].GetAnnotations();

        Assert.Single(annots);
        Assert.Equal(PdfAnnotationSubtypes.Stamp,
            (doc.ResolveObject(annots[0]) as PdfDictionary).GetName(PdfName.Subtype)?.Value);
    }

    /// <summary>
    /// A removal that matches nothing leaves the file <b>byte-identical</b> rather than appending an
    /// empty revision — a pass that found nothing should not grow the document or add a revision
    /// claiming an edit that never happened.
    /// </summary>
    [Fact]
    public void RemoveAnnotations_MatchingNothing_LeavesTheFileUnchanged()
    {
        byte[] original = Write((i, o) => PdfAnnotationWriter.AddStamp(i, o, 0, 20, 20, 120, 40));

        byte[] after = Write((i, o) => PdfOperations.RemoveAnnotations(i, o, PdfAnnotationSubtypes.Highlight), original);

        Assert.Equal(original, after);
    }

    // ================================================================ flattening

    /// <summary>
    /// Flattening draws the appearance into the page's content and drops the annotation, so the
    /// mark survives in a reader that ignores annotations.
    /// </summary>
    [Fact]
    public void FlattenAnnotations_MovesTheAppearanceIntoThePageContent()
    {
        byte[] withStamp = Write((i, o) => PdfAnnotationWriter.AddStamp(i, o, 0, 20, 20, 120, 40));

        byte[] flat = Write((i, o) => PdfOperations.FlattenAnnotations(i, o), withStamp);

        using var ms = new MemoryStream(flat, writable: false);
        using var doc = PdfDocument.Open(ms);

        // Gone from /Annots...
        var annots = doc.Pages[0].GetAnnotations();
        Assert.True(annots == null || annots.Count == 0, "the flattened annotation is still listed");

        // ...and drawn by the page, through an XObject the page's own resources name.
        string content = Encoding.GetEncoding("iso-8859-1").GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains(" Do", content);

        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        Assert.NotEmpty(xobjects.Keys);

        string name = xobjects.Keys.First().Value;
        Assert.Contains("/" + name + " Do", content);
    }

    /// <summary>
    /// An annotation with no appearance is <b>kept</b>, not dropped: there is nothing to draw in its
    /// place, so dropping it would delete the content outright.
    /// </summary>
    [Fact]
    public void FlattenAnnotations_KeepsAnAnnotationThatHasNoAppearance()
    {
        byte[] withNote = Write((i, o) => PdfAnnotationWriter.AddTextNote(i, o, 0, 100, 100, "note"));

        byte[] flat = Write((i, o) => PdfOperations.FlattenAnnotations(i, o), withNote);

        using var ms = new MemoryStream(flat, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.Pages[0].GetAnnotations());
    }

    /// <summary>...and says so, so "nothing to flatten" and "could not flatten" stay distinct.</summary>
    [Fact]
    public void FlattenAnnotations_ReportsWhyAnAnnotationWasKept()
    {
        byte[] withNote = Write((i, o) => PdfAnnotationWriter.AddTextNote(i, o, 0, 100, 100, "note"));

        using var input = new MemoryStream(withNote, writable: false);
        using var output = new MemoryStream();
        var result = PdfOperations.FlattenAnnotations(input, output);

        Assert.Equal(0, result.Affected);
        Assert.Contains(result.Warnings, w => w.Contains("no /AP"));
    }

    /// <summary>
    /// A hidden annotation is not flattened: §12.5.3 says it is not drawn, so burning it into the
    /// page would make invisible content visible.
    /// </summary>
    [Fact]
    public void FlattenAnnotations_LeavesAHiddenAnnotationAlone()
    {
        byte[] withStamp = Write((i, o) => PdfAnnotationWriter.AddStamp(i, o, 0, 20, 20, 120, 40));
        byte[] hidden = HideFirstAnnotation(withStamp);

        using var input = new MemoryStream(hidden, writable: false);
        using var output = new MemoryStream();
        var result = PdfOperations.FlattenAnnotations(input, output);

        Assert.Equal(0, result.Affected);
        Assert.Contains(result.Warnings, w => w.Contains("hidden"));
    }

    /// <summary>Appends a revision setting <c>/F 2</c> (hidden) on page 0's first annotation.</summary>
    private static byte[] HideFirstAnnotation(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        var annots = doc.Pages[0].GetAnnotations();
        var reference = Assert.IsType<PdfReference>(annots[0]);
        var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(reference));

        var updated = PdfDictionary.CloneShallow(annot);
        updated.Set(PdfName.F, new PdfNumber(2));
        updated.ObjectNumber = reference.ReferenceObjectNumber;
        updated.GenerationNumber = 0;

        using var output = new MemoryStream();
        Base.Operations.Documents.PdfPageContentAppender.AppendRevision(
            ms, output, doc,
            new System.Collections.Generic.List<Core.Writing.IndirectObject>
            {
                new Core.Writing.IndirectObject(reference.ReferenceObjectNumber, 0, updated),
            },
            doc.CrossReferenceTable.GetMaxObjectNumber() + 1);
        return output.ToArray();
    }
}
