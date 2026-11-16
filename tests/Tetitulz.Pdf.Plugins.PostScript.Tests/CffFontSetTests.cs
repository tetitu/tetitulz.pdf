// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// CFF font sets (PLRM §5.8.1): <c>/FontSetInit /ProcSet findresource begin /Key n StartData</c> and
/// <c>n</c> bytes of CFF — how Adobe's applications send an OpenType-CFF face. The set's font is a Font
/// resource <c>findfont</c> reaches by its Name INDEX name, embedded as a <c>/FontFile3</c> of
/// <c>/Type1C</c> with its own widths. Before, <c>findresource</c> of the procedure set failed, and the
/// binary data was scanned as PostScript.
/// </summary>
public sealed class CffFontSetTests
{
    /// <summary>'A' at 65, 600 wide; 'B' at 200 — not StandardEncoding's — 500; .notdef 250 — the font named TestCffOtf.</summary>
    private static readonly byte[] Set = MinimalOpenTypeCff.BuildBareCff(new List<MinimalOpenTypeCff.Glyph>
    {
        new() { Name = ".notdef", Charstring = MinimalOpenTypeCff.BoxGlyph(advance: 250) },
        new() { Name = "A", Charstring = MinimalOpenTypeCff.BoxGlyph(advance: 600), Code = 65 },
        new() { Name = "B", Charstring = MinimalOpenTypeCff.BoxGlyph(advance: 500, variant: 1), Code = 200 },
    });

    private static byte[] Program(byte[] data, string after, string startData = null)
    {
        var all = new List<byte>();
        all.AddRange(Encoding.ASCII.GetBytes("/FontSetInit /ProcSet findresource begin\n" +
                                             (startData ?? $"/TestSet {data.Length} StartData") + "\n"));
        all.AddRange(data);
        all.AddRange(Encoding.ASCII.GetBytes("\n" + after));
        return all.ToArray();
    }

    private const string Show = "/TestCffOtf findfont 100 scalefont setfont 0 0 moveto (A\\310) show currentpoint newpath moveto 0 0 lineto stroke";

    private static PostScriptConversionResult Convert(byte[] program) => PostScriptConversion.ConvertFragment(program);

    private static PdfDictionary FontNamed(PostScriptConversionResult r, string baseFont)
        => ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
               .Single(f => f[PdfName.BaseFont] is PdfName n && n.Value == baseFont);

    /// <summary>
    /// The set's font draws in its own program — a <c>/FontFile3</c> of <c>/Type1C</c>, the set's
    /// bytes — through its built-in encoding, with its own widths: the pen 110 on at 100.
    /// </summary>
    [Fact]
    public void AFontSet_DrawsInItsOwnProgram()
    {
        var r = Convert(Program(Set, Show));
        var font = FontNamed(r, "TestCffOtf");
        var descriptor = (PdfDictionary)font[PdfName.FontDescriptor];
        var program = Assert.IsType<PdfStream>(descriptor[PdfName.FontFile3]);
        Assert.Equal("Type1C", ((PdfName)program[PdfName.Subtype]).Value);
        Assert.Equal(Set, program.RawData);
        Assert.Equal(600.0, ((PdfNumber)((PdfArray)font[PdfName.Widths])[65]).DoubleValue);
        Assert.Equal(500.0, ((PdfNumber)((PdfArray)font[PdfName.Widths])[200]).DoubleValue);   // the built-in encoding's, not StandardEncoding's
        // The CFF parser's font-matrix scale is a float: 0.001f leaves 5e-6 at size 100.
        Assert.Equal(110.0, ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands
                                .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).First(), 3);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary><c>StartData</c> defines the set under its key in the FontSet category, and ends the procedure set's dictionary.</summary>
    [Fact]
    public void StartData_DefinesTheFontSet_AndEndsTheProcedureSet()
    {
        var r = Convert(Program(Set, "/TestSet /FontSet resourcestatus { pop pop countdictstack 3 eq { 0 0 1 1 rectfill } if } if"));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>
    /// Data the CFF parser refuses is consumed all the same — the program resumes after it, nothing of
    /// it scanned as names — and said; the font then draws in the face its name maps to.
    /// </summary>
    [Fact]
    public void RefusedData_IsConsumedAndSaid()
    {
        byte[] junk = Encoding.ASCII.GetBytes("not a font: showpage moveto stroke ( [ { <<");
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Program(junk, "0 0 1 1 rectfill"));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.True(diagnostics.Any("font set was not carried"), string.Join(" | ", diagnostics.Messages));
        Assert.DoesNotContain(r.UnimplementedOperators, name => name.Contains("not") || name.Contains("font:"));
    }

    /// <summary>
    /// <c>stringwidth</c> answers the font's widths under the CMap, and <c>xshow</c> places each code
    /// — its own bytes, in the same font — at the displacement given.
    /// </summary>
    [Fact]
    public void ACidKeyedSet_MeasuresAndPlacesByItsCodes()
    {
        byte[] cid = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 });
        const string Compose = "/Composed /Identity-H [/TestCid] composefont pop /Composed findfont 100 scalefont setfont ";

        var width = Convert(Program(cid, Compose + "<000A0014> stringwidth pop 0 moveto 0 0 lineto stroke"));
        Assert.Equal(100.0, ContentStreamAssert.FirstOperandsOf(width.Content, "m")[0], 3);

        var placed = Convert(Program(cid, Compose + "0 0 moveto <000A0014> [70 0] xshow"));
        var shows = ContentStreamAssert.Parse(placed.Content).Where(o => o.Operator is "Tj" or "Tm").Select(o => o.ToString()).ToArray();
        Assert.Equal(new[] { @"(\000\n) Tj", @"(\000\024) Tj" }, shows.Where(s => s.EndsWith(" Tj", System.StringComparison.Ordinal)));
        Assert.EndsWith("70 0 Tm", shows.Where(s => s.EndsWith(" Tm", System.StringComparison.Ordinal)).Last(), System.StringComparison.Ordinal);

        // One displacement short: the last code advances by its own width, 50 at 100 — where an em a
        // code made it 100.
        var shortArray = Convert(Program(cid, Compose + "0 0 moveto <000A0014> [70] xshow currentpoint newpath moveto 0 0 lineto stroke"));
        Assert.Equal(120.0, ContentStreamAssert.Parse(shortArray.Content).Last(o => o.Operator == "m").Operands
                                .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).First(), 3);
    }

    private static readonly byte[] TwoFonts =
        MinimalOpenTypeCff.BuildSet(new[] { "SetRoman", "SetBold" }, new[] { 600, 700 }, new[] { 400, 300 });

    /// <summary>
    /// Every font of a set holding several is a Font <c>findfont</c> reaches, each embedded as a CFF
    /// holding it alone (ISO 32000-2 §9.9): its Name INDEX one name, its <c>/Widths</c> and the pen its
    /// own — 600 and 700 at code 65, 60 and 70 at size 100 — and the FontSet instance names both.
    /// </summary>
    [Fact]
    public void EveryFontOfASet_DrawsInAProgramOfItsOwn()
    {
        const string Draw = "/SetRoman findfont 100 scalefont setfont 0 0 moveto (A) show " +
                            "/SetBold findfont 100 scalefont setfont 0 100 moveto (A) show currentpoint newpath moveto 0 0 lineto stroke";
        var r = Convert(Program(TwoFonts, Draw));
        Assert.True(r.IsClean, r.LastError);

        foreach ((string name, double width) in new[] { ("SetRoman", 600.0), ("SetBold", 700.0) })
        {
            var font = FontNamed(r, name);
            Assert.Equal(width, ((PdfNumber)((PdfArray)font[PdfName.Widths])[65]).DoubleValue, 3);
            var program = (PdfStream)((PdfDictionary)font[PdfName.FontDescriptor])[PdfName.FontFile3];
            Assert.Equal("Type1C", ((PdfName)program[PdfName.Subtype]).Value);
            Assert.Equal(new[] { name }, Core.Fonts.CffFontSet.TryReadFontNames(program.RawData, out _));
        }
        Assert.Equal(new[] { 70.0, 100 }, ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands
                                             .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).Select(v => System.Math.Round(v, 3)));

        var named = Convert(Program(TwoFonts, "/TestSet /FontSet findresource /FontNames get length 2 eq { 0 0 1 1 rectfill } if"));
        Assert.Contains("f", ContentStreamAssert.Operators(named.Content));
    }

    /// <summary>
    /// A synthetic font of a set — the first — is refused by name and its text drawn in the face its
    /// name maps to; the set's font after it is carried.
    /// </summary>
    [Fact]
    public void ASyntheticFontOfASet_IsRefusedByName_TheOthersCarried()
    {
        byte[] set = MinimalOpenTypeCff.BuildSet(new[] { "SetOblique", "SetRoman" }, new[] { 600, 600 }, new[] { 400, 400 }, syntheticFont: 0);
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Program(set, "/SetRoman findfont 100 scalefont setfont 0 0 moveto (A) show"));
        Assert.True(diagnostics.Any("SetOblique, was not carried"), string.Join(" | ", diagnostics.Messages));
        Assert.True(diagnostics.Any("synthetic font"), string.Join(" | ", diagnostics.Messages));
        Assert.NotNull(((PdfDictionary)FontNamed(r, "SetRoman")[PdfName.FontDescriptor])[PdfName.FontFile3]);
    }

    /// <summary>
    /// A deleted Name INDEX entry (its first byte 0) is no font: passed over without a word, the font
    /// after it carried.
    /// </summary>
    [Fact]
    public void ADeletedEntryOfASet_IsPassedOver()
    {
        byte[] set = MinimalOpenTypeCff.BuildSet(new[] { "SetGone", "SetRoman" }, new[] { 600, 700 }, new[] { 400, 300 }, deletedName: 0);
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Program(set, "/SetRoman findfont 100 scalefont setfont 0 0 moveto (A) show"));
        Assert.False(diagnostics.Any("was not carried"), string.Join(" | ", diagnostics.Messages));
        Assert.Equal(700.0, ((PdfNumber)((PdfArray)FontNamed(r, "SetRoman")[PdfName.Widths])[65]).DoubleValue, 3);
    }

    /// <summary>
    /// Each split copies the set, and each copy is charged: two hundred fonts in a set of some ten
    /// kilobytes spend a megabyte budget, where the default budget carries them all.
    /// </summary>
    [Fact]
    public void ManyFontsInASet_SpendTheBudget_HostileInput()
    {
        const int Fonts = 200;
        byte[] set = MinimalOpenTypeCff.BuildSet(Enumerable.Range(0, Fonts).Select(k => "F" + k).ToArray(),
                                                 Enumerable.Repeat(600, Fonts).ToArray(), Enumerable.Repeat(400, Fonts).ToArray());
        Assert.InRange(set.Length * Fonts, 1_000_001, 10_000_000);

        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };
        Assert.True(PostScriptConversion.ConvertFragment(Program(set, "/F199 findfont pop"), options).BudgetExhausted);
        var carried = Convert(Program(set, "/F199 findfont 100 scalefont setfont 0 0 moveto (A) show"));
        Assert.False(carried.BudgetExhausted);
        Assert.NotNull(((PdfDictionary)FontNamed(carried, "F199")[PdfName.FontDescriptor])[PdfName.FontFile3]);
    }

    /// <summary>A Chameleon set (<c>key int name StartData</c>) is consumed and named as unimplemented.</summary>
    [Fact]
    public void AChameleonSet_IsConsumedAndNamed()
    {
        var r = Convert(Program(Set, "0 0 1 1 rectfill", $"/TestSet {Set.Length} /Master StartData"));
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        Assert.Contains(r.UnimplementedOperators, name => name.Contains("Chameleon"));
    }

    /// <summary>
    /// A CID-keyed font set's font is a CIDFont resource under its name, for <c>composefont</c>: the
    /// shown bytes pass through unchanged under the CMap, in a Type0 font over a <c>CIDFontType0</c>
    /// whose program is the set — a <c>/FontFile3</c> of <c>/CIDFontType0C</c>, its
    /// <c>/CIDSystemInfo</c> the font's ROS, its <c>/W</c> the font's widths for the CIDs shown — and
    /// the pen moves by those widths: CIDs 10 and 20, 500 each, 100 at size 100. A CID the font lacks is
    /// its .notdef, which advances nothing.
    /// </summary>
    [Fact]
    public void ACidKeyedSet_IsACidFont_ForComposefont()
    {
        byte[] cid = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 });
        var r = Convert(Program(cid, "/Composed /Identity-H [/TestCid] composefont pop /Composed findfont 100 scalefont setfont " +
                                     "0 0 moveto <000A00140063> show currentpoint newpath moveto 0 0 lineto stroke"));

        Assert.Equal(new[] { @"(\000\n\000\024\000c)" },
                     ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]).ToArray());
        var type0 = ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
                        .Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });
        Assert.Equal("Identity-H", ((PdfName)type0[PdfName.Encoding]).Value);
        var descendant = (PdfDictionary)((PdfArray)type0[PdfName.DescendantFonts])[0];
        Assert.Equal("CIDFontType0", ((PdfName)descendant[PdfName.Subtype]).Value);
        var program = (PdfStream)((PdfDictionary)descendant[PdfName.FontDescriptor])[PdfName.FontFile3];
        Assert.Equal("CIDFontType0C", ((PdfName)program[PdfName.Subtype]).Value);
        Assert.Equal(cid, program.RawData);
        var info = (PdfDictionary)descendant[PdfName.CIDSystemInfo];
        Assert.Equal("Identity", ((PdfString)info[PdfName.Ordering]).GetText());
        var widths = (PdfArray)descendant[PdfName.W];
        Assert.Equal(new[] { 10.0, 500, 20, 500, 99, 0 },
                     Enumerable.Range(0, widths.Count).Select(k => widths[k] is PdfArray one ? ((PdfNumber)one[0]).DoubleValue : ((PdfNumber)widths[k]).DoubleValue));
        Assert.Equal(100.0, ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands
                                .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).First(), 3);
    }

    /// <summary>A byte count past the program's end is said, and nothing is carried.</summary>
    [Fact]
    public void AShortSet_IsSaid()
    {
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Program(Set.Take(20).ToArray(), "", $"/TestSet {Set.Length + 100} StartData"));
        Assert.True(diagnostics.Any("bytes short"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>A count past the allocation budget is refused before anything is read: the budget is spent, not memory.</summary>
    [Fact]
    public void AHugeCount_SpendsTheBudget_HostileInput()
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };
        var r = PostScriptConversion.ConvertFragment(Program(Set, "", "/TestSet 2000000000 StartData"), options);
        Assert.True(r.BudgetExhausted);
    }

    /// <summary>The points each <c>l</c> of the content draws to.</summary>
    private static (double X, double Y)[] LinePoints(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "l")
               .Select(o => (double.Parse(o.Operands[0], System.Globalization.CultureInfo.InvariantCulture),
                             double.Parse(o.Operands[1], System.Globalization.CultureInfo.InvariantCulture)))
               .Select(p => (System.Math.Round(p.Item1, 3), System.Math.Round(p.Item2, 3)))
               .ToArray();

    /// <summary>
    /// <c>charpath</c> in a set's font is each glyph's own outline — the charstring run by Core's Type 2
    /// evaluator — at the pen through the font matrix: 'A' and the built-in encoding's 'B' at 200, each
    /// a 400-unit square, 40 at size 100, the second 60 on, as 'A' is 600 wide. A metric box would run
    /// from the descender to the ascender.
    /// </summary>
    [Fact]
    public void Charpath_InASetsFont_IsItsGlyphsOutlines()
    {
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(Program(Set, "/TestCffOtf findfont 100 scalefont setfont 10 20 moveto (A\\310) false charpath fill"));

        Assert.Equal(new (double, double)[] { (10, 60), (50, 60), (50, 20), (70, 60), (110, 60), (110, 20) }, LinePoints(r));
        Assert.False(diagnostics.Any("metric box"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>
    /// A re-encoding naming a glyph the set lacks draws its <c>.notdef</c> — a 400-unit square 250
    /// wide — as <c>show</c> does, not the metric box.
    /// </summary>
    [Fact]
    public void Charpath_OfAGlyphTheSetLacks_IsItsNotdef()
    {
        var r = Convert(Program(Set,
            "/TestCffOtf findfont dup length dict begin { 1 index /FID ne { def } { pop pop } ifelse } forall " +
            "/Encoding 256 array 0 1 255 { 1 index exch /missing put } for def currentdict end /Re exch definefont pop " +
            "/Re findfont 100 scalefont setfont 0 0 moveto (A) false charpath fill"));

        Assert.Equal(new (double, double)[] { (0, 40), (40, 40), (40, 0) }, LinePoints(r));
    }

    /// <summary>
    /// <c>charpath</c> in a CID-keyed set's font under its CMap: CIDs 10 and 20 are 400-unit squares
    /// 500 wide, at 0 and 50; CID 99, which the font lacks, is CID 0's glyph, which draws nothing.
    /// </summary>
    [Fact]
    public void Charpath_InACidKeyedSetsFont_IsItsGlyphsOutlines()
    {
        byte[] cid = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 });
        var r = Convert(Program(cid, "/Composed /Identity-H [/TestCid] composefont pop /Composed findfont 100 scalefont setfont " +
                                     "0 0 moveto <000A00140063> false charpath fill"));

        Assert.Equal(new (double, double)[] { (0, 40), (40, 40), (40, 0), (50, 40), (90, 40), (90, 0) }, LinePoints(r));
    }
}
