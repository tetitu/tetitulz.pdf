// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Type 3 (user-defined) fonts — glyphs that are PostScript <b>procedures</b>, which is how
/// PostScript carries logos, symbol sets, barcodes and bitmap fonts.
///
/// <para>Until 2026-08-19 nothing read <c>/FontType</c>, so such a font fell through to the
/// standard-14 substitution and drew <b>Helvetica's letters</b> with <c>UnimplementedOperators</c>
/// empty and <c>IsClean</c> true. The glyph procedures are now <i>run</i>, in a child interpreter,
/// and what they draw becomes the emitted font's <c>/CharProcs</c>.</para>
///
/// <para><b>The load-bearing assertion is decoding a CharProc and finding the glyph's own
/// operators in it.</b> An empty CharProc is a perfectly valid stream: it passes every check on
/// the font dictionary's shape, on `Subtype`, on `/Widths` length — everything except looking at
/// what the procedure actually drew. Two wrong ways of invoking the procedure produced exactly
/// that empty stream during development.</para>
/// </summary>
public sealed class Type3FontTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>A Type 3 font whose one glyph declares a width and fills a square.</summary>
    private const string SquareFont =
        "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
        "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put " +
        "/CharProcs 1 dict def " +
        "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
        "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
        "/T3 exch definefont pop ";

    private const string UseIt = "/T3 findfont 12 scalefont setfont 0 0 moveto (A) show";

    private static PdfDictionary OnlyFont(PostScriptConversionResult r)
    {
        if (r.Resources?[PdfName.Font] is not PdfDictionary fonts) return null;
        foreach (var kv in fonts) return (PdfDictionary)kv.Value;
        return null;
    }

    private static string CharProcText(PostScriptConversionResult r, string glyphName)
    {
        var procs = OnlyFont(r)?[PdfName.CharProcs] as PdfDictionary;
        var stream = procs?[new PdfName(glyphName)] as PdfStream;
        return stream == null ? null : Encoding.ASCII.GetString(stream.RawData);
    }

    // ==================================================================
    //  The font that is emitted
    // ==================================================================

    /// <summary>It is a real Type 3 font, not a substituted Type 1.</summary>
    [Fact]
    public void Type3Font_IsEmittedAsSubtypeType3_WithNoBaseFont()
    {
        var r = Convert(SquareFont + UseIt);

        var font = OnlyFont(r);
        Assert.NotNull(font);
        Assert.Equal("Type3", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.Null(font[PdfName.BaseFont]);   // a Type 3 font has no base face
    }

    /// <summary>
    /// <b>The assertion that proves the procedure ran.</b> An empty CharProc satisfies everything
    /// weaker — it is a valid stream in a correctly shaped font.
    /// </summary>
    [Fact]
    public void Type3CharProc_ContainsTheGlyphProceduresOwnDrawing()
    {
        var r = Convert(SquareFont + UseIt);

        string proc = CharProcText(r, "square");
        Assert.NotNull(proc);

        var ops = ContentStreamAssert.Parse(Encoding.ASCII.GetBytes(proc));
        Assert.Contains("f", ops.Select(o => o.Operator));                  // it filled
        Assert.Contains("750 750 l", ops.Select(o => o.ToString()));        // at its own coordinates
    }

    /// <summary>
    /// <c>setcachedevice</c> becomes <c>d1</c> and it is the stream's <b>first</b> operator, which
    /// Table 113 requires — a renderer does not look for it anywhere else.
    /// </summary>
    [Fact]
    public void Type3CharProc_OpensWithD1ForSetCacheDevice()
    {
        var r = Convert(SquareFont + UseIt);

        var ops = ContentStreamAssert.Parse(Encoding.ASCII.GetBytes(CharProcText(r, "square")));
        Assert.Equal("750 0 0 0 750 750 d1", ops[0].ToString());
    }

    /// <summary>
    /// <c>setcharwidth</c> becomes <c>d0</c> instead — the glyph carries its own colour, where a
    /// <c>d1</c> glyph paints in the text fill colour. Swapping the two is invisible everywhere
    /// else.
    /// </summary>
    [Fact]
    public void Type3CharProc_OpensWithD0ForSetCharWidth()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /dot put /CharProcs 1 dict def " +
            "CharProcs /dot { 500 0 setcharwidth 0 0 100 100 rectfill } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " + UseIt);

        var ops = ContentStreamAssert.Parse(Encoding.ASCII.GetBytes(CharProcText(r, "dot")));
        Assert.Equal("500 0 d0", ops[0].ToString());
    }

    /// <summary>
    /// <c>/Widths</c> are in <b>glyph space</b> — the raw <c>wx</c>, not thousandths. Both renderers
    /// multiply by <c>/FontMatrix</c>[0] themselves, so /1000-scaling here makes every advance
    /// 1000× too small.
    /// </summary>
    [Fact]
    public void Type3Widths_AreInGlyphSpace()
    {
        var r = Convert(SquareFont + UseIt);

        var font = OnlyFont(r);
        Assert.Equal(65, ((PdfNumber)font[PdfName.FirstChar]).LongValue);
        Assert.Equal(65, ((PdfNumber)font[PdfName.LastChar]).LongValue);

        var widths = (PdfArray)font[PdfName.Widths];
        Assert.Equal(750, ((PdfNumber)widths[0]).DoubleValue);
    }

    /// <summary>The font's own <c>/FontMatrix</c> travels to the PDF font, verbatim.</summary>
    [Fact]
    public void Type3FontMatrix_IsEmittedOnTheFont()
    {
        var r = Convert(SquareFont + UseIt);

        var matrix = (PdfArray)OnlyFont(r)[PdfName.FontMatrix];
        Assert.Equal(0.001, ((PdfNumber)matrix[0]).DoubleValue, 6);
        Assert.Equal(0.001, ((PdfNumber)matrix[3]).DoubleValue, 6);
    }

    /// <summary>
    /// …and is therefore <b>not</b> multiplied into the text matrix. It used to be: the interpreter's
    /// size-matrix key is literally <c>"FontMatrix"</c>, so <c>12 scalefont</c> produced
    /// <c>0.012 0 0 0.012 0 0 Tm</c> — the whole document at 1/1000 scale, with a clean report.
    /// </summary>
    [Fact]
    public void Type3FontMatrix_IsNotAlsoMultipliedIntoTheTextMatrix()
    {
        var r = Convert(SquareFont + UseIt);

        Assert.Equal(new double[] { 12, 0, 0, 12, 0, 0 },
                     ContentStreamAssert.FirstOperandsOf(r.Content, "Tm"));
    }

    /// <summary>
    /// The encoding carries <c>/Differences</c> and <b>no</b> <c>/BaseEncoding</c>. Both renderers
    /// fall back to the base encoding for an uncovered code, so declaring WinAnsi would make such a
    /// code resolve to a WinAnsi glyph name and draw a notdef box instead of nothing.
    /// </summary>
    [Fact]
    public void Type3Encoding_HasDifferencesAndNoBaseEncoding()
    {
        var r = Convert(SquareFont + UseIt);

        var encoding = (PdfDictionary)OnlyFont(r)[PdfName.Encoding];
        Assert.Null(encoding[PdfName.BaseEncoding]);

        var differences = (PdfArray)encoding[PdfName.Differences];
        Assert.Equal(65, ((PdfNumber)differences[0]).LongValue);
        Assert.Equal("square", ((PdfName)differences[1]).Value);
    }

    /// <summary>An ordinary font is untouched — <c>scalefont</c> still accumulates into its size matrix.</summary>
    [Fact]
    public void AnOrdinaryFont_StillScalesThroughItsSizeMatrix()
    {
        var r = Convert("/Helvetica findfont 12 scalefont setfont 0 0 moveto (A) show");

        Assert.Equal(new double[] { 12, 0, 0, 12, 0, 0 },
                     ContentStreamAssert.FirstOperandsOf(r.Content, "Tm"));
        Assert.Equal("Type1", ((PdfName)OnlyFont(r)[PdfName.Subtype]).Value);
    }

    /// <summary>A font whose glyphs all convert reports nothing — it did nothing wrong.</summary>
    [Fact]
    public void AWellFormedType3Font_ConvertsCleanly()
    {
        var r = Convert(SquareFont + UseIt);

        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// <b>One font resource per font, not per <c>show</c>.</b> Found by a duplication audit: the
    /// three sibling registrars on <c>PdfResourceCollector</c> all memoise and this one did not, so
    /// three <c>show</c>s in one font produced <c>/F1 /F2 /F3</c> — each carrying only the glyphs of
    /// its own string, and 256 of them exhausting the font-resource cap so every later font reused
    /// the capped name.
    /// </summary>
    [Fact]
    public void RepeatedShows_ShareOneFontResource()
    {
        var r = Convert(SquareFont +
                        "/T3 findfont 12 scalefont setfont " +
                        "0 0 moveto (A) show 0 20 moveto (A) show 0 40 moveto (A) show");

        var fonts = r.Resources[PdfName.Font] as PdfDictionary;
        int count = 0;
        foreach (var _ in fonts) count++;
        Assert.Equal(1, count);

        // …and every Tj names it.
        var tf = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tf")
                                    .Select(o => o.Operands[0]).Distinct().ToArray();
        Assert.Single(tf);
    }

    /// <summary>
    /// A second glyph shown later joins the <b>same</b> font rather than starting another, and the
    /// font's <c>/Widths</c> range grows to cover both codes.
    /// </summary>
    [Fact]
    public void ASecondGlyphJoinsTheSameFont()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /a put Encoding 66 /b put /CharProcs 2 dict def " +
            "CharProcs /a { 700 0 setcharwidth 0 0 100 100 rectfill } put " +
            "CharProcs /b { 400 0 setcharwidth 0 0 50 50 rectfill } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end /T3 exch definefont pop " +
            "/T3 findfont 12 scalefont setfont 0 0 moveto (A) show 0 20 moveto (B) show");

        var font = OnlyFont(r);
        Assert.Equal(65, ((PdfNumber)font[PdfName.FirstChar]).LongValue);
        Assert.Equal(66, ((PdfNumber)font[PdfName.LastChar]).LongValue);

        var widths = (PdfArray)font[PdfName.Widths];
        Assert.Equal(2, widths.Count);
        Assert.Equal(700, ((PdfNumber)widths[0]).DoubleValue);
        Assert.Equal(400, ((PdfNumber)widths[1]).DoubleValue);
    }

    // ==================================================================
    //  Honest failure
    // ==================================================================

    /// <summary>
    /// A glyph declaring no width gets zero <b>and a report</b>. Inventing an advance would pile
    /// every following glyph on one point just as silently.
    /// </summary>
    [Fact]
    public void AGlyphThatDeclaresNoWidth_ReportsAndGetsZero()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /bare put /CharProcs 1 dict def " +
            "CharProcs /bare { 0 0 100 100 rectfill } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " + UseIt);

        Assert.False(r.IsClean);
        Assert.Contains("declared no width", r.LastError);

        var widths = (PdfArray)OnlyFont(r)[PdfName.Widths];
        Assert.Equal(0, ((PdfNumber)widths[0]).DoubleValue);
    }

    /// <summary>A code with no CharProc is reported rather than drawn as something else.</summary>
    [Fact]
    public void ACodeWithNoGlyphProcedure_IsReported()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/CharProcs 1 dict def end /T3 exch definefont pop " + UseIt);

        Assert.False(r.IsClean);
        Assert.Contains("no /BuildGlyph", r.LastError);
    }

    /// <summary>
    /// A glyph procedure that shows text in its own font recurses. It is bounded by the nesting
    /// cap, and the assertion is the guard's own evidence — never a wall clock.
    /// </summary>
    [Fact]
    public void AGlyphProcedureThatRecurses_IsBoundedByTheNestingCap()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /loop put /CharProcs 1 dict def " +
            "CharProcs /loop { 500 0 setcharwidth 0 0 moveto (A) show } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " + UseIt);

        Assert.Contains("MaxType3NestingDepth", r.LastError ?? "");
    }

    /// <summary>
    /// <c>showpage</c> inside a glyph procedure is refused. The child engine has no page sink, so
    /// honouring it would take the fragment-mode reset branch and discard the glyph mid-build.
    /// </summary>
    [Fact]
    public void APageOperatorInsideAGlyphProcedure_IsRefused()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /sq put /CharProcs 1 dict def " +
            "CharProcs /sq { 750 0 setcharwidth 0 0 100 100 rectfill showpage } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " + UseIt);

        Assert.Contains("page-level operator", r.LastError ?? "");

        // The glyph survived the attempt.
        var ops = ContentStreamAssert.Parse(Encoding.ASCII.GetBytes(CharProcText(r, "sq")));
        Assert.Contains("f", ops.Select(o => o.Operator));
    }

    /// <summary>
    /// <b>The only test that can catch the budget bypass.</b> A child interpreter built through the
    /// ordinary constructor gets a <i>fresh</i> <c>WorkBudget</c> — twenty million steps per glyph.
    /// With a low <c>MaxTotalOps</c>, a looping glyph procedure must exhaust the <b>parent's</b>
    /// budget, so the drawing that follows never reaches the stream.
    /// </summary>
    [Fact]
    public void GlyphWork_IsChargedToTheParentBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };

        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/Encoding 256 array def Encoding 65 /spin put /CharProcs 1 dict def " +
            "CharProcs /spin { 500 0 setcharwidth { } loop } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " + UseIt +
            " newpath 0 0 moveto 50 0 lineto 50 50 lineto fill"), options);

        Assert.True(r.BudgetExhausted);
        ContentStreamAssert.DoesNotContainOperator(r.Content, "f");
    }

    /// <summary>
    /// <c>setcharwidth</c> outside a glyph procedure keeps the arity table's old contract: it
    /// consumes its two operands and reports, because there it means the glyph cache.
    /// </summary>
    [Fact]
    public void SetCharWidthOutsideAGlyphProcedure_ConsumesItsOperandsAndReports()
    {
        var r = Convert("clear 1 2 3 4 500 0 setcharwidth count 0 newpath moveto 10 10 lineto stroke");

        Assert.Equal(4.0, ContentStreamAssert.FirstOperandsOf(r.Content, "m")[0]);
        Assert.Contains("setcharwidth", r.UnimplementedOperators);
    }
}
