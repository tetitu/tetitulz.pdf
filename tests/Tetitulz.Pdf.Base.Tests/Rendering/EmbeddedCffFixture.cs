// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A one-page PDF carrying a real embedded bare-CFF font program (<c>/FontFile3</c>
/// <c>/Subtype /Type1C</c>), for the raster tests that need the <b>embedded</b> glyph path rather
/// than a system substitute.
///
/// <para><b>Which renderer path this reaches, and why that is worth stating.</b> It reaches
/// <c>DrawOtfCffRun</c> — <b>not</b> <c>DrawGlyphPath</c>. <c>FontCache</c> wraps a bare CFF in an
/// OTF envelope (<c>CffOtfWrapper.TryBuild</c>) so Skia applies Type 2 hinting; that sets
/// <c>IsOtfCff</c> and routes the run to the GID path. <c>HorizontalScalingGlyphOutlineTests</c>
/// established this by mutation: a defect injected into <c>DrawGlyphPath</c> <i>survives</i> this
/// fixture. There is no fixture shape here that reaches <c>DrawGlyphPath</c> through an embedded
/// CFF while the wrap succeeds, and the Type 1 route to it is host-dependent (FontCache tries a PFB
/// wrap first and returns whatever typeface Skia's Type 1 driver produces, where it has
/// one).</para>
///
/// <para>Shared rather than copied: two test classes now build the same font, and the CFF byte
/// layout is fiddly enough that a second copy would drift.</para>
/// </summary>
internal static class EmbeddedCffFixture
{
    /// <summary>The character code the fixture font maps to its one drawable glyph.</summary>
    public const int GlyphCharCode = 65;   // 'A'

    /// <summary>Side of the filled box the glyph draws, in 1000-unit em glyph space.</summary>
    public const int GlyphBoxEm = 400;

    /// <summary>The glyph's advance width, in 1000-unit em glyph space.</summary>
    public const int GlyphAdvanceEm = 500;

    /// <summary>
    /// The <c>/Widths</c> entry the <see cref="CorruptAdvanceCffProgram"/> page declares, in the
    /// same units.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="GlyphAdvanceEm"/>, and deliberately not half an em.</b> These
    /// glyph paths take their advance from the font program, never from <c>/Widths</c> — but
    /// <see cref="GlyphAdvanceEm"/> <i>is</i> exactly half an em, so had this page declared it, a
    /// renderer that quietly fell back to <c>/Widths</c> would place the next glyph at the same
    /// point as the half-em guard and no assertion could tell the two apart. 800 makes them differ.
    /// </remarks>
    public const int CorruptAdvanceWidthsEm = 800;

    /// <summary>
    /// One page whose <c>/F1</c> is the embedded CFF font and whose content stream is
    /// <paramref name="content"/>. The page is 612x792, so
    /// <see cref="PixelAssert.Render(byte[], int)"/> at 72 dpi gives 1 px per point.
    /// </summary>
    /// <param name="extraPageResources">
    /// Additional entries for the page's <c>/Resources</c> dictionary, e.g.
    /// <c>"/ExtGState &lt;&lt; /GS0 8 0 R &gt;&gt;"</c>.
    /// </param>
    /// <param name="extraObjects">
    /// Complete <c>N 0 obj … endobj</c> bodies numbered from <b>8</b> upward (1-7 are the page
    /// skeleton and the font), appended after the font program and cross-referenced.
    /// </param>
    /// <param name="corruptAdvance">
    /// When true the font program is built by <see cref="CorruptAdvanceCffProgram"/> instead, whose
    /// glyph has <b>no advance width this library can use</b>. Everything else about the page is
    /// identical, so the two are a matched pair.
    /// </param>
    public static byte[] Page(string content, string extraPageResources = null,
                              string extraObjects = null, bool corruptAdvance = false)
    {
        byte[] cff = corruptAdvance ? CorruptAdvanceCffProgram() : MinimalCffProgram();
        int widths = corruptAdvance ? CorruptAdvanceWidthsEm : GlyphAdvanceEm;
        string objects =
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /TestCff "
          + $"/FirstChar {GlyphCharCode} /LastChar {GlyphCharCode} /Widths [{widths}] "
          + "/FontDescriptor 6 0 R >>\nendobj\n"
          + "6 0 obj\n<< /Type /FontDescriptor /FontName /TestCff /Flags 4 "
          + $"/FontBBox [0 0 {GlyphAdvanceEm} {GlyphBoxEm}] /ItalicAngle 0 /Ascent {GlyphBoxEm} "
          + $"/Descent 0 /CapHeight {GlyphBoxEm} /StemV 80 /FontFile3 7 0 R >>\nendobj\n"
          + $"7 0 obj\n<< /Subtype /Type1C /Length {cff.Length} >>\nstream\n";

        string resources = "/Font << /F1 5 0 R >>"
                         + (extraPageResources == null ? string.Empty : " " + extraPageResources);

        return SinglePage(content, resources, objects,
                          extraBinary: cff, extraTail: "\nendstream\nendobj\n",
                          extraObjectCount: 3 + CountObjects(extraObjects),
                          trailingObjects: extraObjects);
    }

    /// <summary>Number of <c>N 0 obj</c> headers in a body block, so the xref sizes itself.</summary>
    private static int CountObjects(string bodies)
    {
        if (string.IsNullOrEmpty(bodies)) return 0;
        int n = 0, at = 0;
        while ((at = bodies.IndexOf(" 0 obj", at, StringComparison.Ordinal)) >= 0) { n++; at += 6; }
        return n;
    }

    /// <summary>
    /// A valid CFF binary: GID 0 <c>.notdef</c>, GID 1 a <see cref="GlyphBoxEm"/>-square filled box
    /// of width <see cref="GlyphAdvanceEm"/>, and a format-0 encoding mapping
    /// <see cref="GlyphCharCode"/> to GID 1. A solid box is what makes the ink measurable — a real
    /// letter's bounds depend on its outline rather than on the matrix under test.
    ///
    /// <para>Ported from <c>CffParserTests.BuildMinimalCff</c>, which lives in <c>Core.Tests</c>;
    /// this project cannot reference another test project (CLAUDE.md: no cross-project test
    /// dependencies), so it is a copy, as the benchmark harness's own copy is.</para>
    /// </summary>
    public static byte[] MinimalCffProgram() => BuildCff(corruptAdvance: false);

    /// <summary>
    /// The same font with <b>an advance width no arithmetic can use</b>: the glyph's charstring
    /// supplies no width operand, so §9's <c>defaultWidthX</c> applies, and this font's
    /// <c>defaultWidthX</c> is the CFF real <c>9E99</c> — finite as the <c>double</c> the DICT
    /// parser reads and <c>+Infinity</c> the moment it becomes the <c>float</c> the renderers use.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the fixture shape GAPS.md §9 said was missing.</b> Both renderers pass every
    /// glyph advance through <c>MatrixGuard.SanitizeAdvance</c>, whose half-em fallback keeps a run
    /// moving when the file supplies no usable advance — and the rule was pinned directly while
    /// <b>every call site survived being bypassed</b>, because no fixture in the tree embedded a
    /// font that could produce one. An overflowing <c>Tf</c> reaches the same multiplication, but
    /// only by making the whole run's geometry non-finite, which no on-page assertion can then
    /// distinguish.</para>
    /// <para><b>Nothing here is invalid CFF.</b> A charstring without a width operand is the normal
    /// encoding for a glyph whose advance <i>is</i> <c>defaultWidthX</c>, and a DICT real carries an
    /// exponent by design (nibble <c>0xB</c>). What this font does is legal and unusable, which is
    /// exactly the case the guard exists for.</para>
    /// </remarks>
    public static byte[] CorruptAdvanceCffProgram() => BuildCff(corruptAdvance: true);

    private static byte[] BuildCff(bool corruptAdvance)
    {
        byte[] cs0 = { 14 };                       // .notdef: endchar
        byte[] withWidth =
        {
            248, 136,           // width = 500
            139, 139, 21,       // rmoveto(0, 0)
            139, 248, 36,       // 0, 400
            248, 36, 139,       // 400, 0
            139, 252, 36,       // 0, -400
            5,                  // rlineto
            14                  // endchar
        };
        // The same glyph with the leading width operand removed: rmoveto then has its own two
        // arguments and no third, which is what makes defaultWidthX the advance.
        byte[] noWidth =
        {
            139, 139, 21,
            139, 248, 36,
            248, 36, 139,
            139, 252, 36,
            5,
            14
        };
        byte[] cs1 = corruptAdvance ? noWidth : withWidth;

        // Private DICT: defaultWidthX = the BCD real 9E99. Nibbles 9, E, 9, 9, terminator — see
        // CffParser.AppendBcdNibble, where 0xB is the exponent marker.
        byte[] privateDict = { 30, 0x9B, 0x99, 0xFF, 20 };

        byte[] encoding = { 0, 1, GlyphCharCode };  // format 0: code 65 -> GID 1
        byte[] charset  = { 0, 0, 1 };              // format 0: GID 1 -> SID 1
        byte[] csIndex  = CffIndex(new[] { cs0, cs1 });

        byte[] nameIndex       = CffIndex(new[] { Encoding.ASCII.GetBytes("TestCff") });
        byte[] stringIndex     = CffIndex(Array.Empty<byte[]>());
        byte[] globalSubrIndex = CffIndex(Array.Empty<byte[]>());

        // The Top DICT stores absolute offsets, so its size must be known before they can be
        // computed. Build it once with placeholders to fix the size, then again for real.
        int privSize = corruptAdvance ? privateDict.Length : 0;
        byte[] placeholder = CffIndex(new[] { CffTopDict(0, 0, 0, privSize, 0, corruptAdvance) });

        int encodingOffset = 4 + nameIndex.Length + placeholder.Length
                           + stringIndex.Length + globalSubrIndex.Length;
        int charsetOffset  = encodingOffset + encoding.Length;
        int csIndexOffset  = charsetOffset  + charset.Length;
        int privateOffset  = csIndexOffset  + csIndex.Length;   // the Private DICT goes last

        byte[] topDict = CffIndex(new[] {
            CffTopDict(encodingOffset, charsetOffset, csIndexOffset,
                       privSize, privateOffset, corruptAdvance) });
        Assert.True(topDict.Length == placeholder.Length,
            "The CFF Top DICT changed size once real offsets were encoded, so the fixture's "
          + "single-pass offset computation is no longer valid and the font would not parse.");

        var cff = new MemoryStream();
        void W(byte[] b) => cff.Write(b, 0, b.Length);
        W(new byte[] { 1, 0, 4, 1 });              // major, minor, hdrSize, offSize
        W(nameIndex); W(topDict); W(stringIndex); W(globalSubrIndex);
        W(encoding); W(charset); W(csIndex);
        if (corruptAdvance) W(privateDict);
        return cff.ToArray();
    }

    private static byte[] CffTopDict(int encodingOff, int charsetOff, int csOff,
                                     int privSize, int privOff, bool includePrivate)
    {
        var d = new List<byte>();
        CffInt(d, charsetOff);  d.Add(15);   // charset
        CffInt(d, encodingOff); d.Add(16);   // Encoding
        CffInt(d, csOff);       d.Add(17);   // CharStrings
        if (includePrivate)
        {
            // Fixed-width, unlike the three above: the Private entry's operands are a size and an
            // offset that change between the placeholder pass and the real one, and the compact
            // encoding would change length with them — which the assertion above would then catch
            // as a fixture that cannot lay itself out rather than as the mistake it is.
            CffInt32(d, privSize); CffInt32(d, privOff); d.Add(18);   // Private [size offset]
        }
        return d.ToArray();
    }

    /// <summary>The always-5-byte integer encoding, for an operand whose value is not yet known.</summary>
    private static void CffInt32(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)((uint)v >> 24)); d.Add((byte)((uint)v >> 16));
        d.Add((byte)((uint)v >> 8));  d.Add((byte)v);
    }

    private static void CffInt(List<byte> d, int v)
    {
        if (v >= -107 && v <= 107) { d.Add((byte)(v + 139)); }
        else if (v >= 108 && v <= 1131)
        {
            int b = v - 108;
            d.Add((byte)(247 + b / 256));
            d.Add((byte)(b % 256));
        }
        else
        {
            d.Add(29);
            d.Add((byte)((uint)v >> 24)); d.Add((byte)((uint)v >> 16));
            d.Add((byte)((uint)v >> 8));  d.Add((byte)v);
        }
    }

    private static byte[] CffIndex(byte[][] items)
    {
        if (items.Length == 0) return new byte[] { 0, 0 };

        int totalDataLen = 0;
        foreach (byte[] item in items) totalDataLen += item.Length;

        byte offSize = totalDataLen + 1 <= 0xFF ? (byte)1
                     : totalDataLen + 1 <= 0xFFFF ? (byte)2 : (byte)3;

        var idx = new List<byte>
        {
            (byte)(items.Length >> 8), (byte)(items.Length & 0xFF), offSize
        };

        void WriteOffset(int off)
        {
            for (int i = offSize - 1; i >= 0; i--) idx.Add((byte)((off >> (i * 8)) & 0xFF));
        }

        int running = 1;                          // INDEX offsets are 1-based
        WriteOffset(running);
        foreach (byte[] item in items) { running += item.Length; WriteOffset(running); }
        foreach (byte[] item in items) idx.AddRange(item);
        return idx.ToArray();
    }

    internal static byte[] SinglePage(string content, string resources, string extraObject,
                                     byte[] extraBinary = null, string extraTail = null,
                                     int extraObjectCount = 1, string trailingObjects = null)
    {
        var file = new MemoryStream();
        void Write(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            file.Write(b, 0, b.Length);
        }

        int objectCount = extraObject == null ? 5 : 5 + extraObjectCount;
        var offsets = new long[objectCount];

        Write("%PDF-1.4\n");

        offsets[1] = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = file.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              $"/Contents 4 0 R /Resources << {resources} >> >>\nendobj\n");

        byte[] contentBytes = Encoding.ASCII.GetBytes(content);
        offsets[4] = file.Position;
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        file.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        if (extraObject != null)
        {
            long extraStart = file.Position;
            offsets[5] = extraStart;
            Write(extraObject);
            if (extraBinary != null) file.Write(extraBinary, 0, extraBinary.Length);
            if (extraTail != null) Write(extraTail);

            // Object bodies are pure ASCII, so a char index into the block is a byte offset.
            for (int n = 6; n < objectCount; n++)
            {
                int at = extraObject.IndexOf($"{n} 0 obj", StringComparison.Ordinal);
                if (at >= 0) offsets[n] = extraStart + at;
            }

            if (trailingObjects != null)
            {
                long trailStart = file.Position;
                Write(trailingObjects);
                for (int n = 6; n < objectCount; n++)
                {
                    if (offsets[n] != 0) continue;
                    int at = trailingObjects.IndexOf($"{n} 0 obj", StringComparison.Ordinal);
                    if (at >= 0) offsets[n] = trailStart + at;
                }
            }

            for (int n = 6; n < objectCount; n++)
                Assert.True(offsets[n] > 0,
                    $"The fixture declares {extraObjectCount} extra objects but object {n} is missing.");
        }

        long xrefOffset = file.Position;
        Write($"xref\n0 {objectCount}\n0000000000 65535 f \n");
        for (int i = 1; i < objectCount; i++)
            Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return file.ToArray();
    }
}
