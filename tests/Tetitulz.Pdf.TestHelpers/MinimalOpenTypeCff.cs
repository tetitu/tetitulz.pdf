// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Builds a genuine CFF1 font program — bare, or wrapped in an <c>'OTTO'</c> sfnt with the tables a
/// real OpenType/CFF face carries — and reads charstrings back out of one.
///
/// <para><b>Why the reader half matters as much as the writer half.</b> CFF subsetting works by
/// overwriting unused charstrings in place, so the file length never changes and
/// <c>Assert.True(subset.Length &lt; font.Length)</c> — the obvious assertion — is <b>always false</b>
/// even when the subsetter worked perfectly. The only honest evidence is per-glyph: which
/// charstrings are still their original bytes, and which are now a bare <c>endchar</c>. That is what
/// <see cref="ReadCharstrings"/> is for.</para>
///
/// <para>Shared here rather than copied into three test projects because the CFF byte layout is
/// fiddly enough that copies drift — <c>Base.Tests</c>' <c>EmbeddedCffFixture</c> already had to say
/// as much about its own port of the Core one.</para>
/// </summary>
public static class MinimalOpenTypeCff
{
    /// <summary>
    /// The first SID this builder assigns to a custom glyph name. SIDs 0–390 are Adobe's standard
    /// strings (TN #5176 Appendix A); anything above indexes the font's own string INDEX.
    /// </summary>
    private const int FirstCustomSid = 391;

    /// <summary>
    /// A glyph: its PostScript name, its charstring, and the character code the font's built-in
    /// encoding reaches it through.
    /// </summary>
    public sealed class Glyph
    {
        public string? Name;
        public byte[] Charstring = Array.Empty<byte>();
        /// <summary>Code in the font's format-0 <c>/Encoding</c>, or <c>-1</c> for unencoded.</summary>
        public int Code = -1;
    }

    /// <summary>
    /// A charstring drawing a filled square of side <paramref name="side"/> with advance
    /// <paramref name="advance"/>, padded out to <paramref name="padTo"/> bytes.
    /// </summary>
    /// <param name="variant">
    /// Varies the padding operands deterministically. <b>Not cosmetic.</b> With every glyph
    /// byte-identical, Flate deduplicates the whole CharStrings block and a 201-glyph font
    /// compresses to 2 105 bytes — so blanking 197 of them measured a 1% saving and the compression
    /// test failed against real code that was working. A real font's glyphs all differ; the fixture
    /// has to as well or it cannot show what blanking is worth.
    /// </param>
    public static byte[] BoxGlyph(int advance = 500, int side = 400, int padTo = 0, int variant = 0)
    {
        var cs = new List<byte>();
        // Type 2 §4.1: an extra leading operand on the first stack-clearing operator is the width,
        // so this is "width, then rmoveto 0 0" rather than a three-argument move.
        EncodeInt(cs, advance);
        EncodeInt(cs, 0); EncodeInt(cs, 0); cs.Add(21);        // rmoveto 0 0
        EncodeInt(cs, 0); EncodeInt(cs, side);
        EncodeInt(cs, side); EncodeInt(cs, 0);
        EncodeInt(cs, 0); EncodeInt(cs, -side);
        cs.Add(5);                                              // rlineto

        // Pad with moves. Each pair is stack-clearing, so the outline is unaffected and the glyph
        // stays valid; the point is bytes on the page, which is what a subset removes. Operands stay
        // within -107..107 so each pair is exactly three bytes whatever the variant.
        int step = 0;
        while (cs.Count < padTo - 1)
        {
            int dx = (variant * 31 + step * 17) % 199 - 99;
            int dy = (variant * 53 + step * 29) % 199 - 99;
            EncodeInt(cs, dx); EncodeInt(cs, dy); cs.Add(21);
            step++;
        }

        cs.Add(14);                                             // endchar
        return cs.ToArray();
    }

    /// <summary>
    /// A charstring in the deprecated <c>seac</c> form of <c>endchar</c> (TN #5177 §4.12 note J):
    /// it draws no outline of its own, only the two glyphs Adobe StandardEncoding gives
    /// <paramref name="baseCode"/> and <paramref name="accentCode"/>.
    /// </summary>
    public static byte[] SeacGlyph(int baseCode, int accentCode, int adx = 100, int ady = 200)
    {
        var cs = new List<byte>();
        EncodeInt(cs, adx);
        EncodeInt(cs, ady);
        EncodeInt(cs, baseCode);
        EncodeInt(cs, accentCode);
        cs.Add(14);   // endchar — four operands, so this is the composite form
        return cs.ToArray();
    }

    /// <summary>
    /// A charstring that reaches <see cref="SeacGlyph"/>'s <c>endchar</c> through a
    /// <b>global subroutine</b>, which is how a real font stores a family of accented letters —
    /// and the case a subsetter that scans only the top-level charstring gets wrong.
    /// </summary>
    /// <param name="subrIndex">Index into the global subr INDEX, before bias.</param>
    /// <param name="globalSubrCount">How many global subrs the font has, for the bias (§4.7).</param>
    public static byte[] CallGlobalSubrGlyph(int subrIndex, int globalSubrCount)
    {
        var cs = new List<byte>();
        EncodeInt(cs, subrIndex - SubrBias(globalSubrCount));
        cs.Add(29);   // callgsubr
        cs.Add(14);   // endchar (reached only if the subr returns without one)
        return cs.ToArray();
    }

    /// <summary>The bias a <c>callsubr</c>/<c>callgsubr</c> operand carries (TN #5177 §4.7).</summary>
    public static int SubrBias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>
    /// <paramref name="glyphCount"/> box glyphs named <c>g1</c>…, plus <c>.notdef</c> at GID 0, each
    /// encoded at code <c>32 + i</c>. The workhorse fixture: every glyph is the same size, so a
    /// blanked-byte count is trivially predictable.
    /// </summary>
    public static List<Glyph> BoxGlyphs(int glyphCount, int padTo = 60)
    {
        var glyphs = new List<Glyph>
        {
            new Glyph { Name = ".notdef", Charstring = new byte[] { 14 } },
        };
        for (int i = 1; i <= glyphCount; i++)
            glyphs.Add(new Glyph
            {
                Name = "g" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Charstring = BoxGlyph(500, 400, padTo, variant: i),
                Code = 32 + i,
            });
        return glyphs;
    }

    /// <summary>
    /// One box glyph per character of <paramref name="characters"/>, each named with the Adobe
    /// glyph name for that character, plus <c>.notdef</c> at GID 0.
    /// </summary>
    /// <remarks>
    /// <b>The names are the whole point, and <see cref="BoxGlyphs"/> cannot substitute.</b> That one
    /// names its glyphs <c>g1</c>, <c>g2</c>… — which is realistic for a subset font and carries no
    /// Unicode value at all, so a bare CFF built from it is <i>correctly</i> unresolvable by glyph
    /// name. A test of the name-resolution path built on it fails against working code, which is
    /// exactly what happened first. Restricted to ASCII letters and digits, whose Adobe glyph name
    /// is the character itself.
    /// </remarks>
    public static List<Glyph> NamedBoxGlyphs(string characters, int padTo = 60)
    {
        var glyphs = new List<Glyph>
        {
            new Glyph { Name = ".notdef", Charstring = new byte[] { 14 } },
        };

        string sorted = new string(new SortedSet<char>(characters ?? string.Empty).ToArray());
        for (int i = 0; i < sorted.Length; i++)
        {
            char c = sorted[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                throw new ArgumentException(
                    $"'{c}' is not an ASCII letter, so its Adobe glyph name is not the character itself.",
                    nameof(characters));

            glyphs.Add(new Glyph
            {
                Name = c.ToString(),
                Charstring = BoxGlyph(500, 400, padTo, variant: i + 1),
                Code = c,
            });
        }
        return glyphs;
    }

    /// <summary>Builds the bare CFF1 program. <paramref name="glyphs"/>[0] must be <c>.notdef</c>.</summary>
    /// <param name="globalSubrs">Global subroutines, or null for none.</param>
    /// <param name="privateDict">A Private DICT written after the CharStrings INDEX and named by the
    /// Top DICT, or null for none.</param>
    /// <param name="privateOffsetOverride">The Private offset the Top DICT declares instead of the
    /// real one — a hostile value the parser must survive.</param>
    /// <param name="encodingOffsetOverride">The same for the Encoding offset.</param>
    public static byte[] BuildBareCff(IReadOnlyList<Glyph> glyphs, IReadOnlyList<byte[]>? globalSubrs = null,
                                      byte[]? privateDict = null, int? privateOffsetOverride = null,
                                      int? encodingOffsetOverride = null)
    {
        if (glyphs == null || glyphs.Count == 0)
            throw new ArgumentException("a CFF needs at least .notdef", nameof(glyphs));

        // Charset: format 0, one SID per glyph after .notdef. Custom SIDs index the string INDEX,
        // so the names below are the ones CffParser will report — including "A" and "acute", which
        // is how a seac component resolves.
        var strings = new List<byte[]>();
        var charset = new List<byte> { 0 };
        for (int gid = 1; gid < glyphs.Count; gid++)
        {
            strings.Add(Encoding.ASCII.GetBytes(glyphs[gid].Name ?? ("g" + gid)));
            int sid = FirstCustomSid + gid - 1;
            charset.Add((byte)(sid >> 8));
            charset.Add((byte)(sid & 0xFF));
        }

        // Encoding: format 0, codes listed in GID order (§12, Table 11).
        var encoded = new List<Glyph>();
        for (int gid = 1; gid < glyphs.Count; gid++)
            if (glyphs[gid].Code >= 0 && glyphs[gid].Code <= 255) encoded.Add(glyphs[gid]);
        var encoding = new List<byte> { 0, (byte)encoded.Count };
        foreach (Glyph g in encoded) encoding.Add((byte)g.Code);

        byte[] csIndex = CffIndex(glyphs.Select(g => g.Charstring).ToArray());
        byte[] nameIndex = CffIndex(new[] { Encoding.ASCII.GetBytes("TestCffOtf") });
        byte[] stringIndex = CffIndex(strings.ToArray());
        byte[] globalSubrIndex = CffIndex((globalSubrs ?? Array.Empty<byte[]>()).ToArray());

        // The Top DICT holds absolute offsets, so its encoded size has to be settled before they
        // can be computed. Encoding every offset as a 5-byte 29-form makes the size independent of
        // the values, which removes the fixed-point iteration the older fixtures needed.
        byte[] TopDict(int encodingOff, int charsetOff, int csOff)
        {
            var d = new List<byte>();
            EncodeInt32(d, charsetOff); d.Add(15);
            EncodeInt32(d, encodingOffsetOverride ?? encodingOff); d.Add(16);
            EncodeInt32(d, csOff); d.Add(17);
            if (privateDict != null)
            {
                EncodeInt32(d, privateDict.Length);
                EncodeInt32(d, privateOffsetOverride ?? csOff + csIndex.Length);
                d.Add(18);
            }
            return d.ToArray();
        }

        int topDictLength = CffIndex(new[] { TopDict(0, 0, 0) }).Length;
        int encodingOffset = 4 + nameIndex.Length + topDictLength + stringIndex.Length + globalSubrIndex.Length;
        int charsetOffset = encodingOffset + encoding.Count;
        int csIndexOffset = charsetOffset + charset.Count;

        byte[] topDictIndex = CffIndex(new[] { TopDict(encodingOffset, charsetOffset, csIndexOffset) });
        if (topDictIndex.Length != topDictLength)
            throw new InvalidOperationException("the Top DICT changed size once real offsets were encoded");

        var cff = new MemoryStream();
        void W(byte[] b) => cff.Write(b, 0, b.Length);
        W(new byte[] { 1, 0, 4, 1 });               // major, minor, hdrSize, offSize
        W(nameIndex); W(topDictIndex); W(stringIndex); W(globalSubrIndex);
        W(encoding.ToArray()); W(charset.ToArray()); W(csIndex);
        if (privateDict != null) W(privateDict);
        return cff.ToArray();
    }

    /// <summary>
    /// A CID-keyed CFF whose charset assigns <paramref name="cidsByGid"/>[i] to GID i+1; every glyph
    /// is a <see cref="BoxGlyph"/> with advance 500, and the one Font DICT's Private DICT sets
    /// <c>defaultWidthX</c> 0.
    /// </summary>
    /// <remarks>
    /// <b>The CIDs are a required argument on purpose.</b> A non-identity charset is the entire point
    /// of this fixture — a CID is not a glyph index — and a default would invite someone to make it
    /// identity "for simplicity", which would silently disarm every assertion built on it.
    /// </remarks>
    /// <param name="cidsByGid">The CID of each glyph after <c>.notdef</c>.</param>
    /// <param name="topFontMatrix">Encoded DICT bytes (operands and <c>12 7</c>) for the Top DICT, or null.</param>
    /// <param name="fdFontMatrix">The same for the single Font DICT in the FDArray, or null.</param>
    /// <param name="charsetFormat">
    /// Overrides the charset's leading format byte, leaving the format-0 entry data in place. Null
    /// keeps the well-formed 0.
    /// </param>
    /// <remarks>
    /// <para><b>The format override exists for one branch nothing else can reach.</b> A charset
    /// whose format byte is not 0, 1 or 2 cannot be walked, and the CID→GID builder is the only one
    /// of <c>CffParser</c>'s three charset consumers that acts on that — it falls back to the
    /// identity, because for a CID-keyed font "CID == GID" is a better answer than an empty map.
    /// The two SID builders ignore the refusal, so no non-CID fixture can tell the two outcomes
    /// apart.</para>
    /// </remarks>
    /// <param name="fdIndex">The FD index FDSelect gives every glyph. The FDArray has one entry, so
    /// anything but 0 names a Font DICT that is not there.</param>
    /// <param name="privateOffsetOverride">The Private offset the Font DICT declares instead of the
    /// real one — a hostile value the parser must survive.</param>
    /// <param name="fdCount">How many Font DICTs the FDArray holds, every one naming the one Private DICT.</param>
    /// <param name="localSubr">A Subrs INDEX's one item, placed after the Private DICT, which names it.</param>
    /// <param name="distinctPrivateSizes">Font DICT <i>k</i> declares the Private DICT's size plus <i>k</i>,
    /// so each names a distinct span starting at the one Private DICT.</param>
    /// <param name="privatePadding">Zero bytes ending the Private DICT (with <paramref name="localSubr"/>), to make it larger.</param>
    /// <param name="overlappingSubrsHeaders">With <paramref name="localSubr"/>: this many Subrs INDEXes
    /// sharing their bytes — one-item INDEX headers laid 11 bytes apart in one block, each item running to
    /// the block's end (<paramref name="localSubr"/> last) — and as many Private DICTs, each 8 bytes, Font
    /// DICT <i>k</i>'s naming header <i>k</i>; needs <paramref name="fdCount"/> at least that many. Distinct
    /// INDEXes at distinct offsets whose lengths sum to about that many copies of the item.</param>
    public static byte[] BuildCidKeyed(int[] cidsByGid, byte[]? topFontMatrix = null,
                                       byte[]? fdFontMatrix = null, byte? charsetFormat = null,
                                       byte fdIndex = 0, int? privateOffsetOverride = null,
                                       int fdCount = 1, byte[]? localSubr = null, bool distinctPrivateSizes = false,
                                       int privatePadding = 0, int overlappingSubrsHeaders = 0)
    {
        bool subrsChain = localSubr != null && overlappingSubrsHeaders > 0;
        const int chainPrivateLength = 8;        // defaultWidthX 0 (2 bytes), a 29-form Subrs offset (5), the operator (1)
        const int chainHeaderLength = 11;        // count (2), offSize 4 (1), two offsets (8)
        var glyphs = new List<byte[]> { new byte[] { 14 } };
        for (int i = 0; i < cidsByGid.Length; i++)
            glyphs.Add(BoxGlyph(500, 400, 60, i + 1));
        int glyphCount = glyphs.Count;

        // charset format 0: one uint16 CID per glyph after .notdef.
        var charset = new List<byte> { charsetFormat ?? 0 };
        foreach (int cid in cidsByGid) { charset.Add((byte)(cid >> 8)); charset.Add((byte)cid); }

        // FDSelect format 0: one FD index per glyph. A CID-keyed CFF is REQUIRED to carry FDArray
        // and FDSelect (TN #5176 section 18) and CffParser.ParseCidKeyedCff returns null without
        // them.
        var fdSelect = new List<byte> { 0 };
        for (int g = 0; g < glyphCount; g++) fdSelect.Add(fdIndex);

        byte[] csIndex = CffIndex(glyphs.ToArray());
        byte[] nameIndex = CffIndex(new[] { Encoding.ASCII.GetBytes("TestCid") });
        // The string INDEX supplies the ROS registry/ordering SIDs (391, 392).
        byte[] stringIndex = CffIndex(new[] { Encoding.ASCII.GetBytes("Adobe"), Encoding.ASCII.GetBytes("Identity") });
        byte[] globalSubrs = CffIndex(Array.Empty<byte[]>());
        byte[] privateDict = { 139, 20 };            // defaultWidthX 0
        byte[] subrsIndex = Array.Empty<byte>();
        if (subrsChain && localSubr != null)
        {
            // Header k is a one-item INDEX whose item is the rest of the block: the headers after it
            // and localSubr. Private DICT k follows the FDArray at k × 8, so its Subrs offset is the
            // block's start less its own, 8 × n + 3 × k.
            int n = overlappingSubrsHeaders;
            var block = new List<byte>();
            for (int k = 0; k < n; k++)
            {
                int itemLength = chainHeaderLength * (n - k - 1) + localSubr.Length;
                block.Add(0); block.Add(1); block.Add(4);
                block.AddRange(new byte[] { 0, 0, 0, 1 });
                uint end = (uint)(itemLength + 1);
                block.Add((byte)(end >> 24)); block.Add((byte)(end >> 16)); block.Add((byte)(end >> 8)); block.Add((byte)end);
            }
            block.AddRange(localSubr);
            subrsIndex = block.ToArray();
        }
        else if (localSubr != null)
        {
            var withSubrs = new List<byte>(privateDict);
            EncodeInt32(withSubrs, privateDict.Length + 6 + privatePadding); withSubrs.Add(19);   // Subrs, just after this DICT
            withSubrs.AddRange(new byte[privatePadding]);
            privateDict = withSubrs.ToArray();
            subrsIndex = CffIndex(new[] { localSubr });
        }

        byte[] FdDict(int privOff, int k)
        {
            var d = new List<byte>();
            if (fdFontMatrix != null) d.AddRange(fdFontMatrix);
            EncodeInt32(d, subrsChain ? chainPrivateLength : privateDict.Length + (distinctPrivateSizes ? k : 0));
            EncodeInt32(d, privateOffsetOverride ?? (subrsChain ? privOff + chainPrivateLength * k : privOff)); d.Add(18);   // Private (size, offset)
            return d.ToArray();
        }

        byte[][] FdDicts(int privOff)
        {
            var dicts = new byte[fdCount][];
            for (int k = 0; k < fdCount; k++) dicts[k] = FdDict(privOff, k);
            return dicts;
        }

        byte[] TopDict(int charsetOff, int fdSelectOff, int csOff, int fdArrayOff)
        {
            var d = new List<byte>();
            EncodeInt32(d, 391); EncodeInt32(d, 392); EncodeInt32(d, 0); d.Add(12); d.Add(30);   // ROS
            if (topFontMatrix != null) d.AddRange(topFontMatrix);
            EncodeInt32(d, charsetOff);  d.Add(15);                                       // charset
            EncodeInt32(d, csOff);       d.Add(17);                                       // CharStrings
            EncodeInt32(d, glyphCount);  d.Add(12); d.Add(34);                            // CIDCount
            EncodeInt32(d, fdArrayOff);  d.Add(12); d.Add(36);                            // FDArray
            EncodeInt32(d, fdSelectOff); d.Add(12); d.Add(37);                            // FDSelect
            return d.ToArray();
        }

        // Every offset is written in the 5-byte 29-form, so the encoded sizes do not depend on the
        // values and one pass settles the layout.
        int topDictLength = CffIndex(new[] { TopDict(0, 0, 0, 0) }).Length;
        int fdArrayLength = CffIndex(FdDicts(0)).Length;

        int charsetOffset  = 4 + nameIndex.Length + topDictLength + stringIndex.Length + globalSubrs.Length;
        int fdSelectOffset = charsetOffset + charset.Count;
        int csIndexOffset  = fdSelectOffset + fdSelect.Count;
        int fdArrayOffset  = csIndexOffset + csIndex.Length;
        int privateOffset  = fdArrayOffset + fdArrayLength;

        byte[] topDictIndex = CffIndex(new[] { TopDict(charsetOffset, fdSelectOffset, csIndexOffset, fdArrayOffset) });
        byte[] fdArray      = CffIndex(FdDicts(privateOffset));
        if (topDictIndex.Length != topDictLength || fdArray.Length != fdArrayLength)
            throw new InvalidOperationException("a DICT changed size once real offsets were encoded");

        var cff = new List<byte> { 1, 0, 4, 1 };
        cff.AddRange(nameIndex); cff.AddRange(topDictIndex); cff.AddRange(stringIndex);
        cff.AddRange(globalSubrs); cff.AddRange(charset); cff.AddRange(fdSelect);
        cff.AddRange(csIndex); cff.AddRange(fdArray);
        if (subrsChain)
        {
            for (int k = 0; k < overlappingSubrsHeaders; k++)
            {
                var pd = new List<byte> { 139, 20 };
                EncodeInt32(pd, chainPrivateLength * overlappingSubrsHeaders + (chainHeaderLength - chainPrivateLength) * k); pd.Add(19);
                if (pd.Count != chainPrivateLength) throw new InvalidOperationException("a chained Private DICT is not 8 bytes");
                cff.AddRange(pd);
            }
        }
        else cff.AddRange(privateDict);
        cff.AddRange(subrsIndex);
        if (distinctPrivateSizes) cff.AddRange(new byte[fdCount]);                          // room for the longest span
        return cff.ToArray();
    }

    /// <summary>
    /// A CFF font set of several name-keyed fonts (TN #5176 Table 1): the String INDEX, the encoding
    /// and the charset shared — <c>.notdef</c>, then <c>A</c> at code 65 — and each font its own
    /// CharStrings INDEX, written one after another: font f's <c>A</c> a square of side
    /// <paramref name="sides"/>[f] and advance <paramref name="advances"/>[f], its <c>.notdef</c> 250
    /// wide.
    /// </summary>
    /// <param name="names">Each font's name in the Name INDEX.</param>
    /// <param name="advances">Each font's advance for <c>A</c>.</param>
    /// <param name="sides">Each font's side for <c>A</c>'s square.</param>
    /// <param name="syntheticFont">A font whose Top DICT also carries <c>SyntheticBase 0</c> (12 20), or −1.</param>
    /// <param name="deletedName">A font whose Name INDEX entry's first byte is 0 — deleted — or −1.</param>
    public static byte[] BuildSet(string[] names, int[] advances, int[] sides, int syntheticFont = -1, int deletedName = -1)
    {
        byte[] stringIndex = CffIndex(new[] { Encoding.ASCII.GetBytes("A") });
        byte[] globalSubrs = CffIndex(Array.Empty<byte[]>());
        byte[] encoding = { 0, 1, 65 };
        byte[] charset = { 0, FirstCustomSid >> 8, FirstCustomSid & 0xFF };
        byte[] nameIndex = CffIndex(names.Select((n, f) =>
        {
            byte[] bytes = Encoding.ASCII.GetBytes(n);
            if (f == deletedName) bytes[0] = 0;
            return bytes;
        }).ToArray());
        byte[][] charStrings = names.Select((_, f) =>
            CffIndex(new[] { BoxGlyph(250), BoxGlyph(advances[f], sides[f], 0, f + 1) })).ToArray();

        byte[] TopDict(int f, int encodingOff, int charsetOff, int csOff)
        {
            var d = new List<byte>();
            EncodeInt32(d, charsetOff); d.Add(15);
            EncodeInt32(d, encodingOff); d.Add(16);
            EncodeInt32(d, csOff); d.Add(17);
            if (f == syntheticFont) { EncodeInt32(d, 0); d.Add(12); d.Add(20); }
            return d.ToArray();
        }

        // Every offset in the 5-byte 29-form: the sizes do not depend on the values.
        int topDictLength = CffIndex(names.Select((_, f) => TopDict(f, 0, 0, 0)).ToArray()).Length;
        int encodingOffset = 4 + nameIndex.Length + topDictLength + stringIndex.Length + globalSubrs.Length;
        int charsetOffset = encodingOffset + encoding.Length;
        var csOffsets = new int[names.Length];
        int at = charsetOffset + charset.Length;
        for (int f = 0; f < names.Length; f++) { csOffsets[f] = at; at += charStrings[f].Length; }

        byte[] topDictIndex = CffIndex(names.Select((_, f) => TopDict(f, encodingOffset, charsetOffset, csOffsets[f])).ToArray());
        if (topDictIndex.Length != topDictLength)
            throw new InvalidOperationException("the Top DICT INDEX changed size once real offsets were encoded");

        var cff = new List<byte> { 1, 0, 4, 1 };
        cff.AddRange(nameIndex); cff.AddRange(topDictIndex); cff.AddRange(stringIndex); cff.AddRange(globalSubrs);
        cff.AddRange(encoding); cff.AddRange(charset);
        foreach (byte[] cs in charStrings) cff.AddRange(cs);
        return cff.ToArray();
    }

    /// <summary>
    /// Wraps <paramref name="cff"/> in an <c>'OTTO'</c> sfnt with a format-4 cmap over
    /// <paramref name="characters"/> (mapping the i-th character to GID i+1) and the metric tables
    /// a real face carries.
    /// </summary>
    /// <param name="layoutTableBytes">
    /// Size of a dummy <c>GSUB</c>, so a test can exercise the interaction with
    /// <c>SfntTablePruner</c>. Zero writes none.
    /// </param>
    /// <param name="verticalAdvance">
    /// When positive, adds <c>vhea</c> and <c>vmtx</c> tables giving every glyph this advance height.
    /// </param>
    public static byte[] WrapInOtto(byte[] cff, string characters, int layoutTableBytes = 0, int fsType = 0,
        int verticalAdvance = 0)
    {
        var chars = new List<char>(characters ?? string.Empty);
        int numGlyphs = chars.Count + 1;

        var hmtx = new MemoryStream();
        for (int i = 0; i < numGlyphs; i++)
        {
            MinimalTrueType.WriteUInt16(hmtx, 500);
            MinimalTrueType.WriteInt16(hmtx, 0);
        }

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { "CFF ", cff },
            { "head", MinimalTrueType.BuildHead() },
            { "hhea", MinimalTrueType.BuildHhea(numGlyphs) },
            { "maxp", MinimalTrueType.BuildMaxp(numGlyphs) },
            { "hmtx", hmtx.ToArray() },
            // The CFF fixture is BMP-only; the cmap builder now keys on code points, so the char
            // list is widened here rather than the fixture being taught about surrogate pairs.
            { "cmap", MinimalTrueType.BuildCmap(chars.ConvertAll(c => (int)c), 3, 1) },
            { "name", MinimalTrueType.BuildName() },
            { "post", MinimalTrueType.BuildPost() },
            { "OS/2", Os2WithFsType(fsType) },
        };

        if (layoutTableBytes > 0) tables["GSUB"] = new byte[layoutTableBytes];

        if (verticalAdvance > 0)
        {
            var vmtx = new MemoryStream();
            for (int i = 0; i < numGlyphs; i++)
            {
                MinimalTrueType.WriteUInt16(vmtx, verticalAdvance);
                MinimalTrueType.WriteInt16(vmtx, 0);
            }
            tables["vhea"] = MinimalTrueType.BuildHhea(numGlyphs);   // hhea's layout, numOfLongVerMetrics at 34
            tables["vmtx"] = vmtx.ToArray();
        }

        return MinimalTrueType.Assemble(tables, 0x4F54544F);   // 'OTTO'
    }

    /// <summary>
    /// The shared <c>OS/2</c> table with a chosen <c>fsType</c> (offset 8), so a CFF fixture can
    /// declare an embedding licence. Every other byte is <c>MinimalTrueType.BuildOs2</c>'s, so a
    /// test varying only this parameter varies only the licence.
    /// </summary>
    private static byte[] Os2WithFsType(int fsType)
    {
        byte[] os2 = MinimalTrueType.BuildOs2();
        MinimalTrueType.WriteUInt16At(os2, 8, fsType);
        return os2;
    }

    /// <summary>
    /// A complete OpenType/CFF face over <paramref name="characters"/>, one padded box glyph each.
    /// </summary>
    public static byte[] Build(string characters, int padTo = 60, int layoutTableBytes = 0, int fsType = 0)
    {
        // A format-4 cmap needs ascending segments, and the CFF glyph order has to agree with it,
        // so both sides are built from the same sorted, de-duplicated sequence.
        string sorted = new string(new SortedSet<char>(characters ?? string.Empty).ToArray());
        var glyphs = BoxGlyphs(sorted.Length, padTo);
        for (int i = 0; i < sorted.Length; i++)
            glyphs[i + 1].Code = sorted[i] <= 255 ? sorted[i] : -1;
        return WrapInOtto(BuildBareCff(glyphs), sorted, layoutTableBytes, fsType);
    }

    /// <summary>
    /// Every charstring in a bare CFF or an <c>'OTTO'</c> sfnt, by GID, exactly as it sits in the
    /// file. The only way to see what a <b>blanking</b> subsetter did, since the file length is by
    /// design unchanged.
    /// </summary>
    public static List<byte[]> ReadCharstrings(byte[] fontOrCff)
    {
        byte[] cff = ExtractCff(fontOrCff);
        int pos = cff[2];                                   // hdrSize
        pos = SkipIndex(cff, pos);                          // Name INDEX
        byte[][] topDicts = ReadIndex(cff, ref pos);
        int csOffset = TopDictOperand(topDicts[0], 17);
        int p = csOffset;
        return ReadIndex(cff, ref p).ToList();
    }

    /// <summary>
    /// Where the CharStrings INDEX of a bare CFF or an <c>'OTTO'</c> sfnt begins, within the CFF —
    /// for a test that rewrites the INDEX's own header or offsets.
    /// </summary>
    public static int CharStringsIndexOffset(byte[] fontOrCff)
    {
        byte[] cff = ExtractCff(fontOrCff);
        int pos = cff[2];                                   // hdrSize
        pos = SkipIndex(cff, pos);                          // Name INDEX
        byte[][] topDicts = ReadIndex(cff, ref pos);
        return TopDictOperand(topDicts[0], 17);
    }

    /// <summary>True when <paramref name="charstring"/> is what a blanked glyph looks like.</summary>
    public static bool IsBlanked(byte[] charstring)
    {
        if (charstring == null || charstring.Length == 0) return false;
        if (charstring[0] != 0x0E) return false;
        for (int i = 1; i < charstring.Length; i++)
            if (charstring[i] != 0) return false;
        return true;
    }

    /// <summary>The <c>CFF </c> table of an sfnt, or the input when it already is one.</summary>
    public static byte[] ExtractCff(byte[] fontOrCff)
    {
        if (fontOrCff == null || fontOrCff.Length < 12) throw new ArgumentException("not a font", nameof(fontOrCff));
        if (fontOrCff[0] == 1) return fontOrCff;            // bare CFF1

        int numTables = (fontOrCff[4] << 8) | fontOrCff[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(fontOrCff, at, 4);
            if (tag != "CFF ") continue;
            int off = (int)ReadU32(fontOrCff, at + 8);
            int len = (int)ReadU32(fontOrCff, at + 12);
            var table = new byte[len];
            Array.Copy(fontOrCff, off, table, 0, len);
            return table;
        }
        throw new ArgumentException("the sfnt has no \"CFF \" table", nameof(fontOrCff));
    }

    // ---- CFF primitives ------------------------------------------------------------------

    private static void EncodeInt(List<byte> d, int v)
    {
        if (v >= -107 && v <= 107) { d.Add((byte)(v + 139)); }
        else if (v >= 108 && v <= 1131)
        {
            int b = v - 108;
            d.Add((byte)(247 + b / 256));
            d.Add((byte)(b % 256));
        }
        else if (v >= -1131 && v <= -108)
        {
            int b = -v - 108;
            d.Add((byte)(251 + b / 256));
            d.Add((byte)(b % 256));
        }
        else
        {
            d.Add(28);
            d.Add((byte)(v >> 8));
            d.Add((byte)v);
        }
    }

    /// <summary>The DICT 5-byte 29-form, whose width does not depend on the value.</summary>
    private static void EncodeInt32(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)((uint)v >> 24)); d.Add((byte)((uint)v >> 16));
        d.Add((byte)((uint)v >> 8)); d.Add((byte)v);
    }

    private static byte[] CffIndex(byte[][] items)
    {
        if (items.Length == 0) return new byte[] { 0, 0 };

        int totalDataLen = items.Sum(i => i.Length);
        byte offSize = totalDataLen + 1 <= 0xFF ? (byte)1
                     : totalDataLen + 1 <= 0xFFFF ? (byte)2 : (byte)3;

        var idx = new List<byte> { (byte)(items.Length >> 8), (byte)(items.Length & 0xFF), offSize };

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

    private static byte[][] ReadIndex(byte[] d, ref int pos)
    {
        int count = (d[pos] << 8) | d[pos + 1];
        pos += 2;
        if (count == 0) return Array.Empty<byte[]>();
        int offSize = d[pos++];
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            int off = 0;
            for (int b = 0; b < offSize; b++) off = (off << 8) | d[pos++];
            offsets[i] = off;
        }
        int dataBase = pos - 1;
        var items = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            items[i] = new byte[offsets[i + 1] - offsets[i]];
            Array.Copy(d, dataBase + offsets[i], items[i], 0, items[i].Length);
        }
        pos = dataBase + offsets[count];
        return items;
    }

    private static int SkipIndex(byte[] d, int pos)
    {
        int save = pos;
        ReadIndex(d, ref save);
        return save;
    }

    /// <summary>The single integer operand of DICT operator <paramref name="op"/>.</summary>
    private static int TopDictOperand(byte[] dict, int op)
    {
        var operands = new List<int>();
        int pos = 0;
        while (pos < dict.Length)
        {
            byte b0 = dict[pos];
            if (b0 == 28) { operands.Add((short)((dict[pos + 1] << 8) | dict[pos + 2])); pos += 3; }
            else if (b0 == 29)
            {
                operands.Add((dict[pos + 1] << 24) | (dict[pos + 2] << 16) | (dict[pos + 3] << 8) | dict[pos + 4]);
                pos += 5;
            }
            else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); pos++; }
            else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + dict[pos + 1] + 108); pos += 2; }
            else if (b0 >= 251 && b0 <= 254) { operands.Add(-((b0 - 251) * 256) - dict[pos + 1] - 108); pos += 2; }
            else
            {
                int key = b0 == 12 ? (12 << 8) | dict[pos + 1] : b0;
                pos += b0 == 12 ? 2 : 1;
                if (key == op && operands.Count > 0) return operands[operands.Count - 1];
                operands.Clear();
            }
        }
        throw new InvalidOperationException($"the Top DICT has no operator {op}");
    }

    private static uint ReadU32(byte[] d, int at) => unchecked((uint)(
        (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]));
}
