// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A synthetic ICC device-link profile (class <c>link</c>, RGB in, CMYK out) whose <c>A2B0</c> is an
/// affine CLUT between nonlinear input and output curves — affine so that every CMM's interpolation
/// (multilinear, tetrahedral) agrees inside a cell, the coefficients distinct per channel so a
/// transposed axis or channel shows. Version 2 writes a <c>lut16Type</c>, version 4 the same table as
/// a <c>lutAToBType</c>. The expected values in <see cref="ReferenceVectors"/> are a second independent CMM's
/// evaluation of the version 2 bytes, which its dumper reads as a well-formed link;
/// That CMM does not read version 4, so that one is held to the same numbers.
/// </summary>
public static class DeviceLinkProfile
{
    private static readonly double[] InCurve = { 0.0, 0.12, 0.35, 0.7, 1.0 };
    private static readonly double[] OutCurve = { 0.0, 0.4, 0.62, 0.85, 1.0 };
    private const int Grid = 3;
    private static readonly double[] Base = { 0.95, 0.90, 0.85, 0.30 };
    private static readonly double[,] Coef =
    {
        { -0.80, -0.05, -0.02 },
        { -0.04, -0.70, -0.08 },
        { -0.03, -0.06, -0.75 },
        { -0.10, -0.12, -0.05 },
    };

    /// <summary>
    /// Inputs and the reference's outputs: RGB as bytes (each read as byte / 255, ten places, by the reference),
    /// CMYK in 0..1 to six places.
    /// </summary>
    public static readonly (byte[] Rgb, double[] Cmyk)[] ReferenceVectors =
    {
        (new byte[] { 0, 0, 0 }, new[] { 0.969998, 0.940006, 0.910005, 0.443994 }),
        (new byte[] { 255, 255, 255 }, new[] { 0.128005, 0.128005, 0.015991, 0.047999 }),
        (new byte[] { 128, 128, 128 }, new[] { 0.751674, 0.721897, 0.669407, 0.327607 }),
        (new byte[] { 51, 153, 230 }, new[] { 0.898635, 0.604669, 0.249524, 0.299979 }),
        (new byte[] { 230, 26, 102 }, new[] { 0.378420, 0.885889, 0.736934, 0.308794 }),
        (new byte[] { 84, 196, 13 }, new[] { 0.855372, 0.518516, 0.869515, 0.308461 }),
    };

    /// <summary>
    /// The profile's bytes: <paramref name="version"/> 2 or 4, the header's input and output
    /// signatures (a refusal test changes them), and the table's output channels — each computed on
    /// its own, so a one-channel table's output is the four-channel table's first.
    /// </summary>
    public static byte[] Build(int version = 2, string outputSpace = "CMYK", string inputSpace = "RGB ", int outputs = 4)
    {
        byte[] lut = version >= 4 ? Mab(3, outputs) : Mft2(3, outputs);
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Pad4(TextDescription("tetitulz test link"))),
            ("cprt", Pad4(Text("No copyright, test data"))),
            ("A2B0", Pad4(lut)),
            ("pseq", Pad4(Concat(Ascii("pseq"), new byte[4], U32(0)))),
        };
        int tableLength = 4 + 12 * tags.Count;
        int offset = 128 + tableLength;
        var entries = new MemoryStream();
        var body = new MemoryStream();
        foreach (var (sig, data) in tags)
        {
            Write(entries, Ascii(sig), U32((uint)(offset + body.Length)), U32((uint)data.Length));
            body.Write(data, 0, data.Length);
        }
        int total = 128 + tableLength + (int)body.Length;

        var header = new MemoryStream();
        Write(header, U32((uint)total), new byte[4], U32(version >= 4 ? 0x04200000u : 0x02100000u),
              Ascii("link"), Ascii(inputSpace), Ascii(outputSpace));
        foreach (int v in new[] { 2026, 10, 18, 12, 0, 0 }) Write(header, U16((ushort)v));
        Write(header, Ascii("acsp"), new byte[4], U32(0), new byte[8], new byte[8], U32(0),
              S15(0.9642), S15(1.0), S15(0.8249), new byte[4], new byte[16]);
        header.Write(new byte[128 - header.Length], 0, 128 - (int)header.Length);

        return Concat(header.ToArray(), U32((uint)tags.Count), entries.ToArray(), body.ToArray());
    }

    private static byte[] Mft2(int inputs, int outputs)
    {
        var b = new MemoryStream();
        Write(b, Ascii("mft2"), new byte[4], new[] { (byte)inputs, (byte)outputs, (byte)Grid, (byte)0 });
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) Write(b, S15(r == c ? 1.0 : 0.0));
        Write(b, U16((ushort)InCurve.Length), U16((ushort)OutCurve.Length));
        for (int i = 0; i < inputs; i++) foreach (double v in InCurve) Write(b, U16Of(v));
        foreach (double v in ClutValues(inputs, outputs)) Write(b, U16Of(v));
        for (int k = 0; k < outputs; k++) foreach (double v in OutCurve) Write(b, U16Of(v));
        return b.ToArray();
    }

    private static byte[] Mab(int inputs, int outputs)
    {
        var bCurves = new MemoryStream();
        for (int k = 0; k < outputs; k++) Write(bCurves, Curve(OutCurve));
        var clut = new MemoryStream();
        var gridPoints = new byte[16];
        for (int i = 0; i < inputs; i++) gridPoints[i] = Grid;
        Write(clut, gridPoints, new byte[] { 2, 0, 0, 0 });
        foreach (double v in ClutValues(inputs, outputs)) Write(clut, U16Of(v));
        byte[] clutBytes = Pad4(clut.ToArray());
        var aCurves = new MemoryStream();
        for (int i = 0; i < inputs; i++) Write(aCurves, Curve(InCurve));

        const int headerLength = 32;
        uint bOff = headerLength, clutOff = bOff + (uint)bCurves.Length, aOff = clutOff + (uint)clutBytes.Length;
        var h = new MemoryStream();
        Write(h, Ascii("mAB "), new byte[4], new[] { (byte)inputs, (byte)outputs, (byte)0, (byte)0 },
              U32(bOff), U32(0), U32(0), U32(clutOff), U32(aOff));
        return Concat(h.ToArray(), bCurves.ToArray(), clutBytes, aCurves.ToArray());
    }

    /// <summary>The CLUT, the first input varying slowest (ICC.1 §10.8).</summary>
    private static List<double> ClutValues(int inputs, int outputs)
    {
        var values = new List<double>();
        var index = new int[inputs];
        int count = 1;
        for (int i = 0; i < inputs; i++) count *= Grid;
        for (int n = 0; n < count; n++)
        {
            int rest = n;
            for (int i = inputs - 1; i >= 0; i--) { index[i] = rest % Grid; rest /= Grid; }
            for (int k = 0; k < outputs; k++)
            {
                double v = Base[k];
                for (int i = 0; i < inputs; i++) v += Coef[k, i] * index[i] / (double)(Grid - 1);
                values.Add(Math.Min(1.0, Math.Max(0.0, v)));
            }
        }
        return values;
    }

    private static byte[] Curve(double[] points)
    {
        var b = new MemoryStream();
        Write(b, Ascii("curv"), new byte[4], U32((uint)points.Length));
        foreach (double v in points) Write(b, U16Of(v));
        return Pad4(b.ToArray());
    }

    private static byte[] TextDescription(string s)
    {
        byte[] ascii = Concat(Ascii(s), new byte[1]);
        return Concat(Ascii("desc"), new byte[4], U32((uint)ascii.Length), ascii, U32(0), U32(0),
                      U16(0), new byte[1], new byte[67]);
    }

    private static byte[] Text(string s) => Concat(Ascii("text"), new byte[4], Ascii(s), new byte[1]);

    private static byte[] Pad4(byte[] b) => b.Length % 4 == 0 ? b : Concat(b, new byte[4 - b.Length % 4]);
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U16Of(double v) => U16((ushort)Math.Round(v * 65535, MidpointRounding.AwayFromZero));
    private static byte[] S15(double v) => U32(unchecked((uint)(int)Math.Round(v * 65536, MidpointRounding.AwayFromZero)));

    private static void Write(Stream s, params byte[][] parts)
    {
        foreach (byte[] p in parts) s.Write(p, 0, p.Length);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        Write(ms, parts);
        return ms.ToArray();
    }
}
