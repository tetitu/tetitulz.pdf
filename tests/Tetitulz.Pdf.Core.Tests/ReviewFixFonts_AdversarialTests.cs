// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Adversarial-input regression tests for the font-layer code-review fixes:
///   1. TrueType cmap format-4 huge-segment DoS — the cumulative work cap truncates a crafted
///      subtable (asserted on what the truncated cmap does and does not contain) and a
///      ~32767-segment one terminates.
///   3. PdfType0Font /W Format-2 unbounded allocation — a [c1 c2 w] range with a huge
///      span is clamped instead of OOMing.
///   6. Type0 identity fallback now round-trips the full BMP (U+FFFF was dropped).
/// </summary>
public class ReviewFixFonts_AdversarialTests
{
    // ================================================================
    //  Finding 1 — cmap format-4 unbounded iteration DoS
    // ================================================================

    /// <summary>
    /// <b>The cap's own evidence.</b> <c>ParseCmap4</c> bounds the cumulative code-point visits
    /// across all segments (<c>MaxCmapWork</c> = 0x40000). When that budget is spent the walk stops,
    /// so every segment after the exhaustion point contributes <i>nothing</i> — and that absence is
    /// something a test can read back off the parsed font.
    ///
    /// <para><b>Why the fixture is shaped this way.</b> Five "burner" segments each covering
    /// 0x0100..0xFFFE cost 5 × 65 279 = 326 395 visits, comfortably past the 262 145 the cap allows,
    /// and a sixth segment maps 'A'..'C' — code points no burner touches, so the only thing that can
    /// map them is that sixth segment. The burners' own mappings are asserted first: without them
    /// the test would pass equally well on a font that was never parsed at all, which is the failure
    /// mode banned pattern 3 exists for.</para>
    ///
    /// <para><b>This replaced a 10-second <c>Stopwatch</c> on the calling thread</b> (see CLAUDE.md
    /// → "Diagnosing a red whole-solution run" §4): under a full-solution run elapsed time measures
    /// the machine's backlog, not the parser's work, and three guard tests in exactly that band
    /// flaked on 2026-08-23. The clock also could not fail for the right reason — it passed whether
    /// the cap fired or the subtable was simply never read.</para>
    /// </summary>
    [Fact]
    public void TrueTypeCmap4_WorkCapTruncatesAnOversizedSubtable()
    {
        var capped = TrueTypeFontProgram.Parse(BuildFontWithCmap4Segments(BurnersThenTrailingSegment()));

        // Inside the cap: the burners really ran, so the absences below are the budget being spent
        // rather than a subtable that was never reached.
        Assert.True(capped.TryGetGlyph(BurnerStart, out ushort insideGid));
        Assert.Equal((ushort)(BurnerStart + 1), insideGid);
        Assert.True(capped.Covers(BurnerEnd));

        // Past the cap: the trailing segment is never visited, so nothing maps 'A'..'C'.
        Assert.False(capped.Covers(0x41));
        Assert.False(capped.Covers(0x42));
        Assert.False(capped.Covers(0x43));

        // The control: the identical trailing segment with no burners ahead of it. Its mappings do
        // appear, which is what makes the three absences above an assertion about the cap.
        var control = TrueTypeFontProgram.Parse(BuildFontWithCmap4Segments(TrailingSegmentOnly()));
        Assert.True(control.TryGetGlyph(0x41, out ushort a));
        Assert.Equal((ushort)1, a);
        Assert.True(control.Covers(0x43));
    }

    /// <summary>
    /// The original hostile input, at the largest size the format can express: 32 765 segments that
    /// each iterate 0x0100..0xFFFE, i.e. ~2.14·10⁹ in-bounds dictionary writes with no cap. Same
    /// evidence as the test above — a trailing segment mapping 'A'..'C' that the cap must never let
    /// the walk reach — measured here on the input the guard was actually written for.
    ///
    /// <para><b>Why the dedicated <see cref="Thread"/> and not a stopwatch.</b> Without the cap this
    /// fixture takes ~45 s of real work (measured), so a run with the guard deleted has to fail
    /// rather than wedge the test host; the <c>Join</c> converts a hang into a failure. It is not
    /// the assertion — the two mapping assertions are — which is what CLAUDE.md's remedy list asks
    /// for, and it removes the thread-pool dependency that made the old calling-thread
    /// <c>Stopwatch</c> a measurement of the run's backlog.</para>
    ///
    /// <para><b>32 766 segments, not 32 767, and that number is the whole point.</b> The builder
    /// appends a terminating 0xFFFF segment, so <c>segCountX2</c> is <c>(n + 1) * 2</c> — and it is
    /// a <c>uint16</c>. At n = 32 767 that field is 65 536, which truncates to <b>0</b>: the parser
    /// then read <b>zero</b> segments and returned instantly. That is what this test's predecessor
    /// fed the guard for its entire life, which is why its 10-second ceiling never fired and why it
    /// would have passed with <c>MaxCmapWork</c> deleted outright.</para>
    /// </summary>
    [Fact]
    public void TrueTypeCmap4_MaximalSegmentCount_IsTruncatedAndTerminates()
    {
        byte[] font = BuildFontWithCmap4Segments(MaximalBurnersThenTrailingSegment());

        TrueTypeFontProgram program = null;
        Exception failure = null;

        var worker = new Thread(() =>
        {
            try { program = TrueTypeFontProgram.Parse(font); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "cmap4-work-cap",
        };

        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(120));

        Assert.True(finished,
            "ParseCmap4 did not return. 32765 segments over 0x0100..0xFFFE is ~2 billion inner-loop " +
            "iterations; the cumulative work cap is the only thing that bounds them.");
        Assert.True(failure == null,
            "ParseCmap4 threw " + failure?.GetType().Name + ": " + failure?.Message +
            ". A crafted subtable must be truncated, not fatal.");

        // The segments it did reach mapped their range, so the parse was real …
        Assert.True(program.TryGetGlyph(BurnerStart, out ushort gid));
        Assert.Equal((ushort)(BurnerStart + 1), gid);   // idDelta 1

        // … and the segment past the cap was never reached.
        Assert.False(program.Covers(0x41));
        Assert.False(program.Covers(0x43));
    }

    [Fact]
    public void TrueTypeCmap4_NormalSegments_StillMapGlyphs()
    {
        // A single, well-formed segment 'A'..'C' → glyphs 1..3 (idDelta == 0 means
        // glyph == codepoint? no — use idDelta so gid = cp + delta).  Map 0x41 → gid via
        // idRangeOffset == 0 and idDelta chosen so 0x41 maps to a non-zero gid.
        byte[] font = BuildFontWithCmap4(segCount: 1, start: 0x0041, end: 0x0043, idDelta: -0x40);
        var program = TrueTypeFontProgram.Parse(font);
        Assert.NotNull(program);
        // 0x41 + (-0x40) = 1, 0x42 → 2, 0x43 → 3
        Assert.True(program.TryGetGlyph(0x41, out ushort g41));
        Assert.Equal((ushort)1, g41);
        Assert.True(program.Covers(0x43));
    }

    // ================================================================
    //  Finding 3 — /W Format-2 unbounded allocation (OOM)
    // ================================================================

    [Fact]
    public void Type0Font_WArrayFormat2_HugeRange_IsClampedNotOom()
    {
        using var doc = BuildMinimalDoc();

        // /W = [0 2000000000 1000] — Format 2 (c1 c2 w) with a ~2-billion-wide span.
        // Pre-fix this allocates ~2 billion dictionary entries and OOMs.
        var w = new PdfArray();
        w.Add(new PdfNumber(0));
        w.Add(new PdfNumber(2000000000));
        w.Add(new PdfNumber(1000));

        var fontDict = BuildType0FontDictWithW(w);

        // Construction parses /W. The clamp's own evidence is the shape of the table it produced —
        // widths inside the clamp window, nothing beyond it — so there is no clock here: a wall
        // clock on the calling thread measures the run's backlog, and the four assertions below
        // already fail if the clamp does not fire.
        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        // CID 0 is in-range and gets width 1000; CIDs just inside the clamp window do too;
        // CIDs far beyond the clamp window fall back to the /DW default (1000 here, so we
        // assert via HasExplicitWidth instead).
        Assert.True(font.HasExplicitWidth(0));
        Assert.Equal(1000d, font.GetWidth(0));
        Assert.True(font.HasExplicitWidth(0x10000));        // last CID inside the clamp window
        Assert.False(font.HasExplicitWidth(0x20000000));    // far beyond the clamp — not allocated
    }

    [Fact]
    public void Type0Font_WArrayFormat2_NormalRange_StillExpands()
    {
        using var doc = BuildMinimalDoc();
        var w = new PdfArray();
        w.Add(new PdfNumber(10));
        w.Add(new PdfNumber(12));
        w.Add(new PdfNumber(750));
        var fontDict = BuildType0FontDictWithW(w);

        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);
        Assert.Equal(750d, font.GetWidth(10));
        Assert.Equal(750d, font.GetWidth(11));
        Assert.Equal(750d, font.GetWidth(12));
        Assert.False(font.HasExplicitWidth(13));
    }

    // ================================================================
    //  Finding 6 — Type0 identity fallback drops U+FFFF
    // ================================================================

    [Fact]
    public void Type0Font_IdentityFallback_RoundTripsUFFFF()
    {
        using var doc = BuildMinimalDoc();
        var fontDict = BuildType0FontDictWithW(new PdfArray()); // empty /W

        // No ToUnicode and no recognised CID ordering → identity fallback path.
        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        // 0xFFFF (the highest BMP code point) must now round-trip; pre-fix it was dropped.
        string decoded = font.Decode(new byte[] { 0xFF, 0xFF });
        Assert.Equal(((char)0xFFFF).ToString(), decoded);

        // A normal BMP code point still round-trips.
        string a = font.Decode(new byte[] { 0x00, 0x41 });
        Assert.Equal("A", a);
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static PdfDocument BuildMinimalDoc()
    {
        string pdfText =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\n" +
            "startxref\n107\n%%EOF\n";
        byte[] raw = Encoding.ASCII.GetBytes(pdfText);
        return PdfDocument.Open(new MemoryStream(raw));
    }

    private static PdfDictionary BuildType0FontDictWithW(PdfArray w)
    {
        var cidFontDict = new PdfDictionary();
        cidFontDict[new PdfName("Type")]    = new PdfName("Font");
        cidFontDict[new PdfName("Subtype")] = new PdfName("CIDFontType2");
        cidFontDict[new PdfName("BaseFont")] = new PdfName("ABCDEF+Test");
        cidFontDict[new PdfName("W")]       = w;

        var descendants = new PdfArray();
        descendants.Add(cidFontDict);

        var fontDict = new PdfDictionary();
        fontDict[new PdfName("Type")]            = new PdfName("Font");
        fontDict[new PdfName("Subtype")]         = new PdfName("Type0");
        fontDict[new PdfName("BaseFont")]        = new PdfName("ABCDEF+Test");
        fontDict[new PdfName("Encoding")]        = new PdfName("Identity-H");
        fontDict[new PdfName("DescendantFonts")] = descendants;
        return fontDict;
    }

    // --- Segment-level format-4 fixtures (for the cumulative work cap). ---

    /// <summary>
    /// First code point of a "burner" segment. Deliberately above 'C' so that the trailing
    /// segment's 0x41..0x43 mappings can only ever come from the trailing segment.
    /// </summary>
    private const int BurnerStart = 0x0100;

    /// <summary>Last code point of a burner segment (0xFFFF terminates the inner loop).</summary>
    private const int BurnerEnd = 0xFFFE;

    /// <summary>
    /// 5 × (0xFFFE − 0x0100 + 1) = 326 395 code-point visits, against
    /// <c>TrueTypeFontProgram.ParseCmap4</c>'s 0x40000 (262 145) cumulative budget — so the cap is
    /// spent partway through the fifth burner and the segment after it is never entered.
    /// </summary>
    private const int BurnerCount = 5;

    private static List<(int start, int end, int idDelta)> TrailingSegmentOnly() =>
        new() { (0x0041, 0x0043, -0x40) };   // 'A' → 1, 'B' → 2, 'C' → 3

    private static List<(int start, int end, int idDelta)> BurnersThenTrailingSegment() =>
        BurnersThenTrailingSegment(BurnerCount);

    /// <summary>
    /// The largest a format-4 subtable can be: <c>segCountX2</c> is a <c>uint16</c> and the builder
    /// appends a terminator, so 32 766 declared segments is the ceiling. One of them is the trailing
    /// 'A'..'C' segment, leaving 32 765 burners.
    /// </summary>
    private static List<(int start, int end, int idDelta)> MaximalBurnersThenTrailingSegment() =>
        BurnersThenTrailingSegment(32_765);

    private static List<(int start, int end, int idDelta)> BurnersThenTrailingSegment(int burners)
    {
        var segments = new List<(int start, int end, int idDelta)>(burners + 1);
        for (int i = 0; i < burners; i++) segments.Add((BurnerStart, BurnerEnd, 1));
        segments.AddRange(TrailingSegmentOnly());
        return segments;
    }

    private static byte[] BuildFontWithCmap4Segments(
        IReadOnlyList<(int start, int end, int idDelta)> segments)
    {
        var tables = new List<(string tag, byte[] data)>
        {
            ("cmap", BuildCmapTableFromSegments(segments)),
        };
        return AssembleSfnt(tables);
    }

    private static byte[] BuildCmapTableFromSegments(
        IReadOnlyList<(int start, int end, int idDelta)> segments)
    {
        byte[] sub = BuildCmap4SubtableFromSegments(segments);

        using var ms = new MemoryStream();
        WriteU16(ms, 0);          // version
        WriteU16(ms, 1);          // numTables
        WriteU16(ms, 3);          // platformID = Windows
        WriteU16(ms, 1);          // encodingID = Unicode BMP
        WriteU32(ms, 4 + 8);      // offset to subtable: header(4) + one encoding record(8)
        ms.Write(sub, 0, sub.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// A format-4 subtable whose segments are given individually, so a fixture can mix wide
    /// "burner" ranges with a narrow one after them. <c>idRangeOffset</c> is 0 throughout
    /// (direct mapping through <c>idDelta</c>), and a terminating 0xFFFF segment is appended.
    /// </summary>
    private static byte[] BuildCmap4SubtableFromSegments(
        IReadOnlyList<(int start, int end, int idDelta)> segments)
    {
        int totalSegs = segments.Count + 1;

        using var ms = new MemoryStream();
        WriteU16(ms, 4);                 // format
        WriteU16(ms, 0);                 // length (parser does not validate it)
        WriteU16(ms, 0);                 // language
        WriteU16(ms, totalSegs * 2);     // segCountX2
        WriteU16(ms, 0);                 // searchRange (unused by parser)
        WriteU16(ms, 0);                 // entrySelector
        WriteU16(ms, 0);                 // rangeShift

        foreach (var seg in segments) WriteU16(ms, seg.end);
        WriteU16(ms, 0xFFFF);            // terminator endCode

        WriteU16(ms, 0);                 // reservedPad

        foreach (var seg in segments) WriteU16(ms, seg.start);
        WriteU16(ms, 0xFFFF);            // terminator startCode

        foreach (var seg in segments) WriteI16(ms, seg.idDelta);
        WriteI16(ms, 1);                 // terminator idDelta

        foreach (var _ in segments) WriteU16(ms, 0);
        WriteU16(ms, 0);                 // terminator idRangeOffset

        return ms.ToArray();
    }

    // --- Minimal in-memory TrueType (sfnt) builder with a single format-4 cmap. ---

    private static byte[] BuildFontWithCmap4(int segCount, int start, int end, int idDelta = 1)
    {
        byte[] cmapTable = BuildCmapTable(segCount, start, end, idDelta);

        // Minimal required-ish tables.  The parser tolerates missing head/maxp/hhea/hmtx;
        // we only need a valid sfnt directory pointing at the cmap subtable.
        var tables = new List<(string tag, byte[] data)>
        {
            ("cmap", cmapTable),
        };

        return AssembleSfnt(tables);
    }

    private static byte[] BuildCmapTable(int segCount, int start, int end, int idDelta)
    {
        // cmap header: version(u16)=0, numTables(u16)=1
        // encoding record: platformID(u16)=3, encodingID(u16)=1, offset(u32)
        // then the format-4 subtable.
        var sub = BuildCmap4Subtable(segCount, start, end, idDelta);

        using var ms = new MemoryStream();
        WriteU16(ms, 0);          // version
        WriteU16(ms, 1);          // numTables
        WriteU16(ms, 3);          // platformID = Windows
        WriteU16(ms, 1);          // encodingID = Unicode BMP
        uint subOffset = 4 + 8;   // header(4) + one encoding record(8)
        WriteU32(ms, subOffset);  // offset to subtable
        ms.Write(sub, 0, sub.Length);
        return ms.ToArray();
    }

    private static byte[] BuildCmap4Subtable(int segCount, int start, int end, int idDelta)
    {
        // Layout: each of the `segCount` data segments is identical (start..end), with a
        // mandatory final terminating segment 0xFFFF..0xFFFF.  We craft `segCount` real
        // segments + 1 terminator.
        int totalSegs = segCount + 1;

        using var ms = new MemoryStream();
        WriteU16(ms, 4);                 // format
        WriteU16(ms, 0);                 // length (parser does not validate it)
        WriteU16(ms, 0);                 // language
        WriteU16(ms, totalSegs * 2);     // segCountX2
        WriteU16(ms, 0);                 // searchRange (unused by parser)
        WriteU16(ms, 0);                 // entrySelector
        WriteU16(ms, 0);                 // rangeShift

        // endCode[]
        for (int i = 0; i < segCount; i++) WriteU16(ms, end);
        WriteU16(ms, 0xFFFF);            // terminator endCode

        WriteU16(ms, 0);                 // reservedPad

        // startCode[]
        for (int i = 0; i < segCount; i++) WriteU16(ms, start);
        WriteU16(ms, 0xFFFF);            // terminator startCode

        // idDelta[]
        for (int i = 0; i < segCount; i++) WriteI16(ms, idDelta);
        WriteI16(ms, 1);                 // terminator idDelta

        // idRangeOffset[] — all zero (direct mapping via idDelta)
        for (int i = 0; i < segCount; i++) WriteU16(ms, 0);
        WriteU16(ms, 0);                 // terminator idRangeOffset

        return ms.ToArray();
    }

    private static byte[] AssembleSfnt(List<(string tag, byte[] data)> tables)
    {
        using var ms = new MemoryStream();
        int numTables = tables.Count;

        // Offset table
        WriteU32(ms, 0x00010000);        // sfnt version 1.0 (TrueType)
        WriteU16(ms, numTables);
        WriteU16(ms, 0);                 // searchRange
        WriteU16(ms, 0);                 // entrySelector
        WriteU16(ms, 0);                 // rangeShift

        // Table directory comes next; compute data offsets.
        int dirSize = numTables * 16;
        int dataStart = 12 + dirSize;

        // First pass: write directory with computed offsets, 4-byte aligning each table.
        var offsets = new List<int>();
        int cursor = dataStart;
        foreach (var (_, data) in tables)
        {
            offsets.Add(cursor);
            cursor += Align4(data.Length);
        }

        for (int i = 0; i < tables.Count; i++)
        {
            var (tag, data) = tables[i];
            byte[] tagBytes = Encoding.ASCII.GetBytes(tag.PadRight(4).Substring(0, 4));
            ms.Write(tagBytes, 0, 4);
            WriteU32(ms, 0);                  // checksum (unchecked)
            WriteU32(ms, (uint)offsets[i]);   // offset
            WriteU32(ms, (uint)data.Length);  // length
        }

        // Table data, aligned.
        for (int i = 0; i < tables.Count; i++)
        {
            var (_, data) = tables[i];
            ms.Write(data, 0, data.Length);
            int pad = Align4(data.Length) - data.Length;
            for (int p = 0; p < pad; p++) ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    private static int Align4(int n) => (n + 3) & ~3;

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteI16(Stream s, int v) => WriteU16(s, v & 0xFFFF);

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)((v >> 24) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }
}
