// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Decoded-pixel cover for the two packed-1-bit branches of the raster image decoder — the
/// explicit <c>/BitsPerComponent 1</c> path and the last-resort fallback that claims a buffer
/// whose length is exactly <c>ceil(w/8)·h</c> when the file declared neither
/// <c>/BitsPerComponent</c> nor <c>/ColorSpace</c>.
///
/// <para><b>Why this file exists.</b> The two loops were deduplicated into one
/// <c>DecodePackedBilevel</c> helper. That is a DRY consolidation and not a defect fix — the
/// only spelling difference between the copies (one testing <c>/Decode</c> entries pairwise,
/// the other per entry) is unreachable, because <c>GetDecodeArray</c> returns <c>null</c> for
/// any array shorter than two entries. What the consolidation genuinely risks is the two
/// LENGTH GUARDS, which are different on purpose and stayed at their call sites: the explicit
/// branch accepts <c>&gt;=</c> so a padded <c>/EncodedByteAlign</c> stencil still decodes, the
/// fallback demands <c>==</c> so it cannot claim a short buffer that the 2/4/16-bpc branches
/// declined. Coverage of both was thin to absent before this — the fallback's only test never
/// set <c>/Decode</c> at all — so each guard and the <c>/Decode</c> remap now have a test that
/// reads pixels.</para>
/// </summary>
public sealed class PackedBilevelDecodeTests
{
    // The image is drawn over user space (100,400)-(300,600) by every fixture here, so (200,500)
    // is its centre, (200,550) the middle of its top half and (200,450) of its bottom half.
    private const float CentreX = 200f, CentreY = 500f, TopHalfY = 550f, BottomHalfY = 450f;

    // ------------------------------------------------------------------
    //  Explicit /BitsPerComponent 1 (site A)
    // ------------------------------------------------------------------

    /// <summary>
    /// §8.9.5.2 applies <c>/Decode</c> at every bit depth: on a 1-bpc greyscale image
    /// <c>/Decode [0 0.5]</c> maps the set bit onto 0.5, which quantises to <b>128</b> — the
    /// round-to-nearest of 127.5, not the 127 a truncating cast gives. The identity render of
    /// the same samples is white, so the assertion measures the remap rather than the fixture.
    /// </summary>
    /// <remarks>
    /// Every sample in the fixture is a set bit, so the drawn area is one flat colour and the
    /// comparison is exact at any interior point regardless of how the bitmap is filtered on the
    /// way to the page. Half a level is the whole claim here; a tolerance would erase it.
    /// </remarks>
    [Fact]
    public void ExplicitOneBpc_HalfRangeDecode_QuantisesTheSetBitByRounding()
    {
        using var halfRange = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 1, data: [0xFF], bitsPerComponent: "1", colorSpace: "/DeviceGray",
            decode: "[0 0.5]"));
        using var identity = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 1, data: [0xFF], bitsPerComponent: "1", colorSpace: "/DeviceGray",
            decode: null));

        var mapped = PixelAssert.At(halfRange, CentreX, CentreY);
        Assert.Equal(new SKColor(128, 128, 128), new SKColor(mapped.Red, mapped.Green, mapped.Blue));

        var plain = PixelAssert.At(identity, CentreX, CentreY);
        Assert.Equal(new SKColor(255, 255, 255), new SKColor(plain.Red, plain.Green, plain.Blue));
    }

    /// <summary>
    /// A stencil mask whose sample buffer carries trailing padding must still paint. This is the
    /// <c>&gt;=</c> in the explicit 1-bpc guard, and it is the only path a padded
    /// <c>/ImageMask</c> has: <c>/EncodedByteAlign</c> pads each row to a byte boundary and
    /// producers routinely leave whole bytes past the last row, while the fallback below insists
    /// on an exact length. Tighten this guard to <c>==</c> and every such stencil disappears from
    /// its page, silently.
    /// </summary>
    [Fact]
    public void ExplicitOneBpc_PaddedImageMask_StillPaintsWithTheFillColour()
    {
        // 8x2 stencil = 2 bytes of samples; the third byte is the padding under test.
        // Row 0 all-zero → painted (§8.9.6.4: sample 0 paints), row 1 all-ones → transparent.
        using var bmp = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 2, data: [0x00, 0xFF, 0x00], bitsPerComponent: null, colorSpace: null,
            decode: null, imageMask: true, fillColourOps: "1 0 0 rg"));

        var painted = PixelAssert.At(bmp, CentreX, TopHalfY);
        Assert.True(PixelAssert.Near(painted, SKColors.Red, 8),
            $"the unpadded row of a padded /ImageMask painted {painted}, not the red fill colour; " +
            "the trailing byte made the length guard reject the whole stencil.");

        var masked = PixelAssert.At(bmp, CentreX, BottomHalfY);
        Assert.True(PixelAssert.IsWhite(masked),
            $"the all-ones row painted {masked}; a 1 sample is transparent under the default /Decode.");
    }

    // ------------------------------------------------------------------
    //  Undeclared-format fallback (site B)
    // ------------------------------------------------------------------

    /// <summary>
    /// The fallback path — an image that declares neither <c>/BitsPerComponent</c> nor
    /// <c>/ColorSpace</c>, as CCITT and JBIG2 dictionaries routinely do not — honours
    /// <c>/Decode</c> too. <c>/Decode [1 0]</c> maps the set bit to 0, so an all-ones buffer
    /// paints black where the default <c>[0 1]</c> paints white.
    /// </summary>
    /// <remarks>
    /// This path had no <c>/Decode</c> coverage at all: its one existing test asserts that a
    /// checkerboard produces both a dark and a light pixel somewhere in the frame and never sets
    /// <c>/Decode</c>, so the remap could have been dropped from it without a test noticing.
    /// </remarks>
    [Fact]
    public void UndeclaredFormatFallback_InvertingDecode_PaintsTheSetBitBlack()
    {
        using var inverted = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 1, data: [0xFF], bitsPerComponent: null, colorSpace: null,
            decode: "[1 0]"));
        using var identity = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 1, data: [0xFF], bitsPerComponent: null, colorSpace: null,
            decode: null));

        var dark = PixelAssert.At(inverted, CentreX, CentreY);
        Assert.Equal(new SKColor(0, 0, 0), new SKColor(dark.Red, dark.Green, dark.Blue));

        var light = PixelAssert.At(identity, CentreX, CentreY);
        Assert.Equal(new SKColor(255, 255, 255), new SKColor(light.Red, light.Green, light.Blue));
    }

    /// <summary>
    /// The fallback's length test must stay exact. A 2-bpc greyscale image whose buffer is one
    /// byte short of its own stride is refused with <see cref="RenderDiagnosticKind.ImageDecodeFailed"/>
    /// and nothing is drawn — the honest outcome. Relaxing the guard to <c>&gt;=</c>, the tempting
    /// move now that both bilevel bodies are one helper, would hand every such buffer to the
    /// bilevel decoder and paint a truncated greyscale image as confident black-and-white noise.
    ///
    /// <para>The well-formed control renders from the same builder and does produce ink, so a
    /// blank page cannot pass this test by never reaching the decoder.</para>
    /// </summary>
    [Fact]
    public void UndeclaredFormatFallback_ShortTwoBpcBuffer_IsRefusedRatherThanPaintedAsBilevel()
    {
        // 8x2 at 2 bpc needs ceil(8*2/8)*2 = 4 bytes. Three is short for that branch and is NOT
        // the 2 bytes the packed-1-bit fallback would demand, so no branch may claim it.
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            // RenderDiagnostics.OnEvent is process-wide; without the thread filter this collects
            // whatever else xUnit is rendering in parallel.
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        SKBitmap refused;
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            refused = PixelAssert.Render(BuildBilevelPdf(
                width: 8, height: 2, data: [0x00, 0x00, 0x00], bitsPerComponent: "2",
                colorSpace: "/DeviceGray", decode: null));
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        using (refused)
        {
            Assert.Contains(RenderDiagnosticKind.ImageDecodeFailed, kinds);
            Assert.True(PixelAssert.IsWhite(PixelAssert.At(refused, CentreX, CentreY)),
                "a 2-bpc image with a short buffer was painted; the packed-1-bit fallback claimed " +
                "samples it cannot interpret instead of refusing them.");
        }

        // Control: the same image with its full 4 bytes decodes through the 2-bpc branch and inks
        // the page, which is what proves the fixture reaches the decoder at all.
        using var wellFormed = PixelAssert.Render(BuildBilevelPdf(
            width: 8, height: 2, data: [0x00, 0x00, 0x00, 0x00], bitsPerComponent: "2",
            colorSpace: "/DeviceGray", decode: null));
        var inked = PixelAssert.At(wellFormed, CentreX, CentreY);
        Assert.True(PixelAssert.Near(inked, SKColors.Black, 8),
            $"the well-formed 2-bpc control painted {inked}, not black — the fixture never reached " +
            "the image decoder, so the refusal above proves nothing.");
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page PDF drawing a single raw image XObject over user space (100,400)-(300,600).
    /// <paramref name="bitsPerComponent"/> and <paramref name="colorSpace"/> are omitted from the
    /// dictionary entirely when null — which is what routes an image to the undeclared-format
    /// fallback — and the samples travel as ASCIIHex so the whole file stays ASCII.
    /// </summary>
    private static byte[] BuildBilevelPdf(int width, int height, byte[] data,
                                          string bitsPerComponent, string colorSpace, string decode,
                                          bool imageMask = false, string fillColourOps = "")
    {
        var hex = new StringBuilder();
        foreach (byte b in data) hex.AppendFormat(CultureInfo.InvariantCulture, "{0:X2}", b);
        hex.Append('>');
        string hexData = hex.ToString();

        string entries =
            (bitsPerComponent != null ? $" /BitsPerComponent {bitsPerComponent}" : "") +
            (colorSpace != null ? $" /ColorSpace {colorSpace}" : "") +
            (imageMask ? " /ImageMask true" : "") +
            (decode != null ? $" /Decode {decode}" : "");

        string cs = $"q {fillColourOps} 200 0 0 200 100 400 cm /Im1 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        int o5 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width {0} /Height {1}{2}" +
            " /Filter /ASCIIHexDecode /Length {3} >>\nstream\n{4}\nendstream\nendobj\n",
            width, height, entries, hexData.Length, hexData);

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o1);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o2);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o3);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o4);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
