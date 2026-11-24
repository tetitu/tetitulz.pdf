// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Font subsetting is the optimizer step whose failure mode is invisible: a pruned font that drops
/// a glyph the document draws still produces a structurally valid PDF, and the damage shows up only
/// as a blank where a character should be. So these tests assert the <i>structure of the rewritten
/// font program</i> — which glyph keeps its outline, byte for byte, and which becomes a zero-length
/// <c>loca</c> range — rather than that the file got smaller. A subsetter that deletes the wrong
/// glyphs also makes the file smaller.
/// </summary>
public class FontSubsettingTests
{
    // Glyph IDs used throughout. Glyph 3 is a composite that references glyph 4, so drawing 3 must
    // retain 4 even though nothing draws 4 directly.
    private const int GlyphNotdef = 0;
    private const int GlyphDrawn = 1;
    private const int GlyphUnused = 2;
    private const int GlyphComposite = 3;
    private const int GlyphComponent = 4;
    private const int GlyphUnusedLarge = 5;

    // ------------------------------------------------------------------
    //  TrueTypeGlyphPruner
    // ------------------------------------------------------------------

    [Fact]
    public void Prune_KeepsTheOutlineOfADrawnGlyphByteIdentical()
    {
        byte[] font = BuildTestFont();
        byte[] original = GlyphBytes(font, GlyphDrawn);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(reason);
        Assert.NotNull(pruned);
        Assert.Equal(original, GlyphBytes(pruned, GlyphDrawn));
    }

    [Fact]
    public void Prune_GivesAnUndrawnGlyphAZeroLengthLocaRange()
    {
        byte[] font = BuildTestFont();
        Assert.NotEmpty(GlyphBytes(font, GlyphUnused));   // it had an outline to begin with

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out _);

        long[] loca = ReadLoca(pruned);
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
        Assert.Empty(GlyphBytes(pruned, GlyphUnused));
    }

    /// <summary>
    /// The whole design rests on this: glyph IDs must not move, or <c>/CIDToGIDMap</c>, <c>/W</c>
    /// and <c>hmtx</c> — none of which this pruner rewrites — would all point at the wrong glyph.
    /// </summary>
    [Fact]
    public void Prune_PreservesTheGlyphCountAndEveryRetainedGlyphId()
    {
        byte[] font = BuildTestFont();

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn, GlyphComposite }, out _);

        Assert.Equal(NumGlyphs(font), NumGlyphs(pruned));

        // Each retained glyph is still reachable at its original ID with its original bytes.
        foreach (int gid in new[] { GlyphNotdef, GlyphDrawn, GlyphComposite, GlyphComponent })
            Assert.Equal(GlyphBytes(font, gid), GlyphBytes(pruned, gid));
    }

    [Fact]
    public void Prune_RetainsTheComponentsOfADrawnCompositeGlyph()
    {
        byte[] font = BuildTestFont();
        byte[] component = GlyphBytes(font, GlyphComponent);

        // Only the composite is drawn; its component is never named by the document.
        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphComposite }, out _);

        Assert.Equal(component, GlyphBytes(pruned, GlyphComponent));
    }

    [Fact]
    public void Prune_KeepsGlyphZeroEvenWhenNothingDrawsIt()
    {
        byte[] font = BuildTestFont();
        byte[] notdef = GlyphBytes(font, GlyphNotdef);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out _);

        Assert.Equal(notdef, GlyphBytes(pruned, GlyphNotdef));
    }

    [Fact]
    public void Prune_RemovesTheBytesOfEveryUndrawnGlyph()
    {
        byte[] font = BuildTestFont();

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out _);

        // Glyphs 2 and 5 are the two large ones; both must be gone from glyf.
        long[] loca = ReadLoca(pruned);
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
        Assert.Equal(loca[GlyphUnusedLarge], loca[GlyphUnusedLarge + 1]);
        Assert.True(pruned.Length < font.Length,
            $"pruned {pruned.Length} bytes is not smaller than the original {font.Length}");
    }

    /// <summary>
    /// Every other assertion here reads the pruned font back with this file's own helpers, which
    /// were written alongside the pruner and would agree with it about a wrong reassembly. So the
    /// output is also handed to <see cref="TrueTypeFontProgram"/>, a parser in Core that knows
    /// nothing about any of this, and has to reach the same glyph count.
    ///
    /// <para><b>Measured scope, not assumed scope.</b> Breaking 4-byte table alignment was tried
    /// against this test and it passed anyway — Core's parser reads tables at their declared
    /// offsets and does not care whether those offsets are aligned. So the independent parser
    /// covers the directory, the offsets and <c>numGlyphs</c>, and the explicit assertion below
    /// covers alignment, which §5.2.2 requires and which some consumers do enforce.</para>
    /// </summary>
    [Fact]
    public void Prune_ProducesAFontCoreCanStillParseWithFourByteAlignedTables()
    {
        byte[] font = BuildTestFont();
        var before = TrueTypeFontProgram.Parse(font);
        Assert.NotNull(before);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out _);

        var after = TrueTypeFontProgram.Parse(pruned);
        Assert.NotNull(after);
        Assert.Equal(before!.GlyphCount, after!.GlyphCount);

        foreach (var table in ReadTables(pruned))
        {
            Assert.True(table.Value.Offset % 4 == 0,
                $"table '{table.Key}' starts at {table.Value.Offset}, which is not 4-byte aligned");
        }
    }

    [Fact]
    public void Prune_RefusesCffOutlines()
    {
        byte[] font = BuildTestFont();
        // 'OTTO' marks CFF outlines, which this pruner does not rewrite.
        font[0] = (byte)'O'; font[1] = (byte)'T'; font[2] = (byte)'T'; font[3] = (byte)'O';

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(pruned);
        Assert.Contains("CFF", reason);
    }

    [Fact]
    public void Prune_RefusesAMalformedTableDirectory()
    {
        byte[] font = BuildTestFont();
        // Point the first table record's offset far beyond the end of the font.
        WriteU32(font, 12 + 8, 0x7FFFFFFF);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(pruned);
        // Worded by Core.Fonts.SfntTableDirectory since the three re-emitters were given one
        // directory reader; it names the offending table, which this copy's own message did not.
        Assert.Contains("lies outside the font", reason);
    }

    /// <summary>
    /// A font may legally be written such that composite glyph A references B and B references A.
    /// The closure walk must terminate and still retain both.
    /// </summary>
    [Fact]
    public void Prune_TerminatesOnACompositeGlyphCycle()
    {
        // Glyph 1 → glyph 2, glyph 2 → glyph 1.
        var glyphs = new List<byte[]>
        {
            SimpleGlyph(12, 0xA0),
            CompositeGlyph(GlyphUnused),
            CompositeGlyph(GlyphDrawn),
            SimpleGlyph(64, 0xD0),
        };
        byte[] font = BuildFont(glyphs);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(reason);
        Assert.NotNull(pruned);
        Assert.Equal(GlyphBytes(font, GlyphDrawn), GlyphBytes(pruned, GlyphDrawn));
        Assert.Equal(GlyphBytes(font, GlyphUnused), GlyphBytes(pruned, GlyphUnused));
    }

    /// <summary>
    /// A tag byte outside 32..126 was read through <c>Ascii</c>, which substitutes '?', and
    /// <c>Reassemble</c> writes the tag back out from that string rather than from the original
    /// four bytes. So the table was silently renamed — and with two such tags they collapse onto
    /// one dictionary key, the second table is dropped from the output entirely, and
    /// <c>numTables</c> is written as the surviving count, leaving a self-consistent sfnt that is
    /// no longer the font the document embedded. It came back with <c>reason == null</c>, so
    /// <c>SubsetFonts</c> committed it and counted a success.
    /// </summary>
    [Fact]
    public void Prune_RefusesAFontWhoseTableTagIsNotPrintableAscii()
    {
        byte[] font = BuildTestFont();
        int record = RecordOf(font, "hmtx");
        Assert.True(record > 0, "the fixture no longer has an hmtx table record");

        // hmtx deliberately: the pruner needs glyf, loca, head and maxp, so mangling one of those
        // would be refused for a different reason and the test would pass without this guard.
        font[record] = 0x80;

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(pruned);
        Assert.Contains("printable ASCII", reason);
    }

    /// <summary>
    /// A '?' that really is byte 0x3F round-trips, so the refusal above fires on a substitution
    /// rather than on the character. Written as a whole-font assertion — the fixture's own tables
    /// are untouched, so a correct prune still happens.
    /// </summary>
    [Fact]
    public void Prune_AcceptsATagContainingALiteralQuestionMark()
    {
        byte[] font = BuildTestFont();
        int record = RecordOf(font, "hmtx");
        Assert.True(record > 0, "the fixture no longer has an hmtx table record");
        font[record] = (byte)'?';

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, new HashSet<int> { GlyphDrawn }, out string reason);

        Assert.Null(reason);
        Assert.NotNull(pruned);
        Assert.True(ReadTables(pruned).ContainsKey("?mtx"),
            "the tag was not carried through to the output");
    }

    [Fact]
    public void Prune_RefusesWhenPruningWouldNotShrinkTheProgram()
    {
        byte[] font = BuildTestFont();
        var everything = new HashSet<int>();
        for (int gid = 0; gid < NumGlyphs(font); gid++) everything.Add(gid);

        byte[] pruned = TrueTypeGlyphPruner.Prune(font, everything, out string reason);

        Assert.Null(pruned);
        Assert.Contains("smaller", reason);
    }

    // ------------------------------------------------------------------
    //  End to end, through PdfOptimizer
    // ------------------------------------------------------------------

    [Fact]
    public void Optimize_SubsetsAnIdentityHFontToTheGlyphsThePageDraws()
    {
        byte[] pdf = BuildPdfWithEmbeddedType0Font(BuildTestFont());

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        // A reported count implies a reported saving; the result exposes both and a caller that
        // sees FontsSubset = 1 alongside FontBytesSaved = 0 has been told the pass did nothing.
        Assert.True(result.FontBytesSaved > 0,
            $"FontsSubset was {result.FontsSubset} but FontBytesSaved was {result.FontBytesSaved}");

        byte[] program = ExtractFontProgram(optimized);
        long[] loca = ReadLoca(program);

        // The page draws CIDs 1 and 3; 3 is composite over 4. So 0, 1, 3 and 4 survive...
        Assert.NotEqual(loca[GlyphDrawn], loca[GlyphDrawn + 1]);
        Assert.NotEqual(loca[GlyphComposite], loca[GlyphComposite + 1]);
        Assert.NotEqual(loca[GlyphComponent], loca[GlyphComponent + 1]);
        // ...and the two the page never draws are blanked.
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
        Assert.Equal(loca[GlyphUnusedLarge], loca[GlyphUnusedLarge + 1]);
    }

    [Fact]
    public void Optimize_LeavesTheFontProgramIntactWhenSubsettingIsOff()
    {
        byte[] original = BuildTestFont();
        byte[] pdf = BuildPdfWithEmbeddedType0Font(original);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = false },
            out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);
        Assert.Equal(ReadLoca(original), ReadLoca(ExtractFontProgram(optimized)));
    }

    /// <summary>
    /// A <c>Tf</c> naming a font that will not resolve means text was drawn that cannot be
    /// attributed to any program, so no font in the document may be pruned — the unresolved name
    /// could have been any of them.
    /// </summary>
    [Fact]
    public void Optimize_SubsetsNothingWhenAFontResourceCannotBeResolved()
    {
        byte[] original = BuildTestFont();
        // The content stream selects /F9, which the page's /Font dictionary does not define.
        byte[] pdf = BuildPdfWithEmbeddedType0Font(original, extraContent: "BT /F9 12 Tf (x) Tj ET");

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);
        Assert.Equal(ReadLoca(original), ReadLoca(ExtractFontProgram(optimized)));
    }

    /// <summary>
    /// The size claim, not just the occurrence claim. `FontsSubset = 1` says the pass ran; it says
    /// nothing about whether it was worth running, and a pruner that dropped one glyph would satisfy
    /// it. The fixture's unused glyphs are 9 000 of its ~9 750 outline bytes, so a correct prune is
    /// dramatic — and a regression that quietly retained everything would still report `1`.
    /// </summary>
    [Fact]
    public void Optimize_SubsettingRemovesTheBulkOfAnEmbeddedFontProgram()
    {
        byte[] original = BuildTestFont();
        byte[] pdf = BuildPdfWithEmbeddedType0Font(original);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        byte[] pruned = ExtractFontProgram(optimized);

        Assert.Equal(1, result.FontsSubset);
        Assert.True(pruned.Length * 2 < original.Length,
            $"the pruned program is {pruned.Length} bytes against {original.Length}; " +
            "the glyphs the page never draws are most of the original");
    }

    /// <summary>
    /// §8.10.1 runs a form XObject in the caller's graphics state, and the text font is part of it.
    /// A form that draws text without its own <c>Tf</c> is therefore drawing with the page's font,
    /// and those glyphs must survive. Scanning the form as if it had no font leaves them
    /// unrecorded — and unrecorded is exactly what gets pruned.
    /// </summary>
    [Fact]
    public void Optimize_KeepsGlyphsAFormXObjectDrawsWithTheInheritedFont()
    {
        byte[] pdf = BuildPdfWithFormDrawingInheritedFont(BuildTestFont());

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        long[] loca = ReadLoca(ExtractFontProgram(optimized));
        // The page draws CID 1; the form draws CID 5 with no Tf of its own.
        Assert.NotEqual(loca[GlyphDrawn], loca[GlyphDrawn + 1]);
        Assert.NotEqual(loca[GlyphUnusedLarge], loca[GlyphUnusedLarge + 1]);
        // Nothing draws CID 2, so it is still pruned — the rule must not simply retain everything.
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
    }

    /// <summary>
    /// <c>/CIDToGIDMap</c> may be a stream rather than <c>/Identity</c>, and then the CID the page
    /// draws is <b>not</b> the glyph to keep. The map here sends CID 1 to glyph 4, so honouring it
    /// retains glyph 4 and prunes glyph 1 — the exact reverse of what Identity would do. A pruner
    /// that ignored the stream, or read it with the wrong byte order or stride, would keep glyph 1
    /// and blank the glyph actually drawn.
    /// </summary>
    [Fact]
    public void Optimize_HonoursAStreamCidToGidMapRatherThanAssumingIdentity()
    {
        // CID:      0  1  2  3  4  5
        // GID:      0  4  2  3  1  5
        byte[] map = { 0, 0, 0, 4, 0, 2, 0, 3, 0, 1, 0, 5 };
        byte[] pdf = BuildPdfWithEmbeddedType0Font(BuildTestFont(), cidToGidMap: map);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        long[] loca = ReadLoca(ExtractFontProgram(optimized));

        // The page draws CIDs 1 and 3 → glyphs 4 and 3. Glyph 3 is the composite over glyph 4.
        Assert.NotEqual(loca[GlyphComponent], loca[GlyphComponent + 1]);   // glyph 4, from CID 1
        Assert.NotEqual(loca[GlyphComposite], loca[GlyphComposite + 1]);   // glyph 3, from CID 3

        // Glyph 1 is what Identity would have kept for CID 1. Nothing maps to it, so it goes.
        Assert.Equal(loca[GlyphDrawn], loca[GlyphDrawn + 1]);
    }

    /// <summary>
    /// §7.7.3.2 requires <c>/Type /Pages</c> on an intermediate page-tree node, but producers omit it
    /// and Core reads those files anyway — <c>PdfPageTree</c> recognises a leaf structurally, by the
    /// absence of <c>/Kids</c>. The glyph scanner classified every node that did not say
    /// <c>/Type /Pages</c> as a leaf, so an untyped intermediate node was "scanned" (it draws nothing)
    /// and its <c>/Kids</c> were never followed.
    ///
    /// <para>The damage is not a skipped optimization. Page 1 is reachable and page 2 is behind the
    /// untyped node, and both draw the same font program — so the scan came back looking complete,
    /// with <c>Reason</c> null, and the program was pruned to page 1's glyphs. Page 2's glyph was
    /// deleted out of a document the renderer draws correctly. The assertion is therefore on the
    /// <c>loca</c> range of the glyph only the hidden page draws.</para>
    /// </summary>
    [Fact]
    public void Optimize_KeepsGlyphsDrawnByAPageBehindAnUntypedPageTreeNode()
    {
        byte[] pdf = BuildPdfWithPageBehindUntypedNode(BuildTestFont());

        // The premise: Core reaches both pages, so anything the optimizer misses is its own doing.
        using (var probe = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Equal(2, probe.Pages.Count);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        long[] loca = ReadLoca(ExtractFontProgram(optimized));
        // Page 1 draws CID 1; the page behind the untyped node draws CID 5. Both must survive.
        Assert.NotEqual(loca[GlyphDrawn], loca[GlyphDrawn + 1]);
        Assert.NotEqual(loca[GlyphUnusedLarge], loca[GlyphUnusedLarge + 1]);
        // Nothing draws CID 2, so it still goes — the rule must not degrade into retaining everything.
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
    }

    /// <summary>
    /// The other half of Core's rule, and the half that is easy to lose while fixing the first: a
    /// leaf page whose <c>/Type</c> is missing is still a page, recognised by having no <c>/Kids</c>
    /// but a <c>/Contents</c> and a <c>/MediaBox</c>. Classifying purely on <c>/Type /Page</c> would
    /// make this document scan as having no pages at all — and then nothing is subset, so the claim
    /// under test is that the pass ran and pruned the right glyphs.
    /// </summary>
    [Fact]
    public void Optimize_ScansALeafPageThatDeclaresNoType()
    {
        byte[] pdf = BuildPdfWithUntypedLeafPage(BuildTestFont());

        using (var probe = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Single(probe.Pages);

        var result = Optimize(pdf, new OptimizationOptions { SubsetEmbeddedFonts = true },
            out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);

        long[] loca = ReadLoca(ExtractFontProgram(optimized));
        // The page draws CIDs 1 and 3; 3 is composite over 4.
        Assert.NotEqual(loca[GlyphDrawn], loca[GlyphDrawn + 1]);
        Assert.NotEqual(loca[GlyphComposite], loca[GlyphComposite + 1]);
        Assert.NotEqual(loca[GlyphComponent], loca[GlyphComponent + 1]);
        Assert.Equal(loca[GlyphUnused], loca[GlyphUnused + 1]);
        Assert.Equal(loca[GlyphUnusedLarge], loca[GlyphUnusedLarge + 1]);
    }

    // ------------------------------------------------------------------
    //  Synthetic TrueType font
    // ------------------------------------------------------------------

    /// <summary>
    /// Six glyphs: .notdef, one drawn, one unused, a composite over glyph 4, that component, and a
    /// large unused one. The two unused glyphs carry most of the bytes, so a correct prune is
    /// clearly visible in the output size as well as in loca.
    /// </summary>
    private static byte[] BuildTestFont() => BuildFont(new List<byte[]>
    {
        SimpleGlyph(12,   0xA0),   // 0 .notdef
        SimpleGlyph(200,  0xB1),   // 1 drawn
        SimpleGlyph(4000, 0xC2),   // 2 unused
        CompositeGlyph(GlyphComponent), // 3 drawn, composite
        SimpleGlyph(300,  0xE4),   // 4 component of 3
        SimpleGlyph(5000, 0xF5),   // 5 unused
    });

    private static byte[] SimpleGlyph(int totalLength, byte fill)
    {
        if (totalLength < 10) throw new ArgumentOutOfRangeException(nameof(totalLength));
        var glyph = new byte[totalLength];
        WriteU16(glyph, 0, 1);                     // numberOfContours = 1 → simple
        for (int i = 2; i < totalLength; i++) glyph[i] = fill;
        return glyph;
    }

    private static byte[] CompositeGlyph(int componentGlyphId)
    {
        // numberOfContours = −1, bbox, then one component record: flags = 0 (byte arguments, no
        // scale, no further components).
        var glyph = new byte[16];
        WriteU16(glyph, 0, 0xFFFF);                // −1 → composite
        WriteU16(glyph, 10, 0);                    // flags
        WriteU16(glyph, 12, componentGlyphId);
        return glyph;
    }

    private static byte[] BuildFont(IReadOnlyList<byte[]> glyphs)
    {
        int numGlyphs = glyphs.Count;

        var glyf = new MemoryStream();
        var locaOffsets = new long[numGlyphs + 1];
        for (int i = 0; i < numGlyphs; i++)
        {
            locaOffsets[i] = glyf.Length;
            glyf.Write(glyphs[i], 0, glyphs[i].Length);
            int pad = (int)((4 - (glyf.Length & 3)) & 3);
            for (int p = 0; p < pad; p++) glyf.WriteByte(0);
        }
        locaOffsets[numGlyphs] = glyf.Length;

        // Long loca throughout: it addresses any size, so the fixture never depends on the
        // 128 KiB ceiling of the short format.
        var loca = new byte[(numGlyphs + 1) * 4];
        for (int i = 0; i <= numGlyphs; i++) WriteU32(loca, i * 4, (uint)locaOffsets[i]);

        var head = new byte[54];
        WriteU32(head, 0, 0x00010000);             // version
        WriteU32(head, 12, 0x5F0F3CF5);            // magicNumber
        WriteU16(head, 18, 1000);                  // unitsPerEm
        WriteU16(head, 50, 1);                     // indexToLocFormat = long

        var maxp = new byte[32];
        WriteU32(maxp, 0, 0x00010000);
        WriteU16(maxp, 4, numGlyphs);

        var hhea = new byte[36];
        WriteU32(hhea, 0, 0x00010000);
        WriteU16(hhea, 34, numGlyphs);             // numberOfHMetrics

        var hmtx = new byte[numGlyphs * 4];
        for (int i = 0; i < numGlyphs; i++) WriteU16(hmtx, i * 4, 500);

        return Assemble(new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["loca"] = loca,
            ["maxp"] = maxp,
        });
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int numTables = tables.Count;
        int directory = 12 + numTables * 16;

        long total = directory;
        foreach (var table in tables.Values) total += (table.Length + 3) & ~3;

        var font = new byte[total];
        WriteU32(font, 0, 0x00010000);
        WriteU16(font, 4, numTables);

        int record = 12;
        int dataAt = directory;
        foreach (var pair in tables)
        {
            for (int i = 0; i < 4; i++) font[record + i] = (byte)pair.Key[i];
            WriteU32(font, record + 8, (uint)dataAt);
            WriteU32(font, record + 12, (uint)pair.Value.Length);
            Buffer.BlockCopy(pair.Value, 0, font, dataAt, pair.Value.Length);
            dataAt += (pair.Value.Length + 3) & ~3;
            record += 16;
        }
        return font;
    }

    // ------------------------------------------------------------------
    //  Reading a font back
    // ------------------------------------------------------------------

    /// <summary>Byte offset of the table directory record carrying <paramref name="tag"/>, or −1.</summary>
    private static int RecordOf(byte[] font, string tag)
    {
        int numTables = ReadU16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            if ((char)font[record] == tag[0] && (char)font[record + 1] == tag[1] &&
                (char)font[record + 2] == tag[2] && (char)font[record + 3] == tag[3])
            {
                return record;
            }
        }
        return -1;
    }

    private static Dictionary<string, (int Offset, int Length)> ReadTables(byte[] font)
    {
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        int numTables = ReadU16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            var tag = new string(new[]
            {
                (char)font[record], (char)font[record + 1],
                (char)font[record + 2], (char)font[record + 3]
            });
            tables[tag] = ((int)ReadU32(font, record + 8), (int)ReadU32(font, record + 12));
        }
        return tables;
    }

    private static int NumGlyphs(byte[] font) => ReadU16(font, ReadTables(font)["maxp"].Offset + 4);

    private static long[] ReadLoca(byte[] font)
    {
        var tables = ReadTables(font);
        int numGlyphs = NumGlyphs(font);
        int format = (short)ReadU16(font, tables["head"].Offset + 50);
        int at = tables["loca"].Offset;

        var offsets = new long[numGlyphs + 1];
        for (int i = 0; i <= numGlyphs; i++)
        {
            offsets[i] = format == 0
                ? (long)ReadU16(font, at + i * 2) * 2
                : ReadU32(font, at + i * 4);
        }
        return offsets;
    }

    private static byte[] GlyphBytes(byte[] font, int glyphId)
    {
        var tables = ReadTables(font);
        long[] loca = ReadLoca(font);
        int glyf = tables["glyf"].Offset;

        int length = (int)(loca[glyphId + 1] - loca[glyphId]);
        if (length <= 0) return Array.Empty<byte>();

        var bytes = new byte[length];
        Buffer.BlockCopy(font, glyf + (int)loca[glyphId], bytes, 0, length);
        return bytes;
    }

    // ------------------------------------------------------------------
    //  PDF fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page document whose page draws CIDs 1 and 3 with an Identity-H Type0 font over a
    /// CIDFontType2 descendant carrying <paramref name="fontProgram"/> as its <c>/FontFile2</c>.
    /// </summary>
    private static byte[] BuildPdfWithEmbeddedType0Font(byte[] fontProgram, string extraContent = null,
        byte[] cidToGidMap = null)
    {
        // <0001> and <0003> are two 2-byte Identity-H codes, so they are CIDs 1 and 3 outright.
        string content = "BT /F1 12 Tf 100 700 Td <00010003> Tj ET";
        if (extraContent != null) content += "\n" + extraContent;

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        // Object 9 exists only when /CIDToGIDMap is a stream.
        int objectCount = cidToGidMap == null ? 8 : 9;
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
        Write("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H "
            + "/DescendantFonts [6 0 R] >>\nendobj\n");

        string mapEntry = cidToGidMap == null ? "/Identity" : "9 0 R";
        offsets[6] = ms.Position;
        Write("6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor 7 0 R /CIDToGIDMap {mapEntry} /DW 500 >>\nendobj\n");

        offsets[7] = ms.Position;
        Write("7 0 obj\n<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 "
            + "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
            + "/FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>\nendobj\n");

        offsets[8] = ms.Position;
        Write($"8 0 obj\n<< /Length {fontProgram.Length} /Length1 {fontProgram.Length} >>\nstream\n");
        ms.Write(fontProgram, 0, fontProgram.Length);
        Write("\nendstream\nendobj\n");

        if (cidToGidMap != null)
        {
            offsets[9] = ms.Position;
            Write($"9 0 obj\n<< /Length {cidToGidMap.Length} >>\nstream\n");
            ms.Write(cidToGidMap, 0, cidToGidMap.Length);
            Write("\nendstream\nendobj\n");
        }

        int size = objectCount + 1;
        long xref = ms.Position;
        Write($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// The page selects /F1 and draws CID 1, then invokes a form that draws CID 5 <b>without</b>
    /// setting a font of its own.
    /// </summary>
    private static byte[] BuildPdfWithFormDrawingInheritedFont(byte[] fontProgram)
    {
        const string pageContent = "BT /F1 12 Tf 100 700 Td <0001> Tj ET\n/Fm0 Do";
        const string formContent = "BT 100 600 Td <0005> Tj ET";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[10];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 9 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H "
            + "/DescendantFonts [6 0 R] >>\nendobj\n");

        offsets[6] = ms.Position;
        Write("6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity /DW 500 >>\nendobj\n");

        offsets[7] = ms.Position;
        Write("7 0 obj\n<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 "
            + "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
            + "/FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>\nendobj\n");

        offsets[8] = ms.Position;
        Write($"8 0 obj\n<< /Length {fontProgram.Length} /Length1 {fontProgram.Length} >>\nstream\n");
        ms.Write(fontProgram, 0, fontProgram.Length);
        Write("\nendstream\nendobj\n");

        // No /Resources of its own: the form falls back to the page's, which is what makes the
        // inherited /F1 reachable at all.
        offsets[9] = ms.Position;
        Write($"9 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
            + $"/Length {formContent.Length} >>\nstream\n{formContent}\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 10\n0000000000 65535 f \n");
        for (int i = 1; i <= 9; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 10 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// The standard one-page fixture with the leaf page's <c>/Type</c> key renamed, so the node
    /// carries no <c>/Type</c> at all and has to be recognised by its shape. <c>/Junk</c> is the same
    /// length as <c>/Type</c>, so every xref offset stays valid.
    /// </summary>
    private static byte[] BuildPdfWithUntypedLeafPage(byte[] fontProgram)
    {
        byte[] pdf = BuildPdfWithEmbeddedType0Font(fontProgram);

        // The trailing space is what keeps this off object 2's "/Type /Pages".
        byte[] needle = Encoding.ASCII.GetBytes("/Type /Page ");
        int at = IndexOf(pdf, needle);
        Assert.True(at >= 0, "the fixture no longer contains a '/Type /Page ' leaf");

        foreach (char c in "/Junk") pdf[at++] = (byte)c;
        return pdf;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// Two pages sharing one font. Page 1 hangs off the root <c>/Pages</c> node and draws CID 1;
    /// page 2 hangs off object 10, an intermediate node with <c>/Kids</c> and <b>no <c>/Type</c></b>,
    /// and draws CID 5. Core walks both; a scanner that reads "not <c>/Type /Pages</c>" as "leaf"
    /// stops at object 10 and never sees CID 5.
    /// </summary>
    private static byte[] BuildPdfWithPageBehindUntypedNode(byte[] fontProgram)
    {
        const string page1Content = "BT /F1 12 Tf 100 700 Td <0001> Tj ET";
        const string page2Content = "BT /F1 12 Tf 100 700 Td <0005> Tj ET";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        const int objectCount = 11;
        var offsets = new long[objectCount + 1];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>\nendobj\n");

        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {page1Content.Length} >>\nstream\n{page1Content}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H "
            + "/DescendantFonts [6 0 R] >>\nendobj\n");

        offsets[6] = ms.Position;
        Write("6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity /DW 500 >>\nendobj\n");

        offsets[7] = ms.Position;
        Write("7 0 obj\n<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 "
            + "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 "
            + "/FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>\nendobj\n");

        offsets[8] = ms.Position;
        Write($"8 0 obj\n<< /Length {fontProgram.Length} /Length1 {fontProgram.Length} >>\nstream\n");
        ms.Write(fontProgram, 0, fontProgram.Length);
        Write("\nendstream\nendobj\n");

        offsets[9] = ms.Position;
        Write($"9 0 obj\n<< /Length {page2Content.Length} >>\nstream\n{page2Content}\nendstream\nendobj\n");

        // The whole point of the fixture: an intermediate node with /Kids and no /Type.
        offsets[10] = ms.Position;
        Write("10 0 obj\n<< /Parent 2 0 R /Kids [11 0 R] /Count 1 >>\nendobj\n");

        offsets[11] = ms.Position;
        Write("11 0 obj\n<< /Type /Page /Parent 10 0 R /MediaBox [0 0 612 792] /Contents 9 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int size = objectCount + 1;
        long xref = ms.Position;
        Write($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Walks the optimized document to its <c>/FontFile2</c> and returns the decoded program. The
    /// walk is deliberate rather than a lookup by object number: the optimizer renumbers objects.
    /// </summary>
    private static byte[] ExtractFontProgram(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));

        PdfObject Deref(PdfObject o)
        {
            int guard = 0;
            while (o is PdfReference reference && guard++ < 32) o = document.GetObject(reference.ReferenceObjectNumber);
            return o;
        }

        var catalog = Deref(document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Deref(catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Deref(pages[PdfName.Kids]) as PdfArray;
        var page = Deref(kids[0]) as PdfDictionary;
        var resources = Deref(page[PdfName.Resources]) as PdfDictionary;
        var fonts = Deref(resources[PdfName.Font]) as PdfDictionary;

        var type0 = Deref(fonts[new PdfName("F1")]) as PdfDictionary;
        var descendants = Deref(type0[PdfName.DescendantFonts]) as PdfArray;
        var descendant = Deref(descendants[0]) as PdfDictionary;
        var descriptor = Deref(descendant[PdfName.FontDescriptor]) as PdfDictionary;
        var program = Deref(descriptor[PdfName.FontFile2]) as PdfStream;

        Assert.NotNull(program);
        Assert.True(program.TryGetDecodedData(64 * 1024 * 1024, out byte[] bytes),
            "the font program did not decode");
        return bytes;
    }

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }

    // ------------------------------------------------------------------
    //  Primitives
    // ------------------------------------------------------------------

    private static int ReadU16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static void WriteU16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)((value >> 8) & 0xFF);
        data[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
