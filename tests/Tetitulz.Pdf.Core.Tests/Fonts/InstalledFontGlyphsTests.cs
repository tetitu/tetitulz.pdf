// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="InstalledFontGlyphs"/>: a program's glyphs given their text by the installed font the
/// program was taken from — the face found by its <c>head</c> (every field a copy keeps) and each
/// glyph confirmed by its bytes. The fonts are written into a directory registered on the search path,
/// so nothing depends on what the host has installed.
/// </summary>
[Collection("FontSearchPath")]   // registers a directory: process-global
public sealed class InstalledFontGlyphsTests
{
    // 34 bytes ending in a non-zero delta: two bytes of padding in glyf, so a glyph one byte short is
    // within the padding's reach and only its unmatched byte tells it apart.
    private static readonly byte[] Square = OutlineTrueType.Simple(new[]
    {
        new OutlineTrueType.Point[] { new(0, 0), new(500, 0), new(500, 500), new(0, 499) },
    });

    private static readonly byte[] Triangle = OutlineTrueType.Simple(new[]
    {
        new OutlineTrueType.Point[] { new(0, 0), new(600, 0), new(300, 700) },
    });

    private static readonly byte[] Font = OutlineTrueType.Build(new[] { Square, Triangle }, characters: "AB");

    private static (int Offset, int Length) Table(byte[] font, string tag)
    {
        Assert.True(SfntTableDirectory.TryRead(font, out var tables, out string reason), reason);
        var record = tables.Single(t => t.Tag == tag);
        return (record.Offset, record.Length);
    }

    private static byte[] Head(byte[] font)
    {
        var (offset, length) = Table(font, "head");
        return font.Skip(offset).Take(length).ToArray();
    }

    /// <summary>A glyph's bytes in <paramref name="font"/>, as its <c>loca</c> locates them.</summary>
    private static byte[] GlyphBytes(byte[] font, int glyph)
    {
        var (loca, locaLength) = Table(font, "loca");
        var (glyf, glyfLength) = Table(font, "glyf");
        byte[] head = Head(font);
        return InstalledFontGlyphs.GlyphOf(font.Skip(loca).Take(locaLength).ToArray(),
                                           font.Skip(glyf).Take(glyfLength).ToArray(), (short)((head[50] << 8) | head[51]), glyph);
    }

    /// <summary>Runs <paramref name="test"/> with <paramref name="files"/> written into a directory on the search path.</summary>
    private static void Installed(Action test, params (string Name, byte[] Bytes)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(dir, name), bytes);
        try
        {
            FontSearchPath.Add(dir);
            test();
        }
        finally
        {
            FontSearchPath.Remove(dir);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The installed font's <c>cmap</c> read backwards: glyph 1 is A, glyph 2 B — found from a
    /// <c>head</c> whose checksum adjustment and <c>loca</c> format a re-assembled program changed.
    /// </summary>
    [Fact]
    public void AnInstalledFont_GivesEachGlyphItsText()
        => Installed(() =>
        {
            byte[] head = Head(Font);
            head[8] ^= 0xFF;   // checkSumAdjustment
            head[51] ^= 1;     // indexToLocFormat
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(head);
            Assert.NotNull(installed);
            Assert.Equal("A", installed!.TextOf(1, GlyphBytes(Font, 1)));
            Assert.Equal("B", installed.TextOf(2, GlyphBytes(Font, 2)));
        }, ("font.ttf", Font));

    /// <summary>A glyph whose bytes are not the installed glyph's gets no text: the number alone is no evidence.</summary>
    [Fact]
    public void AGlyphWhoseBytesDiffer_GetsNoText()
        => Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(Font))!;
            Assert.Null(installed.TextOf(1, GlyphBytes(Font, 2)));
            byte[] moved = GlyphBytes(Font, 1);
            moved[3] ^= 1;                                  // the same length, xMin one unit off
            Assert.Null(installed.TextOf(1, moved));
            Assert.Equal("A", installed.TextOf(1, GlyphBytes(Font, 1)));
        }, ("font.ttf", Font));

    /// <summary>A <c>head</c> that differs in a field a copy keeps — here the font revision — finds no font; the same <c>head</c> does.</summary>
    [Fact]
    public void AHeadDifferingInAKeptField_FindsNoFont()
        => Installed(() =>
        {
            byte[] other = Head(Font);
            other[7] ^= 1;
            Assert.Null(InstalledFontGlyphs.TryIdentify(other));
            Assert.NotNull(InstalledFontGlyphs.TryIdentify(Head(Font)));
        }, ("font.ttf", Font));

    /// <summary>A few zero bytes of padding on either side are the same glyph; a non-zero byte, or four of padding, is not.</summary>
    [Fact]
    public void ZeroPaddingIsTolerated_OtherBytesAreNot()
        => Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(Font))!;
            byte[] glyph = GlyphBytes(Font, 1);
            byte[] trimmed = glyph.Reverse().SkipWhile(b => b == 0).Reverse().ToArray();
            Assert.Equal("A", installed.TextOf(1, trimmed.Concat(new byte[] { 0, 0 }).ToArray()));
            Assert.Null(installed.TextOf(1, glyph.Concat(new byte[] { 0, 1 }).ToArray()));          // past the installed glyph, not zero
            Assert.Null(installed.TextOf(1, trimmed.Take(trimmed.Length - 1).ToArray()));          // the installed glyph's last byte unmatched
            Assert.Null(installed.TextOf(1, glyph.Concat(new byte[] { 0, 0, 0, 0 }).ToArray()));
        }, ("font.ttf", Font));

    /// <summary>
    /// A presentation form — the glyph a driver shaped, which the font's cmap reaches only through it —
    /// is read as its letters: bet with dagesh as bet and dagesh, an initial meem as meem, the lam-alef
    /// ligature as lam and alef; so the text searches as typed.
    /// </summary>
    [Fact]
    public void PresentationForms_AreReadAsTheirLetters()
    {
        byte[] forms = OutlineTrueType.Build(new[] { Square, Triangle, Square }, characters: "בּﻣﻻ");
        Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(forms))!;
            Assert.Equal("בּ", installed.TextOf(1, GlyphBytes(forms, 1)));
            Assert.Equal("م", installed.TextOf(2, GlyphBytes(forms, 2)));
            Assert.Equal("لا", installed.TextOf(3, GlyphBytes(forms, 3)));
        }, ("forms.ttf", forms));
    }

    /// <summary>A symbol font's code points are private-use (U+F000 + code): no text is read from them.</summary>
    [Fact]
    public void PrivateUseCodePoints_AreNoText()
    {
        byte[] symbolic = OutlineTrueType.Build(new[] { Square, Triangle }, characters: "AB", symbolic: true);
        Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(symbolic))!;
            Assert.Null(installed.TextOf(1, GlyphBytes(symbolic, 1)));
        }, ("symbol.ttf", symbolic));
    }

    /// <summary>
    /// A face of a collection is read, and of two faces sharing a <c>head</c> the one whose glyph has
    /// the bytes gives the text: the second face's glyph 1 is C.
    /// </summary>
    [Fact]
    public void AFaceOfACollection_GivesItsGlyphsText()
    {
        byte[] second = OutlineTrueType.Build(new[] { Triangle, Square }, characters: "CD");
        Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(second))!;
            Assert.Equal(2, installed.CandidateCount);
            Assert.Equal("C", installed.TextOf(1, GlyphBytes(second, 1)));
            Assert.Equal("A", installed.TextOf(1, GlyphBytes(Font, 1)));
        }, ("faces.ttc", Collection(Font, second)));
    }

    /// <summary>A glyph number past the font, or below zero, gets no text and no glyph.</summary>
    [Fact]
    public void AGlyphOutsideTheFont_GetsNothing()
        => Installed(() =>
        {
            InstalledFontGlyphs installed = InstalledFontGlyphs.TryIdentify(Head(Font))!;
            Assert.Null(installed.TextOf(9, GlyphBytes(Font, 1)));
            Assert.Null(installed.TextOf(-1, GlyphBytes(Font, 1)));
            Assert.Null(GlyphBytes(Font, 3));
            Assert.NotNull(GlyphBytes(Font, 2));
        }, ("font.ttf", Font));

    /// <summary>A TrueType collection of <paramref name="fonts"/>: each one's table offsets made the file's.</summary>
    private static byte[] Collection(params byte[][] fonts)
    {
        int header = 12 + 4 * fonts.Length;
        var offsets = new List<int>();
        int at = header;
        foreach (byte[] font in fonts)
        {
            offsets.Add(at);
            at += (font.Length + 3) & ~3;
        }
        var file = new byte[at];
        "ttcf"u8.CopyTo(file);
        Put32(file, 4, 0x00010000);
        Put32(file, 8, fonts.Length);
        for (int k = 0; k < fonts.Length; k++)
        {
            Put32(file, 12 + 4 * k, offsets[k]);
            Array.Copy(fonts[k], 0, file, offsets[k], fonts[k].Length);
            int tables = (fonts[k][4] << 8) | fonts[k][5];
            for (int t = 0; t < tables; t++)
            {
                int record = offsets[k] + 12 + 16 * t + 8;
                Put32(file, record, Get32(file, record) + offsets[k]);
            }
        }
        return file;
    }

    private static int Get32(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

    private static void Put32(byte[] d, int at, int value)
    {
        d[at] = (byte)(value >> 24);
        d[at + 1] = (byte)(value >> 16);
        d[at + 2] = (byte)(value >> 8);
        d[at + 3] = (byte)value;
    }
}
