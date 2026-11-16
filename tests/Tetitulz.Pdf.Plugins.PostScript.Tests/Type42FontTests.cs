// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Type 42 fonts (Adobe TN 5012): a TrueType program in a font dictionary's <c>/sfnts</c>, its glyphs
/// named through <c>/CharStrings</c>. Each is carried as a Type0 font over a <c>CIDFontType2</c> whose
/// CIDs are glyph indices, its program the <c>/FontFile2</c>; a driver's incrementally downloaded
/// glyphs (<c>/GlyphDirectory</c>, <c>/Metrics</c>) are taken as they are used. Before, every Type 42
/// font drew in the standard face its name mapped to, measured by that face's metrics.
/// </summary>
public sealed class Type42FontTests
{
    /// <summary>'A' is glyph 1, 610 wide; 'B' glyph 2, 620; .notdef 100 — units per em 1000.</summary>
    internal static readonly byte[] Program = MinimalTrueType.BuildWithDistinctWidths("AB");

    internal static string Hex(byte[] bytes) => "<" + System.Convert.ToHexString(bytes) + ">";

    private const string Head =
        "/FontType 42 def /FontMatrix [1 0 0 1 0 0] def /PaintType 0 def /FontBBox [0 0 1 1] def\n";

    /// <summary>A whole-program font, /ProbeT42, set at 100 — <paramref name="sfnts"/> the array's contents.</summary>
    internal static string Font(string sfnts, string extra = "")
        => "12 dict begin /FontName /ProbeT42 def\n" + Head +
           "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for dup 65 /A put dup 66 /B put dup 32 /space put def\n" +
           "/CharStrings 4 dict dup begin /.notdef 0 def /A 1 def /B 2 def end def\n" + extra +
           "/sfnts [" + sfnts + "] def\n" +
           "currentdict end /ProbeT42 exch definefont pop /ProbeT42 findfont 100 scalefont setfont\n";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private const string MarkPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static double[] LastMove(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands
               .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static PdfDictionary Type0(PostScriptConversionResult r)
    {
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        return fonts.Select(kv => kv.Value).OfType<PdfDictionary>().Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });
    }

    private static PdfDictionary Descendant(PostScriptConversionResult r)
        => (PdfDictionary)((PdfArray)Type0(r)[PdfName.DescendantFonts])[0];

    private static byte[] FontFile2(PostScriptConversionResult r)
        => ((PdfStream)((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile2]).RawData;

    private static string[] ShownStrings(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]).ToArray();

    /// <summary>
    /// The text is the program's glyphs by index through a Type0 font under <c>/Identity-H</c>, its
    /// descendant a <c>CIDFontType2</c> with <c>/CIDToGIDMap /Identity</c>, its <c>/W</c> the program's
    /// <c>hmtx</c> advances — and the pen moved by them, 610 + 620 at 100.
    /// </summary>
    [Fact]
    public void Show_IsTheProgramsGlyphs_ThroughAType0Font()
    {
        var r = Convert(Font(Hex(Program)) + "0 0 moveto (AB) show" + MarkPen);

        Assert.Equal("Identity-H", ((PdfName)Type0(r)[PdfName.Encoding]).Value);
        var descendant = Descendant(r);
        Assert.Equal("CIDFontType2", ((PdfName)descendant[PdfName.Subtype]).Value);
        Assert.Equal("Identity", ((PdfName)descendant[PdfName.CIDToGIDMap]).Value);
        Assert.Equal(new[] { @"(\000\001\000\002)" }, ShownStrings(r));

        var widths = (PdfArray)descendant[PdfName.W];
        Assert.Equal(new[] { 1.0, 610, 2, 620 },
                     new[] { ((PdfNumber)widths[0]).DoubleValue, ((PdfNumber)((PdfArray)widths[1])[0]).DoubleValue,
                             ((PdfNumber)widths[2]).DoubleValue, ((PdfNumber)((PdfArray)widths[3])[0]).DoubleValue });

        Assert.Equal(new[] { 123.0, 0 }, LastMove(r));
        Assert.True(TrueTypeFontProgram.Parse(FontFile2(r)) is { GlyphCount: 3, UnitsPerEm: 1000 });
    }

    /// <summary>The text is recoverable: <c>/ToUnicode</c> maps each glyph index to the character its glyph name stands for.</summary>
    [Fact]
    public void ToUnicode_MapsEachGlyphToItsName()
    {
        var r = Convert(Font(Hex(Program)) + "0 0 moveto (BA) show");
        string cmap = Encoding.ASCII.GetString(((PdfStream)Type0(r)[PdfName.ToUnicode]).RawData);
        Assert.Contains("<0001> <0041>", cmap, System.StringComparison.Ordinal);
        Assert.Contains("<0002> <0042>", cmap, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// An odd-length <c>/sfnts</c> string's last byte is padding (TN 5012 §4.2): split after the
    /// table directory, each half padded with a zero, the program reads as the whole one — a kept pad
    /// would move every table by a byte.
    /// </summary>
    [Fact]
    public void OddLengthStrings_LoseTheirPaddingByte()
    {
        int directory = 12 + 16 * ((Program[4] << 8) | Program[5]);
        byte[] first = Program.Take(directory).Append((byte)0).ToArray();
        byte[] second = Program.Skip(directory).Append((byte)0).ToArray();
        Assert.True(first.Length % 2 == 1 && second.Length % 2 == 1);

        var r = Convert(Font(Hex(first) + " " + Hex(second)) + "0 0 moveto (AB) show" + MarkPen);
        Assert.Equal(new[] { 123.0, 0 }, LastMove(r));
        Assert.Null(r.LastError);
    }

    /// <summary><c>stringwidth</c> answers the program's advances, and <c>glyphshow</c> reaches a glyph by name whatever the encoding holds.</summary>
    [Fact]
    public void StringwidthAndGlyphshow_UseTheProgramsGlyphs()
    {
        var width = Convert(Font(Hex(Program)) + "(AB) stringwidth pop 0 moveto 0 0 lineto stroke");
        Assert.Equal(123.0, ContentStreamAssert.FirstOperandsOf(width.Content, "m")[0]);

        var shown = Convert(Font(Hex(Program)).Replace("dup 66 /B put ", "") + "0 0 moveto /B glyphshow" + MarkPen);
        Assert.Equal(new[] { @"(\000\002)" }, ShownStrings(shown));
        Assert.Equal(new[] { 62.0, 0 }, LastMove(shown));
    }

    /// <summary>
    /// <c>Tw</c> spaces a one-byte code 32 alone, so word spacing splits the run after the space and
    /// the rest goes where its own pen is — in the same text object, the 50 of spacing at size 100
    /// written as the <c>TJ</c> number −500 between the pieces: 'A' (61), the space through .notdef
    /// (10), and 50 more.
    /// </summary>
    [Fact]
    public void WordSpacing_MovesTheTextAfterEachSpace()
    {
        var r = Convert(Font(Hex(Program)) + "0 0 moveto 50 0 32 (A B) widthshow" + MarkPen);
        Assert.Empty(ShownStrings(r));
        var shown = ContentStreamAssert.Parse(r.Content).Single(o => o.Operator == "TJ").Operands;
        Assert.Equal(new[] { @"[(\000\001\000\000) -500 (\000\002)]" }, shown);
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "BT"));
        var tm = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tm").Last().Operands;
        Assert.Equal("0", tm[4]);
        Assert.Equal(new[] { 183.0, 0 }, LastMove(r));

        var unsplit = Convert(Font(Hex(Program)) + "0 0 moveto (A B) show");
        Assert.Single(ShownStrings(unsplit));
    }

    /// <summary><c>charpath</c> appends the program's own outline: the fixture's glyph is one point at (100, 100), 10 at size 100.</summary>
    [Fact]
    public void Charpath_AppendsTheProgramsOutline()
    {
        var r = Convert(Font(Hex(Program)) + "0 0 moveto (A) true charpath fill");
        ContentStreamAssert.Contains(r.Content, "10 10 m");
        Assert.DoesNotContain("metric box", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    // ==================================================================
    //  Incrementally downloaded glyphs
    // ==================================================================

    private static readonly HashSet<string> Dropped = new() { "glyf", "loca", "hmtx" };

    /// <summary>The program with its glyf, loca and hmtx left out — the driver's /sfnts.</summary>
    internal static byte[] WithoutGlyphs()
    {
        Assert.True(SfntTableDirectory.TryRead(Program, out var records, out _));
        var tables = records.Where(t => !Dropped.Contains(t.Tag)).Select(t => (t.Tag, SfntTableDirectory.Payload(Program, t))).ToList();
        return SfntBuilder.Build(SfntBuilder.VersionTrueType, tables);
    }

    /// <summary>Glyph <paramref name="index"/>'s glyf bytes from the fixture (short loca).</summary>
    internal static byte[] GlyphBytes(int index)
    {
        Assert.True(SfntTableDirectory.TryRead(Program, out var records, out _));
        var loca = records.Single(t => t.Tag == "loca");
        var glyf = records.Single(t => t.Tag == "glyf");
        int from = ((Program[loca.Offset + index * 2] << 8) | Program[loca.Offset + index * 2 + 1]) * 2;
        int to = ((Program[loca.Offset + index * 2 + 2] << 8) | Program[loca.Offset + index * 2 + 3]) * 2;
        return Program.Skip(glyf.Offset + from).Take(to - from).ToArray();
    }

    /// <summary>A driver's font: no glyphs yet, an empty encoding, set at 100 before anything is downloaded.</summary>
    private static string DriverFont(string extra = "")
        => "12 dict begin /FontName /DriverT42 def\n" + Head +
           "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for def\n" +
           "/CharStrings 4 dict dup begin /.notdef 0 def end def\n" +
           "/GlyphDirectory 4 dict def /Metrics 4 dict def\n" + extra +
           "/sfnts [" + Hex(WithoutGlyphs()) + "] def\n" +
           "currentdict end dup /FontName get exch definefont pop /DriverT42 findfont 100 scalefont setfont\n";

    /// <summary>Wine's download of 'A' as glyph 1, 0.61 em wide, at code 65 — after setfont.</summary>
    private static string DownloadA()
        => "/DriverT42 findfont dup /GlyphDirectory get 1 " + Hex(GlyphBytes(1)) + " put " +
           "dup /CharStrings get /A 1 put dup /Metrics get /A [0 0.61] put /Encoding get 65 /A put\n";

    /// <summary>The glyph data of <paramref name="index"/> in an assembled program (long loca).</summary>
    internal static byte[] AssembledGlyph(byte[] program, int index)
    {
        Assert.True(SfntTableDirectory.TryRead(program, out var records, out _));
        var head = records.Single(t => t.Tag == "head");
        Assert.Equal(1, (program[head.Offset + 50] << 8) | program[head.Offset + 51]);
        var loca = records.Single(t => t.Tag == "loca");
        var glyf = records.Single(t => t.Tag == "glyf");
        long from = Core.IO.BigEndian.U32(program, loca.Offset + index * 4);
        long to = Core.IO.BigEndian.U32(program, loca.Offset + index * 4 + 4);
        return program.Skip(glyf.Offset + (int)from).Take((int)(to - from)).ToArray();
    }

    /// <summary>
    /// A glyph downloaded inside a <c>save</c> and shown there is in the program the reader gets even
    /// though <c>restore</c> rolled the directory back — taken when it was used — with the width
    /// its <c>/Metrics</c> gave; and the encoding entry written after <c>setfont</c> selects it.
    /// </summary>
    [Fact]
    public void GlyphDirectory_GlyphsAreTakenAsUsed_AndSurviveRestore()
    {
        var r = Convert(DriverFont() + "save " + DownloadA() + "0 0 moveto (A) show" + MarkPen + " restore");

        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(r));
        Assert.Equal(new[] { 61.0, 0 }, LastMove(r));
        byte[] taken = AssembledGlyph(FontFile2(r), 1);
        Assert.Equal(GlyphBytes(1), taken.Take(GlyphBytes(1).Length).ToArray());
        Assert.Empty(AssembledGlyph(FontFile2(r), 2));   // never downloaded, never used

        var widths = (PdfArray)Descendant(r)[PdfName.W];
        Assert.Equal(610.0, ((PdfNumber)((PdfArray)widths[1])[0]).DoubleValue);
    }

    /// <summary>A downloaded glyph's outline is the one <c>charpath</c> appends.</summary>
    [Fact]
    public void GlyphDirectory_Charpath_ReadsTheDownloadedGlyph()
    {
        var r = Convert(DriverFont() + DownloadA() + "0 0 moveto (A) true charpath fill");
        ContentStreamAssert.Contains(r.Content, "10 10 m");
    }

    /// <summary>
    /// A <c>/MetricsCount</c> prefix on a Type 42 font's directory strings is refused by name — TN 5012
    /// §5.7 defines it for a Type 2 CIDFont alone — the text drawn in the standard face its name maps
    /// to; the same font without it is carried (the control).
    /// </summary>
    [Fact]
    public void MetricsCount_OnAType42Font_IsRefusedByName()
    {
        var refused = Convert(DriverFont("/MetricsCount 2 def\n") + DownloadA() + "0 0 moveto (A) show");
        Assert.Contains("MetricsCount", refused.LastError ?? "", System.StringComparison.Ordinal);
        Assert.DoesNotContain(((PdfDictionary)refused.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>(),
                              f => f[PdfName.Subtype] is PdfName { Value: "Type0" });

        var carried = Convert(DriverFont() + DownloadA() + "0 0 moveto (A) show");
        Assert.Equal("CIDFontType2", ((PdfName)Descendant(carried)[PdfName.Subtype]).Value);
    }

    /// <summary>A directory whose tables lie outside the program is refused with Core's reason, not read.</summary>
    [Fact]
    public void AHostileTableDirectory_IsRefused()
    {
        byte[] broken = (byte[])Program.Clone();
        Core.IO.BigEndian.Put32(broken, 12 + 12, 0x7FFFFFF0);   // the first table's length
        var r = Convert(Font(Hex(broken)) + "0 0 moveto (A) show");
        Assert.Contains("table directory was refused", r.LastError ?? "", System.StringComparison.Ordinal);
        Assert.DoesNotContain(((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>(),
                              f => f[PdfName.Subtype] is PdfName { Value: "Type0" });
    }
}
