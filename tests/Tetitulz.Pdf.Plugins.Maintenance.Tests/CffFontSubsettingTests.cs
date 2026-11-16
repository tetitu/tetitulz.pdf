// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Re-subsetting a CFF font that is <b>already embedded</b> in a document —
/// <c>OptimizationOptions.SubsetEmbeddedFonts</c> over a <c>CIDFontType0</c>. The TrueType half of
/// this has existed since 2026-08-13; the CFF half was refused outright
/// (<c>"the font has CFF outlines, which this pruner does not rewrite"</c>) until the charstring
/// blanker landed.
///
/// <para><b>Why blanking is the right mechanism here specifically.</b> An already-embedded font's
/// glyph indices are load-bearing: the content stream's CIDs and the <c>/W</c> array both address
/// them, and nothing in the file may renumber. Blanking cannot renumber — it changes charstring
/// contents and no offset at all — which is the same property <c>TrueTypeGlyphPruner</c> achieves
/// by keeping <c>loca</c> slots and reaching by a different route.</para>
///
/// <para><b>Two traps these are written around.</b> The program length never changes, so
/// <c>pruned.Length &lt; original.Length</c> is false for every correct result and only the
/// <i>compressed</i> stream shrinks. And a CID is <b>not</b> a glyph index: the fixture's charset is
/// deliberately not the identity, so a subsetter that skipped the charset lookup would blank the
/// glyph the page draws and keep one it does not.</para>
/// </summary>
public class CffFontSubsettingTests
{
    /// <summary>CIDs the fixture font declares, in GID order. GID 1 ← CID 10, GID 2 ← CID 20, …</summary>
    private static readonly int[] CidsByGid = { 10, 20, 30, 40, 50 };

    /// <summary>The CID the page draws, and the GID its charset entry points at.</summary>
    private const int DrawnCid = 20;
    private const int DrawnGid = 2;

    // =====================================================================
    //  The pass runs, and blanks the right glyphs
    // =====================================================================

    /// <summary>
    /// The load-bearing claim: the embedded CFF comes out with the drawn glyph intact and every
    /// other charstring emptied.
    /// </summary>
    [Fact]
    public void Optimize_SubsetsAnEmbeddedCffFontToTheGlyphsThePageDraws()
    {
        byte[] original = CidKeyedCff(CidsByGid);
        byte[] pdf = PdfWithEmbeddedCidFontType0(original);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(original);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);
        Assert.True(result.FontBytesSaved > 0,
            $"FontsSubset was {result.FontsSubset} but FontBytesSaved was {result.FontBytesSaved}");

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(ExtractFontProgram(optimized));

        Assert.True(before[DrawnGid].SequenceEqual(after[DrawnGid]),
            $"GID {DrawnGid}, which CID {DrawnCid} reaches, was blanked — the page draws it");

        int blanked = 0;
        for (int gid = 1; gid < after.Count; gid++)
            if (gid != DrawnGid && MinimalOpenTypeCff.IsBlanked(after[gid])) blanked++;
        Assert.Equal(CidsByGid.Length - 1, blanked);
    }

    /// <summary>
    /// A CID is not a glyph index. Under an identity assumption CID 20 would name GID 20 — outside
    /// this font — so the drawn glyph would be blanked and GID 2 kept for no reason. Asserting on
    /// the <b>specific</b> glyph is what separates the two behaviours; a count alone would not.
    /// </summary>
    [Fact]
    public void TheCharsetDecidesWhichGlyphSurvives_NotTheCidTakenAsAnIndex()
    {
        byte[] original = CidKeyedCff(CidsByGid);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(original);

        Optimize(PdfWithEmbeddedCidFontType0(original),
            new OptimizationOptions { SubsetEmbeddedFonts = true }, out byte[] optimized);

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(ExtractFontProgram(optimized));

        // GID 2 is the charset's answer for CID 20 and must survive...
        Assert.True(before[2].SequenceEqual(after[2]));
        // ...while GID 1, which an off-by-one or a first-glyph fallback would keep, must not.
        Assert.True(MinimalOpenTypeCff.IsBlanked(after[1]),
            "GID 1 survived, which is what keeping the wrong glyph looks like");
    }

    /// <summary>
    /// The size claim, measured where the saving actually is. Blanking leaves the decoded program
    /// exactly as long, so this asserts on the <b>stored, Flate-encoded</b> stream — the bytes the
    /// document carries.
    /// </summary>
    [Fact]
    public void TheStoredStreamShrinksWhileTheDecodedProgramStaysExactlyAsLong()
    {
        byte[] original = CidKeyedCff(CidsByGid);
        byte[] pdf = PdfWithEmbeddedCidFontType0(original);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        byte[] decoded = ExtractFontProgram(optimized);
        Assert.Equal(original.Length, decoded.Length);
        Assert.True(optimized.Length < pdf.Length,
            $"the optimized file is {optimized.Length} bytes against {pdf.Length}; the blanked " +
            "charstrings are supposed to compress away");
    }

    /// <summary>
    /// <c>/Length1</c> is defined for <c>/FontFile</c> and <c>/FontFile2</c> (Table 127) and not for
    /// <c>/FontFile3</c>. The TrueType path writes it because it must; this path must not, or the
    /// stream gains a key the spec does not give it, over a value no reader consults.
    /// </summary>
    [Fact]
    public void NoLength1IsWrittenOntoAFontFile3Stream()
    {
        Optimize(PdfWithEmbeddedCidFontType0(CidKeyedCff(CidsByGid)),
            new OptimizationOptions { SubsetEmbeddedFonts = true }, out byte[] optimized);

        Assert.DoesNotContain("/Length1", PdfContentProbe.Raw(optimized), StringComparison.Ordinal);
    }

    /// <summary>The control: with the option off, every charstring survives byte for byte.</summary>
    [Fact]
    public void Optimize_LeavesTheCffProgramIntactWhenSubsettingIsOff()
    {
        byte[] original = CidKeyedCff(CidsByGid);
        List<byte[]> before = MinimalOpenTypeCff.ReadCharstrings(original);

        var result = Optimize(PdfWithEmbeddedCidFontType0(original),
            new OptimizationOptions { SubsetEmbeddedFonts = false }, out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);

        List<byte[]> after = MinimalOpenTypeCff.ReadCharstrings(ExtractFontProgram(optimized));
        for (int gid = 0; gid < before.Count; gid++)
            Assert.True(before[gid].SequenceEqual(after[gid]), $"glyph {gid} changed with subsetting off");
    }

    /// <summary>
    /// A CFF2 program is refused, and the file is left carrying the whole thing rather than a
    /// rewritten one. Fail-closed: the cost of guessing here is a document whose text vanishes.
    /// </summary>
    [Fact]
    public void ACff2ProgramIsRefusedAndTheFontIsLeftAlone()
    {
        byte[] original = CidKeyedCff(CidsByGid);
        original[0] = 2;                                  // claim CFF2

        var result = Optimize(PdfWithEmbeddedCidFontType0(original),
            new OptimizationOptions { SubsetEmbeddedFonts = true }, out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);
        Assert.Equal(original, ExtractFontProgram(optimized));
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private static OptimizationResult Optimize(byte[] pdf, OptimizationOptions options, out byte[] output)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var result = new MemoryStream();
        OptimizationResult stats = PdfOptimizer.Optimize(input, result, options);
        output = result.ToArray();
        return stats;
    }

    /// <summary>The decoded <c>/FontFile3</c> program out of an optimized document.</summary>
    private static byte[] ExtractFontProgram(byte[] pdf)
    {
        byte[] program = PdfContentProbe.EmbeddedFontProgram(pdf, "FontFile3");
        Assert.NotNull(program);
        return program!;
    }

    /// <summary>
    /// A one-page document whose only font is a <c>CIDFontType0</c> over <paramref name="fontProgram"/>,
    /// drawing exactly <see cref="DrawnCid"/> through <c>Identity-H</c>.
    /// </summary>
    private static byte[] PdfWithEmbeddedCidFontType0(byte[] fontProgram)
    {
        string content = "BT /F1 12 Tf 100 700 Td <" + DrawnCid.ToString("X4") + "> Tj ET";

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        const int objectCount = 8;
        var offsets = new long[objectCount + 1];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /TestCid /Encoding /Identity-H "
            + "/DescendantFonts [6 0 R] >>\nendobj\n");
        offsets[6] = ms.Position;
        Write("6 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /TestCid "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + "/FontDescriptor 7 0 R /DW 500 >>\nendobj\n");
        offsets[7] = ms.Position;
        Write("7 0 obj\n<< /Type /FontDescriptor /FontName /TestCid /Flags 4 /ItalicAngle 0 "
            + "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
            + "/FontBBox [0 -200 1000 800] /FontFile3 8 0 R >>\nendobj\n");
        offsets[8] = ms.Position;
        Write($"8 0 obj\n<< /Subtype /Type1C /Length {fontProgram.Length} >>\nstream\n");
        ms.Write(fontProgram, 0, fontProgram.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A CID-keyed CFF whose charset assigns <paramref name="cidsByGid"/>[i] to GID i+1, with the
    /// FDArray and FDSelect a CID-keyed font is required to carry (TN #5176 §18).
    /// </summary>
    /// <remarks>
    /// <b>The charset is deliberately not the identity.</b> That is the whole point of the fixture:
    /// with CID == GID every assertion above would pass against a subsetter that never consulted the
    /// charset at all, which is exactly the defect the resolver exists to prevent. Do not "simplify"
    /// the CID list to 1, 2, 3.
    /// </remarks>
    private static byte[] CidKeyedCff(int[] cidsByGid)
    {
        var glyphs = new List<byte[]> { new byte[] { 14 } };
        for (int i = 0; i < cidsByGid.Length; i++)
            glyphs.Add(MinimalOpenTypeCff.BoxGlyph(500, 400, 220, i + 1));
        int glyphCount = glyphs.Count;

        var charset = new List<byte> { 0 };
        foreach (int cid in cidsByGid) { charset.Add((byte)(cid >> 8)); charset.Add((byte)cid); }

        var fdSelect = new List<byte> { 0 };
        for (int g = 0; g < glyphCount; g++) fdSelect.Add(0);

        byte[] csIndex = Index(glyphs.ToArray());
        byte[] nameIndex = Index(new[] { Encoding.ASCII.GetBytes("TestCid") });
        byte[] stringIndex = Index(new[]
        {
            Encoding.ASCII.GetBytes("Adobe"),
            Encoding.ASCII.GetBytes("Identity"),
        });
        byte[] globalSubrs = Index(Array.Empty<byte[]>());
        byte[] privateDict = { 139, 20 };                 // defaultWidthX 0

        byte[] FdDict(int privOff)
        {
            var d = new List<byte>();
            Int32Op(d, privateDict.Length); Int32Op(d, privOff); d.Add(18);
            return d.ToArray();
        }

        byte[] TopDict(int charsetOff, int fdSelectOff, int csOff, int fdArrayOff)
        {
            var d = new List<byte>();
            Int32Op(d, 391); Int32Op(d, 392); Int32Op(d, 0); d.Add(12); d.Add(30);   // ROS
            Int32Op(d, charsetOff);  d.Add(15);
            Int32Op(d, csOff);       d.Add(17);
            Int32Op(d, glyphCount);  d.Add(12); d.Add(34);                            // CIDCount
            Int32Op(d, fdArrayOff);  d.Add(12); d.Add(36);
            Int32Op(d, fdSelectOff); d.Add(12); d.Add(37);
            return d.ToArray();
        }

        int topDictLength = Index(new[] { TopDict(0, 0, 0, 0) }).Length;
        int fdArrayLength = Index(new[] { FdDict(0) }).Length;

        int charsetOffset  = 4 + nameIndex.Length + topDictLength + stringIndex.Length + globalSubrs.Length;
        int fdSelectOffset = charsetOffset + charset.Count;
        int csIndexOffset  = fdSelectOffset + fdSelect.Count;
        int fdArrayOffset  = csIndexOffset + csIndex.Length;
        int privateOffset  = fdArrayOffset + fdArrayLength;

        byte[] topDictIndex = Index(new[] { TopDict(charsetOffset, fdSelectOffset, csIndexOffset, fdArrayOffset) });
        byte[] fdArray = Index(new[] { FdDict(privateOffset) });
        if (topDictIndex.Length != topDictLength || fdArray.Length != fdArrayLength)
            throw new InvalidOperationException("a DICT changed size once real offsets were encoded");

        var cff = new List<byte> { 1, 0, 4, 1 };
        cff.AddRange(nameIndex); cff.AddRange(topDictIndex); cff.AddRange(stringIndex);
        cff.AddRange(globalSubrs); cff.AddRange(charset); cff.AddRange(fdSelect);
        cff.AddRange(csIndex); cff.AddRange(fdArray); cff.AddRange(privateDict);
        return cff.ToArray();
    }

    private static void Int32Op(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)((uint)v >> 24)); d.Add((byte)((uint)v >> 16));
        d.Add((byte)((uint)v >> 8)); d.Add((byte)v);
    }

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0) return new byte[] { 0, 0 };
        int total = items.Sum(i => i.Length);
        byte offSize = total + 1 <= 0xFF ? (byte)1 : total + 1 <= 0xFFFF ? (byte)2 : (byte)3;

        var idx = new List<byte> { (byte)(items.Length >> 8), (byte)(items.Length & 0xFF), offSize };
        void WriteOffset(int off)
        {
            for (int i = offSize - 1; i >= 0; i--) idx.Add((byte)((off >> (i * 8)) & 0xFF));
        }

        int running = 1;
        WriteOffset(running);
        foreach (byte[] item in items) { running += item.Length; WriteOffset(running); }
        foreach (byte[] item in items) idx.AddRange(item);
        return idx.ToArray();
    }
}
