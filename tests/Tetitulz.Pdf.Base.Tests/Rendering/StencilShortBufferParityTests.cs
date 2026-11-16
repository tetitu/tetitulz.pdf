// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
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
/// An <c>/ImageMask</c> whose packed buffer is shorter than its declared geometry requires is
/// refused by the SVG exporter with the <b>same diagnostic kind the raster path uses</b>, and one
/// that is merely padded is honoured by both.
/// </summary>
/// <remarks>
/// <para><b>The defect, stated accurately.</b> <c>BuildImageMaskDataUri</c> bounded the stencil's
/// <i>dimensions</i> against <c>PdfLimits</c> and then indexed <c>raw[bitIdx / 8]</c> with no test
/// of <c>raw.Length</c>. Both raster callers of <c>DecodePackedBilevel</c> check the packed length
/// first, and for a concrete reason: <c>CcittFaxDecodeFilter</c> and <c>Jbig2DecodeFilter</c> report
/// failure by returning their <b>compressed input unchanged</b>, so a stencil whose filter failed
/// arrives far shorter than its geometry claims.</para>
///
/// <para><b>It was not a crash, and the first version of this test wrongly implied it was.</b>
/// Measured with the guard removed: the <c>IndexOutOfRangeException</c> is caught by the SVG
/// writer's per-operator fault isolation and surfaces as
/// <c>ContentStreamError: SVG: operator 'Do' threw and was skipped</c>. So the cost is
/// <i>attribution</i>, and it is a real parity defect: the raster path refuses the same file as
/// <c>ImageDecodeFailed</c> naming the reason, while SVG reported a generic operator throw. A host
/// filtering on <c>ImageDecodeFailed</c> — the kind every other image refusal in both renderers
/// uses — saw the raster refusal and missed the SVG one entirely.</para>
///
/// <para><b>Which is why these assert the diagnostic, not the absence of an image.</b> "The export
/// contains no image" is true under <i>both</i> behaviours, so the first version of this test
/// survived the mutation that deletes the guard. The claim that separates them is the kind and its
/// numbers, and the numbers are <b>derived from the truncation</b> (required versus actual) rather
/// than written alongside it, so removing the cap cannot leave the message still firing.</para>
///
/// <para><b>Why the padded case is here.</b> The guard is <c>&gt;=</c>, not <c>==</c>, and that is
/// not a detail: an <c>/EncodedByteAlign</c> CCITT stencil pads every row to a byte boundary and
/// routinely carries trailing bytes past the last one. A guard written as <c>!=</c> passes the
/// truncation case and silently deletes those masks from the export.</para>
/// </remarks>
public sealed class StencilShortBufferParityTests
{
    private const int MaskWidth = 16;
    private const int MaskHeight = 16;

    /// <summary>(16 + 7) / 8 * 16 — the packed length a complete 16x16 stencil has.</summary>
    private const int RequiredBytes = 32;

    [Fact]
    public void TruncatedStencilBuffer_IsRefusedAsAnImageDecodeFailure_NotAsAnOperatorThrow()
    {
        // Four bytes where thirty-two are needed: the shape a failed CCITT/JBIG2 filter produces
        // when it passes its compressed input straight through.
        byte[] pdf = StencilPdf(HexZeros(4));

        var (hrefs, diagnostics) = ExportSvg(pdf);

        Assert.Empty(hrefs);

        string refusal = Assert.Single(
            diagnostics.Where(d => d.Kind == RenderDiagnosticKind.ImageDecodeFailed)).Detail;
        // Both numbers come from the comparison itself, so a guard that stopped comparing could
        // not still produce them.
        Assert.Contains(RequiredBytes.ToString(CultureInfo.InvariantCulture), refusal);
        Assert.Contains("has 4", refusal);

        // The mutation killer: with the guard deleted the loop throws and the writer reports
        // ContentStreamError instead, which is what "no image was emitted" alone cannot tell apart.
        Assert.DoesNotContain(diagnostics, d => d.Kind == RenderDiagnosticKind.ContentStreamError);

        // Parity: the raster path refuses the same file, so neither renderer paints it.
        using var raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(raster, 200f, 500f)),
            "the raster path painted a stencil whose packed buffer is too short for its geometry");
    }

    [Fact]
    public void StencilBufferOfExactlyTheRequiredLength_IsHonouredByBothRenderers()
    {
        // All bits clear, and the default /Decode [0 1] makes a 0 bit paint (§8.9.6.2), so the
        // whole stencil is inked in the fill colour.
        byte[] pdf = StencilPdf(HexZeros(RequiredBytes));

        var (hrefs, diagnostics) = ExportSvg(pdf);
        Assert.DoesNotContain(diagnostics, d => d.Kind == RenderDiagnosticKind.ImageDecodeFailed);

        using var svg = DecodeOnly(hrefs);
        Assert.Equal(255, svg.GetPixel(0, 0).Alpha);

        using var raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsInk(PixelAssert.At(raster, 200f, 500f)),
            "the raster path dropped a well-formed stencil");
    }

    [Fact]
    public void StencilBufferPaddedPastItsGeometry_IsStillHonouredByBothRenderers()
    {
        // Eight trailing bytes, as /EncodedByteAlign routinely leaves. A `!=` guard would refuse
        // this while still passing the truncation case above.
        byte[] pdf = StencilPdf(HexZeros(RequiredBytes + 8));

        var (hrefs, diagnostics) = ExportSvg(pdf);
        Assert.DoesNotContain(diagnostics, d => d.Kind == RenderDiagnosticKind.ImageDecodeFailed);

        using var svg = DecodeOnly(hrefs);
        Assert.Equal(255, svg.GetPixel(0, 0).Alpha);

        using var raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsInk(PixelAssert.At(raster, 200f, 500f)),
            "the raster path refused a padded stencil, which /EncodedByteAlign produces routinely");
    }

    // ------------------------------------------------------------------

    private readonly record struct Diagnostic(RenderDiagnosticKind Kind, string Detail);

    private static string HexZeros(int byteCount) => new string('0', byteCount * 2) + ">";

    /// <summary>
    /// Exports to SVG, returning every <c>&lt;image&gt;</c> href and the diagnostics raised on this
    /// thread.
    /// </summary>
    /// <remarks>
    /// The listener is filtered to the calling thread. <c>RenderDiagnostics.OnEvent</c> is
    /// process-wide, so an unfiltered handler is an assertion about whatever else the run happens
    /// to be doing — which is how three intermittent failures got into this suite.
    /// </remarks>
    private static (string[] Hrefs, List<Diagnostic> Diagnostics) ExportSvg(byte[] pdf)
    {
        var seen = new List<Diagnostic>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            seen.Add(new Diagnostic(kind, detail ?? string.Empty));
        }

        string svg;
        RenderDiagnostics.OnEvent += Listen;
        try
        {
            using var ms = new MemoryStream(pdf);
            svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        // Parsed as XML rather than substring-matched, so a malformed export fails here instead of
        // passing by accident.
        XDocument doc = XDocument.Parse(svg);
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        string[] hrefs = doc.Descendants()
                            .Where(e => e.Name.LocalName == "image")
                            .Select(e => (string)e.Attribute(xlink + "href") ?? (string)e.Attribute("href"))
                            .Where(h => h != null)
                            .ToArray();
        return (hrefs, seen);
    }

    /// <summary>Decodes the single embedded raster image the export is expected to contain.</summary>
    private static SKBitmap DecodeOnly(string[] hrefs)
    {
        string href = Assert.Single(hrefs);

        const string marker = "base64,";
        int start = href.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The embedded image is not a base64 data URI.");

        var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(start + marker.Length)));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 16x16 <c>/ImageMask</c> drawn at 100..300 x 400..600 in red, its packed samples supplied
    /// as the given ASCIIHex payload so the decoded length is under the fixture's control.
    /// </summary>
    private static byte[] StencilPdf(string hexPayload)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string cs = "q 1 0 0 rg 200 0 0 200 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        int o5 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /ImageMask true /Width {0} /Height {1}"
            + " /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            MaskWidth, MaskHeight, hexPayload.Length, hexPayload);

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
