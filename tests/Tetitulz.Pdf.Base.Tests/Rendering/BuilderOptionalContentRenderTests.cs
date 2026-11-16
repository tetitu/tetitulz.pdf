// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Content written with <see cref="PdfContentStreamBuilder.BeginOptionalContent"/> is really
/// governed by its layer — rendered when the layer is on and absent when it is off.
/// </summary>
/// <remarks>
/// <para><b>Both halves already worked and the join did not.</b> The renderer's optional-content
/// handling is pinned by <c>RenderingSpecFixTests</c> against hand-built PDFs, and the builder's
/// bytes are pinned by <c>ContentStreamBuilderOptionalContentTests</c>. Neither can see whether the
/// builder emits the thing the renderer looks for: <c>/OC /OC0 BDC</c> is well-formed with or
/// without the matching <c>/Properties</c> entry, and a reader that cannot resolve the name shows
/// the content unconditionally. So a broken builder produces a page that renders — just always.</para>
///
/// <para><b>The visible case is the load-bearing one.</b> "Nothing rendered" is what a page with no
/// content, a failed parse or a wrongly-hidden layer all look like, so hiding alone proves very
/// little. The pair does: the same bytes must paint with the layer on and not with it off.</para>
/// </remarks>
public sealed class BuilderOptionalContentRenderTests
{
    private const int Dpi = 72;

    [Fact]
    public void ContentOnAVisibleLayerIsRendered()
        => Assert.True(Ink(Render(BuiltPdf(layerOff: false))) > 0,
                       "content on a visible layer did not paint");

    [Fact]
    public void ContentOnAHiddenLayerIsNotRendered()
        => Assert.Equal(0, Ink(Render(BuiltPdf(layerOff: true))));

    /// <summary>
    /// Two layers, one off: only the visible one paints, and the page is not simply blank.
    /// </summary>
    /// <remarks>
    /// <b>The test that separates "the layer worked" from "the whole page was suppressed".</b> With
    /// one layer, hiding it and hiding everything look identical. Here the red square is on the
    /// hidden layer and the blue one is not, so the assertion is about <em>which</em> ink survived
    /// rather than how much.
    /// </remarks>
    [Fact]
    public void OnlyTheHiddenLayersContentDisappears()
    {
        SKBitmap bitmap = Render(TwoLayerPdf());

        // The two squares are drawn at different x, so their pixels are distinguishable.
        Assert.Equal(0, CountColour(bitmap, red: true));
        Assert.True(CountColour(bitmap, red: false) > 0, "the visible layer's square did not paint");
    }

    // ================================================================
    //  Fixtures — the content really is produced by the builder
    // ================================================================

    /// <summary>
    /// A page whose content stream and resources come from <see cref="PdfContentStreamBuilder"/>,
    /// with an OCG that is on or off.
    /// </summary>
    private static byte[] BuiltPdf(bool layerOff)
    {
        var builder = new PdfContentStreamBuilder();

        // Object 7 is the OCG, allocated below. The builder is handed the reference exactly as a
        // caller holding a PdfOcgManager-created layer would hand it one.
        builder.BeginOptionalContent(new PdfReference(7, 0));
        builder.SetFillColorRgb(1f, 0f, 0f);
        builder.Rectangle(50, 600, 200, 100);
        builder.Fill();
        builder.EndMarkedContent();

        return Assemble(builder, new[] { new PdfReference(7, 0) }, layerOff ? "/OFF [7 0 R]" : "");
    }

    /// <summary>Two OCGs — object 7 (hidden) and object 9 (visible) — one square each.</summary>
    private static byte[] TwoLayerPdf()
    {
        var builder = new PdfContentStreamBuilder();

        builder.BeginOptionalContent(new PdfReference(7, 0));
        builder.SetFillColorRgb(1f, 0f, 0f);
        builder.Rectangle(50, 600, 100, 100);
        builder.Fill();
        builder.EndMarkedContent();

        builder.BeginOptionalContent(new PdfReference(9, 0));
        builder.SetFillColorRgb(0f, 0f, 1f);
        builder.Rectangle(300, 600, 100, 100);
        builder.Fill();
        builder.EndMarkedContent();

        return Assemble(builder,
                        new[] { new PdfReference(7, 0), new PdfReference(9, 0) },
                        "/OFF [7 0 R]");
    }

    /// <summary>
    /// Wraps the builder's content and resources in a one-page document with an
    /// <c>/OCProperties</c> naming every group.
    /// </summary>
    /// <remarks>
    /// The object numbers are fixed so the fixtures can name the OCGs before they are written:
    /// 1 catalog, 2 pages, 3 page, 4 content, 5 unused, 6 <c>/OCProperties</c>, 7 and 9 the groups.
    /// The builder's own side objects would start at 10; these fixtures use none.
    /// </remarks>
    private static byte[] Assemble(
        PdfContentStreamBuilder builder, PdfReference[] groups, string config)
    {
        int next = 10;
        (_, PdfDictionary resources, _) =
            builder.BuildObjects(ref next, compress: false, embeddedFontObjNums: null);

        string content = Encoding.ASCII.GetString(builder.Build(compress: false));
        string resourceText = Serialize(resources);

        var ocgs = new StringBuilder();
        foreach (PdfReference group in groups)
            ocgs.Append(group.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");

        var objects = new List<string>
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /OCProperties 6 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                    + " /Resources " + resourceText + " >>",
            /* 4 */ "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>\nstream\n" + content + "\nendstream",
            /* 5 */ "<< >>",
            /* 6 */ "<< /OCGs [" + ocgs.ToString().TrimEnd() + "] /D << " + config + " >> >>",
            /* 7 */ "<< /Type /OCG /Name (Layer one) >>",
            /* 8 */ "<< >>",
            /* 9 */ "<< /Type /OCG /Name (Layer two) >>",
        };

        return Build(objects);
    }

    /// <summary>
    /// A resources dictionary as PDF syntax. Only the categories these fixtures produce are
    /// handled — <c>/Properties</c>, whose values are references.
    /// </summary>
    private static string Serialize(PdfDictionary resources)
    {
        var properties = resources.Get(PdfName.Properties) as PdfDictionary;
        if (properties == null) return "<< >>";

        var sb = new StringBuilder("<< /Properties << ");
        foreach (PdfName key in properties.Keys)
        {
            var reference = (PdfReference)properties.Get(key);
            sb.Append('/').Append(key.Value).Append(' ')
              .Append(reference.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(reference.ReferenceGenerationNumber.ToString(CultureInfo.InvariantCulture))
              .Append(" R ");
        }
        return sb.Append(">> >>").ToString();
    }

    private static byte[] Build(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        foreach (string body in objects)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ================================================================
    //  Pixels
    // ================================================================

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        return bitmap!;
    }

    /// <summary>Pixels that are neither white nor transparent.</summary>
    private static int Ink(SKBitmap bitmap)
    {
        int n = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    /// <summary>Pixels that are predominantly red, or predominantly blue.</summary>
    private static int CountColour(SKBitmap bitmap, bool red)
    {
        int n = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Alpha <= 8) continue;
                if (red ? c.Red > 200 && c.Blue < 80 : c.Blue > 200 && c.Red < 80) n++;
            }
        return n;
    }
}
