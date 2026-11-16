// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading the annotation writer, the annotation editor, the form flattener's placement and
/// the layer manager found, beside the appender's own findings (<see cref="PageAppendFindingsTests"/>).
/// </summary>
public class AnnotationAndLayerWriterFindingsTests
{
    private const string EncryptTrailer =
        " /Encrypt 9 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]";

    private const string EncryptDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    private static byte[] OnePage(bool encrypted = false, string pageExtras = "", string content = null)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                    (content != null ? "/Contents 8 0 R " : "") + pageExtras + " >>");
        if (content != null) b.StreamObj(8, "<<", content);
        if (!encrypted) return b.Finish();
        return b.Obj(9, EncryptDictionary).Finish(EncryptTrailer);
    }

    private static bool IsDark(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return page.Pixels[i] + page.Pixels[i + 1] + page.Pixels[i + 2] < 100;
    }

    // ================================================================== annotation editor

    /// <summary>
    /// Two squares, each with an indirect appearance: a black one at the origin (object 5) and one
    /// at x = 100 (object 7), on a page whose content is <paramref name="content"/>.
    /// </summary>
    private static byte[] TwoSquares(string content) => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Annots [4 0 R 6 0 R] >>")
        .Obj(4, "<< /Type /Annot /Subtype /Square /Rect [0 0 20 20] /AP << /N 5 0 R >> >>")
        .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f")
        .Obj(6, "<< /Type /Annot /Subtype /Square /Rect [100 0 120 20] /AP << /N 7 0 R >> >>")
        .StreamObj(7, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0.5 g 0 0 20 20 re f")
        .StreamObj(8, "<<", content)
        .Finish();

    /// <summary>
    /// Each flattened annotation is drawn from its own appearance. Placements were named
    /// "AnnotXO" + the next object number, which nothing advances when <c>/AP /N</c> is already
    /// indirect, so both got one name, the second registration replaced the first, and the square
    /// at the origin was drawn with the other square's appearance.
    /// </summary>
    [Fact]
    public void FlatteningTwoAnnotations_RegistersEachUnderItsOwnName()
    {
        using var output = new MemoryStream();
        var result = PdfAnnotationEditor.Flatten(new MemoryStream(TwoSquares("")), output);
        Assert.Equal(2, result.Affected);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var xobjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var targets = xobjects.Select(kv => ((PdfReference)kv.Value).ReferenceObjectNumber).OrderBy(n => n);
        Assert.Equal(new[] { 5, 7 }, targets);
    }

    /// <summary>
    /// A placement's name also avoids the page's own resource names: a page that already draws an
    /// XObject called <c>AnnotXO1</c> keeps it, and the flattened annotation is registered beside it.
    /// </summary>
    [Fact]
    public void FlatteningKeepsAnXObjectThePageAlreadyNamesLikeAPlacement()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Annots [4 0 R] " +
                    "/Resources << /XObject << /AnnotXO1 9 0 R >> >> >>")
            .Obj(4, "<< /Type /Annot /Subtype /Square /Rect [0 0 20 20] /AP << /N 5 0 R >> >>")
            .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f")
            .StreamObj(8, "<<", "/AnnotXO1 Do")
            .StreamObj(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1]", "")
            .Finish();

        using var output = new MemoryStream();
        PdfAnnotationEditor.Flatten(new MemoryStream(pdf), output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var xobjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        Assert.Equal(9, ((PdfReference)xobjects[new PdfName("AnnotXO1")]).ReferenceObjectNumber);
        Assert.Contains(xobjects, kv => ((PdfReference)kv.Value).ReferenceObjectNumber == 5);
    }

    /// <summary>
    /// Flattened appearances are placed in the page's initial graphics state. The page's content
    /// leaves a translation in force, and the black square from the origin was drawn 100 points up
    /// and to the right.
    /// </summary>
    [Fact]
    public void FlattenedAnnotations_AreDrawnInThePagesInitialState()
    {
        using var output = new MemoryStream();
        PdfAnnotationEditor.Flatten(new MemoryStream(TwoSquares("1 0 0 1 100 100 cm")), output);

        var page = PdfOperations.RenderPageToPixels(new MemoryStream(output.ToArray()), 0, 72);
        Assert.True(IsDark(page, 10, 190), "the square was not drawn at its /Rect");
        Assert.False(IsDark(page, 110, 90), "the square was drawn where the page's own cm moved it");
    }

    /// <summary>
    /// The editor refuses an encrypted document with <see cref="PdfEncryptedDocumentException"/>,
    /// as every other Base writer does. It threw a plain <see cref="PdfException"/>, which a caller
    /// catching the encrypted-document type missed.
    /// </summary>
    [Fact]
    public void TheEditorRefusesAnEncryptedDocument_AsAnEncryptedDocument()
    {
        using var output = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAnnotationEditor.RemoveBySubtype(new MemoryStream(OnePage(encrypted: true)), output, "Square"));
        Assert.Equal(0, output.Length);
    }

    // ================================================================== form flattener

    /// <summary>
    /// Flattened widgets are placed in the page's initial graphics state, for the same reason and
    /// through the same q/Q bracket as the annotation editor.
    /// </summary>
    [Fact]
    public void FlattenedWidgets_AreDrawnInThePagesInitialState()
    {
        byte[] form = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Annots [4 0 R] >>")
            .Obj(4, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (b) /Rect [0 0 20 20] " +
                    "/P 3 0 R /AP << /N 5 0 R >> >>")
            .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f")
            .StreamObj(8, "<<", "1 0 0 1 100 100 cm")
            .Finish();

        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(new MemoryStream(form), output);

        var page = PdfOperations.RenderPageToPixels(new MemoryStream(output.ToArray()), 0, 72);
        Assert.True(IsDark(page, 10, 190), "the widget was not drawn at its /Rect");
        Assert.False(IsDark(page, 110, 90), "the widget was drawn where the page's own cm moved it");
    }

    /// <summary>
    /// The page is bracketed once however many widgets are stamped on it, and not at all when none
    /// is. Two widgets give <c>[q, own, Q, stamp, stamp]</c>. A widget with no appearance stamps
    /// nothing, and that page keeps its content as it was.
    /// </summary>
    [Fact]
    public void TheBracketIsAddedOncePerPage_AndOnlyWhenSomethingIsStamped()
    {
        byte[] form = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 6 0 R 10 0 R] >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 11 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Annots [4 0 R 6 0 R] >>")
            .Obj(4, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (a) /Rect [0 0 20 20] /P 3 0 R /AP << /N 5 0 R >> >>")
            .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f")
            .Obj(6, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (b) /Rect [50 0 70 20] /P 3 0 R /AP << /N 5 0 R >> >>")
            .StreamObj(8, "<<", "1 0 0 1 100 100 cm")
            .Obj(10, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (c) /Rect [0 0 20 20] /P 11 0 R >>")
            .Obj(11, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 12 0 R /Annots [10 0 R] >>")
            .StreamObj(12, "<<", "1 0 0 1 100 100 cm")
            .Finish();

        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(new MemoryStream(form), output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var stamped = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]));
        Assert.Equal(5, stamped.Count);
        var unstamped = doc.ResolveObject(doc.Pages[1].Dictionary[PdfName.Contents]);
        Assert.Equal(1, unstamped is PdfArray a ? a.Count : 1);
    }

    // ================================================================== annotation writer

    /// <summary>
    /// A free-text annotation's <c>/DA</c> is written culture-invariantly, with its font name
    /// escaped. Under a culture with a decimal comma a 10.5 pt size came out as <c>10,5 Tf</c>.
    /// </summary>
    [Fact]
    public void AFreeTextDefaultAppearance_IsCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        byte[] written;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            using var output = new MemoryStream();
            PdfAnnotationWriter.AddFreeText(new MemoryStream(OnePage()), output, 0, 10, 10, 100, 30,
                "note", fontName: "Times Roman", fontSize: 10.5f, r: 0.25f, g: 0, b: 0);
            written = output.ToArray();
        }
        finally { CultureInfo.CurrentCulture = saved; }

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        var annot = (PdfDictionary)doc.ResolveObject(annots[0]);
        Assert.Equal("/Times#20Roman 10.5 Tf 0.25 0 0 rg", ((PdfString)annot[PdfName.DA]).GetText());
    }

    /// <summary>
    /// The writer refuses an encrypted document. With no catalog loaded it has no pages, so a single
    /// annotation failed as "page index out of range" and a batch skipped every one and returned 0.
    /// </summary>
    [Fact]
    public void TheWriterRefusesAnEncryptedDocument()
    {
        byte[] encrypted = OnePage(encrypted: true);

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAnnotationWriter.AddHighlight(new MemoryStream(encrypted), new MemoryStream(), 0, 10, 10, 50, 20));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAnnotationWriter.AddUriLinks(new MemoryStream(encrypted), new MemoryStream(), new[]
            {
                new PdfAnnotationWriter.UriLinkRequest { PageIndex = 0, X2 = 10, Y2 = 10, Uri = "https://x.test" },
            }));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAnnotationWriter.WriteAnnotations(new MemoryStream(encrypted), new MemoryStream(),
                Array.Empty<FormDataAnnotation>()));
    }

    /// <summary>
    /// A link batch that writes nothing copies the input through. It appended an empty revision,
    /// which changes nothing and still moves every offset a signature covers.
    /// </summary>
    [Fact]
    public void AnEmptyLinkBatch_LeavesTheFileByteIdentical()
    {
        byte[] pdf = OnePage();
        using var output = new MemoryStream();

        int written = PdfAnnotationWriter.AddUriLinks(new MemoryStream(pdf), output,
            Array.Empty<PdfAnnotationWriter.UriLinkRequest>());

        Assert.Equal(0, written);
        Assert.Equal(pdf, output.ToArray());
    }

    /// <summary>The control: one link is written, in an appended revision.</summary>
    [Fact]
    public void AOneLinkBatch_AppendsARevision()
    {
        byte[] pdf = OnePage();
        using var output = new MemoryStream();

        int written = PdfAnnotationWriter.AddUriLinks(new MemoryStream(pdf), output, new[]
        {
            new PdfAnnotationWriter.UriLinkRequest { PageIndex = 0, X2 = 10, Y2 = 10, Uri = "https://x.test" },
        });

        Assert.Equal(1, written);
        Assert.True(output.Length > pdf.Length);
    }

    // ================================================================== layers

    /// <summary>
    /// A document whose one layer (object 5) is at generation 1, and every reference to it says so.
    /// Object 6 is a form XObject to put under a layer.
    /// </summary>
    private static byte[] LayerAtGenerationOne() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 1 R] /D << /ON [5 1 R] >> >> >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>")
        .ObjAtGeneration(5, 1, "<< /Type /OCG /Name (Old) >>")
        .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1]", "")
        .Finish();

    private static string Appended(byte[] original, MemoryStream output)
    {
        byte[] all = output.ToArray();
        return Encoding.Latin1.GetString(all, original.Length, all.Length - original.Length);
    }

    /// <summary>
    /// Creating a layer rewrites the inventory and the visibility lists with every existing group at
    /// its own generation. They were rebuilt as <c>N 0 R</c>, which names no object when the group is
    /// at generation 1, so adding a layer dropped the existing one from the document's layer panel.
    /// </summary>
    [Fact]
    public void CreatingALayer_KeepsAnExistingLayersGeneration()
    {
        byte[] pdf = LayerAtGenerationOne();
        using var output = new MemoryStream();

        int created = PdfOcgManager.CreateLayer(new MemoryStream(pdf), output, "New");

        string appended = Appended(pdf, output);
        Assert.True(created > 0);
        Assert.Contains("5 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("5 0 R", appended, StringComparison.Ordinal);
        Assert.Equal(new[] { "New", "Old" },
            PdfOcgManager.ListLayers(new MemoryStream(output.ToArray())).Select(l => l.Name).OrderBy(n => n));
    }

    /// <summary>Putting an object under that layer names it at its generation too.</summary>
    [Fact]
    public void AssigningToALayer_NamesItAtItsGeneration()
    {
        byte[] pdf = LayerAtGenerationOne();
        using var output = new MemoryStream();

        Assert.True(PdfOcgManager.AssignObjectToLayer(new MemoryStream(pdf), output, 6, 5));

        string appended = Appended(pdf, output);
        Assert.Contains("/OC 5 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"/OC 5 0 R"), appended);
    }

    /// <summary>
    /// Putting a page's content on that layer names it at its generation in the page's
    /// <c>/Properties</c>, which the <c>/OC … BDC</c> resolves through.
    /// </summary>
    [Fact]
    public void AssigningAPagesContentToALayer_NamesItAtItsGeneration()
    {
        byte[] pdf = LayerAtGenerationOne();
        using var output = new MemoryStream();

        Assert.True(PdfOcgManager.AssignPageContentToLayer(new MemoryStream(pdf), output, 0, 5));

        string appended = Appended(pdf, output);
        Assert.Matches(new Regex(@"/Properties\s*<<\s*/\w+ 5 1 R"), appended);
        Assert.DoesNotContain("5 0 R", appended, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layer writers refuse an encrypted document. Assigning an object to a layer wrote it back
    /// with its strings still encrypted and appended in clear, and creating a layer reported
    /// "nothing written" as if the document had no catalog.
    /// </summary>
    [Fact]
    public void TheLayerWritersRefuseAnEncryptedDocument()
    {
        byte[] encrypted = OnePage(encrypted: true, pageExtras: "/Resources << >>");

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOcgManager.CreateLayer(new MemoryStream(encrypted), new MemoryStream(), "L"));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOcgManager.AssignObjectToLayer(new MemoryStream(encrypted), new MemoryStream(), 3, 5));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOcgManager.AssignPageContentToLayer(new MemoryStream(encrypted), new MemoryStream(), 0, 5));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOcgManager.SetLayerVisibility(new MemoryStream(encrypted), new MemoryStream(), "L", false));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOcgManager.RenameLayer(new MemoryStream(encrypted), new MemoryStream(), "L", "M"));
    }
}
