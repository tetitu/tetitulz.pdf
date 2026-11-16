// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A <see cref="MinimalTrueType"/> font whose <c>glyf</c> holds glyphs a test wrote point by point —
/// simple glyphs from contours, composites from component records — so an outline reader's every
/// coordinate is known. Glyph 0 is an empty <c>.notdef</c>; glyph <c>k</c> is reached by the
/// character <c>'A' + k − 1</c>.
/// </summary>
public static class OutlineTrueType
{
    /// <summary>One point of a contour.</summary>
    public readonly record struct Point(int X, int Y, bool OnCurve = true);

    /// <summary>A simple glyph: every coordinate a word delta, no flag repeats, no instructions.</summary>
    public static byte[] Simple(params Point[][] contours) => Simple(contours, null, compact: false);

    /// <summary>
    /// A simple glyph with <paramref name="instructions"/> after its end points; when
    /// <paramref name="compact"/>, written as fonts write them — a zero delta as "same" (no bytes), a
    /// delta under 256 as a byte with its sign in the flag, and a run of equal flags as one flag with
    /// <c>REPEAT_FLAG</c> and a count.
    /// </summary>
    public static byte[] Simple(Point[][] contours, byte[] instructions, bool compact)
    {
        var s = new MemoryStream();
        W16(s, contours.Length);
        for (int k = 0; k < 4; k++) W16(s, 0);                     // bounding box, unread
        int end = -1;
        foreach (Point[] c in contours) W16(s, end += c.Length);
        W16(s, instructions?.Length ?? 0);
        if (instructions != null) s.Write(instructions, 0, instructions.Length);

        Point[] all = contours.SelectMany(c => c).ToArray();
        var flags = new byte[all.Length];
        var xs = new MemoryStream();
        var ys = new MemoryStream();
        int x = 0, y = 0;
        for (int k = 0; k < all.Length; k++)
        {
            int dx = all[k].X - x, dy = all[k].Y - y;
            x = all[k].X; y = all[k].Y;
            int flag = all[k].OnCurve ? 1 : 0;
            flag |= Axis(dx, xs, compact, shortFlag: 0x02, sameFlag: 0x10);
            flag |= Axis(dy, ys, compact, shortFlag: 0x04, sameFlag: 0x20);
            flags[k] = (byte)flag;
        }
        for (int k = 0; k < flags.Length;)
        {
            int run = 1;
            while (compact && k + run < flags.Length && flags[k + run] == flags[k] && run < 256) run++;
            if (run > 1) { s.WriteByte((byte)(flags[k] | 0x08)); s.WriteByte((byte)(run - 1)); }
            else s.WriteByte(flags[k]);
            k += run;
        }
        xs.WriteTo(s);
        ys.WriteTo(s);
        return s.ToArray();
    }

    /// <summary>One axis's delta: its bytes into <paramref name="to"/>, its flag bits returned.</summary>
    private static int Axis(int delta, Stream to, bool compact, int shortFlag, int sameFlag)
    {
        if (compact && delta == 0) return sameFlag;
        if (compact && delta > -256 && delta < 256)
        {
            to.WriteByte((byte)System.Math.Abs(delta));
            return shortFlag | (delta > 0 ? sameFlag : 0);
        }
        W16(to, delta);
        return 0;
    }

    /// <summary>A composite glyph from <see cref="Component"/> records; <c>MORE_COMPONENTS</c> set on all but the last.</summary>
    public static byte[] Composite(params byte[][] records)
    {
        var s = new MemoryStream();
        W16(s, -1);
        for (int k = 0; k < 4; k++) W16(s, 0);
        for (int k = 0; k < records.Length; k++)
        {
            byte[] r = (byte[])records[k].Clone();
            if (k < records.Length - 1) r[1] |= 0x20;
            s.Write(r, 0, r.Length);
        }
        return s.ToArray();
    }

    /// <summary>
    /// A component record with word arguments: offsets (<c>ARGS_ARE_XY_VALUES</c>) unless
    /// <paramref name="pointMatching"/>, then <paramref name="transform"/>'s one, two or four F2Dot14
    /// words (a scale, an x and y scale, or the 2 × 2 in the record's order).
    /// </summary>
    public static byte[] Component(int glyph, int arg1, int arg2, int extraFlags = 0, bool pointMatching = false,
                                   params double[] transform)
    {
        var s = new MemoryStream();
        int flags = 0x0001 | (pointMatching ? 0 : 0x0002) | extraFlags
                  | (transform.Length == 1 ? 0x0008 : transform.Length == 2 ? 0x0040 : transform.Length == 4 ? 0x0080 : 0);
        W16(s, flags);
        W16(s, glyph);
        W16(s, arg1);
        W16(s, arg2);
        foreach (double t in transform) W16(s, (int)System.Math.Round(t * 16384));
        return s.ToArray();
    }

    /// <summary>
    /// The font: <paramref name="glyphs"/> as glyphs 1, 2, …, at <paramref name="unitsPerEm"/>, each 600
    /// units wide, reached by <paramref name="characters"/> in order (ascending; 'A', 'B', … when null) —
    /// through a (3,1) Unicode cmap, or when <paramref name="symbolic"/> a (3,0) symbol cmap with each
    /// code at U+F000 + code, as a Windows symbol font has it.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> glyphs, int unitsPerEm = 1000, bool longLoca = false,
                               string characters = null, bool symbolic = false)
    {
        var glyf = new MemoryStream();
        var offsets = new List<int> { 0, 0 };
        foreach (byte[] g in glyphs)
        {
            glyf.Write(g, 0, g.Length);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            offsets.Add((int)glyf.Length);
        }
        var loca = new MemoryStream();
        foreach (int o in offsets)
        {
            if (longLoca) { W16(loca, o >> 16); W16(loca, o & 0xFFFF); }
            else W16(loca, o / 2);
        }

        characters ??= new string(Enumerable.Range(0, glyphs.Count).Select(k => (char)('A' + k)).ToArray());
        if (characters.Length != glyphs.Count || !characters.SequenceEqual(characters.OrderBy(c => c)))
            throw new System.ArgumentException("one ascending character a glyph", nameof(characters));
        var extra = new Dictionary<string, byte[]>
        {
            ["glyf"] = glyf.ToArray(),
            ["loca"] = loca.ToArray(),
        };
        if (symbolic) extra["cmap"] = MinimalTrueType.BuildCmap(characters.Select(c => (int)c).ToList(), 3, 0, 0xF000);
        byte[] font = MinimalTrueType.BuildWithTables(characters, extra);

        if (!SfntTableDirectory.TryRead(font, out IReadOnlyList<SfntTableDirectory.TableRecord> tables, out string reason))
            throw new System.InvalidOperationException(reason);
        int head = tables.Single(t => t.Tag == "head").Offset;
        font[head + 18] = (byte)(unitsPerEm >> 8);
        font[head + 19] = (byte)unitsPerEm;
        font[head + 50] = 0;
        font[head + 51] = (byte)(longLoca ? 1 : 0);
        return font;
    }

    private static void W16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
}
