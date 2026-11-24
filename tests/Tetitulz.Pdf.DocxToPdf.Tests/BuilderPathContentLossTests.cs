// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.DocxToPdf;
using Xunit;
using Mth = DocumentFormat.OpenXml.Math;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// What the Builder path (<c>UseLegacyRenderer = false</c>) does with content it has no case for.
///
/// <para><b>The defect these guard.</b> A <c>switch</c> over inline elements with <b>no
/// <c>default</c></b> lets any element outside its cases vanish without a word. Office Math is the
/// instance: dropped while <see cref="DocxToPdfConverter.LastDiagnostic"/> stays <c>null</c>, "the
/// document had an equation" and "the document had none" would be the same output, which is
/// exactly what <c>DocxDiagnostics</c> exists to prevent.</para>
///
/// <para>Math goes through <c>MathLinearizer</c>, which the legacy path shares and whose own
/// summary says it exists "so equations are preserved as text instead of being dropped", and
/// anything else that would have contributed text is reported.</para>
/// </summary>
public class BuilderPathContentLossTests
{
    private static DocxToPdfOptions Builder() => new DocxToPdfOptions { UseLegacyRenderer = false };
    private static DocxToPdfOptions Legacy() => new DocxToPdfOptions { UseLegacyRenderer = true };

    private static byte[] Docx(Action<MainDocumentPart, Body> build)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            build(main, body);
            main.Document = new Document(body);
        }
        return ms.ToArray();
    }

    private static string TextOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < doc.Pages.Count; i++)
            sb.Append(PdfTextExtractor.ExtractText(doc.Pages[i]));
        return sb.ToString();
    }

    private static int AnnotationCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        int n = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
            if (doc.ResolveObject(doc.Pages[i].Dictionary[new PdfName("Annots")]) is PdfArray a)
                n += a.Count;
        return n;
    }

    private static int XObjectCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        int n = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
            if (doc.ResolveObject(doc.Pages[i].Resources?[PdfName.XObject]) is PdfDictionary xo)
                n += xo.Keys.Count();
        return n;
    }

    /// <summary>A paragraph whose only content is an equation with two distinctive identifiers.</summary>
    private static byte[] EquationDocx() => Docx((main, body) =>
    {
        body.Append(new Paragraph(new Run(new Text("CONTROLTEXT"))));
        body.Append(new Paragraph(new Mth.OfficeMath(
            new Mth.Run(new Mth.Text("EQUATIONALPHA")),
            new Mth.Fraction(
                new Mth.Numerator(new Mth.Run(new Mth.Text("NUMER"))),
                new Mth.Denominator(new Mth.Run(new Mth.Text("DENOM")))))));
    });

    // ────────────────────────────────────────────────────────────────────────
    //  Math
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The equation's text reaches the PDF.
    /// </summary>
    /// <remarks>
    /// The control paragraph is the half that makes a failure legible: without it, "the equation is
    /// missing" and "the fixture never reached the converter" look identical.
    /// </remarks>
    [Fact]
    public void The_builder_path_preserves_an_equations_text()
    {
        string text = TextOf(DocxToPdfConverter.ConvertToBytes(EquationDocx(), Builder()));

        Assert.Contains("CONTROLTEXT", text);
        Assert.Contains("EQUATIONALPHA", text);
        Assert.Contains("NUMER", text);
        Assert.Contains("DENOM", text);
    }

    /// <summary>
    /// The fraction is linearised in the shape <c>MathLinearizer</c> defines, not merely
    /// concatenated — so this fails if the equation's text arrives by some other route.
    /// </summary>
    [Fact]
    public void The_builder_path_linearises_a_fraction_rather_than_concatenating_it()
    {
        string text = TextOf(DocxToPdfConverter.ConvertToBytes(EquationDocx(), Builder()));

        // MathLinearizer renders m:f as "(num)/(den)".
        Assert.Contains("(NUMER)/(DENOM)", text.Replace(" ", ""));
    }

    /// <summary>
    /// Linearising is an approximation of the legacy path's 2-D typesetting, so it is reported.
    /// </summary>
    /// <remarks>
    /// This is the assertion the original defect would have failed even if the text had somehow
    /// survived: the whole point of the diagnostics sink is that a caller can tell an approximation
    /// from a faithful rendering, and silence claims the latter.
    /// </remarks>
    [Fact]
    public void The_builder_path_reports_that_it_linearised_the_equation()
    {
        DocxToPdfConverter.ConvertToBytes(EquationDocx(), Builder());

        string? diagnostic = DocxToPdfConverter.LastDiagnostic;
        Assert.False(string.IsNullOrEmpty(diagnostic),
            "the equation was approximated and nothing was reported");
        Assert.Contains("linearised", diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The acceptance control: an ordinary document reports nothing. Without it the assertion above
    /// passes with a converter that reports on every conversion, which is the "fires on conformant
    /// input" failure this repository keeps recording.
    /// </summary>
    [Fact]
    public void An_ordinary_document_reports_nothing_on_the_builder_path()
    {
        byte[] plain = Docx((main, body) =>
            body.Append(new Paragraph(new Run(new Text("just some ordinary text")))));

        DocxToPdfConverter.ConvertToBytes(plain, Builder());

        Assert.Null(DocxToPdfConverter.LastDiagnostic);
    }

    /// <summary>
    /// Inert markup — a bookmark, a proofing error — loses nothing and must not be reported.
    /// </summary>
    /// <remarks>
    /// This is what the <c>InnerText</c> test in <c>MapUnhandledInline</c> buys. A <c>default</c>
    /// arm that reported every unmatched element would fire on essentially every real Word
    /// document, since Word emits bookmarks and proofing marks constantly.
    /// </remarks>
    [Fact]
    public void Inert_markup_is_not_reported_as_lost_content()
    {
        byte[] withMarkup = Docx((main, body) =>
        {
            var p = new Paragraph();
            p.Append(new BookmarkStart { Id = "1", Name = "mark" });
            p.Append(new ProofError { Type = ProofingErrorValues.SpellStart });
            p.Append(new Run(new Text("visible text")));
            p.Append(new ProofError { Type = ProofingErrorValues.SpellEnd });
            p.Append(new BookmarkEnd { Id = "1" });
            body.Append(p);
        });

        string text = TextOf(DocxToPdfConverter.ConvertToBytes(withMarkup, Builder()));

        Assert.Contains("visible text", text);
        Assert.Null(DocxToPdfConverter.LastDiagnostic);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  What DocxToPdfOptions.UseLegacyRenderer claims about this path
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins the Builder path's actual coverage against the list on
    /// <see cref="DocxToPdfOptions.UseLegacyRenderer"/>.
    /// </summary>
    /// <remarks>
    /// The list is prose, and a wrong one misleads: a list saying the Builder path emits no inline
    /// images or headers/footers would send a caller to the legacy renderer for a document that does
    /// not need it. This test is the enforcement.
    /// </remarks>
    [Fact]
    public void The_builder_path_emits_images_headers_and_link_annotations()
    {
        byte[] docx = Docx((main, body) =>
        {
            body.Append(new Paragraph(new Run(new Text("BODYTEXT"))));

            var rel = main.AddHyperlinkRelationship(new Uri("https://example.com/probe"), true);
            body.Append(new Paragraph(new Hyperlink(new Run(new Text("LINKTEXT"))) { Id = rel.Id }));

            var image = main.AddImagePart(ImagePartType.Png);
            using (var png = new MemoryStream(OnePixelPng())) image.FeedData(png);
            body.Append(new Paragraph(new Run(InlineDrawing(main.GetIdOfPart(image)))));

            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text("HEADERTEXT"))));
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(new Paragraph(new Run(new Text("FOOTERTEXT"))));
            body.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
        });

        byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, Builder());
        string text = TextOf(pdf);

        // Emitted, as the doc says.
        Assert.True(XObjectCount(pdf) > 0, "the inline image was not emitted");
        Assert.Contains("HEADERTEXT", text);
        Assert.Contains("FOOTERTEXT", text);

        // The link: its text, and its /Link annotation to the relationship's target — read for its anchor
        // alone, an external hyperlink would be text that cannot be clicked.
        Assert.Contains("LINKTEXT", text);
        Assert.Equal(1, AnnotationCount(pdf));
        Assert.Contains("(https://example.com/probe)", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        // The legacy path agrees.
        Assert.Equal(1, AnnotationCount(DocxToPdfConverter.ConvertToBytes(docx, Legacy())));
    }

    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static Drawing InlineDrawing(string embedId) => new Drawing(
        new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 914400L, Cy = 914400L },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = "img" },
            new DocumentFormat.OpenXml.Drawing.Graphic(
                new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = "img" },
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                        new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                            new DocumentFormat.OpenXml.Drawing.Blip { Embed = embedId },
                            new DocumentFormat.OpenXml.Drawing.Stretch(
                                new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                        new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                            new DocumentFormat.OpenXml.Drawing.Transform2D(
                                new DocumentFormat.OpenXml.Drawing.Offset { X = 0L, Y = 0L },
                                new DocumentFormat.OpenXml.Drawing.Extents { Cx = 914400L, Cy = 914400L }),
                            new DocumentFormat.OpenXml.Drawing.PresetGeometry(
                                new DocumentFormat.OpenXml.Drawing.AdjustValueList())
                            { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
}
