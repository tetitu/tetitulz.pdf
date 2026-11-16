// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="WoffDecoder"/> — unwrapping a WOFF 1.0 container back to the sfnt every other font
/// consumer in this tree already reads — and the hook in <see cref="TrueTypeFontProgram.Parse"/>
/// that makes one signature check reach all of them.
///
/// <para><b>Why a WOFF writer lives in this file rather than in <c>Tetitulz.Pdf.TestHelpers</c>.</b>
/// <c>Tetitulz.Pdf.Core.Tests</c> deliberately does not reference that project — it keeps its own
/// <c>Helpers/MinimalPdfBuilder</c> for the same reason — and pulling it in would drag SkiaSharp
/// into the one test project whose subject has no native dependency at all. The end-to-end
/// <c>@font-face</c> suite uses <c>TestHelpers.MinimalWoff</c>; this file needs only the container
/// shape and a two-table font, and says so.</para>
/// </summary>
public sealed class WoffDecoderTests
{
    private const int UnitsPerEm = 2048;
    private const int MappedCodePoint = 'A';
    private const ushort MappedGlyph = 5;

    /// <summary>A highly compressible filler table, so the container has a table that really deflates.</summary>
    private const string FillerTag = "ZZZZ";
    private const int FillerLength = 1024;

    // ================================================================
    //  The round trip
    // ================================================================

    /// <summary>
    /// The behaviour the decoder exists for: a WOFF 1.0 container parses to the same font as the
    /// sfnt inside it. Asserting on the metrics and the cmap rather than on "a program came back"
    /// is what makes this fail if the unwrap produces a differently-shaped sfnt — the directory
    /// offsets are all recomputed, so a one-entry mistake yields a parseable font with no glyphs.
    /// </summary>
    [Fact]
    public void Woff1Container_ParsesToTheSameFontAsTheSfntInsideIt()
    {
        byte[] sfnt = BuildSfnt();
        byte[] woff = WrapAsWoff(sfnt);

        TrueTypeFontProgram fromWoff = TrueTypeFontProgram.Parse(woff);

        Assert.Equal(UnitsPerEm, fromWoff.UnitsPerEm);
        Assert.True(fromWoff.TryGetGlyph(MappedCodePoint, out ushort gid));
        Assert.Equal(MappedGlyph, gid);
    }

    /// <summary>
    /// <c>FontData</c> is what a caller writes as <c>/FontFile2</c>, so it must be the unwrapped
    /// sfnt and not the container. Leaving the WOFF bytes there produces a PDF that embeds a font
    /// no reader can open, which no metric assertion above would notice.
    /// </summary>
    [Fact]
    public void Woff1Container_LeavesTheUnwrappedSfntInFontData()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());

        byte[] embedded = TrueTypeFontProgram.Parse(woff).FontData;

        Assert.Equal(0x00010000u, ReadU32(embedded, 0));   // sfnt version 1.0, not 'wOFF'
        Assert.Equal(3, (embedded[4] << 8) | embedded[5]);
    }

    /// <summary>
    /// The stored arm and the inflate arm are separate branches of the table loop, and a container
    /// that used one form throughout would leave the other untested. This fixture uses both — the
    /// first assertion proves that, so the test cannot quietly degrade into testing one of them.
    /// </summary>
    [Fact]
    public void Woff1Container_DecodesCompressedAndStoredTablesAlike()
    {
        byte[] sfnt = BuildSfnt();
        byte[] woff = WrapAsWoff(sfnt);

        Assert.Equal(1, CompressedTableCount(woff));   // the filler deflates; the rest are stored

        byte[] decoded = WoffDecoder.Decode(woff);

        Assert.Equal(new byte[FillerLength], ReadTable(decoded, FillerTag));          // inflated
        Assert.Equal(ReadTable(sfnt, "cmap"), ReadTable(decoded, "cmap"));            // stored
    }

    /// <summary>
    /// The sfnt directory must be sorted by tag (OpenType §5.1). The filler tag sorts after
    /// <c>head</c> and <c>cmap</c>, and the writer below emits it first on purpose, so a decoder
    /// that copies the container's order rather than sorting fails here.
    /// </summary>
    [Fact]
    public void Woff1Container_EmitsATableDirectorySortedByTag()
    {
        byte[] decoded = WoffDecoder.Decode(WrapAsWoff(BuildSfnt()));

        Assert.Equal(new[] { "ZZZZ", "cmap", "head" }, TableTags(decoded));
    }

    // ================================================================
    //  WOFF 2.0
    // ================================================================

    /// <summary>
    /// A <b>malformed</b> WOFF 2.0 container is refused rather than parsed as a font with no
    /// glyphs.
    /// </summary>
    /// <remarks>
    /// <para><b>The refusal moved but the hazard did not.</b> WOFF 2.0 is now decoded
    /// (<c>Woff2Decoder</c>, and the vectors are in <c>Woff2DecoderTests</c>); what this pins is
    /// what happens when the decode <i>fails</i>. The bytes below are an sfnt with the <c>wOF2</c>
    /// signature stamped over its version, and the WOFF 2.0 header happens to put a plausible
    /// <c>u16</c> where sfnt keeps <c>numTables</c> — so if the signature check were dropped they
    /// would parse into a program with no <c>cmap</c> and default metrics, reaching the caller as
    /// "this font does not cover your text" rather than "this is a container I cannot read".</para>
    /// <para>The test was called <c>Woff2Container_IsRefusedRatherThanParsedAsAFontWithNoGlyphs</c>
    /// and renamed rather than re-asserted, because the behaviour genuinely changed: WOFF 2.0
    /// containers are no longer refused, malformed ones are.</para>
    /// </remarks>
    [Fact]
    public void MalformedWoff2Container_IsRefusedRatherThanParsedAsAFontWithNoGlyphs()
    {
        byte[] woff2 = BuildSfnt();
        woff2[0] = 0x77; woff2[1] = 0x4F; woff2[2] = 0x46; woff2[3] = 0x32;   // 'wOF2'

        Assert.Null(TrueTypeFontProgram.Parse(woff2));
    }

    // ================================================================
    //  Hostile containers
    // ================================================================

    /// <summary>
    /// <c>origLength</c> is a number the file chose. Sizing a buffer from it is how a 300-byte
    /// container asks for a 4&#160;GiB allocation — so the decoder inflates into what the deflate
    /// stream actually produces and takes the emitted table length from that.
    /// </summary>
    [Fact]
    public void MalformedWoff_DeclaringAnAbsurdOrigLength_StillDecodesFromWhatInflates()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());
        SetDirectoryField(woff, FillerTag, fieldOffset: 12, value: 0xFFFFFFF0);   // origLength

        byte[] decoded = WoffDecoder.Decode(woff);

        Assert.Equal(new byte[FillerLength], ReadTable(decoded, FillerTag));
    }

    /// <summary>A table pointing past the end of the container is refused, not clamped.</summary>
    [Fact]
    public void MalformedWoff_TableOffsetOutsideTheContainer_IsRefused()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());
        SetDirectoryField(woff, "cmap", fieldOffset: 4, value: (uint)woff.Length);   // offset

        Assert.Null(WoffDecoder.Decode(woff));
    }

    /// <summary>
    /// <c>offset</c> and <c>compLength</c> are <c>uint32</c> in the file, so adding them in 32-bit
    /// arithmetic wraps and a crafted pair sails past a naive in-bounds test. The bounds check is
    /// done in <c>long</c> for exactly this input.
    /// </summary>
    [Fact]
    public void MalformedWoff_OffsetPlusLengthOverflowsUInt32_IsRefused()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());
        SetDirectoryField(woff, "cmap", fieldOffset: 4, value: 0xFFFFFFF0);   // offset
        SetDirectoryField(woff, "cmap", fieldOffset: 8, value: 0x00000020);   // compLength: wraps to 0x10

        Assert.Null(WoffDecoder.Decode(woff));
    }

    /// <summary>
    /// A directory that does not fit inside the bytes supplied is refused before any entry is
    /// read — which is also what bounds <c>numTables</c>, with no second "sane count" cap to
    /// disagree with it.
    /// </summary>
    [Fact]
    public void MalformedWoff_DeclaringMoreTablesThanTheDirectoryHolds_IsRefused()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());
        woff[12] = 0xFF; woff[13] = 0xFF;   // numTables

        Assert.Null(WoffDecoder.Decode(woff));
    }

    /// <summary>
    /// WOFF 1.0 §4: a stored table is signalled by <c>compLength == origLength</c> and a
    /// compressed one is always smaller. A container claiming otherwise is malformed, and reading
    /// it as "stored" would hand the raw deflate stream to the sfnt parser as table data.
    /// </summary>
    [Fact]
    public void MalformedWoff_CompressedLengthLargerThanTheOriginal_IsRefused()
    {
        byte[] woff = WrapAsWoff(BuildSfnt());
        SetDirectoryField(woff, "cmap", fieldOffset: 12, value: 1);   // origLength < compLength

        Assert.Null(WoffDecoder.Decode(woff));
    }

    /// <summary>
    /// The decompression bomb. A ~65&#160;MiB zero-fill deflates to a few tens of kilobytes, so a
    /// container barely larger than its directory can ask the decoder to materialise more memory
    /// than the machine has. The assertion is the guard's own evidence — the refusal — never a
    /// wall clock: a bomb test that finishes fast has quite possibly not run.
    /// </summary>
    [Fact]
    public void OversizedWoff_ZlibBombTable_IsRefusedByTheDecodeBudget()
    {
        const long BombLength = 65L * 1024 * 1024;   // one MiB past the decoder's 64 MiB ceiling
        var bodies = new List<(string tag, byte[] stored, uint origLength)>
        {
            (FillerTag, DeflateZeros(BombLength), (uint)BombLength),
        };

        Assert.Null(WoffDecoder.Decode(BuildContainer(bodies)));
    }

    // ================================================================
    //  A two-table sfnt: head (for the metrics) + cmap (for the glyph)
    // ================================================================

    private static byte[] BuildSfnt()
    {
        var tables = new List<(string tag, byte[] data)>
        {
            ("head", BuildHead()),
            ("cmap", BuildCmap()),
            (FillerTag, new byte[FillerLength]),
        };
        tables.Sort((a, b) => string.CompareOrdinal(a.tag, b.tag));

        var output = new MemoryStream();
        WriteU32(output, 0x00010000);
        WriteU16(output, tables.Count);
        WriteU16(output, 0); WriteU16(output, 0); WriteU16(output, 0);

        int cursor = 12 + tables.Count * 16;
        foreach ((string tag, byte[] data) in tables)
        {
            output.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            WriteU32(output, 0);            // checksum: nothing in this tree verifies one
            WriteU32(output, cursor);
            WriteU32(output, data.Length);
            cursor += Align4(data.Length);
        }
        foreach ((string _, byte[] data) in tables)
        {
            output.Write(data, 0, data.Length);
            for (int pad = data.Length; pad % 4 != 0; pad++) output.WriteByte(0);
        }
        return output.ToArray();
    }

    private static byte[] BuildHead()
    {
        var s = new MemoryStream();
        WriteU32(s, 0x00010000);          // version
        WriteU32(s, 0x00010000);          // fontRevision
        WriteU32(s, 0);                   // checkSumAdjustment
        WriteU32(s, 0x5F0F3CF5);          // magicNumber
        WriteU16(s, 0);                   // flags
        WriteU16(s, UnitsPerEm);          // offset 18
        for (int i = 0; i < 16; i++) s.WriteByte(0);   // created + modified
        WriteU16(s, 0); WriteU16(s, 0); WriteU16(s, 1000); WriteU16(s, 1000);   // bbox
        WriteU16(s, 0); WriteU16(s, 8); WriteU16(s, 2); WriteU16(s, 0); WriteU16(s, 0);
        return s.ToArray();
    }

    /// <summary>A format-4 cmap with one segment mapping <see cref="MappedCodePoint"/>.</summary>
    private static byte[] BuildCmap()
    {
        var sub = new MemoryStream();
        WriteU16(sub, 4);                 // format
        WriteU16(sub, 32);                // length
        WriteU16(sub, 0);                 // language
        WriteU16(sub, 4);                 // segCountX2: one real segment + terminator
        WriteU16(sub, 0); WriteU16(sub, 0); WriteU16(sub, 0);
        WriteU16(sub, MappedCodePoint); WriteU16(sub, 0xFFFF);        // endCode
        WriteU16(sub, 0);                                             // reservedPad
        WriteU16(sub, MappedCodePoint); WriteU16(sub, 0xFFFF);        // startCode
        WriteU16(sub, (MappedGlyph - MappedCodePoint) & 0xFFFF); WriteU16(sub, 1);   // idDelta
        WriteU16(sub, 0); WriteU16(sub, 0);                           // idRangeOffset

        var s = new MemoryStream();
        WriteU16(s, 0);                   // cmap version
        WriteU16(s, 1);                   // one encoding record
        WriteU16(s, 3); WriteU16(s, 1);   // Windows / Unicode BMP
        WriteU32(s, 12);
        byte[] subtable = sub.ToArray();
        s.Write(subtable, 0, subtable.Length);
        return s.ToArray();
    }

    // ================================================================
    //  A WOFF 1.0 writer, only as general as this file needs
    // ================================================================

    /// <summary>
    /// Wraps an sfnt, deflating only the filler table. The tables are emitted in reverse tag order
    /// so the decoder's own sort is what puts the output directory right.
    /// </summary>
    private static byte[] WrapAsWoff(byte[] sfnt)
    {
        var bodies = new List<(string tag, byte[] stored, uint origLength)>();
        foreach ((string tag, byte[] data) in ReadTables(sfnt))
        {
            byte[] stored = tag == FillerTag ? Deflate(data) : data;
            bodies.Add((tag, stored, (uint)data.Length));
        }
        bodies.Reverse();
        return BuildContainer(bodies);
    }

    private static byte[] BuildContainer(IReadOnlyList<(string tag, byte[] stored, uint origLength)> bodies)
    {
        int cursor = 44 + bodies.Count * 20;
        var offsets = new List<int>();
        foreach ((string _, byte[] stored, uint _) in bodies)
        {
            offsets.Add(cursor);
            cursor += Align4(stored.Length);
        }

        var output = new MemoryStream();
        WriteU32(output, 0x774F4646);     // 'wOFF'
        WriteU32(output, 0x00010000);     // flavor
        WriteU32(output, cursor);         // length
        WriteU16(output, bodies.Count);
        WriteU16(output, 0);              // reserved
        WriteU32(output, 0);              // totalSfntSize — the decoder must not trust it
        WriteU16(output, 1); WriteU16(output, 0);
        WriteU32(output, 0); WriteU32(output, 0); WriteU32(output, 0);
        WriteU32(output, 0); WriteU32(output, 0);

        for (int i = 0; i < bodies.Count; i++)
        {
            (string tag, byte[] stored, uint origLength) = bodies[i];
            output.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            WriteU32(output, offsets[i]);
            WriteU32(output, stored.Length);
            WriteU32(output, origLength);
            WriteU32(output, 0);          // origChecksum
        }
        foreach ((string _, byte[] stored, uint _) in bodies)
        {
            output.Write(stored, 0, stored.Length);
            for (int pad = stored.Length; pad % 4 != 0; pad++) output.WriteByte(0);
        }
        return output.ToArray();
    }

    /// <summary>Overwrites one <c>uint32</c> of a container's directory entry for <paramref name="tag"/>.</summary>
    private static void SetDirectoryField(byte[] woff, string tag, int fieldOffset, uint value)
    {
        int numTables = (woff[12] << 8) | woff[13];
        for (int i = 0; i < numTables; i++)
        {
            int at = 44 + i * 20;
            if (Encoding.ASCII.GetString(woff, at, 4) != tag) continue;
            int p = at + fieldOffset;
            woff[p] = (byte)(value >> 24); woff[p + 1] = (byte)(value >> 16);
            woff[p + 2] = (byte)(value >> 8); woff[p + 3] = (byte)value;
            return;
        }
        throw new InvalidOperationException($"the fixture has no '{tag}' table, so this test is not testing what it says");
    }

    private static int CompressedTableCount(byte[] woff)
    {
        int numTables = (woff[12] << 8) | woff[13];
        int count = 0;
        for (int i = 0; i < numTables; i++)
        {
            int at = 44 + i * 20;
            if (ReadU32(woff, at + 8) < ReadU32(woff, at + 12)) count++;
        }
        return count;
    }

    // ================================================================
    //  sfnt readers
    // ================================================================

    private static List<(string tag, byte[] data)> ReadTables(byte[] sfnt)
    {
        var result = new List<(string, byte[])>();
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            var data = new byte[ReadU32(sfnt, record + 12)];
            Array.Copy(sfnt, (int)ReadU32(sfnt, record + 8), data, 0, data.Length);
            result.Add((Encoding.ASCII.GetString(sfnt, record, 4), data));
        }
        return result;
    }

    private static byte[] ReadTable(byte[] sfnt, string tag)
    {
        foreach ((string t, byte[] data) in ReadTables(sfnt))
            if (t == tag) return data;
        throw new InvalidOperationException($"the decoded sfnt has no '{tag}' table");
    }

    private static string[] TableTags(byte[] sfnt)
    {
        var tags = new List<string>();
        foreach ((string tag, byte[] _) in ReadTables(sfnt)) tags.Add(tag);
        return tags.ToArray();
    }

    // ================================================================
    //  Primitives
    // ================================================================

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// A zlib stream that inflates to <paramref name="length"/> zero bytes, written in chunks so
    /// the test itself never allocates the payload it is asking the decoder to refuse.
    /// </summary>
    private static byte[] DeflateZeros(long length)
    {
        var chunk = new byte[1 << 20];
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            for (long written = 0; written < length; written += chunk.Length)
                z.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
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
