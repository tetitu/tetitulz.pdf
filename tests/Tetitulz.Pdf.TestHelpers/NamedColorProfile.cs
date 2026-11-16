// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A synthetic ICC named-colour profile (class <c>nmcl</c>, an <c>ncl2</c> tag, CMYK device
/// coordinates, prefix <c>SPOT </c> and suffix <c> C</c>). <see cref="ReferenceLab"/> is an independent ICC tool's/// (2.3.2.3): its profile dumper validates each version and decodes the tag, and
/// its named-colour CMM resolves each name to Lab. <see cref="ReferenceSrgb"/> is a second independent CMM's
/// the CMM taking that Lab through its <c>sRGB.icm</c> (relative colorimetric) — an independent ICC tool's own
/// chain through its <c>sRGB_D65_MAT.icc</c> disagrees with both by up to sixteen levels, and was not used.
/// </summary>
public static class NamedColorProfile
{
    /// <summary>Root, Lab written, CMYK percentages written.</summary>
    public static readonly (string Root, double L, double A, double B, double[] Cmyk)[] Colours =
    {
        ("485", 47.0, 69.0, 52.0, new double[] { 0, 95, 100, 0 }),
        ("286", 27.5, 13.25, -64.0, new double[] { 100, 75, 0, 0 }),
        ("354", 61.0, -63.5, 41.0, new double[] { 80, 0, 90, 0 }),
        ("Black", 16.0, 0.5, -1.5, new double[] { 0, 0, 0, 100 }),
    };

    /// <summary>The reference CMM's Lab for each colour, in order (the legacy encoding's rounding included).</summary>
    public static readonly double[][] ReferenceLab =
    {
        new[] { 47.0006, 69.0, 52.0 },
        new[] { 27.5, 13.25, -64.0 },
        new[] { 61.0003, -63.5, 41.0 },
        new[] { 16.0003, 0.5, -1.5 },
    };

    /// <summary>The second CMM's sRGB for each colour's Lab, clipped to the gamut.</summary>
    public static readonly double[][] ReferenceSrgb =
    {
        new[] { 0.847020, 0.095848, 0.109522 },
        new[] { 0.000000, 0.245835, 0.651264 },
        new[] { 0.000000, 0.677885, 0.261839 },
        new[] { 0.155851, 0.155219, 0.164312 },
    };

    /// <summary>
    /// The profile: <paramref name="version"/> 2 or 4; the PCS Lab or, with <paramref name="xyz"/>, XYZ; with
    /// <paramref name="bareNames"/>, no prefix or suffix, so each root is a full name (the oracles' values are for
    /// the prefixed profile).
    /// </summary>
    public static byte[] Build(int version = 4, bool xyz = false, string deviceClass = "nmcl", bool bareNames = false)
    {
        bool v4 = version >= 4;
        var tags = new List<(string, byte[])>
        {
            ("desc", Pad4(v4 ? Mluc("tetitulz named") : DescV2("tetitulz named"))),
            ("ncl2", Pad4(Ncl2(xyz, bareNames))),
            ("wtpt", Pad4(Concat(Ascii("XYZ "), new byte[4], S15(0.9642), S15(1.0), S15(0.8249)))),
            ("cprt", Pad4(v4 ? Mluc("No copyright, test data") : Text("No copyright, test data"))),
        };
        int tableLength = 4 + 12 * tags.Count;
        var entries = new MemoryStream();
        var body = new MemoryStream();
        foreach (var (sig, data) in tags)
        {
            Write(entries, Ascii(sig), U32((uint)(128 + tableLength + body.Length)), U32((uint)data.Length));
            body.Write(data, 0, data.Length);
        }
        var header = new MemoryStream();
        Write(header, U32((uint)(128 + tableLength + body.Length)), new byte[4], U32(v4 ? 0x04200000u : 0x02100000u),
              Ascii(deviceClass), Ascii("CMYK"), Ascii(xyz ? "XYZ " : "Lab "));
        foreach (int v in new[] { 2026, 10, 18, 12, 0, 0 }) Write(header, U16((ushort)v));
        Write(header, Ascii("acsp"), new byte[4], U32(0), new byte[16], U32(1),
              S15(0.9642), S15(1.0), S15(0.8249), new byte[4], new byte[16]);
        header.Write(new byte[128 - header.Length], 0, 128 - (int)header.Length);
        return Concat(header.ToArray(), U32((uint)tags.Count), entries.ToArray(), body.ToArray());
    }

    private static byte[] Ncl2(bool xyz, bool bareNames)
    {
        var b = new MemoryStream();
        Write(b, Ascii("ncl2"), new byte[4], U32(0), U32((uint)Colours.Length), U32(4),
            Fixed32(bareNames ? "" : "SPOT "), Fixed32(bareNames ? "" : " C"));
        foreach (var (root, l, a, bb, cmyk) in Colours)
        {
            Write(b, Fixed32(root));
            if (xyz)
            {
                LabToXyz(l, a, bb, out double x, out double y, out double z);
                Write(b, U16Of(x * 32768), U16Of(y * 32768), U16Of(z * 32768));
            }
            else Write(b, U16Of(l / 100 * 65280), U16Of((a + 128) * 256), U16Of((bb + 128) * 256));
            foreach (double c in cmyk) Write(b, U16Of(c / 100 * 65535));
        }
        return b.ToArray();
    }

    private static void LabToXyz(double l, double a, double b, out double x, out double y, out double z)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Inv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
        x = 0.9642 * Inv(fx); y = Inv(fy); z = 0.8249 * Inv(fz);
    }

    private static byte[] DescV2(string s)
    {
        byte[] ascii = Concat(Ascii(s), new byte[1]);
        return Concat(Ascii("desc"), new byte[4], U32((uint)ascii.Length), ascii, U32(0), U32(0), U16(0), new byte[1], new byte[67]);
    }

    private static byte[] Mluc(string s)
    {
        byte[] u = Encoding.BigEndianUnicode.GetBytes(s);
        return Concat(Ascii("mluc"), new byte[4], U32(1), U32(12), Ascii("enUS"), U32((uint)u.Length), U32(28), u);
    }

    private static byte[] Text(string s) => Concat(Ascii("text"), new byte[4], Ascii(s), new byte[1]);
    private static byte[] Fixed32(string s) { var b = new byte[32]; Encoding.ASCII.GetBytes(s, 0, s.Length, b, 0); return b; }
    private static byte[] Pad4(byte[] b) => b.Length % 4 == 0 ? b : Concat(b, new byte[4 - b.Length % 4]);
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U16Of(double v) => U16((ushort)Math.Round(v, MidpointRounding.AwayFromZero));
    private static byte[] S15(double v) => U32(unchecked((uint)(int)Math.Round(v * 65536, MidpointRounding.AwayFromZero)));

    private static void Write(Stream s, params byte[][] parts) { foreach (byte[] p in parts) s.Write(p, 0, p.Length); }

    private static byte[] Concat(params byte[][] parts) { var ms = new MemoryStream(); Write(ms, parts); return ms.ToArray(); }
}
