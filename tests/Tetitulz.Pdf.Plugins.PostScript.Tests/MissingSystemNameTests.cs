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
/// The 2026-08-19 gap round: names and operators real PostScript uses <b>unconditionally</b> that
/// this interpreter did not have, so each raised <c>undefined</c> and cost the program its
/// top-level object — with <c>UnimplementedOperators</c> staying <b>empty</b>, because that channel
/// only ever carried the operators <c>PsSystemDict</c> already knew it lacked.
///
/// <para><b>How these assert, and why nothing weaker would do.</b> A top-level object here is one
/// parsed token, so an <c>undefined</c> abandons only that token and everything after it still
/// runs. "The drawing still happened" is therefore true <i>with the fix reverted</i> and is worth
/// nothing as an assertion. Every test below instead reads a value back out of the emitted stream —
/// the operands of an <c>m</c>, the grey level of a <c>g</c>, the operator that painted, or the
/// operand-stack depth via <c>count</c> — so it fails when the name is missing.</para>
/// </summary>
public sealed class MissingSystemNameTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>
    /// Leaves the top two numbers of the stack in an <c>m</c> operator, so a test can read a
    /// computed pair straight out of the emitted content.
    /// </summary>
    ///
    /// <para>The <c>newpath</c> is load-bearing: <c>pathbbox</c> and <c>clippath</c> leave a path
    /// behind, so without it the probe <c>moveto</c> extends that path instead of starting one, and
    /// the values under test never reach an operator of their own.</para>
    private const string ShowTopTwo = " newpath moveto 0 0 lineto stroke ";

    /// <summary>
    /// The operands of the <b>last</b> occurrence of an operator. The last, not the first: a clip
    /// emits its own path ahead of anything the test draws, so <c>FirstOperandsOf</c> reads the
    /// fixture's setup back rather than its result.
    /// </summary>
    private static double[] LastOperandsOf(byte[] content, string op)
    {
        var found = ContentStreamAssert.Parse(content).Last(o => o.Operator == op);
        return found.Operands
                    .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
    }

    /// <summary>The pair <see cref="ShowTopTwo"/> put into the stream.</summary>
    private static double[] ShownPair(PostScriptConversionResult r) => LastOperandsOf(r.Content, "m");

    /// <summary>
    /// <c>pathbbox</c> pushes <c>llx lly urx ury</c>; this shows the upper-right corner.
    ///
    /// <para>Named variables rather than <c>roll</c> juggling, because a test fixture should not
    /// need a second operator to be correct in order to measure a first. <b>An earlier revision of
    /// this comment claimed <c>4 2 roll</c> "does the opposite of what it reads as", and that was
    /// wrong</b> — <c>roll</c> is right (PLRM's own <c>a b c 3 1 roll → c a b</c>, pinned by
    /// <c>PsInterpreterTests.Roll_RotatesTheTopNOperands</c>), and <c>4 2 roll pop pop</c> does
    /// leave the upper-right corner. The two real causes were both in this file: the assertion read
    /// the <i>first</i> <c>m</c> in the stream, which for a clipping fixture is the clip's own path,
    /// and <see cref="ShowTopTwo"/> was missing its <c>newpath</c>. Recorded rather than quietly
    /// deleted — a comment that blames working code is the thing that makes the next reader
    /// "fix" it.</para>
    /// </summary>
    private const string ShowUpperRight =
        " /ury exch def /urx exch def /lly exch def /llx exch def urx ury " + ShowTopTwo;

    /// <summary>The same, for the lower-left corner.</summary>
    private const string ShowLowerLeft =
        " /ury exch def /urx exch def /lly exch def /llx exch def llx lly " + ShowTopTwo;

    /// <summary>Paints one of two distinguishable greys, so a PostScript boolean is observable.</summary>
    private const string GreyForTrue  = " { 0.25 } { 0.75 } ifelse setgray ";
    private const string PaintTriangle = " newpath 0 0 moveto 10 0 lineto 10 10 lineto fill ";

    private static double GreyOf(PostScriptConversionResult r)
        => ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0];

    // ==================================================================
    //  ISOLatin1Encoding / StandardEncoding
    // ==================================================================

    /// <summary>
    /// The bare name is an array of glyph names. 0xE9 is <c>eacute</c> in ISOLatin1Encoding —
    /// which is the whole reason a document names it.
    /// </summary>
    [Fact]
    public void IsoLatin1Encoding_IsAnArrayOfGlyphNames()
    {
        var r = Convert("ISOLatin1Encoding 233 get /eacute eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// <c>StandardEncoding</c> too, and at a code where the two encodings <b>differ</b> — so this
    /// cannot pass by returning either array for both names.
    /// </summary>
    [Fact]
    public void StandardEncoding_IsAnArrayOfGlyphNames()
    {
        var r = Convert("StandardEncoding 225 get /AE eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// The two are not the same vector. Code 39 is <c>quoteright</c> in StandardEncoding and
    /// <c>quotesingle</c> in ISOLatin1Encoding — the difference that makes an apostrophe come out
    /// typographic or straight.
    /// </summary>
    [Fact]
    public void TheTwoEncodings_DifferAtCode39()
    {
        var r = Convert("StandardEncoding 39 get /quoteright eq " +
                        "ISOLatin1Encoding 39 get /quotesingle eq " +
                        "and" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// A program may legally <c>put</c> into an encoding vector, so one conversion's edit must not
    /// reach the next. The arrays are built per interpreter for exactly this reason.
    /// </summary>
    [Fact]
    public void AnEditToAnEncodingVector_DoesNotSurviveIntoTheNextConversion()
    {
        Convert("ISOLatin1Encoding 233 /zzzMutated put");

        var second = Convert("ISOLatin1Encoding 233 get /eacute eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(second), 3);
    }

    /// <summary>
    /// The canonical re-encoding idiom, written exactly as a real prologue writes it — the form
    /// every existing re-encoding test avoids by supplying the array by hand. It must reach the
    /// PDF font as a <c>/Differences</c>, and must not report <c>ISOLatin1Encoding</c> as missing.
    /// </summary>
    [Fact]
    public void TheCanonicalReEncodingIdiom_ReachesTheFontAsDifferences()
    {
        var r = Convert(
            "/Helvetica findfont dup length dict begin " +
            "{1 index /FID ne {def} {pop pop} ifelse} forall " +
            "/Encoding ISOLatin1Encoding def " +
            "currentdict end /Helv-ISO exch definefont pop " +
            "/Helv-ISO findfont 12 scalefont setfont " +
            @"newpath 20 20 moveto (\351) show");

        Assert.DoesNotContain("ISOLatin1Encoding", r.UnimplementedOperators);

        Assert.NotNull(r.Resources);
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        Assert.NotNull(fonts);

        PdfDictionary font = null;
        foreach (var kv in fonts) { font = (PdfDictionary)kv.Value; break; }
        Assert.NotNull(font);

        var encoding = font[PdfName.Encoding] as PdfDictionary;
        Assert.NotNull(encoding);

        var differences = (PdfArray)encoding[PdfName.Differences];
        Assert.NotNull(differences);

        // 0xE9 is eacute in ISOLatin1Encoding, and it is the byte the fixture shows.
        var names = new List<string>();
        for (int k = 0; k < differences.Count; k++)
            if (differences[k] is PdfName n) names.Add(n.Value);

        Assert.Contains("eacute", names);
    }

    /// <summary>
    /// <c>findencoding</c> answers with the array the PLRM specifies. It used to push the key back
    /// as a name for every input, which is a <c>typecheck</c> at the first <c>get</c>.
    /// </summary>
    [Fact]
    public void FindEncoding_ReturnsTheArray_NotTheName()
    {
        var r = Convert("/ISOLatin1Encoding findencoding 233 get /eacute eq" +
                        GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// …and still passes a CJK CMap name through as a name, which is what <c>composefont</c>
    /// recognises it by. Narrowing the passthrough would break the composite-font path.
    /// </summary>
    [Fact]
    public void FindEncoding_PassesANonEncodingNameThroughAsAName()
    {
        var r = Convert("/90ms-RKSJ-H findencoding type /nametype eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    // ==================================================================
    //  FontDirectory, errordict, $error
    // ==================================================================

    /// <summary>
    /// <c>FontDirectory /Foo known</c> is the standard "define it once" guard, and it must see a
    /// font the program has defined.
    /// </summary>
    [Fact]
    public void FontDirectory_SeesAFontTheProgramDefined()
    {
        var r = Convert(
            "/Helvetica findfont /MyFont exch definefont pop " +
            "FontDirectory /MyFont known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>And must not claim a font that was never defined.</summary>
    [Fact]
    public void FontDirectory_DoesNotClaimAFontThatWasNeverDefined()
    {
        var r = Convert("FontDirectory /NeverDefined known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 3);
    }

    /// <summary><c>undefinefont</c> takes it out again.</summary>
    [Fact]
    public void FontDirectory_ForgetsAnUndefinedFont()
    {
        var r = Convert(
            "/Helvetica findfont /MyFont exch definefont pop " +
            "/MyFont undefinefont " +
            "FontDirectory /MyFont known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 3);
    }

    /// <summary>
    /// A prologue installing its own error handler must not lose the object to <c>undefined</c>.
    /// Asserted on the dictionary really being there, not merely on the conversion surviving.
    /// </summary>
    [Fact]
    public void ErrorDict_IsARealDictionaryAHandlerCanBeInstalledIn()
    {
        var r = Convert("errordict /handleerror { } put " +
                        "errordict /handleerror known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
        Assert.DoesNotContain("errordict", r.UnimplementedOperators);
    }

    /// <summary><c>$error /newerror false put</c> is the other half of the same idiom.</summary>
    [Fact]
    public void DollarError_IsARealDictionary()
    {
        var r = Convert("$error /newerror false put " +
                        "$error /newerror known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// <c>statusdict</c> stays undefined on purpose — everything reached through it is a device
    /// operator this interpreter does not have, so defining it moves the failure one token later
    /// and costs <c>/statusdict where</c> its working fallback branch. Pinned so the next reader
    /// does not "complete the set".
    /// </summary>
    [Fact]
    public void StatusDict_StaysUndefined()
    {
        var r = Convert("/statusdict where" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 3);
    }

    // ==================================================================
    //  clippath
    // ==================================================================

    /// <summary>
    /// A rectangular clip is tracked exactly, so <c>clippath pathbbox</c> gives that rectangle
    /// back. This is the idiom the operator exists for.
    /// </summary>
    [Fact]
    public void ClipPath_AfterARectangularClip_ReturnsThatRectangle()
    {
        var r = Convert("newpath 10 20 moveto 110 20 lineto 110 70 lineto 10 70 lineto closepath " +
                        "clip newpath clippath pathbbox" + ShowUpperRight);

        Assert.Equal(new[] { 110.0, 70.0 }, ShownPair(r));
    }

    /// <summary>The clip is reported in the <b>current</b> user space, not the one it was set in.</summary>
    [Fact]
    public void ClipPath_IsExpressedInTheCurrentUserSpace()
    {
        // Clip to 0..100 square, then halve the unit: the same region is 0..200 in the new space.
        var r = Convert("newpath 0 0 moveto 100 0 lineto 100 100 lineto 0 100 lineto closepath " +
                        "clip newpath 0.5 0.5 scale clippath pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(200.0, m[0], 3);
        Assert.Equal(200.0, m[1], 3);
    }

    /// <summary>Two clips intersect, and the tracked region intersects with them.</summary>
    [Fact]
    public void ClipPath_AfterTwoClips_ReturnsTheIntersection()
    {
        var r = Convert("newpath 0 0 moveto 100 0 lineto 100 100 lineto 0 100 lineto closepath clip " +
                        "newpath 0 0 moveto 60 0 lineto 60 40 lineto 0 40 lineto closepath clip " +
                        "newpath clippath pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(60.0, m[0], 3);
        Assert.Equal(40.0, m[1], 3);
    }

    /// <summary>
    /// With nothing clipped, the answer is the document's declared <c>%%BoundingBox</c> — which is
    /// what a program calling <c>clippath pathbbox</c> first thing is looking for.
    /// </summary>
    [Fact]
    public void ClipPath_WithNoClip_AnswersWithTheDeclaredBoundingBox()
    {
        var r = Convert("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 10 20 200 130\n%%EndComments\n" +
                        "clippath pathbbox" + ShowUpperRight);

        // The fixture really did reach the DSC scanner.
        Assert.NotNull(r.BoundingBox);

        var m = ShownPair(r);
        Assert.Equal(200.0, m[0], 3);
        Assert.Equal(130.0, m[1], 3);
    }

    /// <summary><c>initclip</c> puts the answer back to the page.</summary>
    [Fact]
    public void ClipPath_AfterInitClip_AnswersWithThePageAgain()
    {
        var r = Convert("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 300 400\n%%EndComments\n" +
                        "newpath 0 0 moveto 10 0 lineto 10 10 lineto 0 10 lineto closepath clip " +
                        "initclip newpath clippath pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(300.0, m[0], 3);
        Assert.Equal(400.0, m[1], 3);
    }

    /// <summary>
    /// A curved clip cannot be kept exactly, and the bounding box is the documented fallback —
    /// which is the exact answer <c>pathbbox</c> wants anyway.
    /// </summary>
    [Fact]
    public void ClipPath_AfterACurvedClip_AnswersWithItsBoundingBox()
    {
        var r = Convert("newpath 0 0 moveto 20 80 60 80 80 0 curveto closepath clip " +
                        "newpath clippath pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(80.0, m[0], 3);
        Assert.Equal(80.0, m[1], 3);
    }

    // ==================================================================
    //  strokepath
    // ==================================================================

    /// <summary>
    /// <c>strokepath fill</c> paints the stroke, so it must emit <c>S</c> and must not emit
    /// <c>f</c>. Filling the un-outlined path would paint a zero-area sliver, i.e. nothing.
    /// </summary>
    [Fact]
    public void StrokePath_Fill_EmitsAStrokeAndNotAFill()
    {
        var r = Convert("4 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath fill");

        Assert.Contains("S", ContentStreamAssert.Operators(r.Content));
        ContentStreamAssert.DoesNotContainOperator(r.Content, "f");
    }

    /// <summary>The line style rides along, because it is read from the same graphics state.</summary>
    [Fact]
    public void StrokePath_Fill_KeepsTheLineWidth()
    {
        var r = Convert("7 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath fill");

        Assert.Equal(new[] { 7.0 }, ContentStreamAssert.FirstOperandsOf(r.Content, "w"));
    }

    /// <summary><c>pathbbox</c> after <c>strokepath</c> includes half the line width on each side.</summary>
    [Fact]
    public void StrokePath_PathBBox_IncludesHalfTheLineWidth()
    {
        var r = Convert("10 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath pathbbox" +
                        ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(105.0, m[0], 3);
        Assert.Equal(5.0, m[1], 3);
    }

    /// <summary>…and grows the lower-left corner outwards by the same half width, negative.</summary>
    [Fact]
    public void StrokePath_PathBBox_GrowsTheLowerLeftCornerOutwards()
    {
        var r = Convert("10 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath pathbbox" +
                        ShowLowerLeft);

        var m = ShownPair(r);
        Assert.Equal(-5.0, m[0], 3);
        Assert.Equal(-5.0, m[1], 3);
    }

    /// <summary>Without <c>strokepath</c> the same path measures with no padding at all.</summary>
    [Fact]
    public void PathBBox_WithoutStrokePath_IsUnpadded()
    {
        var r = Convert("10 setlinewidth newpath 0 0 moveto 100 0 lineto pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(100.0, m[0], 3);
        Assert.Equal(0.0, m[1], 3);
    }

    /// <summary>
    /// <c>strokepath clip</c> has no PDF expression, and clipping to the un-outlined path would
    /// hide everything after it. It is skipped, so no clip reaches the stream.
    /// </summary>
    [Fact]
    public void StrokePath_Clip_EmitsNoClip()
    {
        var r = Convert("4 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath clip " +
                        "newpath 10 10 moveto 20 20 lineto 20 10 lineto fill");

        ContentStreamAssert.DoesNotContainOperator(r.Content, "W");
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>Extending the path afterwards drops the outline rather than stroking the addition.</summary>
    [Fact]
    public void StrokePath_ThenMorePath_RevertsToAnOrdinaryFill()
    {
        var r = Convert("4 setlinewidth newpath 0 0 moveto 100 0 lineto strokepath " +
                        "100 50 lineto fill");

        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
        ContentStreamAssert.DoesNotContainOperator(r.Content, "S");
    }

    // ==================================================================
    //  reversepath
    // ==================================================================

    /// <summary>
    /// The path comes back in the opposite direction: what was the last point is now the first.
    /// Under the non-zero winding rule this is the difference between a hole and a solid.
    /// </summary>
    [Fact]
    public void ReversePath_ReversesTheDirectionOfASubpath()
    {
        var r = Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto reversepath stroke");

        Assert.Equal(new[] { 10.0, 10.0 }, ContentStreamAssert.FirstOperandsOf(r.Content, "m"));

        var ops = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "l").ToArray();
        Assert.Equal("10 0 l", ops[0].ToString());
        Assert.Equal("0 0 l",  ops[1].ToString());
    }

    /// <summary>
    /// A Bézier reversed by swapping its control points is the identical curve, so the reversal is
    /// exact rather than a re-fit.
    /// </summary>
    [Fact]
    public void ReversePath_ReversesACurveExactly()
    {
        var r = Convert("newpath 0 0 moveto 1 2 3 4 5 6 curveto reversepath stroke");

        Assert.Equal(new[] { 5.0, 6.0 }, ContentStreamAssert.FirstOperandsOf(r.Content, "m"));
        Assert.Equal(new[] { 3.0, 4.0, 1.0, 2.0, 0.0, 0.0 },
                     ContentStreamAssert.FirstOperandsOf(r.Content, "c"));
    }

    /// <summary>A closed subpath stays closed.</summary>
    [Fact]
    public void ReversePath_KeepsAClosedSubpathClosed()
    {
        var r = Convert("newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath reversepath fill");

        Assert.Contains("h", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>Every subpath is reversed, not just the last.</summary>
    [Fact]
    public void ReversePath_ReversesEverySubpath()
    {
        var r = Convert("newpath 0 0 moveto 10 0 lineto " +
                        "50 50 moveto 60 50 lineto reversepath stroke");

        var ops = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "m").ToArray();
        Assert.Equal("10 0 m", ops[0].ToString());
        Assert.Equal("60 50 m", ops[1].ToString());
    }

    // ==================================================================
    //  rectfill / rectstroke / rectclip
    // ==================================================================

    /// <summary>
    /// The rectangle is built the way the PLRM writes it — <c>x y moveto w 0 rlineto 0 h rlineto
    /// w neg 0 rlineto closepath</c> — and filled. Generated PostScript paints rules, cells and
    /// backgrounds with this by the hundred.
    /// </summary>
    [Fact]
    public void RectFill_PaintsTheRectangle()
    {
        var r = Convert("10 20 100 50 rectfill");

        Assert.Equal(new[] { 10.0, 20.0 }, ShownPair(r));

        var lines = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "l").ToArray();
        Assert.Equal("110 20 l", lines[0].ToString());
        Assert.Equal("110 70 l", lines[1].ToString());
        Assert.Equal("10 70 l",  lines[2].ToString());
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>
    /// PLRM defines it as <c>gsave newpath … fill grestore</c>, so the current path survives. It
    /// has to be saved explicitly here: this engine keeps the path outside the graphics-state
    /// stack, so its <c>gsave</c> does not save one.
    /// </summary>
    [Fact]
    public void RectFill_LeavesTheCurrentPathAlone()
    {
        var r = Convert("newpath 5 5 moveto 15 5 lineto 10 20 100 50 rectfill stroke");

        // The last path emitted is the stroke's — the one the program was building.
        Assert.Equal(new[] { 5.0, 5.0 }, ShownPair(r));
        Assert.Contains("S", ContentStreamAssert.Operators(r.Content));
    }

    /// <summary>An array operand paints one rectangle per four numbers.</summary>
    [Fact]
    public void RectFill_AcceptsAnArrayOfRectangles()
    {
        var r = Convert("[ 0 0 10 10  50 60 10 10 ] rectfill");

        var moves = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "m").ToArray();
        Assert.Equal("0 0 m",   moves[0].ToString());
        Assert.Equal("50 60 m", moves[1].ToString());
    }

    /// <summary>An array that is not a multiple of four is a <c>rangecheck</c>, not a guess.</summary>
    [Fact]
    public void RectFill_RefusesAnArrayThatIsNotAMultipleOfFour()
    {
        var r = Convert("[ 1 2 3 ] rectfill" + PaintTriangle);

        // Exactly one fill — the triangle after it. The malformed rectfill painted nothing.
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "f"));
        Assert.False(r.IsClean);
    }

    /// <summary><c>rectstroke</c> strokes rather than fills.</summary>
    [Fact]
    public void RectStroke_EmitsAStroke()
    {
        var r = Convert("10 20 100 50 rectstroke");

        Assert.Contains("S", ContentStreamAssert.Operators(r.Content));
        ContentStreamAssert.DoesNotContainOperator(r.Content, "f");
    }

    /// <summary>
    /// Its optional matrix operand transforms the pen, so it is concatenated for the stroke and
    /// gone afterwards. Six elements is what tells a matrix from a rectangle array — a rectangle
    /// array is always a multiple of four.
    /// </summary>
    [Fact]
    public void RectStroke_ConcatenatesItsMatrixOperandForTheStrokeOnly()
    {
        var r = Convert("10 20 100 50 [ 2 0 0 2 0 0 ] rectstroke");

        Assert.Equal(new[] { 2.0, 0.0, 0.0, 2.0, 0.0, 0.0 },
                     ContentStreamAssert.FirstOperandsOf(r.Content, "cm"));
        ContentStreamAssert.AssertBalancedSaveRestore(r.Content);
    }

    /// <summary>
    /// <c>rectclip</c> ends with an implicit <c>newpath</c> — unlike <c>clip</c>, which leaves the
    /// path intact. So the following <c>stroke</c> has nothing to stroke.
    /// </summary>
    [Fact]
    public void RectClip_ClearsTheCurrentPath()
    {
        var r = Convert("newpath 5 5 moveto 15 5 lineto 0 0 100 100 rectclip stroke");

        Assert.Contains("W", ContentStreamAssert.Operators(r.Content));
        ContentStreamAssert.DoesNotContainOperator(r.Content, "S");
    }

    /// <summary>And it really does clip: <c>clippath</c> afterwards reports the rectangle.</summary>
    [Fact]
    public void RectClip_IntersectsTheClip()
    {
        var r = Convert("0 0 60 40 rectclip clippath pathbbox" + ShowUpperRight);

        var m = ShownPair(r);
        Assert.Equal(60.0, m[0], 3);
        Assert.Equal(40.0, m[1], 3);
    }

    // ==================================================================
    //  User objects
    // ==================================================================

    /// <summary>
    /// <c>execuserobject</c> runs a stored procedure, through the same entry <c>exec</c> uses — so
    /// it pushes a frame rather than recursing.
    /// </summary>
    [Fact]
    public void UserObject_RoundTripsAndExecutes()
    {
        var r = Convert("1 { 0.25 setgray } defineuserobject 1 execuserobject" + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary><c>undefineuserobject</c> takes it away again, so the procedure does not run.</summary>
    [Fact]
    public void UserObject_UndefineStopsItExecuting()
    {
        var r = Convert("0.75 setgray 1 { 0.25 setgray } defineuserobject " +
                        "1 undefineuserobject 1 execuserobject" + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 3);
    }

    /// <summary>
    /// One document's user objects must not be visible to the next — the same per-conversion reset
    /// rule every other slot in this assembly follows.
    /// </summary>
    [Fact]
    public void UserObjects_DoNotSurviveIntoTheNextConversion()
    {
        Convert("1 { 0.25 setgray } defineuserobject");

        var second = Convert("0.75 setgray 1 execuserobject" + PaintTriangle);

        Assert.Equal(0.75, GreyOf(second), 3);
    }

    // ==================================================================
    //  The remaining readbacks
    // ==================================================================

    /// <summary>
    /// Stroke adjustment is carried as the <c>/SA</c> a reader is given, so <c>currentstrokeadjust</c>
    /// answers what <c>setstrokeadjust</c> set — false until then.
    /// </summary>
    [Fact]
    public void CurrentStrokeAdjust_AnswersWhatWasSet()
    {
        Assert.Equal(0.25, GreyOf(Convert("true setstrokeadjust currentstrokeadjust" + GreyForTrue + PaintTriangle)), 3);
        Assert.Equal(0.75, GreyOf(Convert("currentstrokeadjust" + GreyForTrue + PaintTriangle)), 3);
    }

    /// <summary>
    /// <c>GlobalFontDirectory</c> is the same dictionary, because this interpreter has one VM.
    /// Two would mean a font defined through one being invisible through the other.
    /// </summary>
    [Fact]
    public void GlobalFontDirectory_SeesTheSameFonts()
    {
        var r = Convert("/Helvetica findfont /MyFont exch definefont pop " +
                        "GlobalFontDirectory /MyFont known" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    // ==================================================================
    //  Arity-table completeness
    // ==================================================================

    /// <summary>
    /// Each newly recognised operator consumes exactly the operands the PLRM says and pushes
    /// exactly the results, measured with <c>count</c>.
    ///
    /// <para><b>This is what separates "recognised" from "undefined", and nothing else does.</b> A
    /// top-level object is one token here, so an undefined name abandons only itself and the
    /// drawing after it still appears — but the interpreter's recovery truncates the operand stack
    /// back to its depth before that token, so the stack depth is exactly what tells the two
    /// apart. Under the old build every case below reads back 4.</para>
    ///
    /// <para><b>An operator that consumes and pushes nothing cannot be tested this way</b>, and
    /// finding that out cost a mutation round: recovery restores the stack to exactly the depth a
    /// (0, 0) operator would have left it at, so <c>nulldevice</c> reads back 4 whether it is
    /// registered or not. It is covered by <see cref="ZeroEffectOperator_IsRecognisedRatherThanUndefined"/>
    /// instead — a case that passes for the wrong reason is banned pattern 3, not a free extra.
    /// (<c>clipsave</c> and <c>cliprestore</c> are carried now: <c>ClipHistoryTests</c>.)</para>
    /// </summary>
    [Theory]
    // name,                    operands the PLRM consumes, results it pushes
    [InlineData("currentcolorscreen",   0, 12)]
    [InlineData("currentcolortransfer", 0, 4)]
    [InlineData("currentsmoothness",    0, 1)]
    [InlineData("currentcachelimit",    0, 1)]
    [InlineData("currentvmthreshold",   0, 1)]
    [InlineData("cachestatus",          0, 7)]
    [InlineData("gstate",               0, 1)]   // currentgstate and setgstate take a gstate: ClipHistoryTests
    [InlineData("execform",             1, 0)]
    [InlineData("setuserparams",        1, 0)]
    [InlineData("currentuserparams",    0, 1)]
    [InlineData("setsystemparams",      1, 0)]
    [InlineData("currentsystemparams",  0, 1)]
    [InlineData("setdevparams",         2, 0)]
    [InlineData("currentdevparams",     1, 1)]
    [InlineData("setobjectformat",      1, 0)]
    [InlineData("currentobjectformat",  0, 1)]
    [InlineData("fileposition",         1, 1)]
    [InlineData("setfileposition",      2, 0)]
    [InlineData("filenameforall",       3, 0)]
    [InlineData("deletefile",           1, 0)]
    [InlineData("renamefile",           2, 0)]
    [InlineData("printobject",          2, 0)]
    [InlineData("writeobject",          3, 0)]
    [InlineData("setstrokeadjust",      1, 0)]
    [InlineData("currentstrokeadjust",  0, 1)]
    [InlineData("setcharwidth",         2, 0)]
    [InlineData("read",                 1, 1)]   // a non-file reads as at its end: false
    [InlineData("write",                2, 0)]
    [InlineData("=",                    1, 0)]
    [InlineData("==",                   1, 0)]
    public void RecognisedOperator_HasThePlrmStackEffect(string op, int pops, int pushes)
    {
        const int Seed = 4;
        var r = Convert($"clear 1 2 3 4 {op} count 0" + ShowTopTwo);

        Assert.Equal(Seed - pops + pushes,
                     ShownPair(r)[0]);
    }

    /// <summary>
    /// An operator with no stack effect at all is distinguished from an undefined name by <b>which
    /// diagnostic it produced</b>, since the stack cannot tell them apart — an unhandled error
    /// restores the operand stack to the depth the operator would have left it at anyway.
    ///
    /// <para>"Recognised" reports <i>is not implemented; its operands were consumed</i>; an
    /// undefined name reports that the object was <i>abandoned</i>. Both now land in
    /// <c>UnimplementedOperators</c>, so the list alone does not separate them either.</para>
    /// </summary>
    [Theory]
    [InlineData("nulldevice")]
    public void ZeroEffectOperator_IsRecognisedRatherThanUndefined(string op)
    {
        var r = Convert(op);

        Assert.Contains(op, r.UnimplementedOperators);
        Assert.Contains("is not implemented", r.LastError);
        Assert.DoesNotContain("abandoned the current top-level object", r.LastError);
    }

    /// <summary>
    /// The user-path and hit-test families must stay <b>undefined</b>, because a prologue probes
    /// for them and has a fallback that draws correctly. Registering them is the mistake the
    /// completeness sweep above invites, so it is pinned.
    /// </summary>
    [Theory]
    [InlineData("ufill")]
    [InlineData("ustroke")]
    [InlineData("uappend")]
    [InlineData("upath")]
    [InlineData("infill")]
    [InlineData("ineofill")]
    [InlineData("instroke")]
    [InlineData("inustroke")]
    [InlineData("ustrokepath")]
    [InlineData("ucache")]
    public void UserPathAndHitTestFamilies_StayUndefined(string op)
    {
        var r = Convert($"/{op} where" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 3);
    }

    // ==================================================================
    //  Base-85 string literals
    // ==================================================================

    /// <summary>
    /// <c>&lt;~ … ~&gt;</c> is an ASCII base-85 string (PLRM §3.2.2), not a hex string.
    ///
    /// <para><b>Falling through to the hex scanner did not fail — it succeeded with the wrong
    /// bytes.</b> Base-85 uses the whole printable range, so the hex scanner kept whichever
    /// characters happened to look hexadecimal and dropped the rest, leaving a string of the right
    /// sort of length made of unrelated bytes. That is why this asserts the decoded <i>value</i>:
    /// a length assertion passes on the corruption, which is how the first draft of this test
    /// passed against the unfixed scanner.</para>
    /// </summary>
    [Fact]
    public void Base85String_DecodesToItsBytes()
    {
        // "Hell" -> 87cUR (a full group), "o" -> DZ (a short final group of k=2 giving k-1 bytes).
        var r = Convert("<~87cURDZ~> (Hello) eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary><c>z</c> abbreviates a whole group of four zero bytes.</summary>
    [Fact]
    public void Base85String_ExpandsZToFourZeroBytes()
    {
        var r = Convert("<~z~> length 4 eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// Whitespace is ignored anywhere inside, which is the point of the encoding — it has to
    /// survive the line-wrapping a mail gateway or print spooler applies.
    /// </summary>
    [Fact]
    public void Base85String_IgnoresWhitespace()
    {
        var r = Convert("<~87c\n UR\r\nDZ~> (Hello) eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>A hex string is still a hex string — the new branch is entered only on <c>~</c>.</summary>
    [Fact]
    public void HexString_IsUnaffected()
    {
        var r = Convert("<48656C6C6F> (Hello) eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 3);
    }

    /// <summary>
    /// A character outside <c>!</c>..<c>u</c> is reported rather than silently folded into the
    /// value — <c>{</c> is 123, past the top of the alphabet.
    /// </summary>
    [Fact]
    public void Base85String_ReportsAnInvalidCharacter()
    {
        var r = Convert("<~87cU{RDZ~> pop");   // '{' is 123, past the top of the alphabet

        Assert.False(r.IsClean);
        Assert.Contains("base-85", r.LastError);
    }

    // ==================================================================
    //  The reporting channel
    // ==================================================================

    /// <summary>
    /// An undefined name now reaches <c>UnimplementedOperators</c>. It used to leave the list empty
    /// while <c>IsClean</c> went false, so a caller reading the documented channel could not learn
    /// what the drawing was missing.
    /// </summary>
    [Fact]
    public void AnUndefinedName_IsRecordedInUnimplementedOperators()
    {
        var r = Convert("zzzNotARealOperator 0 0 moveto 10 10 lineto stroke");

        Assert.Contains("zzzNotARealOperator", r.UnimplementedOperators);
        Assert.False(r.IsClean);
    }

    /// <summary>
    /// A name inside <c>{ … } stopped</c> is a deliberate feature probe answering "no". Recording
    /// it would fill the list with names that are not gaps — every prologue probes for several.
    /// </summary>
    [Fact]
    public void AnUndefinedNameHandledByStopped_IsNotRecorded()
    {
        var r = Convert("{ zzzProbedFeature } stopped pop 0 0 moveto 10 10 lineto stroke");

        Assert.DoesNotContain("zzzProbedFeature", r.UnimplementedOperators);
    }

    /// <summary>
    /// <c>undefined</c> is overloaded in this interpreter: it is also what an <i>implemented</i>
    /// operator raises for "no current point". Those must not be reported as missing operators, or
    /// the list stops meaning anything.
    /// </summary>
    [Fact]
    public void UndefinedRaisedByAnImplementedOperator_IsNotRecordedAsMissing()
    {
        var r = Convert("newpath 5 5 rlineto");

        Assert.DoesNotContain("rlineto", r.UnimplementedOperators);
        Assert.False(r.IsClean);   // still reported — it is a real failure, just not a missing name
    }

    /// <summary>
    /// The list is deduplicated, so a hostile file cannot make the diagnostics the attack. One
    /// undefined name repeated across many top-level objects is one entry.
    /// </summary>
    [Fact]
    public void RepeatedUndefinedNames_AreRecordedOnce()
    {
        var r = Convert(string.Concat(System.Linq.Enumerable.Repeat("zzzRepeated ", 200)));

        Assert.Single(r.UnimplementedOperators, n => n == "zzzRepeated");
    }

    /// <summary>The operators fixed by this round no longer appear as gaps at all.</summary>
    [Theory]
    [InlineData("clippath",   "clippath pathbbox pop pop pop pop")]
    [InlineData("strokepath", "newpath 0 0 moveto 10 0 lineto strokepath fill")]
    [InlineData("reversepath","newpath 0 0 moveto 10 0 lineto reversepath stroke")]
    [InlineData("findencoding", "/ISOLatin1Encoding findencoding pop")]
    public void ClosedGaps_AreNoLongerReportedAsMissing(string name, string program)
    {
        var r = Convert(program);

        Assert.DoesNotContain(name, r.UnimplementedOperators);
    }

    // ------------------------------------------------------------------
    //  $error and handleerror
    // ------------------------------------------------------------------

    /// <summary>
    /// An error caught by <c>stopped</c> leaves its record in <c>$error</c> (PLRM §3.11.2:
    /// <c>newerror</c>, <c>errorname</c>, <c>command</c>), which is how a program tells an error
    /// from a plain <c>stop</c>. The dictionary existed and stayed empty, so the idiom
    /// <c>stopped { $error /newerror get { … } if } if</c> raised <c>undefined</c> on the
    /// <c>get</c> and lost its object.
    /// </summary>
    [Fact]
    public void Stopped_RecordsTheErrorInErrorDict()
    {
        var r = Convert("{ 1 0 div } stopped pop $error /newerror get" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 6);
    }

    [Fact]
    public void Stopped_RecordsTheErrorName()
    {
        var r = Convert("{ 1 0 div } stopped pop $error /errorname get /undefinedresult eq" +
                        GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 6);
    }

    /// <summary>A plain <c>stop</c> is not an error: <c>newerror</c> stays false.</summary>
    [Fact]
    public void Stop_DoesNotSetNewError()
    {
        var r = Convert("{ stop } stopped pop $error /newerror get" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.75, GreyOf(r), 6);
    }

    /// <summary>
    /// <c>handleerror</c> is what a program calls once it has seen <c>newerror</c>. On a printer it
    /// prints the error; here the error was already reported, so it clears <c>newerror</c> and the
    /// procedure around it runs on — where an undefined name abandoned that procedure whole.
    /// </summary>
    [Fact]
    public void HandleError_IsDefined_SoTheProcedureAroundItCompletes()
    {
        var r = Convert("{ { 1 0 div } stopped { handleerror } if" + PaintTriangle + "} exec " +
                        "$error /newerror get" + GreyForTrue + PaintTriangle);

        // Two fills: the one inside the procedure (black, so the FIRST `g` is 0) and the probe's.
        Assert.Equal(2, ContentStreamAssert.Count(r.Content, "f"));
        Assert.Equal(0.75, LastOperandsOf(r.Content, "g")[0], 6);
    }

    // ------------------------------------------------------------------
    //  findresource / resourcestatus for fonts and encodings
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/Times-Bold /Font findresource</c> is the Level 2 spelling of <c>findfont</c>, and
    /// <c>/ISOLatin1Encoding /Encoding findresource</c> of <c>findencoding</c>. Both answered a null
    /// from the arity table, so the <c>scalefont</c> or <c>get</c> after them lost the object.
    /// </summary>
    [Fact]
    public void FindResource_Font_IsFindFont()
    {
        var r = Convert("/Times-Bold /Font findresource 12 scalefont setfont newpath 0 0 moveto (A) show");

        Assert.Contains("Tj", ContentStreamAssert.Operators(r.Content));
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        foreach (var kv in fonts)
            Assert.Equal("Times-Bold", ((PdfName)((PdfDictionary)kv.Value)[PdfName.BaseFont]).Value);
        Assert.DoesNotContain("findresource", r.UnimplementedOperators);
    }

    [Fact]
    public void FindResource_Encoding_IsTheEncodingArray()
    {
        var r = Convert("/ISOLatin1Encoding /Encoding findresource 233 get /eacute eq" +
                        GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, GreyOf(r), 6);
    }

    /// <summary>
    /// <c>resourcestatus</c> answers <c>status size true</c> for a font this interpreter can
    /// supply and <c>false</c> otherwise, so a program's own fallback branch runs for a face
    /// nothing here can stand in for.
    /// </summary>
    [Theory]
    [InlineData("/Helvetica", 0.25)]
    [InlineData("/Arial-BoldMT", 0.25)]
    [InlineData("/NoSuchFaceAnywhere", 0.75)]
    public void ResourceStatus_Font_AnswersWhetherTheFontExists(string name, double grey)
    {
        var r = Convert(name + " /Font resourcestatus { pop pop true } { false } ifelse" +
                        GreyForTrue + PaintTriangle);

        Assert.Equal(grey, GreyOf(r), 6);
    }

    /// <summary>
    /// An instance the program never defined is <c>undefinedresource</c> (PLRM §3.9.4): the object is
    /// abandoned and the drawing after it survives — no longer an unimplemented operator answering null
    /// (<c>ResourceTests</c> carries the categories).
    /// </summary>
    [Fact]
    public void FindResource_OfAnUndefinedInstance_IsUndefinedresource()
    {
        var r = Convert("/Foo /ProcSet findresource" + PaintTriangle);

        Assert.DoesNotContain("findresource", r.UnimplementedOperators);
        Assert.Contains("undefinedresource", r.LastError ?? "", System.StringComparison.Ordinal);
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "f"));
    }
}
