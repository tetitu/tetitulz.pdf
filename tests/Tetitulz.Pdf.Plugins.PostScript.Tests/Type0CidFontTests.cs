// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Type 0 CIDFonts (PLRM §5.11.3, Table 5.13): <c>/CIDInit /ProcSet findresource begin</c>, the
/// CIDFont dictionary, and <c>(Binary) n StartData</c> with the binary data — an offset table by CID
/// (an FD index, then a charstring offset; the next entry's offset ends each charstring), the Type 1
/// charstrings encrypted under the FD's <c>lenIV</c>, and the subroutines its <c>/SubrMapOffset</c>
/// locates. Each CID shown is run into its outline and carried in a CFF under a <c>CIDFontType0</c>,
/// its glyph index the code the text shows. Before, <c>/CIDInit /ProcSet findresource</c> failed and
/// the binary data was scanned as PostScript.
/// </summary>
public sealed class Type0CidFontTests
{
    private const byte Hsbw = 13, RMoveTo = 21, RLineTo = 5, ClosePath = 9, EndChar = 14, CallSubr = 10, Return = 11;

    private static byte[] Op(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (object p in parts)
        {
            if (p is int n) bytes.AddRange(MinimalType1.Num(n));
            else bytes.Add((byte)p);
        }
        return bytes.ToArray();
    }

    /// <summary>CID 1: 600 wide, a box from x = 100.</summary>
    private static readonly byte[] Box = Op(0, 600, Hsbw, 100, 0, RMoveTo, 400, 0, RLineTo, 0, 700, RLineTo, -400, 0, RLineTo, ClosePath, EndChar);

    /// <summary>CID 2: 500 wide, its outline in subroutine 0, which starts at x = 200.</summary>
    private static readonly byte[] CallsSubr = Op(0, 500, Hsbw, 0, CallSubr, EndChar);

    private static readonly byte[] Subr0 = Op(200, 0, RMoveTo, 100, 0, RLineTo, 0, 100, RLineTo, ClosePath, Return);

    /// <summary>
    /// The binary data: the offset table (FDBytes 1, GDBytes 2, CIDCount 4 — five entries), the
    /// charstrings of CIDs 0 to 2 (CID 3's entry equals the next: a missing glyph), and the
    /// subroutine table (SDBytes 2, one subroutine — two entries) with the subroutine; every charstring
    /// and the subroutine encrypted under the default lenIV. <paramref name="subrMapOffset"/> is where
    /// the subroutine table begins; <paramref name="fdOfCid"/>, when given, is each entry's FD index.
    /// </summary>
    private static byte[] Data(out int subrMapOffset, byte[] fdOfCid = null)
    {
        byte[][] charstrings =
        {
            MinimalType1.EncryptCharstring(MinimalType1.AdvanceOnly(250)),
            MinimalType1.EncryptCharstring(Box),
            MinimalType1.EncryptCharstring(CallsSubr),
        };
        byte[] subr = MinimalType1.EncryptCharstring(Subr0);

        const int Entries = 5, EntryBytes = 3;
        int at = Entries * EntryBytes;
        var table = new List<byte>();
        var body = new List<byte>();
        for (int cid = 0; cid < Entries; cid++)
        {
            table.Add(fdOfCid?[cid] ?? 0);
            table.Add((byte)(at >> 8));
            table.Add((byte)at);
            if (cid < charstrings.Length) { body.AddRange(charstrings[cid]); at += charstrings[cid].Length; }
        }
        subrMapOffset = at;
        int subrStart = at + 4;
        var subrs = new List<byte> { (byte)(subrStart >> 8), (byte)subrStart, (byte)((subrStart + subr.Length) >> 8), (byte)(subrStart + subr.Length) };
        subrs.AddRange(subr);
        return table.Concat(body).Concat(subrs).ToArray();
    }

    /// <summary>
    /// The CIDFont file, /ProbeT0, composed under Identity-H as /Composed and set at 100, then
    /// <paramref name="after"/>. <paramref name="fds"/> replaces the one FD; <paramref name="hex"/>
    /// writes the data as hexadecimal digits.
    /// </summary>
    private static byte[] Program(string after, string fds = null, bool hex = false, byte[] data = null, string extra = "")
    {
        data ??= Data(out int subrMapOffset);
        int map = data.Length > 15 ? DataOffset() : 0;
        string fd = $"3 dict dup begin /FontMatrix [0.001 0 0 0.001 0 0] def /Private 3 dict dup begin " +
                    $"/SubrMapOffset {map} def /SDBytes 2 def /SubrCount 1 def end def end";
        var all = new List<byte>();
        all.AddRange(Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\n20 dict begin\n" +
            "/CIDFontName /ProbeT0 def /CIDFontType 0 def\n" +
            "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Identity) def /Supplement 0 def end def\n" +
            "/FontBBox [0 -200 1000 900] def\n" +
            $"/FDArray [ {fds ?? fd} ] def\n" +
            "/CIDMapOffset 0 def /FDBytes 1 def /GDBytes 2 def /CIDCount 4 def\n" + extra +
            (hex ? $"(Hex) {data.Length} StartData\n" : $"(Binary) {data.Length} StartData\n")));
        all.AddRange(hex ? Encoding.ASCII.GetBytes(System.Convert.ToHexString(data)) : data);
        all.AddRange(Encoding.ASCII.GetBytes(
            "\n/Composed /Identity-H [/ProbeT0] composefont pop /Composed findfont 100 scalefont setfont\n" + after));
        return all.ToArray();
    }

    private static int DataOffset()
    {
        Data(out int subrMapOffset);
        return subrMapOffset;
    }

    private static PostScriptConversionResult Convert(byte[] program) => PostScriptConversion.ConvertFragment(program);

    private const string MarkPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static double LastMoveX(PostScriptConversionResult r)
        => double.Parse(ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands[0], System.Globalization.CultureInfo.InvariantCulture);

    private static string[] ShownStrings(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]).ToArray();

    private static PdfDictionary Descendant(PostScriptConversionResult r)
    {
        var type0 = ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
                        .Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });
        return (PdfDictionary)((PdfArray)type0[PdfName.DescendantFonts])[0];
    }

    /// <summary>
    /// A four-byte FD index is a number, not a sign: one with its high bit set names no FD, in the offset
    /// table as in <c>/GlyphDirectory</c>, so the glyph is missing and the conversion goes on. The drawing
    /// after it is the evidence: an index read as negative threw out of the interpreter and cost it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFourByteFdIndexWithItsHighBitSet_IsAMissingGlyph(bool directory)
    {
        byte[] table =
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x08,
            0xFF, 0xFF, 0xFF,
        };
        string extra = directory
            ? "/FDBytes 4 def /GlyphDirectory 2 dict dup 1 <FFFFFFFF0011223344556677> put def\n"
            : "/FDBytes 4 def\n";
        var r = Convert(Program("0 0 moveto <0001> show 0 0 moveto 10 10 lineto stroke", data: table, extra: extra));

        Assert.NotNull(r.Content);
        Assert.Contains("\nS\n", Encoding.ASCII.GetString(r.Content), StringComparison.Ordinal);
    }

    /// <summary>
    /// The CIDs shown are the program's glyphs: CID 0 is glyph 0, and CIDs 1 and 2 the next two, shown
    /// by those indices in a <c>CIDFontType0</c> whose <c>/FontFile3</c> of <c>/CIDFontType0C</c> holds
    /// them — the pen 110 on at 100 by their hsbw widths, 600 and 500.
    /// </summary>
    [Fact]
    public void Show_IsTheCharstringsGlyphs()
    {
        var r = Convert(Program("0 0 moveto <00010002> show" + MarkPen));
        Assert.Equal(new[] { @"(\000\001\000\002)" }, ShownStrings(r));
        Assert.Equal(110.0, LastMoveX(r), 3);

        var descendant = Descendant(r);
        Assert.Equal("CIDFontType0", ((PdfName)descendant[PdfName.Subtype]).Value);
        Assert.Null(descendant[PdfName.CIDToGIDMap]);
        var program = (PdfStream)((PdfDictionary)descendant[PdfName.FontDescriptor])[PdfName.FontFile3];
        Assert.Equal("CIDFontType0C", ((PdfName)program[PdfName.Subtype]).Value);

        var cff = CffFontSet.TryRead(program.RawData, out string reason);
        Assert.True(cff != null, reason);
        Assert.False(cff.IsCidKeyed);   // ISO 32000-2 §9.7.4.2: its CIDs are glyph indices
        Assert.True(cff.TryGetAdvance("g1", out double one));
        Assert.True(cff.TryGetAdvance("g2", out double two));
        // The CFF parser's font-matrix scale is a float: 0.001f leaves a few parts in 1e7.
        Assert.Equal(600.0, one, 3);
        Assert.Equal(500.0, two, 3);
        Assert.True(cff.TryGetAdvance(".notdef", out double notdef));
        Assert.Equal(250.0, notdef, 3);

        var widths = (PdfArray)descendant[PdfName.W];
        Assert.Equal(new[] { 1.0, 600, 2, 500 },
                     Enumerable.Range(0, widths.Count).Select(k => widths[k] is PdfArray w ? ((PdfNumber)w[0]).DoubleValue : ((PdfNumber)widths[k]).DoubleValue));
    }

    /// <summary>A missing charstring (its entry equals the next) and a CID past /CIDCount are CID 0's glyph.</summary>
    [Fact]
    public void AMissingCid_IsCidZero()
    {
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(Convert(Program("0 0 moveto <0003> show"))));
        Assert.Equal(new[] { @"(\000\000)" }, ShownStrings(Convert(Program("0 0 moveto <0009> show"))));
        Assert.Equal(new[] { @"(\000\001)" }, ShownStrings(Convert(Program("0 0 moveto <0001> show"))));   // the control
    }

    /// <summary>
    /// <c>charpath</c> reads the charstrings: CID 1's box from x = 100 (10 at size 100), CID 2's outline
    /// through its subroutine from x = 200 — the subroutine table read and decrypted.
    /// </summary>
    [Fact]
    public void Charpath_ReadsTheCharstringsAndTheirSubroutines()
    {
        ContentStreamAssert.Contains(Convert(Program("0 0 moveto <0001> true charpath fill")).Content, "10 0 m");
        ContentStreamAssert.Contains(Convert(Program("0 0 moveto <0002> true charpath fill")).Content, "20 0 m");
    }

    /// <summary><c>(Hex)</c> data reads as the same font.</summary>
    [Fact]
    public void HexData_IsTheSameFont()
    {
        var r = Convert(Program("0 0 moveto <00010002> show" + MarkPen, hex: true));
        Assert.Equal(110.0, LastMoveX(r), 3);
    }

    /// <summary>
    /// A <c>/GlyphDirectory</c> glyph — an FD index, then the encrypted charstring — is read by CID
    /// instead of the offset table, even when it is added after the font is defined.
    /// </summary>
    [Fact]
    public void AGlyphDirectory_IsReadByCid()
    {
        byte[] entry = new byte[] { 0 }.Concat(MinimalType1.EncryptCharstring(MinimalType1.AdvanceOnly(700))).ToArray();
        var r = Convert(Program("/ProbeT0 /CIDFont findresource /GlyphDirectory get 1 <" + System.Convert.ToHexString(entry) + "> put " +
                                "0 0 moveto <0001> show" + MarkPen,
                                extra: "/GlyphDirectory 4 dict def\n"));
        Assert.Equal(70.0, LastMoveX(r), 3);
    }

    /// <summary>A <c>/Metrics</c> entry keyed by the CID overrides the charstring's width: 300 units, 30 at 100.</summary>
    [Fact]
    public void Metrics_AreKeyedByCid()
    {
        var r = Convert(Program("0 0 moveto <0001> show" + MarkPen, extra: "/Metrics 2 dict dup 1 [0 300] put def\n"));
        Assert.Equal(30.0, LastMoveX(r), 3);
    }

    /// <summary>
    /// Two FDs, CID 1 under the second: each FD's matrix, the subroutine-bearing FD's first, the other's
    /// second, with CID 1 run under FD 1.
    /// </summary>
    private static byte[] TwoFdProgram(string after, string first, string second, string metrics = "")
    {
        byte[] data = Data(out int map, new byte[] { 0, 1, 0, 0, 0 });
        string fds = $"3 dict dup begin /FontMatrix [{first} 0 0 {first} 0 0] def /Private 3 dict dup begin " +
                     $"/SubrMapOffset {map} def /SDBytes 2 def /SubrCount 1 def end def end " +
                     $"3 dict dup begin /FontMatrix [{second} 0 0 {second} 0 0] def /Private 1 dict def end";
        return Program(after, fds: fds, data: data, extra: metrics);
    }

    private static CffFontSet EmbeddedCff(PostScriptConversionResult r)
    {
        var program = (PdfStream)((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile3];
        var cff = CffFontSet.TryRead(program.RawData, out string reason);
        Assert.True(cff != null, reason);
        return cff;
    }

    /// <summary>
    /// A CID under an FD twice as coarse is drawn twice the size: the CFF keeps the finer FD's 1000-unit
    /// space, CID 1's box from x = 200 (20 at 100) and 1200 wide; CID 2, under the finer FD, as it was.
    /// </summary>
    [Fact]
    public void ACoarserFd_IsScaledIntoTheFinerOnesSpace()
    {
        var r = Convert(TwoFdProgram("0 0 moveto <0001> show" + MarkPen, "0.001", "0.002"));
        Assert.Equal(120.0, LastMoveX(r), 3);
        ContentStreamAssert.Contains(Convert(TwoFdProgram("0 0 moveto <0001> true charpath fill", "0.001", "0.002")).Content, "20 0 m");
        Assert.Equal(50.0, LastMoveX(Convert(TwoFdProgram("0 0 moveto <0002> show" + MarkPen, "0.001", "0.002"))), 3);

        CffFontSet cff = EmbeddedCff(r);
        Assert.True(cff.TryGetAdvance("g1", out double advance));
        Assert.Equal(1200.0, advance, 2);
        var widths = (PdfArray)Descendant(r)[PdfName.W];
        Assert.Equal(1200.0, ((PdfNumber)((PdfArray)widths[1])[0]).DoubleValue, 3);
    }

    /// <summary>
    /// The finest FD need not be the first: under FDs of 0.0005 and 0.001 the CFF takes 0.0005, CID 1
    /// (FD 1) added at twice its units — 600 wide, 60 at 100 — and CID 2 (FD 0) at its own, 250 wide.
    /// </summary>
    [Fact]
    public void TheFinestFd_IsTheFontsSpace_WhereverItIs()
    {
        Assert.Equal(60.0, LastMoveX(Convert(TwoFdProgram("0 0 moveto <0001> show" + MarkPen, "0.0005", "0.001"))), 3);
        Assert.Equal(25.0, LastMoveX(Convert(TwoFdProgram("0 0 moveto <0002> show" + MarkPen, "0.0005", "0.001"))), 3);
        ContentStreamAssert.Contains(Convert(TwoFdProgram("0 0 moveto <0002> true charpath fill", "0.0005", "0.001")).Content, "10 0 m");
    }

    /// <summary>
    /// FDs whose scales differ by more than a double counts — 1e-300 and 1e300 — are refused by name,
    /// the font left on the predefined-CMap path; a product of matrices past a double's range is not a
    /// uniform scale. Each with the same program at sane scales as its control.
    /// </summary>
    [Fact]
    public void FdScalesPastADouble_AreRefusedByName_HostileInput()
    {
        using var diagnostics = DiagnosticCollector.Start();
        var r = Convert(TwoFdProgram("0 0 moveto <0001> show", "1e-300", "1e300"));
        Assert.True(diagnostics.Any("differ by more than a number counts"), string.Join(" | ", diagnostics.Messages));
        Assert.Null(((PdfDictionary)Descendant(r)[PdfName.FontDescriptor])[PdfName.FontFile3]);
        Assert.NotNull(((PdfDictionary)Descendant(Convert(TwoFdProgram("0 0 moveto <0001> show", "0.001", "0.002")))[PdfName.FontDescriptor])[PdfName.FontFile3]);

        var overflow = Convert(TwoFdProgram("0 0 moveto <0001> show", "1e300", "1e300", "/FontMatrix [1e300 0 0 1e300 0 0] def\n"));
        Assert.True(diagnostics.Any("is not a uniform scale"), string.Join(" | ", diagnostics.Messages));
        Assert.Null(((PdfDictionary)Descendant(overflow)[PdfName.FontDescriptor])[PdfName.FontFile3]);
        var sane = Convert(TwoFdProgram("0 0 moveto <0001> show", "0.001", "0.001", "/FontMatrix [1 0 0 1 0 0] def\n"));
        Assert.NotNull(((PdfDictionary)Descendant(sane)[PdfName.FontDescriptor])[PdfName.FontFile3]);
    }

    /// <summary>
    /// A <c>/Metrics</c> width is in the glyph space of the CID's own FD: 300 under the coarser FD is
    /// 600 units, 60 at 100.
    /// </summary>
    [Fact]
    public void Metrics_AreInTheCidsFdsSpace()
    {
        var r = Convert(TwoFdProgram("0 0 moveto <0001> show" + MarkPen, "0.001", "0.002", "/Metrics 2 dict dup 1 [0 300] put def\n"));
        Assert.Equal(60.0, LastMoveX(r), 3);
    }

    /// <summary>Data shorter than StartData declared is said, and no CIDFont is defined; the program resumes.</summary>
    [Fact]
    public void ShortData_IsSaid()
    {
        using var diagnostics = DiagnosticCollector.Start();
        byte[] program = Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 20 dict begin /CIDFontName /Short def /CIDFontType 0 def (Binary) 500 StartData\nabc");
        Convert(program);
        Assert.True(diagnostics.Any("bytes short"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>A count past the allocation budget is refused before anything is read.</summary>
    [Fact]
    public void AHugeCount_SpendsTheBudget_HostileInput()
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 1_000_000 };
        byte[] program = Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 20 dict begin /CIDFontName /Big def (Binary) 2000000000 StartData\n");
        Assert.True(PostScriptConversion.ConvertFragment(program, options).BudgetExhausted);
    }
}
