// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §12.7.5.5 (ISO 32000-2, lines 45287–45290 of <c>docs/iso/iso32000-2.txt</c>): "PDF processors
/// shall also treat signatures as not visible if either the Hidden bit or the NoView bit of the F
/// entry is true." Table 167's NoView (38668–38671) lets an ordinary annotation be printed; the
/// signature rule has no such clause, and the specific rule wins for the field it names.
/// </summary>
/// <remarks>
/// <para>Both renderers applied only the general §12.5.3 rule, so a signature widget flagged
/// <c>NoView | Print</c> was printed. The decision is now one implementation,
/// <c>AnnotationVisibility.IsHidden</c>, reached from both renderers; the field type is inherited
/// through <c>/Parent</c> (Table 220) by the climb every other widget read already uses.</para>
///
/// <para>Each fixture's widget carries an appearance that paints blue, so "printed" is a decoded
/// pixel and "not printed" a blank page — and the text-field and Print-only controls prove the
/// fixture can paint at all.</para>
/// </remarks>
public class SignatureNoViewPrintTests
{
    private const int NoViewPrint = 32 | 4;
    private const int PrintOnly = 4;

    private static readonly PdfRenderOptions Print = new() { Intent = RenderIntent.Print };
    private static readonly PdfRenderOptions Screen = new() { Intent = RenderIntent.Screen };

    // ------------------------------------------------------------------ raster

    [Fact]
    public void Raster_ANoViewSignatureWidgetIsNotPrinted()
        => Assert.True(PixelAssert.IsBlank(Render(Signature(NoViewPrint), Print)));

    [Fact]
    public void Raster_ANoViewSignatureWidgetIsNotShownOnScreenEither()
        => Assert.True(PixelAssert.IsBlank(Render(Signature(NoViewPrint), Screen)));

    [Fact]
    public void Raster_TheFieldTypeIsInheritedThroughParent()
        => Assert.True(PixelAssert.IsBlank(Render(SignatureViaParent(NoViewPrint), Print)));

    /// <summary>The control: the general rule still prints a NoView text field.</summary>
    [Fact]
    public void Raster_ANoViewTextWidgetStillPrints()
        => Assert.False(PixelAssert.IsBlank(Render(TextField(NoViewPrint), Print)));

    /// <summary>The fixture control: a signature widget flagged Print alone does print.</summary>
    [Fact]
    public void Raster_APrintOnlySignatureWidgetPrints()
        => Assert.False(PixelAssert.IsBlank(Render(Signature(PrintOnly), Print)));

    // ------------------------------------------------------------------ SVG

    [Fact]
    public void Svg_ANoViewSignatureWidgetIsNotExportedForPrint()
        => Assert.False(HasAnnotationContent(Signature(NoViewPrint), Print));

    [Fact]
    public void Svg_TheFieldTypeIsInheritedThroughParent()
        => Assert.False(HasAnnotationContent(SignatureViaParent(NoViewPrint), Print));

    [Fact]
    public void Svg_ANoViewTextWidgetIsStillExportedForPrint()
        => Assert.True(HasAnnotationContent(TextField(NoViewPrint), Print));

    [Fact]
    public void Svg_APrintOnlySignatureWidgetIsExportedForPrint()
        => Assert.True(HasAnnotationContent(Signature(PrintOnly), Print));

    // ------------------------------------------------------------------ helpers

    private static SKBitmap Render(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        SKBitmap? bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, PixelAssert.Dpi, options));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Whether the export's annotation layer holds any element at all — parsed, not substring-matched.</summary>
    private static bool HasAnnotationContent(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        XDocument doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0, options));
        XElement layer = Assert.Single(doc.Descendants().Where(
            e => e.Attribute("data-layer")?.Value == "annotations"));
        return layer.Elements().Any();
    }

    private static byte[] Signature(int flags) => Page("/FT /Sig", flags);

    private static byte[] TextField(int flags) => Page("/FT /Tx", flags);

    /// <summary>The widget carries no <c>/FT</c>; its parent field is the signature.</summary>
    private static byte[] SignatureViaParent(int flags) => Page("/Parent 7 0 R", flags,
        parent: "<< /FT /Sig /T (parent) /Kids [5 0 R] >>");

    /// <summary>
    /// A 200×200 page with one widget over <c>/Rect [50 50 150 100]</c> whose appearance fills it
    /// blue, flagged <paramref name="flags"/>.
    /// </summary>
    private static byte[] Page(string fieldEntry, int flags, string? parent = null)
    {
        const string ap = "0 0 1 rg 0 0 100 50 re f";
        string fieldsRoot = parent != null ? "7 0 R" : "5 0 R";
        var objects = new System.Collections.Generic.List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{fieldsRoot}] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            $"<< /Type /Annot /Subtype /Widget {fieldEntry} /T (sig) /F {flags}" +
            " /Rect [50 50 150 100] /AP << /N 6 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 50]" +
            $" /Length {ap.Length} >>\nstream\n{ap}\nendstream",
        };
        if (parent != null) objects.Add(parent);
        return Build(objects.ToArray());
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
