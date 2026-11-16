// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>/Subtype</c> is the single most-repeated instance of this defect class in the tree: it was
/// read without following an indirect reference at <b>fourteen</b> sites across ten files, and at
/// every one of them the null <c>continue</c>s or returns, so the object is dropped.
/// </summary>
/// <remarks>
/// <para><b>Why it concentrates on this key.</b> <c>/Subtype</c> is a gate — it is read to decide
/// whether an object is the kind of thing this code processes — so the read is almost always the
/// first line of a loop body, written before anyone is thinking about resolution. And the failure
/// is always the same shape and always silent: an image not exported, a form's text not extracted,
/// an attachment not listed, a font classified as something else.</para>
///
/// <para><b>An absent object is the correct answer for most documents</b>, which is what makes the
/// whole class expensive: a caller cannot tell "this PDF has no attachments" from "I could not read
/// the annotation that says so".</para>
///
/// <para>The register named ten of the fourteen. The other four were siblings in the same files —
/// <c>PdfRichMediaExtractor</c> reads it twice, <c>PdfColorSpaceConverter</c> twice,
/// <c>PdfImageReplacer</c> twice and <c>PdfAttachmentManager</c> three times. <b>Fix the family,
/// not the row.</b></para>
///
/// <para>Each case here asserts on the object the code produced. "The call returned a list" is
/// satisfied by the empty list every one of these defects produces.</para>
///
/// <para><b>Coverage is 4 sites of the 14, and saying so is the point.</b> Mutation-verified here:
/// <c>PdfTextExtractor</c> (form text), <c>PdfImageReplacer</c> (image discovery),
/// <c>PdfOperations.FacadeCompleteness</c> (font subtype) and <c>PdfAttachmentManager</c>
/// (attachment annotation). <b>Not covered</b>, and therefore fixed on the strength of the shared
/// shape alone: <c>PdfTemplateEngine</c>, <c>PdfRichMediaExtractor</c> (×2),
/// <c>PdfStructuredTextExtractor</c>, <c>PdfImageExporter</c>, <c>PdfImageReplacer</c>'s second
/// site, <c>PdfColorSpaceConverter</c> (×2), <c>PdfAttachmentManager</c>'s two MIME reads, and
/// <c>Core.Fonts.PdfFont</c>.</para>
///
/// <para>That list is written down rather than left implicit because a family fixed together
/// otherwise gets <i>believed</i> together — the trap this suite hit two commits earlier, where a
/// mutation of an uncovered family member survived and only then revealed that its fixture had
/// never contained the indirect spelling. Adding a case per site is the fix; until then this
/// paragraph is the honest statement of what is proven.</para>
/// </remarks>
public class IndirectSubtypeReadTests
{
    /// <summary>
    /// Text inside a form XObject is extracted whichever way the form's <c>/Subtype</c> is spelled.
    /// <c>false</c> is the control.
    /// </summary>
    /// <remarks>
    /// <c>PdfTextExtractor</c> resolves the <c>/XObject</c> resource, the named entry, the stream
    /// and its <c>/Resources</c> — the <c>/Subtype</c> gate between them was the one raw read, and
    /// it returns before <c>LastFormXObjectError</c> is written, so the diagnostic cannot tell
    /// "the form had no text" from "the form was never read".
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Subtype /Form
    [InlineData(true)]   // /Subtype 9 0 R
    public void FormXObjectTextIsExtracted_WhicheverWaySubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(FormTextPdf(indirect));
        string text = PdfOperations.ExtractText(ms);

        Assert.Contains("InsideTheForm", text.Replace(" ", ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// An image XObject is exported whichever way its <c>/Subtype</c> is spelled.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Subtype /Image
    [InlineData(true)]   // /Subtype 9 0 R
    public void TheImageIsFound_WhicheverWaySubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(ImagePdf(indirect));
        IReadOnlyList<DocumentImageInfo> images = PdfImageReplacer.FindImages(ms);

        var image = Assert.Single(images);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
    }

    /// <summary>
    /// An image whose <c>/Width</c>, <c>/Height</c> and <c>/BitsPerComponent</c> are their own
    /// objects is listed, exported and replaced with its real geometry.
    /// </summary>
    /// <remarks>
    /// The same defect one key over from <c>/Subtype</c>: each of the three read the numbers with
    /// <c>GetDouble</c>, which does not follow a reference. <c>FindImages</c> listed the image as
    /// 0x0, <c>ExtractImage</c> refused a raw image as "dimensions out of range: 0x0", and
    /// <c>Replace</c> sized its fit against a 0x0 original. Each is asserted, since they are three
    /// separate reads.
    /// </remarks>
    [Fact]
    public void AnImageWithIndirectGeometry_IsListedExportedAndReplacedAtItsRealSize()
    {
        byte[] pdf = ImagePdf(indirect: false, indirectGeometry: true);

        using (var ms = new MemoryStream(pdf))
        {
            var listed = Assert.Single(PdfImageReplacer.FindImages(ms));
            Assert.Equal(2, listed.Width);
            Assert.Equal(2, listed.Height);
        }

        using (var ms = new MemoryStream(pdf))
        {
            var exported = PdfImageExporter.ExtractImage(ms, 0, 0);
            Assert.Equal(2, exported.Width);
            using var bitmap = SkiaSharp.SKBitmap.Decode(exported.Data);
            Assert.Equal(new SkiaSharp.SKColor(255, 0, 0), bitmap.GetPixel(0, 0));
        }

        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            var result = PdfImageReplacer.Replace(input, output, 0, 0,
                ReplacementImage.Rgb24(new byte[] { 0, 0, 255 }, 1, 1), null);
            Assert.Equal(2, result.OriginalWidth);
            Assert.Equal(2, result.OriginalHeight);
        }
    }

    /// <summary>
    /// An indirect <c>/BitsPerComponent</c> of 4 is read as 4, not as 8.
    /// </summary>
    /// <remarks>
    /// The raw read's fallback is 8, so for an 8-bit image resolving changes nothing. For a 4-bit one
    /// it is the difference between 4-bit samples and the same bytes read as 8-bit pixels: the first
    /// row's bytes <c>ff 00 00</c> are nibbles <c>f f 0</c>, a yellow pixel, where 8 bits read red.
    /// This asserted a refusal while the exporter supported 8 bits alone; it decodes through the
    /// renderer now, at every depth.
    /// </remarks>
    [Fact]
    public void AnIndirectFourBitDepth_IsReadAsFourNotEight()
    {
        byte[] pdf = ImagePdf(indirect: false, indirectGeometry: true, bitsPerComponentObject: "4");

        var img = PdfImageExporter.ExtractImage(new MemoryStream(pdf), 0, 0);
        using var bitmap = SkiaSharp.SKBitmap.Decode(img.Data);

        Assert.Equal(new SkiaSharp.SKColor(255, 255, 0), bitmap.GetPixel(0, 0));
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 255), bitmap.GetPixel(0, 1));
    }

    /// <summary>
    /// The floor: a form XObject is not an image, so <c>FindImages</c> must not report one — the
    /// assertion above cannot be met by a finder that reports every XObject.
    /// </summary>
    [Fact]
    public void AFormXObject_IsNotReportedAsAnImage()
    {
        using var ms = new MemoryStream(ImagePdf(indirect: false, subtype: "/Form"));
        IReadOnlyList<DocumentImageInfo> images = PdfImageReplacer.FindImages(ms);

        Assert.Empty(images);
    }

    /// <summary>
    /// A font's subtype is reported whichever way it is spelled — the facade's font listing is the
    /// public surface where <c>PdfOperations.FacadeCompleteness</c>'s read shows up directly.
    /// </summary>
    /// <remarks>
    /// This is the one site whose null is <b>not</b> a dropped object: it substitutes the literal
    /// string <c>"Unknown"</c>. That is arguably worse than dropping it, because the caller receives
    /// a confident answer about a font whose type the file states plainly.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Subtype /Type1
    [InlineData(true)]   // /Subtype 9 0 R
    public void TheFontSubtypeIsReported_WhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(FontPdf(indirect));
        IReadOnlyList<PdfFontSummary> fonts = PdfOperations.ListFonts(ms);

        var font = Assert.Single(fonts);
        Assert.Equal("Type1", font.Subtype);
    }

    /// <summary>
    /// The font's name too: the listing resolved <c>/Subtype</c> one line above a raw
    /// <c>/BaseFont</c>, and reported an indirect name as the empty string.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /BaseFont /Helvetica
    [InlineData(true)]   // /BaseFont 10 0 R
    public void TheFontNameIsReported_WhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(FontPdf(indirect: false, indirectBaseFont: indirect));
        IReadOnlyList<PdfFontSummary> fonts = PdfOperations.ListFonts(ms);

        Assert.Equal("Helvetica", Assert.Single(fonts).BaseFont);
    }

    /// <summary>
    /// A file-attachment annotation is listed whichever way its <c>/Subtype</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <c>PdfAttachmentManager</c> read <c>/Subtype</c> three times — the annotation gate here, and
    /// twice more for the MIME type. All three were raw; the register named one.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Subtype /FileAttachment
    [InlineData(true)]   // /Subtype 9 0 R
    public void TheAttachmentAnnotationIsListed_WhicheverWaySubtypeIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(AttachmentPdf(indirect));
        IReadOnlyList<PdfAttachmentInfo> attachments = PdfOperations.ListAttachments(ms);

        var attachment = Assert.Single(attachments);
        Assert.Equal("note.txt", attachment.Filename);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A page drawing a form XObject that shows text. Object 9 holds the form's <c>/Subtype</c>
    /// when <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] FormTextPdf(bool indirect)
    {
        string subtype = indirect ? "9 0 R" : "/Form";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", "/Fx1 Do\n"),
            Stream("/Type /XObject /Subtype " + subtype + " /BBox [0 0 200 200]"
                   + " /Resources << /Font << /F1 6 0 R >> >>",
                   "BT /F1 12 Tf 20 100 Td (InsideTheForm) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "null", "null",
            "/Form");
    }

    /// <summary>
    /// A page drawing a 2x2 image XObject. Object 9 holds its <c>/Subtype</c> when
    /// <paramref name="indirect"/>.
    /// </summary>
    private static byte[] ImagePdf(bool indirect, string subtype = "/Image", bool indirectGeometry = false,
                                   string bitsPerComponentObject = "8")
    {
        string subtypeEntry = indirect ? "9 0 R" : subtype;
        string geometry = indirectGeometry
            ? " /Width 6 0 R /Height 7 0 R /ColorSpace /DeviceRGB /BitsPerComponent 8 0 R"
            : " /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q\n"),
            Stream("/Type /XObject /Subtype " + subtypeEntry + geometry + " /BBox [0 0 2 2]"
                   + " /Filter /ASCIIHexDecode",
                   "ff0000 00ff00 0000ff ffffff>"),
            "2", "2", bitsPerComponentObject,
            subtype);
    }

    /// <summary>
    /// A page whose only resource is one Type 1 font. Object 9 holds its <c>/Subtype</c> when
    /// <paramref name="indirect"/>.
    /// </summary>
    private static byte[] FontPdf(bool indirect, bool indirectBaseFont = false)
    {
        string subtype = indirect ? "9 0 R" : "/Type1";
        string baseFont = indirectBaseFont ? "10 0 R" : "/Helvetica";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 12 Tf 20 100 Td (x) Tj ET\n"),
            "<< /Type /Font /Subtype " + subtype + " /BaseFont " + baseFont + " >>",
            "null", "null", "null",
            "/Type1",
            "/Helvetica");
    }

    /// <summary>
    /// A page carrying one file-attachment annotation. Object 9 holds the annotation's
    /// <c>/Subtype</c> when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] AttachmentPdf(bool indirect)
    {
        string subtype = indirect ? "9 0 R" : "/FileAttachment";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Annots [5 0 R] >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype " + subtype + " /Rect [10 10 30 30] /FS 6 0 R >>",
            "<< /Type /Filespec /F (note.txt) /UF (note.txt) /EF << /F 7 0 R >> >>",
            Stream("/Type /EmbeddedFile /Subtype /text#2Fplain", "hello"),
            "null",
            "/FileAttachment");
    }

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
