// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A document's own Type 1 program read for its glyphs: <c>charpath</c> appends each glyph's outline,
/// <c>stringwidth</c> and the pen after <c>show</c> advance by the program's <c>hsbw</c> widths, and
/// the emitted font dictionary carries the <c>/Widths</c> ISO 32000-1 Table 111 requires, read from
/// the same program (§9.6.2.1). Before, all three took Helvetica's metrics — a metric box for
/// <c>charpath</c>, Helvetica's AFM for every advance — and the font went out with no widths at all.
///
/// <para>The fixture is a real program — clear text, an <c>eexec</c>-encrypted private part whose
/// charstrings are encrypted again — built by <see cref="MinimalType1"/>, so the capture's split
/// and Core's parser are both under test.</para>
/// </summary>
[Collection(PsLimitsCollection.Name)]
public sealed class EmbeddedFontOutlineTests
{
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

    private const byte Hsbw = 13, RMoveTo = 21, RLineTo = 5, RRCurveTo = 8, ClosePath = 9, EndChar = 14;

    /// <summary>'A': 600 wide, a 400 × 700 box from x = 100.</summary>
    private static readonly byte[] BoxGlyph =
        Op(0, 600, Hsbw, 100, 0, RMoveTo, 400, 0, RLineTo, 0, 700, RLineTo, -400, 0, RLineTo, ClosePath, EndChar);

    /// <summary>'B': 500 wide, one curve from the origin.</summary>
    private static readonly byte[] CurveGlyph =
        Op(0, 500, Hsbw, 0, 0, RMoveTo, 100, 0, 100, 100, 0, 100, RRCurveTo, ClosePath, EndChar);

    /// <summary>
    /// The document: the program — '.notdef', 'A' and 'B' encoded at 65 and 66, and 'C' (700 wide),
    /// which its encoding does not reach — defined as /ProbeFont and set at <paramref name="size"/>.
    /// With <paramref name="standardEncoding"/> the program declares <c>/Encoding StandardEncoding
    /// def</c> instead of an array.
    /// </summary>
    private static byte[] Document(string after, int notdefWidth = 250,
                                   string fontMatrix = "[0.001 0 0 0.001 0 0]", int size = 100,
                                   bool standardEncoding = false,
                                   string define = "/ProbeFont exch definefont pop")
    {
        var (program, length1, _) = MinimalType1.Build(
            new List<(string, byte[])>
            {
                (".notdef", MinimalType1.AdvanceOnly(notdefWidth)),
                ("A", BoxGlyph),
                ("B", CurveGlyph),
                ("C", MinimalType1.AdvanceOnly(700)),
            },
            standardEncoding ? null : new Dictionary<int, string> { [65] = "A", [66] = "B" },
            fontMatrix: fontMatrix);

        if (standardEncoding)
        {
            const string array = "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\nreadonly def\n";
            string clear = Encoding.Latin1.GetString(program, 0, length1);
            Assert.Contains(array, clear, System.StringComparison.Ordinal);
            byte[] replaced = Encoding.Latin1.GetBytes(clear.Replace(array, "/Encoding StandardEncoding def\n"));
            program = replaced.Concat(program.Skip(length1)).ToArray();
        }

        var all = new MemoryStream();
        void Text(string s) { byte[] b = Encoding.Latin1.GetBytes(s); all.Write(b, 0, b.Length); }
        Text("12 dict begin\n");
        all.Write(program, 0, program.Length);
        Text("\n" + new string('0', 512) + "\ncleartomark\n");
        Text($"{define} /ProbeFont findfont {size} scalefont setfont\n");
        Text(after);
        return all.ToArray();
    }

    private static PostScriptConversionResult Convert(string after, int notdefWidth = 250)
        => PostScriptConversion.ConvertFragment(Document(after, notdefWidth));

    /// <summary>
    /// The path's segments as a painter reads them: a move another move follows, or that ends the
    /// path, paints nothing (the pen's own <c>moveto</c> before the glyph, the current point left after it).
    /// </summary>
    private static List<ContentOp> Path(byte[] content)
    {
        var ops = ContentStreamAssert.Parse(content).Where(o => o.Operator is "m" or "l" or "c" or "h").ToList();
        return ops.Where((o, k) => o.Operator != "m" || (k + 1 < ops.Count && ops[k + 1].Operator != "m")).ToList();
    }

    private static double[] Numbers(ContentOp op)
        => op.Operands.Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static double[] LastOperandsOf(byte[] content, string op)
        => Numbers(ContentStreamAssert.Parse(content).Last(o => o.Operator == op));

    private const string ShowTopTwo = " newpath moveto 0 0 lineto stroke ";

    // ==================================================================
    //  The size
    // ==================================================================

    /// <summary>
    /// A program's own font dictionary carries its glyph matrix, <c>[0.001 0 0 0.001 0 0]</c>, in
    /// the slot <c>scalefont</c> accumulates into: <c>100 scalefont</c> made a 0.1 Tm and the text
    /// came out at 1/1000 of its size. The text is drawn at 100.
    /// </summary>
    [Fact]
    public void Show_ProgramsOwnFontDictionary_IsDrawnAtItsSize()
    {
        var r = Convert("0 0 moveto (A) show");

        double[] tm = LastOperandsOf(r.Content, "Tm");
        Assert.Equal(100.0, tm[0], 6);
        Assert.Equal(100.0, tm[3], 6);
    }

    /// <summary>A re-encoded copy names its program already, and is not taken into thousandths twice.</summary>
    [Fact]
    public void Show_AReencodedCopy_IsDrawnAtItsSize()
    {
        var r = Convert(
            "/ProbeFont findfont dup length dict begin {1 index /FID ne {def} {pop pop} ifelse} forall " +
            "currentdict end /Re exch definefont pop /Re findfont 100 scalefont setfont 0 0 moveto (A) show");

        Assert.Equal(100.0, LastOperandsOf(r.Content, "Tm")[0], 6);
    }

    /// <summary>
    /// A program's matrix that is not the conventional one is honoured: at 0.002 its glyphs are
    /// twice the size, so <c>50 scalefont</c> draws as 100 would.
    /// </summary>
    [Fact]
    public void Show_AProgramsOwnMatrix_IsHonoured()
    {
        var r = PostScriptConversion.ConvertFragment(Document(
            "0 0 moveto (A) show", fontMatrix: "[0.002 0 0 0.002 0 0]", size: 50));

        Assert.Equal(100.0, LastOperandsOf(r.Content, "Tm")[0], 6);
    }

    /// <summary>
    /// A copy of a <c>findfont</c> result carries this interpreter's size matrix, not a glyph matrix,
    /// so defined under the program's name it is drawn at its size — the control on which
    /// dictionaries the glyph matrix is taken from.
    /// </summary>
    [Fact]
    public void Show_AFindfontCopyDefinedUnderTheProgramsName_IsDrawnAtItsSize()
    {
        var r = Convert(
            "/Helvetica findfont dup length dict copy /ProbeFont exch definefont 100 scalefont setfont " +
            "0 0 moveto (A) show");

        Assert.Equal(100.0, LastOperandsOf(r.Content, "Tm")[0], 6);
    }

    // ==================================================================
    //  charpath
    // ==================================================================

    /// <summary>
    /// The box glyph at size 100 from (10, 20): glyph units through the 0.001 matrix, so x = 100
    /// lands at 20 and the box's far corner at (60, 90) — the outline, not the metric box (which
    /// would start at 10 and run the ascender's height).
    /// </summary>
    [Fact]
    public void CharPath_EmbeddedFont_AppendsTheGlyphsOutline()
    {
        var r = Convert("newpath 10 20 moveto (A) true charpath fill");

        var path = Path(r.Content);
        Assert.Equal("m", path[0].Operator);
        Assert.Equal(new[] { 20.0, 20.0 }, Numbers(path[0]));
        Assert.Equal(new[] { 60.0, 20.0 }, Numbers(path[1]));
        Assert.Equal(new[] { 60.0, 90.0 }, Numbers(path[2]));
        Assert.Equal(new[] { 20.0, 90.0 }, Numbers(path[3]));
        Assert.Equal("h", path[4].Operator);
    }

    /// <summary>A curve stays a curve, its control points where the charstring put them.</summary>
    [Fact]
    public void CharPath_EmbeddedFont_KeepsACurveACurve()
    {
        var r = Convert("newpath 0 0 moveto (B) true charpath fill");

        var curve = Path(r.Content).Single(o => o.Operator == "c");
        Assert.Equal(new[] { 10.0, 0.0, 20.0, 10.0, 20.0, 20.0 }, Numbers(curve));
    }

    /// <summary>The second glyph starts at the first's hsbw advance (600 → 60), not Helvetica's 667.</summary>
    [Fact]
    public void CharPath_EmbeddedFont_AdvancesByTheProgramsWidths()
    {
        var r = Convert("newpath 0 0 moveto (AA) true charpath fill");

        var moves = Path(r.Content).Where(o => o.Operator == "m").ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(10.0, Numbers(moves[0])[0], 6);
        Assert.Equal(70.0, Numbers(moves[1])[0], 6);
    }

    /// <summary>The current point is left after the last glyph, as <c>show</c> leaves it.</summary>
    [Fact]
    public void CharPath_EmbeddedFont_LeavesTheCurrentPointAtTheEnd()
    {
        var r = Convert("newpath 5 7 moveto (AB) false charpath currentpoint" + ShowTopTwo);

        double[] m = LastOperandsOf(r.Content, "m");
        Assert.Equal(5 + 60 + 50, m[0], 6);
        Assert.Equal(7.0, m[1], 6);
    }

    /// <summary>A code the encoding leaves at <c>.notdef</c> advances by the program's .notdef (250 → 25).</summary>
    [Fact]
    public void CharPath_EmbeddedFont_ACodeWithNoGlyphAdvancesByNotdef()
    {
        var r = Convert("newpath 0 0 moveto (ZA) true charpath fill");

        var move = Path(r.Content).First(o => o.Operator == "m");
        Assert.Equal(25 + 10.0, Numbers(move)[0], 6);
    }

    /// <summary>
    /// A code the document re-encodes to a glyph the program lacks is drawn as the program's
    /// .notdef: 'Zeta' at 90 advances 25 before 'A'.
    /// </summary>
    [Fact]
    public void CharPath_AGlyphTheProgramLacks_AdvancesByNotdef()
    {
        var r = Convert(ReencodedZeta + "newpath 0 0 moveto (ZA) true charpath fill");

        var move = Path(r.Content).First(o => o.Operator == "m");
        Assert.Equal(25 + 10.0, Numbers(move)[0], 6);
    }

    /// <summary>The program's font re-encoded with 90 naming 'Zeta', which the program does not have.</summary>
    private const string ReencodedZeta =
        "/ProbeFont findfont dup length dict begin {1 index /FID ne {def} {pop pop} ifelse} forall " +
        "/Encoding Encoding 256 array copy dup 90 /Zeta put def currentdict end /Re exch definefont pop " +
        "/Re findfont 100 scalefont setfont ";

    /// <summary>
    /// <c>charpath</c> is charged by the outline it appends, not only by the string: 2000 box glyphs
    /// are 12 000 steps where 2000 are the string's, so a budget of 8000 is spent — the control,
    /// <c>stringwidth</c> of the same string, is not.
    /// </summary>
    [Fact]
    public void CharPath_EmbeddedFont_IsChargedByTheOutlineItAppends()
    {
        string text = new string('A', 2000);
        var options = new PostScriptConversionOptions { MaxTotalOps = 8000 };

        var outlined = PostScriptConversion.ConvertFragment(
            Document($"newpath 0 0 moveto ({text}) true charpath newpath", standardEncoding: true), options);
        var measured = PostScriptConversion.ConvertFragment(
            Document($"({text}) stringwidth pop pop", standardEncoding: true), options);

        Assert.True(outlined.BudgetExhausted);
        Assert.False(measured.BudgetExhausted);
    }

    /// <summary>Real outlines are not reported as a metric box; the control is a standard face with no outline font, which still is.</summary>
    [Fact]
    public void CharPath_EmbeddedFont_IsNotReportedAsAMetricBox()
    {
        var embedded = Convert("newpath 0 0 moveto (A) true charpath fill");
        var standard = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            "/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (A) true charpath fill"),
            new PostScriptConversionOptions { OutlineFontProvider = _ => null });

        Assert.DoesNotContain("metric box", embedded.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.Contains("metric box", standard.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }

    // ==================================================================
    //  Widths
    // ==================================================================

    /// <summary>(AB) is 600 + 500 glyph units: 110 points at size 100, where Helvetica would say 133.4.</summary>
    [Fact]
    public void StringWidth_EmbeddedFont_MeasuresTheProgramsWidths()
    {
        var r = Convert("(AB) stringwidth" + ShowTopTwo);

        double[] m = LastOperandsOf(r.Content, "m");
        Assert.Equal(110.0, m[0], 6);
        Assert.Equal(0.0, m[1], 6);
    }

    /// <summary>A code the encoding leaves at <c>.notdef</c> measures as the program's .notdef: 25 + 60.</summary>
    [Fact]
    public void StringWidth_EmbeddedFont_ACodeWithNoGlyphMeasuresAsNotdef()
    {
        var r = Convert("(ZA) stringwidth" + ShowTopTwo);

        Assert.Equal(85.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>A code naming a glyph the program lacks measures as its .notdef: 25 + 60.</summary>
    [Fact]
    public void StringWidth_AGlyphTheProgramLacks_MeasuresAsNotdef()
    {
        var r = Convert(ReencodedZeta + "(ZA) stringwidth" + ShowTopTwo);

        Assert.Equal(85.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>The pen after <c>show</c> moves by the same widths.</summary>
    [Fact]
    public void Show_EmbeddedFont_MovesThePenByTheProgramsWidths()
    {
        var r = Convert("0 0 moveto (AB) show currentpoint" + ShowTopTwo);

        Assert.Equal(110.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>
    /// The font dictionary declares 0–255 and each code's width from the program: 'A' 600, 'B' 500,
    /// and an unencoded code its .notdef, 250 — set to 333 in a second document, so the width is read
    /// and not a constant.
    /// </summary>
    [Fact]
    public void EmbeddedFont_DeclaresTheProgramsWidths()
    {
        var font = OnlyFont(Convert("0 0 moveto (AB) show"));
        var other = OnlyFont(Convert("0 0 moveto (AB) show", notdefWidth: 333));

        Assert.Equal(0, ((PdfNumber)font[PdfName.FirstChar]).IntValue);
        Assert.Equal(255, ((PdfNumber)font[PdfName.LastChar]).IntValue);
        var widths = (PdfArray)font[PdfName.Widths];
        Assert.Equal(256, widths.Count);
        Assert.Equal(600.0, ((PdfNumber)widths[65]).DoubleValue, 6);
        Assert.Equal(500.0, ((PdfNumber)widths[66]).DoubleValue, 6);
        Assert.Equal(250.0, ((PdfNumber)widths[90]).DoubleValue, 6);
        Assert.Equal(333.0, ((PdfNumber)((PdfArray)other[PdfName.Widths])[90]).DoubleValue, 6);
    }

    /// <summary>
    /// The program's own encoding array decides: 'C' is in the program but the array leaves 67 at
    /// <c>.notdef</c>, so 67 is 250 wide — not the 700 StandardEncoding's 'C' would give.
    /// </summary>
    [Fact]
    public void EmbeddedFont_AGlyphTheProgramsArrayDoesNotReach_IsNotdefWide()
    {
        var widths = (PdfArray)OnlyFont(Convert("0 0 moveto (AB) show"))[PdfName.Widths];

        Assert.Equal(250.0, ((PdfNumber)widths[67]).DoubleValue, 6);
    }

    /// <summary>
    /// A program that declares <c>/Encoding StandardEncoding def</c> reaches its glyphs through it:
    /// 65 is 'A' (600) and 67 'C' (700); 90 'Z', which the program lacks, is .notdef wide.
    /// </summary>
    [Fact]
    public void EmbeddedFont_AProgramOnStandardEncoding_IsMeasuredThroughIt()
    {
        var r = PostScriptConversion.ConvertFragment(Document(
            "0 0 moveto (AC) show currentpoint" + ShowTopTwo, standardEncoding: true));

        var widths = (PdfArray)OnlyFont(r)[PdfName.Widths];
        Assert.Equal(600.0, ((PdfNumber)widths[65]).DoubleValue, 6);
        Assert.Equal(700.0, ((PdfNumber)widths[67]).DoubleValue, 6);
        Assert.Equal(250.0, ((PdfNumber)widths[90]).DoubleValue, 6);
        Assert.Equal(130.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>
    /// A re-encoding the document applies is the one the widths follow: with code 90 set to /A the
    /// width at 90 is 600.
    /// </summary>
    [Fact]
    public void EmbeddedFont_WidthsFollowTheDocumentsEncoding()
    {
        var font = OnlyFont(Convert(
            "/ProbeFont findfont dup length dict begin {1 index /FID ne {def} {pop pop} ifelse} forall " +
            "/Encoding Encoding 256 array copy dup 90 /A put def currentdict end /Re exch definefont pop " +
            "/Re findfont 100 scalefont setfont 0 0 moveto (Z) show"));

        Assert.Equal(600.0, ((PdfNumber)((PdfArray)font[PdfName.Widths])[90]).DoubleValue, 6);
    }

    /// <summary>
    /// A font dictionary with no <c>/Encoding</c>, defined under the name of a program that declares
    /// <c>/Encoding StandardEncoding</c>, reaches the program's glyphs through StandardEncoding: 65 'A'
    /// (600), 66 'B' (500), 67 'C' (700), and 90 'Z', which it lacks, .notdef (250).
    /// </summary>
    [Fact]
    public void EmbeddedFont_ADictionaryWithNoEncoding_IsMeasuredThroughStandardEncoding()
    {
        var widths = (PdfArray)OnlyFont(PostScriptConversion.ConvertFragment(Document(
            "0 0 moveto (AB) show", standardEncoding: true,
            define: "pop /ProbeFont 12 dict definefont pop")))[PdfName.Widths];

        Assert.Equal(new[] { 600.0, 500.0, 700.0 },
                     new[] { 65, 66, 67 }.Select(c => ((PdfNumber)widths[c]).DoubleValue));
        Assert.Equal(250.0, ((PdfNumber)widths[90]).DoubleValue, 6);
    }

    /// <summary>
    /// A font dictionary with no <c>/Encoding</c> around a program with its own array reaches the
    /// glyphs through that array: 65 'A' (600), and 67 the array leaves unnamed — .notdef (250), not
    /// the 700 StandardEncoding's 'C' would give. <c>charpath</c> and <c>show</c> agree.
    /// </summary>
    [Fact]
    public void EmbeddedFont_ADictionaryWithNoEncoding_IsMeasuredThroughTheProgramsArray()
    {
        var r = PostScriptConversion.ConvertFragment(Document(
            "0 0 moveto (CA) show currentpoint" + ShowTopTwo + "newpath 0 0 moveto (CA) true charpath fill",
            define: "pop /ProbeFont 12 dict definefont pop"));

        var widths = (PdfArray)OnlyFont(r)[PdfName.Widths];
        Assert.Equal(600.0, ((PdfNumber)widths[65]).DoubleValue, 6);
        Assert.Equal(250.0, ((PdfNumber)widths[67]).DoubleValue, 6);
        Assert.Equal(85.0, LastOperandsOf(r.Content, "m")[0], 6);
        Assert.Equal(25 + 10.0, Numbers(Path(r.Content).Last(o => o.Operator == "m"))[0], 6);
    }

    /// <summary>
    /// A program whose encrypted part the binary-skip cap cut short — no trailer, the program's
    /// bytes running to the cap — is refused at capture, so nothing reads it for glyphs:
    /// <c>charpath</c> keeps the metric box, where a whole program (the control) gives outlines.
    /// </summary>
    [Fact]
    public void CharPath_AProgramCutShortByTheSkipCap_IsNotReadForItsGlyphs()
    {
        var (program, _, length2) = MinimalType1.Build(
            new List<(string, byte[])> { (".notdef", MinimalType1.AdvanceOnly(250)), ("A", BoxGlyph) },
            new Dictionary<int, string> { [65] = "A" });
        var all = new MemoryStream();
        void Text(string s) { byte[] b = Encoding.Latin1.GetBytes(s); all.Write(b, 0, b.Length); }
        Text("12 dict begin\n");
        all.Write(program, 0, program.Length);
        Text("\n/ProbeFont exch definefont pop /ProbeFont findfont 100 scalefont setfont " +
             "newpath 0 0 moveto (A) true charpath fill");

        PostScriptConversionResult cut;
        try
        {
            PsLimits.MaxBinarySkipBytes = length2;
            cut = PostScriptConversion.ConvertFragment(all.ToArray(),
                new PostScriptConversionOptions { OutlineFontProvider = _ => null });
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
        var whole = Convert("newpath 0 0 moveto (A) true charpath fill");

        Assert.Contains("metric box", cut.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.DoesNotContain("metric box", whole.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }

    private static PdfDictionary OnlyFont(PostScriptConversionResult r)
    {
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        Assert.Single(fonts);
        foreach (var kv in fonts) return (PdfDictionary)kv.Value;
        return null;
    }
}
