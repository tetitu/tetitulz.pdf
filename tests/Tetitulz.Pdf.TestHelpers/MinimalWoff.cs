// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Wraps a real sfnt (see <see cref="MinimalTrueType"/>) in a <b>WOFF 1.0</b> container, so a
/// test can exercise the decoder against a font whose tables are genuinely zlib-compressed
/// rather than a hand-waved byte array.
///
/// <para><see cref="Wrap(byte[])"/> deliberately compresses <b>some</b> tables and stores the
/// rest verbatim. WOFF 1.0 §4 allows either per table (<c>compLength == origLength</c> means
/// stored), and a container that used one form throughout would leave half the decoder
/// untested — the stored arm and the inflate arm are separate branches.</para>
/// </summary>
public static class MinimalWoff
{
    /// <summary>
    /// A WOFF 1.0 container carrying <paramref name="sfnt"/>, with every other table
    /// zlib-compressed and the rest stored.
    /// </summary>
public static byte[] Wrap(byte[] sfnt) => Wrap(sfnt, reverseTableOrder: false);

    /// <summary>
    /// As <see cref="Wrap(byte[])"/>, optionally emitting the tables in REVERSE tag order.
    /// </summary>
    /// <remarks>
    /// <b>A container whose order already matches tag order cannot detect a decoder that reorders
    /// the data</b>, because the reordering is then a no-op — which is exactly why the defect this
    /// exists for stayed green. WOFF 1.0 §6 requires the reconstructed table data to be in the same
    /// order as the original font, and a real container is under no obligation to have sorted it.
    /// </remarks>
    public static byte[] Wrap(byte[] sfnt, bool reverseTableOrder)
    {
        List<(string tag, byte[] data, uint checksum)> tables = ReadTables(sfnt);
        if (reverseTableOrder) tables.Reverse();

        var bodies = new List<(string tag, byte[] stored, uint origLength, uint checksum)>();
        for (int i = 0; i < tables.Count; i++)
        {
            (string tag, byte[] data, uint checksum) = tables[i];
            byte[] stored = i % 2 == 0 ? Deflate(data) : data;

            // WOFF 1.0 §4: compLength never exceeds origLength, and equality is how a reader
            // knows a table is stored. A tiny table can "compress" larger than the original, so
            // fall back to storing it rather than emitting a container no reader may accept.
            if (stored.Length >= data.Length) stored = data;
            bodies.Add((tag, stored, (uint)data.Length, checksum));
        }

        int headerSize = 44;
        int directorySize = bodies.Count * 20;
        int cursor = headerSize + directorySize;

        var offsets = new List<int>();
        long totalSfntSize = 12 + bodies.Count * 16;
        foreach ((string _, byte[] stored, uint origLength, uint _) in bodies)
        {
            offsets.Add(cursor);
            cursor += Align4(stored.Length);
            totalSfntSize += Align4((int)origLength);
        }

        var output = new MemoryStream();
        WriteU32(output, 0x774F4646);        // signature: 'wOFF'
        WriteU32(output, 0x00010000);        // flavor: sfnt version 1.0 (TrueType)
        WriteU32(output, cursor);            // length of this file
        WriteU16(output, bodies.Count);      // numTables
        WriteU16(output, 0);                 // reserved
        WriteU32(output, totalSfntSize);
        WriteU16(output, 1); WriteU16(output, 0);   // major / minor version
        WriteU32(output, 0); WriteU32(output, 0); WriteU32(output, 0);   // metadata block
        WriteU32(output, 0); WriteU32(output, 0);                        // private block

        for (int i = 0; i < bodies.Count; i++)
        {
            (string tag, byte[] stored, uint origLength, uint checksum) = bodies[i];
            output.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            WriteU32(output, offsets[i]);
            WriteU32(output, stored.Length);
            WriteU32(output, origLength);
            WriteU32(output, checksum);
        }

        foreach ((string _, byte[] stored, uint _, uint _) in bodies)
        {
            output.Write(stored, 0, stored.Length);
            for (int pad = stored.Length; pad % 4 != 0; pad++) output.WriteByte(0);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Bytes opening with the WOFF 2.0 signature (<c>wOF2</c>).
    ///
    /// <para>Nothing in this tree decodes WOFF 2.0, so a byte-accurate Brotli container would
    /// test nothing a signature does not: every consumer's decision is made on those four bytes
    /// and stops there.</para>
    /// </summary>
    public static byte[] Woff2Signature(byte[] payload)
    {
        var output = new MemoryStream();
        output.Write(new byte[] { 0x77, 0x4F, 0x46, 0x32 }, 0, 4);
        if (payload != null && payload.Length > 4) output.Write(payload, 4, payload.Length - 4);
        return output.ToArray();
    }

    /// <summary>The tag, bytes and directory checksum of every table in an sfnt.</summary>
    private static List<(string, byte[], uint)> ReadTables(byte[] sfnt)
    {
        var result = new List<(string, byte[], uint)>();
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(sfnt, record, 4);
            uint checksum = ReadU32(sfnt, record + 4);
            int offset = (int)ReadU32(sfnt, record + 8);
            int length = (int)ReadU32(sfnt, record + 12);
            var data = new byte[length];
            Array.Copy(sfnt, offset, data, 0, length);
            result.Add((tag, data, checksum));
        }
        return result;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static int Align4(int n) => (n + 3) & ~3;

    private static uint ReadU32(byte[] d, int at) =>
        ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteU32(Stream s, long v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}
