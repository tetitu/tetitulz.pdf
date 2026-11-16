// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A JPEG 2000 image in a built document: embedded byte for byte as a <c>/JPXDecode</c> XObject
/// with the dictionary the file wants — its colour space named when the file names a device
/// space, left out otherwise — and reported embeddable by <see cref="BuilderImageDecoder.CanDecode"/>
/// by the same rule.
/// </summary>
public class Jpeg2000ImageTests
{
    private static byte[] Samples(int width, int height, int components)
    {
        var samples = new byte[width * height * components];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7 + (i / components) % 13);
        return samples;
    }

    private static byte[] Jp2(int width, int height, int components, bool container = true) =>
        JpxImageEncoding.Encode(Samples(width, height, components), width, height, components, 8, new JpxEncodingOptions { Jp2Container = container });

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    [Fact]
    public void Jp2_InlineImage_EmbedsTheFileVerbatimAsJpxDecode()
    {
        byte[] jp2 = Jp2(12, 9, 3);
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(jp2, 48, 36)))
            .Build().ToPdf();

        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Subtype /Image", raw);
        Assert.Contains("/Filter /JPXDecode", raw);
        Assert.Contains("/ColorSpace /DeviceRGB", raw);   // the JP2's colr box names sRGB, so the dictionary says so
        Assert.Contains("/Width 12", raw);
        Assert.Contains("/Height 9", raw);
        Assert.Contains("/BitsPerComponent 8", raw);
        Assert.DoesNotContain("/SMaskInData", raw);
        Assert.True(IndexOf(pdf, jp2) > 0, "the JP2 file is not in the PDF byte for byte");
        Assert.DoesNotContain("/DCTDecode", raw);
    }

    [Fact]
    public void RawCodestream_Grey_IsEmbeddedAsDeviceGray()
    {
        byte[] j2k = Jp2(10, 10, 1, container: false);
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(j2k, 30, 30)))
            .Build().ToPdf();
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Filter /JPXDecode", raw);
        Assert.Contains("/ColorSpace /DeviceGray", raw);
        Assert.True(IndexOf(pdf, j2k) > 0);
    }

    /// <summary>A declared opacity channel is asked for with <c>/SMaskInData 1</c>; an sYCC file gets no <c>/ColorSpace</c> at all (only images name one in a built document).</summary>
    [Fact]
    public void OpacityChannel_AndSYcc_AreWrittenAsTheFileWants()
    {
        byte[] withOpacity = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.GreyWithOpacityJp2(), 40, 24)))
            .Build().ToPdf();
        string raw = Encoding.Latin1.GetString(withOpacity);
        Assert.Contains("/Filter /JPXDecode", raw);
        Assert.Contains("/ColorSpace /DeviceGray", raw);
        Assert.Contains("/SMaskInData 1", raw);
        Assert.DoesNotContain("/SMask ", raw);

        byte[] sYcc = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.SYccJp2(), 40, 24)))
            .Build().ToPdf();
        raw = Encoding.Latin1.GetString(sYcc);
        Assert.Contains("/Filter /JPXDecode", raw);
        Assert.DoesNotContain("/ColorSpace", raw);
        Assert.DoesNotContain("/SMaskInData", raw);
    }

    /// <summary>The opacity mode the file declares (Table 89): a premultiplied channel writes <c>/SMaskInData 2</c>; a per-colour one, which the key cannot express, writes none.</summary>
    [Fact]
    public void PremultipliedOpacity_IsWrittenAsSMaskInData2_AndPerColourOpacityGetsNoKey()
    {
        byte[] premultiplied = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.GreyWithPremultipliedOpacityJp2(), 40, 24)))
            .Build().ToPdf();
        Assert.Contains("/SMaskInData 2", Encoding.Latin1.GetString(premultiplied));

        byte[] perColour = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.RgbWithPerColourOpacityJp2(), 40, 24)))
            .Build().ToPdf();
        string raw = Encoding.Latin1.GetString(perColour);
        Assert.Contains("/Filter /JPXDecode", raw);
        Assert.Contains("/ColorSpace /DeviceRGB", raw);
        Assert.DoesNotContain("/SMaskInData", raw);
    }

    [Fact]
    public void CanDecode_SaysYesToAJpeg2000File_AndNamesTheFormatForATiff()
    {
        Assert.True(BuilderImageDecoder.CanDecode(Jp2(4, 4, 3), out string? reason), reason);
        Assert.True(BuilderImageDecoder.CanDecode(Jp2(4, 4, 1, container: false), out reason), reason);

        // A TIFF header is recognised and declined by name, with JPEG 2000 among the formats named as supported.
        byte[] tiff = { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0 };
        Assert.False(BuilderImageDecoder.CanDecode(tiff, out reason));
        Assert.Contains("JPEG 2000", reason, StringComparison.Ordinal);
    }
}
