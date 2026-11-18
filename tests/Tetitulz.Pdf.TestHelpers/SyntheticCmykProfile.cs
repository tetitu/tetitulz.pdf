// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A synthetic <c>prtr</c> CMYK output profile — a press with tables simple enough to reason about: <c>B2A0</c> and
/// <c>B2A1</c> share one lut16 over Lab, <c>A2B0</c> and <c>A2B1</c> another, so a destination black-point search has
/// round trips to walk. Shared by the colour engine's tests and the PDF/X converter's.
/// </summary>
public static class SyntheticCmykProfile
{
    /// <summary>A B2A table (3 Lab inputs → CMYK), grid 2, cyan constant.</summary>
    public static byte[] ConstantCyan(int c)
    {
        var clut = new int[8 * 4];
        for (int cell = 0; cell < 8; cell++) clut[cell * 4] = c;
        return Lut16(3, 4, 2, clut, OutputCurveIdentity(4));
    }

    /// <summary>K = 1 − L*/100 (grid 2 along L*), no chromatic ink.</summary>
    public static byte[] KFromLightness()
    {
        var clut = new int[8 * 4];
        for (int l = 0; l < 2; l++)
            for (int ab = 0; ab < 4; ab++)
                clut[((l * 4) + ab) * 4 + 3] = l == 0 ? 0xFFFF : 0;
        return Lut16(3, 4, 2, clut, OutputCurveIdentity(4));
    }

    /// <summary>
    /// CMYK → Lab: L* from K alone through an output curve with a toe — L* = 100·√(0.04 + 0.96·t²)
    /// for t = 1 − K, so full black is L* 20 and the round trip is curved near it — a* = b* = 0.
    /// </summary>
    public static byte[] LightnessFromKWithToe()
    {
        const int grid = 2;
        var clut = new int[16 * 3];
        for (int cell = 0; cell < 16; cell++)
        {
            bool kHigh = (cell & 1) != 0;                // K is the last input: varies fastest
            clut[cell * 3] = kHigh ? 0 : 0xFFFF;         // t = 1 - K, the curve below makes it L*
            clut[cell * 3 + 1] = 0x8080;                 // a* = 0 (legacy Lab: 128/255 scaled)
            clut[cell * 3 + 2] = 0x8080;
        }
        var curves = new List<int[]>();
        var toe = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0, lstar = 100 * Math.Sqrt(0.04 + 0.96 * t * t);
            toe[i] = (int)Math.Round(lstar / 100.0 * 0xFF00);   // legacy 16-bit Lab: 0xFF00 is L* 100
        }
        curves.Add(toe);
        curves.Add(Identity());
        curves.Add(Identity());
        return Lut16(4, 3, grid, clut, curves);
    }

    /// <summary>CMYK → Lab: L* = 100·(1 − K), no toe — full black is L* 0.</summary>
    public static byte[] LightnessFromK()
    {
        var clut = new int[16 * 3];
        for (int cell = 0; cell < 16; cell++)
        {
            clut[cell * 3] = (cell & 1) != 0 ? 0 : 0xFF00;   // legacy 16-bit Lab: 0xFF00 is L* 100
            clut[cell * 3 + 1] = 0x8080;
            clut[cell * 3 + 2] = 0x8080;
        }
        return Lut16(4, 3, 2, clut, new List<int[]> { Identity(), Identity(), Identity() });
    }

    /// <summary>A prtr CMYK profile: <paramref name="a2b"/> as <c>A2B1</c>, and as <c>A2B0</c> unless <paramref name="perceptualA2b"/> gives its own.</summary>
    /// <param name="b2a">The B2A table, for both intents.</param>
    /// <param name="a2b">The A2B table; <see cref="LightnessFromKWithToe"/> by default.</param>
    /// <param name="perceptualA2b">A separate perceptual A2B table, or null.</param>
    /// <param name="mediaWhite">A <c>wtpt</c> tag's X, Y, Z, or null for none.</param>
    public static byte[] Build(byte[] b2a, byte[]? a2b = null, byte[]? perceptualA2b = null, double[]? mediaWhite = null)
    {
        a2b ??= LightnessFromKWithToe();
        var tags = new List<(string, int)> { ("A2B0", perceptualA2b == null ? 1 : 2), ("A2B1", 1), ("B2A0", 0), ("B2A1", 0) };
        var blobList = new List<byte[]> { b2a, a2b };
        if (perceptualA2b != null) blobList.Add(perceptualA2b);
        if (mediaWhite != null)
        {
            tags.Add(("wtpt", blobList.Count));
            blobList.Add(XyzTag(mediaWhite[0], mediaWhite[1], mediaWhite[2]));
        }
        byte[][] blobs = blobList.ToArray();
        int[] at = new int[blobs.Length];
        int offset = 128 + 4 + tags.Count * 12;
        for (int i = 0; i < blobs.Length; i++) { at[i] = offset; offset += (blobs[i].Length + 3) & ~3; }

        var s = new MemoryStream();
        U32(s, (uint)offset);
        while (s.Length < 8) s.WriteByte(0);
        s.WriteByte(2); s.WriteByte(0x10); s.WriteByte(0); s.WriteByte(0);   // version 2.1
        Ascii(s, "prtr"); Ascii(s, "CMYK"); Ascii(s, "Lab ");
        while (s.Length < 36) s.WriteByte(0);
        Ascii(s, "acsp");
        while (s.Length < 128) s.WriteByte(0);
        U32(s, (uint)tags.Count);
        foreach (var (sig, blob) in tags) { Ascii(s, sig); U32(s, (uint)at[blob]); U32(s, (uint)blobs[blob].Length); }
        for (int i = 0; i < blobs.Length; i++)
        {
            s.Write(blobs[i], 0, blobs[i].Length);
            while (s.Length % 4 != 0) s.WriteByte(0);
        }
        return s.ToArray();
    }

    /// <summary>A press whose CMYK is black alone, from lightness — <see cref="KFromLightness"/> over its default A2B.</summary>
    public static byte[] BlackFromLightness() => Build(KFromLightness());

    /// <summary>An XYZType tag (ICC.1 §10.31): signature, reserved, three s15Fixed16 values.</summary>
    private static byte[] XyzTag(double x, double y, double z)
    {
        var s = new MemoryStream();
        Ascii(s, "XYZ ");
        U32(s, 0);
        foreach (double v in new[] { x, y, z }) U32(s, unchecked((uint)(int)Math.Round(v * 65536.0)));
        return s.ToArray();
    }

    private static int[] Identity()
    {
        var c = new int[256];
        for (int i = 0; i < 256; i++) c[i] = (int)Math.Round(i * 65535.0 / 255);
        return c;
    }

    private static List<int[]> OutputCurveIdentity(int n)
    {
        var l = new List<int[]>();
        for (int i = 0; i < n; i++) l.Add(Identity());
        return l;
    }

    /// <summary>An <c>mft2</c> (lut16Type): identity matrix, 2-entry identity input curves, the CLUT, 256-entry output curves.</summary>
    private static byte[] Lut16(int inputs, int outputs, int grid, int[] clut, List<int[]> outputCurves)
    {
        var s = new MemoryStream();
        Ascii(s, "mft2"); U32(s, 0);
        s.WriteByte((byte)inputs); s.WriteByte((byte)outputs); s.WriteByte((byte)grid); s.WriteByte(0);
        int[] identity = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000 };
        foreach (int v in identity) U32(s, (uint)v);
        U16(s, 2); U16(s, 256);
        for (int i = 0; i < inputs; i++) { U16(s, 0); U16(s, 0xFFFF); }
        foreach (int v in clut) U16(s, v);
        foreach (int[] curve in outputCurves) foreach (int v in curve) U16(s, v);
        return s.ToArray();
    }

    private static void Ascii(Stream s, string t) { byte[] b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, b.Length); }
    private static void U32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
}
