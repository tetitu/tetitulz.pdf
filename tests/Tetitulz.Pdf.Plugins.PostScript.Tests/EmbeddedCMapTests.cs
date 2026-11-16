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
/// CMaps a program builds with the <c>CIDInit</c> operators (PLRM §5.11.4): <c>begincmap</c> …
/// <c>endcmap</c> and <c>/CMap defineresource</c> make a CMap <c>composefont</c> composes with; its codes
/// are split and mapped as the reader will split and map them, and it is embedded as the Type0 font's
/// <c>/Encoding</c> stream (ISO 32000-2 §9.7.5.3). Before, the procedure set held <c>StartData</c> alone
/// and a program's own CMap was undefined.
/// </summary>
public sealed class EmbeddedCMapTests
{
    /// <summary>
    /// A CMap named <paramref name="name"/> in the collection <paramref name="ordering"/>: one-byte codes
    /// over <c>&lt;00&gt; &lt;FF&gt;</c> unless <paramref name="codespace"/> says otherwise, and
    /// <paramref name="body"/>'s mappings.
    /// </summary>
    internal static string CMap(string name, string body, string codespace = "1 begincodespacerange <00> <FF> endcodespacerange\n",
                                string ordering = "Identity", string extra = "")
        => "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
           $"/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering ({ordering}) def /Supplement 0 def end def\n" +
           $"/CMapName /{name} def /CMapType 1 def\n" + extra + codespace + body +
           "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

    /// <summary>The CID-keyed CFF font set /TestCid: CIDs 10, 20 and 30, 500 wide.</summary>
    private static readonly byte[] CidSet = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 });

    /// <summary>The font set, then <paramref name="cmaps"/>, then /TestCid composed with <paramref name="cmap"/> as /Composed at 100, then <paramref name="after"/>.</summary>
    private static PostScriptConversionResult WithCidSet(string cmaps, string cmap, string after)
    {
        var all = new List<byte>();
        all.AddRange(Encoding.ASCII.GetBytes($"/FontSetInit /ProcSet findresource begin\n/TestSet {CidSet.Length} StartData\n"));
        all.AddRange(CidSet);
        all.AddRange(Encoding.ASCII.GetBytes("\n" + cmaps +
            $"/Composed /{cmap} [/TestCid] composefont pop /Composed findfont 100 scalefont setfont\n" + after));
        return PostScriptConversion.ConvertFragment(all.ToArray());
    }

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private const string MarkPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static double PenX(PostScriptConversionResult r)
        => double.Parse(ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "m").Operands[0], System.Globalization.CultureInfo.InvariantCulture);

    private static PdfDictionary Type0(PostScriptConversionResult r)
        => ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
               .Single(f => f[PdfName.Subtype] is PdfName { Value: "Type0" });

    private static PdfDictionary Descendant(PostScriptConversionResult r)
        => (PdfDictionary)((PdfArray)Type0(r)[PdfName.DescendantFonts])[0];

    /// <summary>The CIDs a descendant's <c>/W</c> lists — those the text used.</summary>
    private static int[] WidthCids(PostScriptConversionResult r)
    {
        var w = (PdfArray)Descendant(r)[PdfName.W];
        return Enumerable.Range(0, w.Count).Where(k => w[k] is PdfNumber && k + 1 < w.Count && w[k + 1] is PdfArray)
                         .Select(k => ((PdfNumber)w[k]).IntValue).ToArray();
    }

    private static string ProgramText(PdfStream cmap) => Encoding.ASCII.GetString(cmap.RawData);

    private const string OneByte = "1 begincidrange <41> <43> 10 endcidrange\n1 begincidchar <61> 20 endcidchar\n";

    /// <summary>
    /// A CMap the program defined decodes the codes a CID-keyed CFF font shows: <c>(Aa)</c> is two
    /// one-byte codes, CIDs 10 and 20, 500 wide each — the pen 100 on at 100 — and the bytes pass into
    /// <c>Tj</c> unchanged under the Type0 font's <c>/Encoding</c>, the CMap embedded as a stream: its
    /// name, collection and writing mode, and its program, which a reader decodes to the same CIDs.
    /// </summary>
    [Fact]
    public void AProgramsCMap_DecodesTheCodes_AndIsEmbeddedAsTheEncoding()
    {
        var r = WithCidSet(CMap("Test-OneByte", OneByte), "Test-OneByte", "0 0 moveto (Aa) show" + MarkPen);

        Assert.Equal(new[] { "(Aa)" }, ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]));
        Assert.Equal(new[] { 10, 20 }, WidthCids(r));
        Assert.Equal(100.0, PenX(r), 3);

        var cmap = Assert.IsType<PdfStream>(Type0(r)[PdfName.Encoding]);
        Assert.Equal("CMap", ((PdfName)cmap[PdfName.Type]).Value);
        Assert.Equal("Test-OneByte", ((PdfName)cmap[PdfName.CMapName]).Value);
        Assert.Equal("Identity", ((PdfString)((PdfDictionary)cmap[PdfName.CIDSystemInfo])[PdfName.Ordering]).GetText());
        Assert.Equal(0, ((PdfNumber)cmap[PdfName.WMode]).IntValue);
        Assert.False(cmap.ContainsKey(PdfName.UseCMap));
        string text = ProgramText(cmap);
        Assert.Contains("1 begincodespacerange\n<00> <FF>\nendcodespacerange", text, StringComparison.Ordinal);
        Assert.Contains("1 begincidrange\n<41> <43> 10\nendcidrange", text, StringComparison.Ordinal);
        Assert.Contains("1 begincidchar\n<61> 20\nendcidchar", text, StringComparison.Ordinal);
        Assert.Equal(new[] { 10, 20 }, EncodingCMap.Parse(cmap.RawData).Decode("Identity", "Aa"u8.ToArray()).Select(c => c.Cid));
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// The CMap dictionary itself composes as well as its key, and the CMap's own <c>/WMode</c> is the
    /// stream's.
    /// </summary>
    [Fact]
    public void ACMapDictionary_Composes_AndCarriesItsWritingMode()
    {
        string define = CMap("Test-Vertical", OneByte, extra: "/WMode 1 def\n");
        var r = WithCidSet(define + "/Test-Vertical /CMap findresource /CMapDict exch def\n", "Test-Vertical",
                           "/Again /CMapDict load [/TestCid] composefont 100 scalefont setfont 0 0 moveto (A) show");
        var cmap = Assert.IsType<PdfStream>(Type0(r)[PdfName.Encoding]);
        Assert.Equal(1, ((PdfNumber)cmap[PdfName.WMode]).IntValue);
        Assert.Contains("/WMode 1 def", ProgramText(cmap), StringComparison.Ordinal);
        Assert.Equal(new[] { 10 }, WidthCids(r));
    }

    /// <summary>
    /// A Type 2 CIDFont composed with the program's CMap shows each code's CID through its
    /// <c>/CIDMap</c> as a glyph index: <c>(AB)</c>, CIDs 3 and 4, glyphs 1 and 2 ('A' 610, 'B' 620) —
    /// the pen 123 on at 100.
    /// </summary>
    [Fact]
    public void AType2CidFont_ShowsThroughTheProgramsCMap()
    {
        var r = Convert(CMap("Test-Glyphs", "1 begincidrange <41> <42> 3 endcidrange\n") +
            "20 dict begin /CIDFontName /ProbeCID def /CIDFontType 2 def /FontType 42 def\n" +
            "/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Identity) def /Supplement 0 def end def\n" +
            "/FontMatrix [1 0 0 1 0 0] def /FontBBox [0 0 1 1] def /PaintType 0 def\n" +
            "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for def\n" +
            "/CharStrings 1 dict dup begin /.notdef 0 def end def\n" +
            "/CIDCount 5 def /GDBytes 2 def /CIDMap <00000000000000010002> def\n" +
            "/sfnts [" + Type42FontTests.Hex(Type42FontTests.Program) + "] def\n" +
            "/ProbeCID currentdict end /CIDFont defineresource pop\n" +
            "/Composed /Test-Glyphs [/ProbeCID] composefont pop /Composed findfont 100 scalefont setfont\n" +
            "0 0 moveto (AB) show" + MarkPen);

        Assert.Equal(new[] { @"(\000\001\000\002)" }, ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tj").Select(o => o.Operands[0]));
        Assert.Equal(123.0, PenX(r), 3);
    }

    /// <summary>
    /// A composite font of a face the document does not embed, under the program's CMap: the codes
    /// are the CMap's — <c>&lt;C1C2C3&gt;</c> three one-byte codes, an em each, 300 at 100, where the
    /// predefined CMaps' naming rule takes a byte from 0x80 up as the start of a two-byte code and
    /// makes it two — and the descendant declares the CMap's collection.
    /// </summary>
    [Fact]
    public void AHostFace_UnderTheProgramsCMap_CountsItsCodes()
    {
        var r = Convert(CMap("Test-HighBytes", "1 begincidrange <C1> <C3> 100 endcidrange\n", ordering: "Japan1") +
                        "/Composed /Test-HighBytes /SomeFace composefont pop /Composed findfont 100 scalefont setfont " +
                        "0 0 moveto <C1C2C3> show" + MarkPen);

        Assert.Equal(300.0, PenX(r), 3);
        Assert.Equal("Japan1", ((PdfString)((PdfDictionary)Descendant(r)[PdfName.CIDSystemInfo])[PdfName.Ordering]).GetText());
        Assert.IsType<PdfStream>(Type0(r)[PdfName.Encoding]);
    }

    /// <summary>
    /// <c>cshow</c> runs its procedure once a code of the program's CMap: three one-byte codes in
    /// <c>&lt;C1C2C3&gt;</c>, where the naming rule would make two.
    /// </summary>
    [Fact]
    public void Cshow_SplitsByTheProgramsCMap()
    {
        var r = Convert(CMap("Test-HighBytes", "1 begincidrange <C1> <C3> 100 endcidrange\n", ordering: "Japan1") +
                        "/Composed /Test-HighBytes /SomeFace composefont pop /Composed findfont 100 scalefont setfont " +
                        "/n 0 def { pop pop pop /n n 1 add def } <C1C2C3> cshow n 10 mul 0 moveto 0 0 lineto stroke");
        Assert.Equal(30.0, PenX(r), 3);
    }

    /// <summary>
    /// A CMap defined again under its name is another CMap: a font composed with each shows through its
    /// own, two Type0 fonts with two streams — <c>A</c> CID 10 in the first, 20 in the second — for a
    /// font set's font and a host face alike.
    /// </summary>
    [Fact]
    public void ACMapDefinedAgain_IsAnotherCMap()
    {
        string again = CMap("Test-Again", "1 begincidchar <41> 20 endcidchar\n") +
                       "/Second /Test-Again [/TestCid] composefont 100 scalefont setfont (A) show\n" +
                       "/Host2 /Test-Again /SomeFace composefont 100 scalefont setfont (A) show\n";
        var r = WithCidSet(CMap("Test-Again", "1 begincidchar <41> 10 endcidchar\n") +
                           "/Host1 /Test-Again /SomeFace composefont 100 scalefont setfont 0 0 moveto (A) show\n",
                           "Test-Again", "(A) show\n" + again);

        var type0s = ((PdfDictionary)r.Resources[PdfName.Font]).Select(kv => kv.Value).OfType<PdfDictionary>()
                         .Where(f => f[PdfName.Subtype] is PdfName { Value: "Type0" }).ToArray();
        Assert.Equal(4, type0s.Length);
        var cids = type0s.Select(f => EncodingCMap.Parse(((PdfStream)f[PdfName.Encoding]).RawData).Decode("Identity", "A"u8.ToArray())[0].Cid)
                         .OrderBy(c => c).ToArray();
        Assert.Equal(new[] { 10, 10, 20, 20 }, cids);
    }

    /// <summary>
    /// PLRM §5.11.4's example gives <c>/CIDSystemInfo</c> as an array, one entry a font: font 0's
    /// dictionary is the collection written.
    /// </summary>
    [Fact]
    public void ACollectionArray_GivesFontZerosCollection()
    {
        string program = CMap("Test-Array", OneByte)
            .Replace("/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Identity) def /Supplement 0 def end def",
                     "/CIDSystemInfo [ 3 dict dup begin /Registry (Adobe) def /Ordering (Korea1) def /Supplement 1 def end null ] def");
        var r = WithCidSet(program, "Test-Array", "0 0 moveto (A) show");
        var info = (PdfDictionary)((PdfStream)Type0(r)[PdfName.Encoding])[PdfName.CIDSystemInfo];
        Assert.Equal("Korea1", ((PdfString)info[PdfName.Ordering]).GetText());
        Assert.Equal(1, ((PdfNumber)info[PdfName.Supplement]).IntValue);
    }

    /// <summary>A CMap with no <c>/CIDSystemInfo</c> is written Adobe-Identity-0, and said.</summary>
    [Fact]
    public void ACMapWithNoCollection_IsSaid()
    {
        using var diagnostics = DiagnosticCollector.Start();
        string program = CMap("Test-NoInfo", OneByte, ordering: "Japan1")
            .Replace("/CIDSystemInfo 3 dict dup begin /Registry (Adobe) def /Ordering (Japan1) def /Supplement 0 def end def", "");
        var r = WithCidSet(program, "Test-NoInfo", "0 0 moveto (A) show");

        var info = (PdfDictionary)((PdfStream)Type0(r)[PdfName.Encoding])[PdfName.CIDSystemInfo];
        Assert.Equal("Identity", ((PdfString)info[PdfName.Ordering]).GetText());
        Assert.True(diagnostics.Any("declares no character collection"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>
    /// The written program puts at most 100 entries in a block, as ISO 32000-2 §9.7.5.4's example and
    /// Adobe's CMap files do: 150 characters are a block of 100 and one of 50, and every one maps.
    /// </summary>
    [Fact]
    public void TheWrittenProgram_HoldsAHundredEntriesABlock()
    {
        var body = new StringBuilder("150 begincidchar\n");
        for (int k = 0; k < 150; k++) body.Append($"<{k:X2}> {1000 + k}\n");
        body.Append("endcidchar\n");
        var r = WithCidSet(CMap("Test-Blocks", body.ToString()), "Test-Blocks", "0 0 moveto <0095> show");

        var cmap = (PdfStream)Type0(r)[PdfName.Encoding];
        string text = ProgramText(cmap);
        Assert.Contains("100 begincidchar\n<00> 1000\n", text, StringComparison.Ordinal);
        Assert.Contains("50 begincidchar\n<64> 1100\n", text, StringComparison.Ordinal);
        Assert.Equal(new[] { 1000, 1149 }, EncodingCMap.Parse(cmap.RawData).Decode("Identity", new byte[] { 0x00, 0x95 }).Select(c => c.Cid));
    }

    /// <summary>
    /// A CMap whose key a name token cannot spell — here a space — is written into its program as the
    /// key's bytes through <c>cvn</c>, so the program names the CMap it was given; the stream's
    /// <c>/CMapName</c> is the name itself.
    /// </summary>
    [Fact]
    public void AnIrregularName_IsWrittenThroughCvn()
    {
        string program = CMap("Unused", OneByte).Replace("CMapName currentdict /CMap defineresource", "(Test Space) currentdict /CMap defineresource");
        // Composed by the string key; the helper's own composition, under Identity-H, shows nothing.
        var r = WithCidSet(program, "Identity-H",
                           "/ByString (Test Space) [/TestCid] composefont 100 scalefont setfont 0 0 moveto (A) show");

        var cmap = (PdfStream)Type0(r)[PdfName.Encoding];
        Assert.Equal("Test Space", ((PdfName)cmap[PdfName.CMapName]).Value);
        Assert.Contains("/CMapName <54657374205370616365> cvn def", ProgramText(cmap), StringComparison.Ordinal);
        Assert.Equal(new[] { 10 }, WidthCids(r));
    }

    /// <summary>
    /// <c>usecmap</c> of a CMap the program defined takes its codespace and the mappings this one does
    /// not give — <c>B</c> is the parent's CID 10, <c>A</c> this CMap's 20 over the parent's 30 — and
    /// is embedded as its own stream under <c>/UseCMap</c>, the program naming it.
    /// </summary>
    [Fact]
    public void UsecmapOfAProgramsCMap_TakesWhatThisOneDoesNotGive()
    {
        string parent = CMap("Test-Parent", "2 begincidchar <41> 30 <42> 10 endcidchar\n");
        string child = CMap("Test-Child", "1 begincidchar <41> 20 endcidchar\n", codespace: "", extra: "/Test-Parent usecmap\n");
        var r = WithCidSet(parent + child, "Test-Child", "0 0 moveto (AB) show" + MarkPen);

        Assert.Equal(new[] { 10, 20 }, WidthCids(r));
        Assert.Equal(100.0, PenX(r), 3);
        var cmap = Assert.IsType<PdfStream>(Type0(r)[PdfName.Encoding]);
        Assert.Contains("/Test-Parent usecmap", ProgramText(cmap), StringComparison.Ordinal);
        var used = Assert.IsType<PdfStream>(cmap[PdfName.UseCMap]);
        Assert.Equal("Test-Parent", ((PdfName)used[PdfName.CMapName]).Value);
        Assert.Contains("<42> 10", ProgramText(used), StringComparison.Ordinal);
    }

    /// <summary><c>usecmap</c> of a predefined CMap is written as its name: Identity-H's two-byte codes, CID the code.</summary>
    [Fact]
    public void UsecmapOfAPredefinedCMap_IsItsName()
    {
        var r = WithCidSet(CMap("Test-OverIdentity", "", codespace: "", extra: "/Identity-H usecmap\n"), "Test-OverIdentity",
                           "0 0 moveto <000A0014> show" + MarkPen);

        Assert.Equal(new[] { 10, 20 }, WidthCids(r));
        Assert.Equal("Identity-H", ((PdfName)((PdfStream)Type0(r)[PdfName.Encoding])[PdfName.UseCMap]).Value);
    }

    /// <summary>
    /// What a PDF encoding CMap cannot hold (ISO 32000-2 §9.7.5.4) is consumed and said — font 1's
    /// mappings, base-font mappings, a font matrix — while font 0's mappings are carried.
    /// </summary>
    [Fact]
    public void WhatAPdfCMapCannotHold_IsConsumedAndSaid()
    {
        using var diagnostics = DiagnosticCollector.Start();
        string body = "1 usefont 1 begincidrange <41> <41> 30 endcidrange\n" +
                      "1 beginbfchar <62> /b endbfchar\n1 beginusematrix [1 0 0 1 0 0] endusematrix\n" +
                      "0 usefont 1 begincidchar <41> 10 endcidchar\n";
        var r = WithCidSet(CMap("Test-Fonts", body), "Test-Fonts", "0 0 moveto (A) show" + MarkPen);

        Assert.Equal(new[] { 10 }, WidthCids(r));
        string text = ProgramText((PdfStream)Type0(r)[PdfName.Encoding]);
        Assert.DoesNotContain("30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bfchar", text, StringComparison.Ordinal);
        Assert.True(diagnostics.Any("3 entries a PDF encoding CMap cannot"), string.Join(" | ", diagnostics.Messages));
        Assert.True(diagnostics.Any("font 1"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>
    /// The entries are the ones <c>n</c> counted, in the order written: an <c>end…</c> with no matching
    /// <c>begin…</c>, a code of five bytes, a range whose bounds differ in length and a negative CID
    /// are each a <c>rangecheck</c>; the well-formed control defines its CMap.
    /// </summary>
    [Theory]
    [InlineData("1 begincidchar <41> 10 endcidrange\n")]
    [InlineData("1 begincidchar <4142434445> 10 endcidchar\n")]
    [InlineData("1 begincidrange <41> <4142> 10 endcidrange\n")]
    [InlineData("1 begincidchar <41> -1 endcidchar\n")]
    public void AMalformedEntry_IsARangecheck(string body)
    {
        var r = Convert(CMap("Test-Bad", body) + "/Test-Bad /CMap resourcestatus { pop pop 0 0 1 1 rectfill } if");
        Assert.Contains("rangecheck", r.LastError ?? "", StringComparison.Ordinal);

        var control = Convert(CMap("Test-Good", "1 begincidchar <41> 10 endcidchar\n") + "/Test-Good /CMap resourcestatus { pop pop 0 0 1 1 rectfill } if");
        Assert.Contains("f", ContentStreamAssert.Operators(control.Content));
    }

    /// <summary>A CMap one conversion defined is not the next one's: there <c>composefont</c> finds no CMap and says so.</summary>
    [Fact]
    public void ACMap_DoesNotOutliveItsConversion()
    {
        Assert.True(WithCidSet(CMap("Test-Once", OneByte), "Test-Once", "0 0 moveto (A) show").IsClean);

        using var diagnostics = DiagnosticCollector.Start();
        WithCidSet("", "Test-Once", "0 0 moveto (A) show");
        Assert.True(diagnostics.Any("named neither a CMap the program defined"), string.Join(" | ", diagnostics.Messages));
    }

    /// <summary>
    /// A count past every cap is a <c>limitcheck</c> before anything is allocated for it — no
    /// entries are popped, and the program goes on.
    /// </summary>
    [Fact]
    public void AHugeCount_IsALimitcheck_HostileInput()
    {
        var r = Convert(CMap("Test-Huge", "2000000000 begincidrange\n") + "0 0 1 1 rectfill");
        Assert.Contains("limitcheck", r.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }
}

[Collection(PsLimitsCollection.Name)]
public sealed class EmbeddedCMapCapTests
{
    /// <summary>
    /// <see cref="PsLimits.MaxCMapEntries"/> is the most a CMap may hold: at the cap its CMap is
    /// defined; one entry more is a <c>limitcheck</c> naming the cap.
    /// </summary>
    [Fact]
    public void TheEntryCap_AdmitsItsCount_AndRefusesOneMore()
    {
        const string Probe = "/Test-Capped /CMap resourcestatus { pop pop 0 0 1 1 rectfill } if";
        try
        {
            PsLimits.MaxCMapEntries = 3;   // the codespace range and two characters
            var at = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                EmbeddedCMapTests.CMap("Test-Capped", "2 begincidchar <41> 10 <42> 11 endcidchar\n") + Probe));
            Assert.Contains("f", ContentStreamAssert.Operators(at.Content));
            Assert.True(at.IsClean, at.LastError);

            // The refused begincidchar's endcidchar then finds none open, so the cap is the first error, not the last.
            using var diagnostics = DiagnosticCollector.Start();
            PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                EmbeddedCMapTests.CMap("Test-Capped", "3 begincidchar <41> 10 <42> 11 <43> 12 endcidchar\n") + Probe));
            Assert.True(diagnostics.Any("MaxCMapEntries); the cap was reached"), string.Join(" | ", diagnostics.Messages));
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A chain of CMaps each using the one before is refused at <c>PdfLimits.MaxCMapUsecmapDepth</c>
    /// levels — the reader follows no more — with a <c>limitcheck</c> one level past it; at it, the
    /// last CMap is defined.
    /// </summary>
    [Fact]
    public void AUsecmapChain_IsRefusedPastTheReadersDepth()
    {
        static string Chain(int levels)
        {
            var program = new StringBuilder(EmbeddedCMapTests.CMap("Test-Level0", "1 begincidchar <41> 10 endcidchar\n"));
            for (int k = 1; k <= levels; k++)
                program.Append(EmbeddedCMapTests.CMap($"Test-Level{k}", "", codespace: "", extra: $"/Test-Level{k - 1} usecmap\n"));
            return program + $"/Test-Level{levels} /CMap resourcestatus {{ pop pop 0 0 1 1 rectfill }} if";
        }
        try
        {
            Core.Constants.PdfLimits.MaxCMapUsecmapDepth = 3;
            var within = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(Chain(3)));
            Assert.Contains("f", ContentStreamAssert.Operators(within.Content));
            Assert.True(within.IsClean, within.LastError);

            var past = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(Chain(4)));
            Assert.Contains("limitcheck", past.LastError ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Core.Constants.PdfLimits.ResetToDefaults();
        }
    }
}
