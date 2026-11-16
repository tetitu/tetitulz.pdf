// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The image XObject a visible signature stamp writes must describe the JPEG it actually carries.
/// </summary>
/// <remarks>
/// <c>/ColorSpace /DeviceRGB</c> and <c>/BitsPerComponent 8</c> were hard-coded whatever the frame
/// header said, so a grayscale scanned-ink signature — one component, the common case for a real
/// signature image — had one component's worth of samples read as RGB triples and rendered as
/// noise. And a payload that is not a JPEG at all was embedded under <c>/DCTDecode</c> anyway,
/// after <c>GetJpegDimensions</c> quietly substituted its 100x100 placeholder: every reader failed
/// to decode it and the stamp came out blank, with no error anywhere in the pipeline.
/// </remarks>
public sealed class VisualStampImageTests
{
    /// <summary>
    /// A four-component frame is CMYK only under Adobe's APP14 marker, and then carries the
    /// inverting <c>/Decode</c>; <c>VisualSignatureTextAppearanceTests</c> pins both, and the
    /// refusal of a four-component frame without the marker.
    /// </summary>
    [Theory]
    [InlineData(1, "/DeviceGray")]
    [InlineData(3, "/DeviceRGB")]
    public void TheStampImageDeclaresTheColourSpaceItsFrameHeaderNames(int components, string expected)
    {
        byte[] output = SignWithStamp(Jpeg(components, width: 40, height: 20));

        Assert.Contains("/ColorSpace " + expected, Encoding.Latin1.GetString(output));
    }

    /// <summary>
    /// A payload with no SOI/SOF is refused rather than embedded. The old behaviour produced a
    /// signed document whose stamp no reader could draw.
    /// </summary>
    [Fact]
    public void ANonJpegPayload_IsRefusedRatherThanEmbedded()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };

        var ex = Assert.Throws<PdfException>(() => SignWithStamp(png));

        Assert.Contains("JPEG", ex.Message);
    }

    private static byte[] SignWithStamp(byte[] image)
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        string pfxPath = Path.Combine(Path.GetTempPath(), $"stamp-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfxPath, pfx);
        try
        {
            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            using var output = new MemoryStream();
            VisualSignatureBuilder.Sign(input, output, pfxPath, pw, image,
                pageNumber: 1, x: 50, y: 50, width: 120, height: 60);
            return output.ToArray();
        }
        finally
        {
            try { File.Delete(pfxPath); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// A JPEG carrying only what the embedder reads: SOI, a baseline SOF0 frame header declaring
    /// <paramref name="components"/> channels, and EOI. No entropy-coded data is needed — nothing
    /// in this path decodes the image.
    /// </summary>
    private static byte[] Jpeg(int components, int width, int height)
    {
        var bytes = new System.Collections.Generic.List<byte> { 0xFF, 0xD8, 0xFF, 0xC0 };

        int segmentLength = 8 + 3 * components;   // length(2) + precision(1) + h(2) + w(2) + n(1) + 3/comp
        bytes.Add((byte)(segmentLength >> 8));
        bytes.Add((byte)(segmentLength & 0xFF));
        bytes.Add(8);                              // sample precision
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));  bytes.Add((byte)(width & 0xFF));
        bytes.Add((byte)components);
        for (int i = 1; i <= components; i++)
        {
            bytes.Add((byte)i);      // component id
            bytes.Add(0x11);         // sampling factors
            bytes.Add(0);            // quantisation table
        }

        bytes.Add(0xFF); bytes.Add(0xD9);          // EOI
        return bytes.ToArray();
    }
}
