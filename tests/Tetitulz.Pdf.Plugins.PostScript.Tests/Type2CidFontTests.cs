// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Type 2 CIDFonts (PLRM §5.11.3, Table 5.17): a Type 42 program reached by CID through
/// <c>/CIDMap</c> (<c>/GDBytes</c> bytes a CID, high-order byte first), defined with
/// <c>/CIDFont defineresource</c> and joined to a CMap by <c>composefont</c> — how the Windows driver
/// sends a CJK or Unicode TrueType face. The shown codes go through the CMap to CIDs, through
/// <c>/CIDMap</c> to glyph indices, and out through the Type0 font a Type 42 font uses. Before, such a
/// font drew in the reader's substitute for its name, every code a full em.
/// </summary>
public sealed class Type2CidFontTests
{
    static Type2CidFontTests()
    {
        new AdobeCMapPlugin().Initialize();   // idempotent
    }

    /// <summary>CIDs 3 and 4 are the fixture's glyphs 1 ('A', 610) and 2 ('B', 620); CIDs 0–2 glyph 0.</summary>
    private const string IdentityMap = "<00000000000000010002>";

    /// <summary>
    /// A Type 2 CIDFont, /ProbeCID, composed with <paramref name="cmap"/> as /Composed and set at 100.
    /// </summary>
    private static string Font(string extra = "", string cmap = "Identity-H", int cidCount = 5,
                               string cidMap = IdentityMap, string ordering = "Identity", string sfnts = null,
                               string define = "/CIDFont defineresource pop")
        => "20 dict begin /CIDFontName /ProbeCID def /CIDFontType 2 def /FontType 42 def\n" +
           $"/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering ({ordering}) def /Supplement 0 def end def\n" +
           "/FontMatrix [1 0 0 1 0 0] def /FontBBox [0 0 1 1] def /PaintType 0 def\n" +
           "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for def\n" +
           "/CharStrings 1 dict dup begin /.notdef 0 def end def\n" +
           $"/CIDCount {cidCount} def /GDBytes 2 def /CIDMap {cidMap} def\n" + extra +
           "/sfnts [" + (sfnts ?? Type42FontTests.Hex(Type42FontTests.Program)) + "] def\n" +
           $"/ProbeCID currentdict end {define}\n" +
           $"/Composed /{cmap} [/ProbeCID] composefont pop /Composed findfont 100 scalefont setfont\n";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private const string MarkPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static double[] LastMove(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands
               .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static string[] ShownStrings(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]).ToArray();

    private static PdfDictionary Type0(PostScriptConversionResult r)
        => ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
               .Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });

    private static PdfDictionary Descendant(PostScriptConversionResult r)
        => (PdfDictionary)((PdfArray)Type0(r)[PdfName.DescendantFonts])[0];

    /// <summary>
    /// A four-byte <c>/CIDMap</c> entry is a number, not a sign: one with its high bit set is past the
    /// program's glyphs, so glyph 0, and the entry beside it is still read.
    /// </summary>
    [Fact]
    public void AFourByteCidMapEntryWithItsHighBitSet_IsGlyphZero()
    {
        string font = Font(cidMap: "<00000000000000000000000000000000FFFFFFFF>", cidCount: 5).Replace("/GDBytes 2 def", "/GDBytes 4 def");
        var r = Convert(font + "0 0 moveto <0004> show");

        Assert.NotNull(r.Content);
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(r));

        string control = Font(cidMap: "<0000000000000000000000000000000000000002>", cidCount: 5).Replace("/GDBytes 2 def", "/GDBytes 4 def");
        Assert.Equal(new[] { @"(\000\002)" }, ShownStrings(Convert(control + "0 0 moveto <0004> show")));
    }

    /// <summary>
    /// Under Identity-H the codes are CIDs: 3 and 4 are glyphs 1 and 2 through <c>/CIDMap</c>, shown by
    /// index in a <c>CIDFontType2</c> carrying the program, at their <c>hmtx</c> widths — the pen 123
    /// on at 100, where a full em a code made it 200.
    /// </summary>
    [Fact]
    public void Show_IsTheCidMapsGlyphs()
    {
        var r = Convert(Font() + "0 0 moveto <00030004> show" + MarkPen);
        Assert.Equal(new[] { @"(\000\001\000\002)" }, ShownStrings(r));
        Assert.Equal("CIDFontType2", ((PdfName)Descendant(r)[PdfName.Subtype]).Value);
        Assert.IsType<PdfStream>(((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile2]);
        var widths = (PdfArray)Descendant(r)[PdfName.W];
        Assert.Equal(610.0, ((PdfNumber)((PdfArray)widths[1])[0]).DoubleValue);
        Assert.Equal(new[] { 123.0, 0 }, LastMove(r));
    }

    /// <summary>A CID past <c>/CIDCount</c>, or past the map, is the undefined glyph 0 (PLRM Table 5.17).</summary>
    [Fact]
    public void ACidOutsideTheCountOrTheMap_IsGlyphZero()
    {
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(Convert(Font() + "0 0 moveto <0009> show")));
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(Convert(Font(cidCount: 9) + "0 0 moveto <0008> show")));
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(Convert(Font(cidCount: 3) + "0 0 moveto <0003> show")));   // in the map, past the count
        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(Convert(Font() + "0 0 moveto <0003> show")));   // the control
    }

    /// <summary>A <c>/Metrics</c> entry is keyed by the CID and overrides <c>hmtx</c>: half an em.</summary>
    [Fact]
    public void Metrics_AreKeyedByCid()
    {
        var r = Convert(Font("/Metrics 2 dict dup 3 [0 0.5] put def\n") + "0 0 moveto <0003> show" + MarkPen);
        Assert.Equal(new[] { 50.0, 0 }, LastMove(r));
    }

    /// <summary><c>stringwidth</c> and <c>charpath</c> read the same glyphs: 123, and the glyph's point at (10, 10).</summary>
    [Fact]
    public void StringwidthAndCharpath_ReadTheCidMapsGlyphs()
    {
        var width = Convert(Font() + "<00030004> stringwidth pop 0 moveto 0 0 lineto stroke");
        Assert.Equal(123.0, ContentStreamAssert.FirstOperandsOf(width.Content, "m")[0]);

        var path = Convert(Font() + "0 0 moveto <0003> true charpath fill");
        ContentStreamAssert.Contains(path.Content, "10 10 m");
    }

    /// <summary>
    /// Under a registered CMap the code is decoded as a reader decodes it, and the <c>/ToUnicode</c>
    /// carries the CID's text in the CIDFont's collection: 'A' under UniJIS-UCS2-H and Adobe-Japan1.
    /// </summary>
    [Fact]
    public void ARegisteredCMap_DecodesTheCode_AndTheTextIsTheCollections()
    {
        var code = Core.Fonts.CidCollectionText.Decode("UniJIS-UCS2-H", "Japan1", new byte[] { 0x00, 0x41 })!.Single();
        Assert.Equal("A", code.Text);
        string map = "<" + string.Concat(Enumerable.Range(0, code.Cid + 1).Select(c => c == code.Cid ? "0001" : "0000")) + ">";

        var r = Convert(Font(cmap: "UniJIS-UCS2-H", cidCount: code.Cid + 1, cidMap: map, ordering: "Japan1")
                        + "0 0 moveto <0041> show" + MarkPen);
        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(r));
        Assert.Equal(new[] { 61.0, 0 }, LastMove(r));
        string cmap = Encoding.ASCII.GetString(((PdfStream)Type0(r)[PdfName.ToUnicode]).RawData);
        Assert.Contains("<0001> <0041>", cmap, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The text is the CIDFont's collection's, whatever the CMap: under Identity-H a CIDFont of
    /// Adobe-Japan1 still writes 'A' for the CID that is 'A' in Japan1 — the CMap's own name says
    /// Identity, which has no text.
    /// </summary>
    [Fact]
    public void TheText_IsTheCidFontsCollection_UnderIdentityH()
    {
        int cid = Core.Fonts.CidCollectionText.Decode("UniJIS-UCS2-H", "Japan1", new byte[] { 0x00, 0x41 })!.Single().Cid;
        string map = "<" + string.Concat(Enumerable.Range(0, cid + 1).Select(c => c == cid ? "0001" : "0000")) + ">";
        var r = Convert(Font(cidCount: cid + 1, cidMap: map, ordering: "Japan1") + $"0 0 moveto <{cid:X4}> show");
        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(r));
        string cmap = Encoding.ASCII.GetString(((PdfStream)Type0(r)[PdfName.ToUnicode]).RawData);
        Assert.Contains("<0001> <0041>", cmap, System.StringComparison.Ordinal);
    }

    /// <summary>A CIDFont defined with <c>definefont</c> and composed from its dictionary draws the same way.</summary>
    [Fact]
    public void DefinefontAndADictionaryInTheArray_DrawTheSame()
    {
        string program = Font(define: "definefont /cid exch def")
            .Replace("/Composed /Identity-H [/ProbeCID] composefont", "/Composed /Identity-H [cid] composefont");
        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(Convert(program + "0 0 moveto <0003> show")));
    }

    /// <summary>
    /// A driver's CIDFont: no glyf, the glyph downloaded into <c>/GlyphDirectory</c> by index — the
    /// index <c>/CIDMap</c> gives — after the font is composed, and taken when shown.
    /// </summary>
    [Fact]
    public void AGlyphDirectory_IsReadByTheIndexTheCidMapGives()
    {
        string program = Font("/GlyphDirectory 4 dict def /Metrics 4 dict def\n", sfnts: Type42FontTests.Hex(Type42FontTests.WithoutGlyphs()))
            + "/ProbeCID /CIDFont findresource dup /GlyphDirectory get 1 " + Type42FontTests.Hex(Type42FontTests.GlyphBytes(1)) + " put "
            + "/Metrics get 3 [0 0.61] put\n"
            + "save 0 0 moveto <0003> show" + MarkPen + " restore";
        var r = Convert(program);
        Assert.Equal(new[] { 61.0, 0 }, LastMove(r));
        byte[] fontFile = ((PdfStream)((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile2]).RawData;
        byte[] taken = Type42FontTests.AssembledGlyph(fontFile, 1);
        Assert.Equal(Type42FontTests.GlyphBytes(1), taken.Take(Type42FontTests.GlyphBytes(1).Length).ToArray());
    }

    /// <summary>
    /// A driver's CIDFont whose <c>/GlyphDirectory</c> strings carry <paramref name="prefix"/> before
    /// glyph 1's description (TN 5012 §5.7), under <c>/MetricsCount</c> <paramref name="metricsCount"/>,
    /// then <paramref name="after"/>.
    /// </summary>
    private static string PrefixedFont(int metricsCount, byte[] prefix, string after, string extra = "")
        => Font($"/GlyphDirectory 4 dict def /MetricsCount {metricsCount} def\n" + extra, sfnts: Type42FontTests.Hex(Type42FontTests.WithoutGlyphs()))
           + "/ProbeCID /CIDFont findresource /GlyphDirectory get 1 "
           + Type42FontTests.Hex(prefix.Concat(Type42FontTests.GlyphBytes(1)).ToArray()) + " put\n" + after;

    /// <summary>The advance and side bearing of <paramref name="glyph"/>'s <c>hmtx</c> record in an assembled program.</summary>
    private static (int Advance, short SideBearing) HmtxRecord(byte[] program, int glyph)
    {
        Assert.True(Core.Fonts.SfntTableDirectory.TryRead(program, out var records, out _));
        var hmtx = records.Single(t => t.Tag == "hmtx");
        return (Core.IO.BigEndian.U16(program, hmtx.Offset + glyph * 4), (short)Core.IO.BigEndian.U16(program, hmtx.Offset + glyph * 4 + 2));
    }

    private static byte[] FontFile(PostScriptConversionResult r)
        => ((PdfStream)((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile2]).RawData;

    /// <summary>
    /// <c>/MetricsCount 2</c>: the string's first four bytes are the horizontal advance and left side
    /// bearing — 640 and 7 — and the glyph description follows them. The pen moves 64 at 100, the
    /// program's glyph is the description alone, and its <c>hmtx</c> record the prefix's pair.
    /// </summary>
    [Fact]
    public void MetricsCountTwo_IsTheHorizontalPairBeforeTheGlyph()
    {
        var r = Convert(PrefixedFont(2, new byte[] { 0x02, 0x80, 0x00, 0x07 }, "0 0 moveto <0003> show" + MarkPen));
        Assert.Equal(new[] { 64.0, 0 }, LastMove(r));
        Assert.Equal(640.0, ((PdfNumber)((PdfArray)((PdfArray)Descendant(r)[PdfName.W])[1])[0]).DoubleValue);
        byte[] glyph = Type42FontTests.GlyphBytes(1);
        Assert.Equal(glyph, Type42FontTests.AssembledGlyph(FontFile(r), 1).Take(glyph.Length).ToArray());
        Assert.Equal((640, (short)7), HmtxRecord(FontFile(r), 1));
    }

    /// <summary>
    /// <c>/MetricsCount 4</c>: eight bytes, the vertical advance and top side bearing (1000, 50) first,
    /// then the horizontal pair (650, −3) — the pen moves 65, the glyph the description after all eight.
    /// </summary>
    [Fact]
    public void MetricsCountFour_IsTheVerticalPairThenTheHorizontal()
    {
        var r = Convert(PrefixedFont(4, new byte[] { 0x03, 0xE8, 0x00, 0x32, 0x02, 0x8A, 0xFF, 0xFD }, "0 0 moveto <0003> show" + MarkPen));
        Assert.Equal(new[] { 65.0, 0 }, LastMove(r));
        byte[] glyph = Type42FontTests.GlyphBytes(1);
        Assert.Equal(glyph, Type42FontTests.AssembledGlyph(FontFile(r), 1).Take(glyph.Length).ToArray());
        Assert.Equal((650, (short)-3), HmtxRecord(FontFile(r), 1));
    }

    /// <summary>
    /// The prefix's advance is in the program's units: under a <c>head.unitsPerEm</c> of 2000, 640 is
    /// 320 of the 1000-unit glyph space, 32 at 100.
    /// </summary>
    [Fact]
    public void TheMetricsCountPrefix_IsInTheProgramsUnits()
    {
        byte[] sfnt = Type42FontTests.WithoutGlyphs();
        Assert.True(Core.Fonts.SfntTableDirectory.TryRead(sfnt, out var records, out _));
        Core.IO.BigEndian.Put16(sfnt, records.Single(t => t.Tag == "head").Offset + 18, 2000);

        string program = Font("/GlyphDirectory 4 dict def /MetricsCount 2 def\n", sfnts: Type42FontTests.Hex(sfnt))
            + "/ProbeCID /CIDFont findresource /GlyphDirectory get 1 "
            + Type42FontTests.Hex(new byte[] { 0x02, 0x80, 0x00, 0x07 }.Concat(Type42FontTests.GlyphBytes(1)).ToArray()) + " put\n"
            + "0 0 moveto <0003> show" + MarkPen;
        Assert.Equal(new[] { 32.0, 0 }, LastMove(Convert(program)));
    }

    /// <summary>A <c>/Metrics</c> entry still comes first: half an em over the prefix's 640.</summary>
    [Fact]
    public void Metrics_OverrideTheMetricsCountPrefix()
    {
        var r = Convert(PrefixedFont(2, new byte[] { 0x02, 0x80, 0x00, 0x07 }, "0 0 moveto <0003> show" + MarkPen,
                                     "/Metrics 2 dict dup 3 [0 0.5] put def\n"));
        Assert.Equal(new[] { 50.0, 0 }, LastMove(r));
    }

    /// <summary>
    /// A <c>/MetricsCount</c> outside 0, 2 and 4 is refused by name, the composite font left on the
    /// predefined-CMap path; 2 is carried (the control).
    /// </summary>
    [Fact]
    public void AMetricsCountOutsideTheNotes_IsRefusedByName()
    {
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(PrefixedFont(3, new byte[] { 0, 0, 0, 0, 0, 0 }, "0 0 moveto <0003> show"));
        Assert.True(diagnostics.Any("/MetricsCount is not 0, 2 or 4"), string.Join(" | ", diagnostics.Messages));
        Assert.Equal("CIDFontType0", ((PdfName)Descendant(r)[PdfName.Subtype]).Value);
        Assert.Equal("CIDFontType2", ((PdfName)Descendant(Convert(PrefixedFont(2, new byte[4], "0 0 moveto <0003> show")))[PdfName.Subtype]).Value);
    }

    /// <summary>
    /// A string shorter than its prefix is not taken, said once — the glyph left empty — where a string
    /// of exactly the prefix is a glyph with no description.
    /// </summary>
    [Fact]
    public void AStringShorterThanItsPrefix_IsNotTaken_HostileInput()
    {
        using var diagnostics = DiagnosticCollector.Start();
        string program = Font("/GlyphDirectory 4 dict def /MetricsCount 4 def\n", sfnts: Type42FontTests.Hex(Type42FontTests.WithoutGlyphs()))
                         + "/ProbeCID /CIDFont findresource /GlyphDirectory get 1 <0102030405> put\n0 0 moveto <0003> show";
        var r = Convert(program);
        Assert.True(diagnostics.Any("short of the 8 its /MetricsCount puts before the glyph"), string.Join(" | ", diagnostics.Messages));
        Assert.Empty(Type42FontTests.AssembledGlyph(FontFile(r), 1));
    }

    /// <summary>
    /// A CIDFont this converter cannot carry — a <c>/GDBytes</c> outside 1–4 — is said, and the
    /// composite font keeps the predefined-CMap path (a <c>CIDFontType0</c> naming the face, no
    /// program); the same font with <c>/GDBytes 2</c> is carried (the control).
    /// </summary>
    [Fact]
    public void ABadGdBytes_IsRefusedByName()
    {
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Font().Replace("/GDBytes 2 def", "/GDBytes 9 def") + "0 0 moveto <0003> show");
        Assert.True(diagnostics.Any("/GDBytes"), string.Join(" | ", diagnostics.Messages));
        Assert.Equal("CIDFontType0", ((PdfName)Descendant(r)[PdfName.Subtype]).Value);
        Assert.Equal("CIDFontType2", ((PdfName)Descendant(Convert(Font() + "0 0 moveto <0003> show"))[PdfName.Subtype]).Value);
    }

    /// <summary><c>defineresource</c> makes a Type 2 CIDFont's <c>/FontType</c> 11, as PLRM Table 5.12 says.</summary>
    [Fact]
    public void Defineresource_MakesTheFontType11()
    {
        var r = Convert(Font() + "/ProbeCID /CIDFont findresource /FontType get 11 eq { 0 0 1 1 rectfill } if");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }
}
