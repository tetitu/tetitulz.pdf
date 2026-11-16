// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfContentFilter"/>: a class of content — text, images, vector graphics — taken out of
/// every page and every form the pages reach, each operation the way that keeps the rest of the
/// content as it was (<see cref="ContentOperationRemoval"/>). The reference renderer's <c>-dFILTERTEXT</c>,
/// <c>-dFILTERIMAGE</c> and <c>-dFILTERVECTOR</c>.
/// </summary>
/// <remarks>
/// The fixture page holds one of everything: text, text in clip mode 7 followed by a fill it clips,
/// an image XObject, an inline image, a filled rectangle, a stroked line, a clip set with <c>W n</c>
/// before a fill, a shading, and a form XObject that holds a filled square and text of its own. Each
/// class's test asserts on the operators the output holds and the control that the others stay.
/// </remarks>
public class ContentFilterTests
{
    private const string PageContent =
        "BT /F1 12 Tf 10 180 Td (Hello) Tj ET\n" +
        "q BT 7 Tr /F1 40 Tf 10 120 Td (Clip) Tj ET 0 0 1 rg 0 100 200 60 re f Q\n" +
        "q 50 0 0 50 10 10 cm /Im1 Do Q\n" +
        "q 20 0 0 20 100 10 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI Q\n" +
        "1 0 0 rg 150 150 40 40 re f\n" +
        "0 G 4 w 10 10 m 190 190 l S\n" +
        "q 0 0 100 100 re W n 0 1 0 rg 0 0 200 200 re f Q\n" +
        "q 0 100 60 60 re W n /Sh1 sh Q\n" +
        "/Fx1 Do\n";
    private const string FormContent = "0 0 1 rg 60 60 20 20 re f BT /F1 8 Tf 60 90 Td (Form) Tj ET";

    // ── text ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_TakesEveryShowOut_InThePageAndTheForm_KeepingAClippingOne()
    {
        (byte[] pdf, ContentFilterResult result) = Filter(PdfContentClass.Text);
        List<string> page = Operators(PageOps(pdf));
        List<string> form = Operators(FormOps(pdf));

        // Three shows: Hello and Form dropped; Clip kept in mode 7 so the fill under it is still clipped.
        Assert.Equal(1, page.Count(o => o == PdfOperators.ShowText));
        Assert.DoesNotContain(PdfOperators.ShowText, form);
        int clip = page.IndexOf(PdfOperators.ShowText);
        Assert.Equal(PdfOperators.SetTextRenderMode, page[clip - 1]);
        Assert.Equal(PdfOperators.SetTextRenderMode, page[clip + 1]);
        Assert.Equal(3, result.OperationsRemoved);
        Assert.Equal(2, result.StreamsRewritten);

        // The rest stays: the image, the inline image, the paths, the shading, the form.
        Assert.Equal(2, page.Count(o => o == PdfOperators.InvokeXObject));
        Assert.Contains(PdfOperators.BeginInlineImage, page);
        Assert.Equal(3, page.Count(o => o == PdfOperators.Fill));
        Assert.Contains(PdfOperators.Stroke, page);
        Assert.Contains(PdfOperators.ShadingFill, page);
        Assert.Contains(PdfOperators.Fill, form);

        // And the document reads as the clipping text alone — kept as text, since it still clips —
        // where the input read "Hello" too.
        Assert.Equal("Clip", ExtractText(pdf).Trim());
        Assert.Contains("Hello", ExtractText(Fixture()));
    }

    // ── images ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Images_TakesTheImageAndTheInlineImageOut_AndKeepsTheForm()
    {
        (byte[] pdf, ContentFilterResult result) = Filter(PdfContentClass.Images);
        List<ContentStreamOperation> ops = PageOps(pdf);
        List<string> page = Operators(ops);

        Assert.DoesNotContain(PdfOperators.BeginInlineImage, page);
        var invoked = ops.Where(o => o.Operator == PdfOperators.InvokeXObject).Select(o => ((PdfName)o.Operands[0]).Value).ToList();
        Assert.Equal(new[] { "Fx1" }, invoked);
        Assert.Equal(2, result.OperationsRemoved);
        Assert.Equal(1, result.StreamsRewritten);   // the form holds no image and is left as it is

        Assert.Equal(2, page.Count(o => o == PdfOperators.ShowText));
        Assert.Equal(3, page.Count(o => o == PdfOperators.Fill));
        Assert.Contains(PdfOperators.ShadingFill, page);
        Assert.Contains(PdfOperators.Fill, Operators(FormOps(pdf)));
    }

    // ── vector ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Vector_EndsEveryPathUnpainted_DropsTheShading_AndKeepsTheClip()
    {
        (byte[] pdf, ContentFilterResult result) = Filter(PdfContentClass.Vector);
        List<string> page = Operators(PageOps(pdf));
        List<string> form = Operators(FormOps(pdf));

        Assert.DoesNotContain(PdfOperators.Fill, page);
        Assert.DoesNotContain(PdfOperators.Stroke, page);
        Assert.DoesNotContain(PdfOperators.ShadingFill, page);
        Assert.DoesNotContain(PdfOperators.Fill, form);
        // The two W n clips, then the four paints that became n (three fills and a stroke) in the page; one in the form.
        Assert.Equal(6, page.Count(o => o == PdfOperators.EndPath));
        Assert.Equal(1, form.Count(o => o == PdfOperators.EndPath));
        Assert.Contains(PdfOperators.Clip, page);
        Assert.Equal(6, result.OperationsRemoved);   // 3 fills, a stroke, a shading on the page; the form's fill
        Assert.Equal(2, result.StreamsRewritten);

        Assert.Equal(2, page.Count(o => o == PdfOperators.ShowText));
        Assert.Equal(2, page.Count(o => o == PdfOperators.InvokeXObject));
        Assert.Contains(PdfOperators.BeginInlineImage, page);
    }

    /// <summary>
    /// The output renders: the red square at (170, 30) is white once the vector content is gone and
    /// the text is still drawn — the pixel under "Hello" is dark both before and after.
    /// </summary>
    [Fact]
    public void Vector_RendersWithoutThePaths_AndWithTheText()
    {
        (byte[] filtered, _) = Filter(PdfContentClass.Vector);
        RenderedPage before = PdfOperations.RenderPageToPixels(new MemoryStream(Fixture()), 0, 72);
        RenderedPage after = PdfOperations.RenderPageToPixels(new MemoryStream(filtered), 0, 72);

        Assert.True(IsDark(before, 170, 30), "the red square was not drawn in the input");
        Assert.False(IsDark(after, 170, 30), "the red square survived the vector filter");
        Assert.True(DarkPixels(after, 10, 12, 50, 20) > 0, "the text was taken out with the vector content");
    }

    // ── all, none, shared, encrypted ─────────────────────────────────────────────

    [Fact]
    public void EveryClass_LeavesOnlyStateAndTheForm()
    {
        (byte[] pdf, ContentFilterResult result) = Filter(PdfContentClass.Text | PdfContentClass.Images | PdfContentClass.Vector);
        List<string> page = Operators(PageOps(pdf));

        Assert.Equal(1, page.Count(o => o == PdfOperators.ShowText));   // the clipping one, kept in mode 7
        Assert.DoesNotContain(PdfOperators.BeginInlineImage, page);
        Assert.DoesNotContain(PdfOperators.Fill, page);
        Assert.DoesNotContain(PdfOperators.ShadingFill, page);
        Assert.Equal(new[] { "Fx1" }, PageOps(pdf).Where(o => o.Operator == PdfOperators.InvokeXObject).Select(o => ((PdfName)o.Operands[0]).Value));
        Assert.Equal(11, result.OperationsRemoved);
    }

    /// <summary>Nothing asked for, or nothing of the class present: the input is copied through, byte for byte.</summary>
    [Theory]
    [InlineData(PdfContentClass.None)]
    [InlineData(PdfContentClass.Images)]
    public void NothingToTakeOut_CopiesTheInputThrough(PdfContentClass classes)
    {
        byte[] input = classes == PdfContentClass.None ? Fixture() : Fixture(content: "0 0 1 rg 0 0 50 50 re f", form: false);
        using var output = new MemoryStream();
        ContentFilterResult result = PdfContentFilter.Remove(new MemoryStream(input), output, classes);

        Assert.Equal(input, output.ToArray());
        Assert.Equal(0, result.StreamsRewritten);
        Assert.Equal(0, result.OperationsRemoved);
    }

    /// <summary>A content stream two pages share is rewritten once, under its own object number.</summary>
    [Fact]
    public void ASharedContentStream_IsRewrittenOnce()
    {
        byte[] input = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>")
            .Obj(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>")
            .StreamObj(5, "<<", "1 0 0 rg 0 0 50 50 re f")
            .Finish();
        using var output = new MemoryStream();
        ContentFilterResult result = PdfContentFilter.Remove(new MemoryStream(input), output, PdfContentClass.Vector);

        Assert.Equal(1, result.StreamsRewritten);
        Assert.Equal(1, result.OperationsRemoved);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        foreach (int p in new[] { 0, 1 })
            Assert.Equal(new[] { PdfOperators.SetRgbFill, PdfOperators.Rectangle, PdfOperators.EndPath },
                Operators(ContentStreamParser.Parse(doc.Pages[p].GetContentStreamData())));
    }

    /// <summary>
    /// A content stream that will not decode is left as it is and named — on the result and on the
    /// thread's slot, which the next call clears again.
    /// </summary>
    [Fact]
    public void AnUnreadableStream_IsLeftAsItIs_AndNamed()
    {
        byte[] input = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>")
            .StreamObj(4, "<< /Filter /FlateDecode", "this is not deflate data at all, so the filter cannot read it")
            .Finish();
        using var output = new MemoryStream();
        ContentFilterResult result = PdfContentFilter.Remove(new MemoryStream(input), output, PdfContentClass.Vector);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("page 1's content", warning);
        Assert.Contains("could not be decoded", warning);
        Assert.Equal(warning + ".", PdfContentFilter.LastFilterWarning);
        Assert.Equal(0, result.StreamsRewritten);
        Assert.Equal(input, output.ToArray());

        using var clean = new MemoryStream();
        PdfContentFilter.Remove(new MemoryStream(Fixture()), clean, PdfContentClass.Vector);
        Assert.Null(PdfContentFilter.LastFilterWarning);
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused()
    {
        using var output = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfContentFilter.Remove(new MemoryStream(Fixture(encrypted: true)), output, PdfContentClass.Text));
        Assert.Equal(0, output.Length);
    }

    /// <summary>The facade reaches the writer with the same result.</summary>
    [Fact]
    public void TheFacade_RemovesContent()
    {
        using var output = new MemoryStream();
        ContentFilterResult result = PdfOperations.RemoveContent(new MemoryStream(Fixture()), output, PdfContentClass.Images);
        Assert.Equal(2, result.OperationsRemoved);
        Assert.DoesNotContain(PdfOperators.BeginInlineImage, Operators(PageOps(output.ToArray())));
    }

    // ---------------------------------------------------------------- helpers

    private const string EncryptTrailer =
        " /Encrypt 9 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]";

    private const string EncryptDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    /// <summary>
    /// Object 3 is the page, 4 its content, 5 the image, 6 the form, 7 the shading, 8 the font.
    /// </summary>
    private static byte[] Fixture(bool encrypted = false, string content = PageContent, bool form = true)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                    "/Resources << /Font << /F1 8 0 R >> /XObject << /Im1 5 0 R " + (form ? "/Fx1 6 0 R " : "") + ">> /Shading << /Sh1 7 0 R >> >> >>")
            .StreamObj(4, "<<", content)
            .Obj(5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>",
                 new byte[] { 0, 0, 255 })
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Font << /F1 8 0 R >> >>", FormContent)
            .Obj(7, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 1 1] /N 1 >> /Extend [true true] >>")
            .Obj(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        if (!encrypted) return b.Finish();
        return b.Obj(9, EncryptDictionary).Finish(EncryptTrailer);
    }

    private static (byte[] Pdf, ContentFilterResult Result) Filter(PdfContentClass classes)
    {
        using var output = new MemoryStream();
        ContentFilterResult result = PdfContentFilter.Remove(new MemoryStream(Fixture()), output, classes);
        return (output.ToArray(), result);
    }

    private static List<ContentStreamOperation> PageOps(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    private static List<ContentStreamOperation> FormOps(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var xobjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var form = (PdfStream)doc.ResolveObject(xobjects[new PdfName("Fx1")]);
        return ContentStreamParser.Parse(form.GetDecodedData());
    }

    private static List<string> Operators(List<ContentStreamOperation> ops) => ops.Select(o => o.Operator).ToList();

    private static string ExtractText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return PdfTextExtractor.ExtractText(doc);
    }

    private static bool IsDark(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return page.Pixels[i] + page.Pixels[i + 1] + page.Pixels[i + 2] < 300;
    }

    private static int DarkPixels(RenderedPage page, int x0, int y0, int width, int height)
    {
        int n = 0;
        for (int y = y0; y < y0 + height; y++)
            for (int x = x0; x < x0 + width; x++)
                if (IsDark(page, x, y)) n++;
        return n;
    }

    /// <summary>
    /// <b>Adversarial: twenty-five pages whose content will not decode.</b> Each is a warning naming
    /// its page, all of them in the result; <see cref="PdfContentFilter.LastFilterWarning"/> names
    /// the first twenty and counts the rest — joined whole, a file chose its length.
    /// </summary>
    [Fact]
    public void AdversarialManyUndecodableStreams_AreEachWarned_AndTheSlotIsCapped()
    {
        const int pages = 25;
        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(3 + 2 * p).Append(" 0 R ");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 100 100] >>");
        for (int p = 0; p < pages; p++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {4 + 2 * p} 0 R >>");
            bodies.Add("<< /Filter /FlateDecode /Length 40 >>\nstream\nthis is not deflate data at all, no sir.\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var result = PdfContentFilter.Remove(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())), new MemoryStream(), PdfContentClass.Text);

        Assert.Equal(pages, result.Warnings.Count);
        Assert.Equal(pages, result.Warnings.Distinct().Count());
        Assert.EndsWith("; and 5 more.", PdfContentFilter.LastFilterWarning, System.StringComparison.Ordinal);
        Assert.Contains("page 20's content", PdfContentFilter.LastFilterWarning, System.StringComparison.Ordinal);
        Assert.DoesNotContain("page 21's content", PdfContentFilter.LastFilterWarning, System.StringComparison.Ordinal);
    }
}
