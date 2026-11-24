// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfSeparations.ToTiff"/> / <c>PdfImageEncoder.EncodeSeparatedTiff</c>: plates as one
/// separated TIFF (TIFF 6.0 §16). The file is read back here by a minimal IFD reader and Core's
/// <see cref="RunLengthDecodeFilter"/> — PDF's RunLength is TIFF's PackBits — rather than by the
/// encoder's own assumptions; an independent tool opens the CMYK form with the same values.
/// </summary>
public class SeparatedTiffTests
{
    private const int TagWidth = 256, TagHeight = 257, TagBits = 258, TagCompression = 259, TagPhotometric = 262,
        TagStripOffsets = 273, TagSamples = 277, TagRowsPerStrip = 278, TagStripCounts = 279, TagXRes = 282,
        TagInkSet = 332, TagInkNames = 333, TagNumberOfInks = 334;

    private static byte[] Plane(int w, int h, Func<int, int, byte> at)
    {
        var p = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) p[y * w + x] = at(x, y);
        return p;
    }

    [Fact]
    public void FourProcessPlates_AreACmykTiff_WithEveryInkWhereItWasPainted()
    {
        const int w = 5, h = 3;
        var inks = new List<(string, byte[])>
        {
            ("Cyan", Plane(w, h, (x, y) => (byte)(x * 50))),
            ("Magenta", Plane(w, h, (x, y) => (byte)(y * 100))),
            ("Yellow", Plane(w, h, (x, y) => 7)),
            ("Black", Plane(w, h, (x, y) => (byte)(x == y ? 255 : 0))),
        };
        var tiff = Tiff.Read(PdfImageEncoder.EncodeSeparatedTiff(w, h, inks, 150));

        Assert.Equal((5u, 3u, 5u, 32773u, 4u, 1u), (tiff.Scalar(TagWidth), tiff.Scalar(TagHeight), tiff.Scalar(TagPhotometric), tiff.Scalar(TagCompression), tiff.Scalar(TagSamples), tiff.Scalar(TagInkSet)));
        Assert.Equal(new uint[] { 8, 8, 8, 8 }, tiff.Shorts(TagBits));
        Assert.Equal(1u, tiff.Scalar(TagRowsPerStrip));
        Assert.Equal((150u, 1u), tiff.Rational(TagXRes));
        for (int y = 0; y < h; y++)
        {
            byte[] row = tiff.Row(y);
            Assert.Equal(w * 4, row.Length);
            for (int x = 0; x < w; x++)
                for (int i = 0; i < 4; i++)
                    Assert.Equal(inks[i].Item2[y * w + x], row[x * 4 + i]);
        }
    }

    [Fact]
    public void SpotPlates_MakeAnNInkTiff_NamingEachInk()
    {
        const int w = 4, h = 2;
        var inks = new List<(string, byte[])>
        {
            ("Cyan", Plane(w, h, (x, y) => 1)), ("Magenta", Plane(w, h, (x, y) => 2)),
            ("Yellow", Plane(w, h, (x, y) => 3)), ("Black", Plane(w, h, (x, y) => 4)),
            ("SPOT 185 C", Plane(w, h, (x, y) => (byte)(x * 60))),
        };
        var tiff = Tiff.Read(PdfImageEncoder.EncodeSeparatedTiff(w, h, inks, 72));

        Assert.Equal((5u, 2u, 5u), (tiff.Scalar(TagSamples), tiff.Scalar(TagInkSet), tiff.Scalar(TagNumberOfInks)));
        Assert.Equal("Cyan\0Magenta\0Yellow\0Black\0SPOT 185 C\0", tiff.Ascii(TagInkNames));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 180 }, tiff.Row(1)[15..20]);
    }

    /// <summary>The CMYK InkSet is for the four process plates in device order only: reordered, they are named inks.</summary>
    [Fact]
    public void ProcessPlatesOutOfOrder_AreNamedInks_NotCmyk()
    {
        var p = new byte[1];
        var tiff = Tiff.Read(PdfImageEncoder.EncodeSeparatedTiff(1, 1,
            new List<(string, byte[])> { ("Magenta", p), ("Cyan", p), ("Yellow", p), ("Black", p) }, 72));
        Assert.Equal(2u, tiff.Scalar(TagInkSet));
    }

    /// <summary>
    /// Rows with every PackBits shape — single bytes, a 2-run, a 128-run, a 129-run that must split,
    /// and 300 bytes of noise that need three literal packets — come back as written.
    /// </summary>
    [Fact]
    public void EveryRunShape_SurvivesTheRow()
    {
        var row = new List<byte> { 9, 1, 1, 2 };
        for (int i = 0; i < 128; i++) row.Add(5);
        for (int i = 0; i < 129; i++) row.Add(6);
        var rng = new Random(7);
        for (int i = 0; i < 300; i++) row.Add((byte)rng.Next(256));
        byte[] plane = row.ToArray();

        var tiff = Tiff.Read(PdfImageEncoder.EncodeSeparatedTiff(plane.Length, 1, new List<(string, byte[])> { ("Black", plane) }, 72));
        Assert.Equal(plane, tiff.Row(0));
        Assert.Equal(new uint[] { 8 }, tiff.Shorts(TagBits));
    }

    /// <summary>A rendered page's plates reach the file at its resolution: the rich black square's four inks, as separation output has them.</summary>
    [Fact]
    public void ARenderedPagesPlates_ReachTheFile()
    {
        byte[] pdf = OnePage("0.6 0.5 0.4 1 k 10 10 80 80 re f");
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new System.IO.MemoryStream(pdf), 0, 144);
        var tiff = Tiff.Read(s.ToTiff());
        Assert.Equal(1u, tiff.Scalar(TagInkSet));
        Assert.Equal((144u, 1u), tiff.Rational(TagXRes));
        byte[] row = tiff.Row(400 - 60);
        Assert.Equal(new byte[] { 153, 128, 102, 255 }, row[(60 * 4)..(60 * 4 + 4)]);
    }

    [Fact]
    public void APlaneOfTheWrongSize_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfImageEncoder.EncodeSeparatedTiff(2, 2,
            new List<(string, byte[])> { ("Black", new byte[3]) }, 72));
        Assert.Contains("is not 2x2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2u, Tiff.Read(PdfImageEncoder.EncodeSeparatedTiff(2, 2, new List<(string, byte[])> { ("Black", new byte[4]) }, 72)).Scalar(TagWidth));
    }

    private static byte[] OnePage(string content)
    {
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A little-endian, single-IFD TIFF reader: just what these tests ask.</summary>
    private sealed class Tiff
    {
        private readonly byte[] _d;
        private readonly Dictionary<int, (int Type, uint Count, uint Value)> _tags = new();

        private Tiff(byte[] d) => _d = d;

        public static Tiff Read(byte[] d)
        {
            Assert.Equal((byte)'I', d[0]);
            Assert.Equal(42, BitConverter.ToUInt16(d, 2));
            var t = new Tiff(d);
            int ifd = (int)BitConverter.ToUInt32(d, 4);
            int n = BitConverter.ToUInt16(d, ifd);
            int previous = -1;
            for (int i = 0; i < n; i++)
            {
                int e = ifd + 2 + 12 * i;
                int tag = BitConverter.ToUInt16(d, e);
                Assert.True(tag > previous, "TIFF 6.0 §2: IFD entries sorted ascending by tag");
                previous = tag;
                t._tags[tag] = (BitConverter.ToUInt16(d, e + 2), BitConverter.ToUInt32(d, e + 4), BitConverter.ToUInt32(d, e + 8));
                var (type, count, value) = t._tags[tag];
                int size = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
                if (size * count > 4) Assert.True(value % 2 == 0, $"TIFF 6.0 §2: tag {tag}'s value at {value} is not on a word boundary");
            }
            Assert.Equal(0u, BitConverter.ToUInt32(d, ifd + 2 + 12 * n));
            // Each strip's byte count is exact: it ends where the next strip starts (the encoder writes them back to back).
            uint rows = t.Scalar(TagHeight);
            for (int y = 0; y + 1 < rows; y++)
                Assert.Equal(t.Long(TagStripOffsets, y + 1), t.Long(TagStripOffsets, y) + t.Long(TagStripCounts, y));
            return t;
        }

        public uint Scalar(int tag)
        {
            var (type, count, value) = _tags[tag];
            Assert.Equal(1u, count);
            return type == 3 ? value & 0xFFFF : value;
        }

        public uint[] Shorts(int tag)
        {
            var (type, count, value) = _tags[tag];
            Assert.Equal(3, type);
            var r = new uint[count];
            for (int i = 0; i < count; i++)
                r[i] = count <= 2 ? (value >> (16 * i)) & 0xFFFF : BitConverter.ToUInt16(_d, (int)value + 2 * i);
            return r;
        }

        public (uint, uint) Rational(int tag)
        {
            var (_, _, at) = _tags[tag];
            return (BitConverter.ToUInt32(_d, (int)at), BitConverter.ToUInt32(_d, (int)at + 4));
        }

        public string Ascii(int tag)
        {
            var (_, count, at) = _tags[tag];
            return Encoding.ASCII.GetString(_d, (int)at, (int)count);
        }

        public uint Long(int tag, int index)
        {
            var (_, count, value) = _tags[tag];
            return count == 1 ? value : BitConverter.ToUInt32(_d, (int)value + 4 * index);
        }

        public byte[] Row(int y)
        {
            int at = (int)Long(TagStripOffsets, y), len = (int)Long(TagStripCounts, y);
            return new RunLengthDecodeFilter().Decode(_d[at..(at + len)], null);
        }
    }
}
