// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Unit tests for <see cref="VisualSignatureBuilder"/> — specifically the JPEG dimension
/// parser which previously mishandled standalone markers and 0xFF fill bytes.
/// </summary>
public class VisualSignatureBuilderTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>Builds a minimal valid JFIF byte array with a SOF0 segment.</summary>
    private static byte[] MakeJpeg(int width, int height)
    {
        var bytes = new System.Collections.Generic.List<byte>();

        // SOI
        bytes.Add(0xFF); bytes.Add(0xD8);

        // APP0 (JFIF) — minimal, 16-byte segment
        bytes.Add(0xFF); bytes.Add(0xE0);
        bytes.Add(0x00); bytes.Add(0x10); // length = 16
        bytes.AddRange(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 }); // "JFIF\0"
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 }); // rest of APP0

        // SOF0 (baseline DCT)
        bytes.Add(0xFF); bytes.Add(0xC0);
        bytes.Add(0x00); bytes.Add(0x11); // length = 17 (8 + 3 * num_components=3)
        bytes.Add(0x08);                  // precision = 8 bits
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));  bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);                  // num_components = 3 (Y, Cb, Cr)
        // Component specs (3 ֳ— 3 bytes)
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });

        // EOI
        bytes.Add(0xFF); bytes.Add(0xD9);

        return bytes.ToArray();
    }

    // ----------------------------------------------------------------
    //  Happy path
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_ValidJpeg_ReturnsCorrectSize()
    {
        byte[] jpeg = MakeJpeg(320, 240);
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void GetJpegDimensions_LargeImage_ReturnsCorrectSize()
    {
        byte[] jpeg = MakeJpeg(3840, 2160);
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(3840, w);
        Assert.Equal(2160, h);
    }

    // ----------------------------------------------------------------
    //  Bug A: EOI before SOF must not crash / should return defaults
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_EoiBeforeSof_ReturnsDefaults()
    {
        // SOI + EOI — no SOF segment
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(100, w);  // defaults
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug B: SOS before SOF must not crash / should return defaults
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_SosBeforeSof_ReturnsDefaults()
    {
        // SOI + SOS (with length 0x000C) — no SOF
        byte[] jpeg =
        {
            0xFF, 0xD8,              // SOI
            0xFF, 0xDA,              // SOS marker
            0x00, 0x0C,              // segment length = 12 (length field, includes itself)
            0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
            0xFF, 0xD9               // EOI
        };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug C: RST marker (standalone, no length) must not corrupt pos
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_RstMarkerBeforeSof_ReturnsDefaults()
    {
        // Pathological: SOI + RST0 (no length) + EOI — no SOF
        // Previously the code would read the next 2 bytes after RST0 as a segment length,
        // treating part of EOI as offset data, potentially reading out of bounds.
        byte[] jpeg =
        {
            0xFF, 0xD8,  // SOI
            0xFF, 0xD0,  // RST0 (no length)
            0xFF, 0xD9   // EOI
        };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        // No exception; defaults returned
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug D: 0xFF fill bytes before SOF marker
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_FillBytesBeforeSof_ParsesCorrectly()
    {
        // Build JPEG with a 0xFF fill byte injected before the SOF0 marker byte.
        // JPEG spec allows multiple 0xFF bytes between segments as padding.
        byte[] jpeg = MakeJpeg(100, 200);

        // Find where SOF0 marker is: 0xFF 0xC0
        int sofIdx = -1;
        for (int i = 0; i < jpeg.Length - 1; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0) { sofIdx = i; break; }
        Assert.True(sofIdx >= 0, "SOF0 not found in test JPEG");

        // Insert a 0xFF fill byte before the 0xC0 to make it: 0xFF 0xFF 0xC0
        var withFill = new System.Collections.Generic.List<byte>(jpeg);
        withFill.Insert(sofIdx + 1, 0xFF);
        byte[] paddedJpeg = withFill.ToArray();

        VisualSignatureBuilder.GetJpegDimensions(paddedJpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(200, h);
    }

    // ----------------------------------------------------------------
    //  Edge case: not a JPEG at all
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_NotJpeg_ReturnsDefaults()
    {
        byte[] notJpeg = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        VisualSignatureBuilder.GetJpegDimensions(notJpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    [Fact]
    public void GetJpegDimensions_TooShort_ReturnsDefaults()
    {
        VisualSignatureBuilder.GetJpegDimensions(new byte[] { 0xFF }, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }
}
