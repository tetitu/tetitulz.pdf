// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A JPEG header carrying an EXIF orientation: SOI, an APP1 <c>Exif</c> segment whose first image
/// directory holds tag 274, a baseline SOF0, an empty SOS and EOI.
/// </summary>
/// <remarks>
/// No entropy data — only the header is ever read, and the bytes are embedded verbatim. The
/// parameters past <c>orientation</c> build the malformed variants a reader must survive: a
/// directory offset outside the segment, an entry count the segment cannot hold, a tag stored
/// with the wrong type.
/// </remarks>
public static class ExifJpeg
{
    public static byte[] Build(int width, int height, int orientation, bool littleEndian = false,
        uint ifdOffset = 8, int entryCount = 1, int valueType = 3, bool exif = true, int magic = 42)
    {
        var b = new List<byte> { 0xFF, 0xD8 };

        if (exif)
        {
            var tiff = new List<byte>();
            void U16(int v) { if (littleEndian) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); } else { tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); } }
            void U32(uint v)
            {
                if (littleEndian) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); tiff.Add((byte)(v >> 16)); tiff.Add((byte)(v >> 24)); }
                else { tiff.Add((byte)(v >> 24)); tiff.Add((byte)(v >> 16)); tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); }
            }

            tiff.Add(littleEndian ? (byte)'I' : (byte)'M');
            tiff.Add(littleEndian ? (byte)'I' : (byte)'M');
            U16(magic);
            U32(ifdOffset);
            U16(entryCount);
            U16(0x0112);          // Orientation
            U16(valueType);       // SHORT
            U32(1);               // count
            U16(orientation);     // the value, left-justified in the 4-byte field
            U16(0);
            U32(0);               // no next directory

            var segment = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
            segment.AddRange(tiff);
            int length = segment.Count + 2;
            b.AddRange(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length });
            b.AddRange(segment);
        }

        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        b.Add((byte)(height >> 8)); b.Add((byte)height);
        b.Add((byte)(width >> 8)); b.Add((byte)width);
        b.Add(0x03);
        for (int c = 1; c <= 3; c++) { b.Add((byte)c); b.Add(0x11); b.Add(0x00); }

        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 });
        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }
}
