// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The MEDIUM findings of the assembly-wide scan (2026-08-13) that belong to this plugin.
///
/// <para>Ten were confirmed against the code and fixed; the eleventh — the undeduplicated
/// <c>gsave</c>-depth report — was already closed by the HIGH round and is pinned by
/// <c>GraphicsStateStackDepthTests</c>, so it has no test here.</para>
///
/// <para><b>Every assertion is on something the fix produces, never on the absence of an
/// exception.</b> Where the finding is about a budget, the evidence is <c>BudgetExhausted</c>
/// against a stated ceiling and a <b>bounded</b> loop, so the test is fast whether or not the fix
/// is present — a wall-clock threshold would be the banned shape and would also make the mutation
/// check take minutes. Where it is about emitted output, the evidence is the parsed content-stream
/// operation, not a substring.</para>
/// </summary>
public sealed class MediumScanRegressionTests
{
    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), options);

    private static (PostScriptConversionResult Result, List<string> Reports) RunCollecting(
        string ps, PostScriptConversionOptions options = null)
    {
        // Thread-filtered — OnEvent is process-wide while the rest of the sink is [ThreadStatic].
        using var collector = DiagnosticCollector.Start();
        var result = Run(ps, options);
        return (result, new List<string>(collector.Messages));
    }

    // ==================================================================
    //  Tz / Tr / Ts are neutralised with Tc and Tw
    // ==================================================================

    /// <summary>
    /// <c>Tz</c>, <c>Tr</c> and <c>Ts</c> are text-state parameters of the <b>graphics</b> state
    /// (§9.3, Table 104) exactly as <c>Tc</c> and <c>Tw</c> are: they persist across <c>BT</c>/
    /// <c>ET</c>, and a form XObject inherits the state in force at the <c>Do</c> (§8.10.1). The
    /// emitter wrote only <c>Tc</c> and <c>Tw</c> while its own doc claimed the text object was
    /// "independent of whatever the host page left in the state", so a page that had done
    /// <c>3 Tr</c> for an invisible OCR layer rendered every converted <c>show</c> invisible, and
    /// the milder <c>50 Tz</c> case halved the width of converted text while the interpreter's pen
    /// advance went on assuming 100.
    ///
    /// <para>Asserted as an ordered <b>sequence</b> inside the text object rather than as three
    /// separate presence checks: the operators have to land between <c>BT</c> and the <c>Tj</c>, or
    /// they set the state for the following text object instead of this one.</para>
    /// </summary>
    [Fact]
    public void ShowText_NeutralisesEveryTextStateParameterTheHostPageCouldHaveLeftSet()
    {
        var result = Run("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        ContentStreamAssert.ContainsSequence(result.Content,
            "BT", "/F1 1 Tf", "0 Tc", "0 Tw", "100 Tz", "0 Tr", "0 Ts");

        // …and they precede the painting operator, which is the point of emitting them at all.
        string[] operators = ContentStreamAssert.Operators(result.Content);
        int rise = Array.IndexOf(operators, "Ts");
        int show = Array.IndexOf(operators, "Tj");
        Assert.True(rise >= 0 && show > rise,
            "the text-state resets must be emitted before the Tj they protect.");
    }

    /// <summary>Every <c>show</c> gets its own reset, not just the first one in the stream.</summary>
    [Fact]
    public void EveryShow_CarriesItsOwnTextStateReset()
    {
        var result = Run("/Helvetica findfont 12 scalefont setfont " +
                         "newpath 0 0 moveto (A) show 0 20 moveto (B) show");

        Assert.Equal(2, ContentStreamAssert.Count(result.Content, "Tj"));
        Assert.Equal(2, ContentStreamAssert.Count(result.Content, "Tz"));
        Assert.Equal(2, ContentStreamAssert.Count(result.Content, "Tr"));
        Assert.Equal(2, ContentStreamAssert.Count(result.Content, "Ts"));
    }

    // ==================================================================
    //  One /FontFile per program, not per encoding
    // ==================================================================

    /// <summary>
    /// A minimal but structurally real Type 1 program — header, <c>eexec</c>, body, 512-zero
    /// trailer, <c>cleartomark</c>.
    /// </summary>
    private static string FontProgram(string name)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: ").Append(name).Append(" 001.000\n");
        sb.Append("/FontName /").Append(name).Append(" def\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n");
        sb.Append("/FontType 1 def\n/Encoding StandardEncoding def\n");
        sb.Append("currentfile eexec\n");
        sb.Append("A1B2C3D4E5F60718293A4B5C6D7E8F90\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        return sb.ToString();
    }

    /// <summary>A 256-slot <c>/Encoding</c> array under <paramref name="arrayName"/>.</summary>
    private static string EncodingArray(string arrayName, int code, string glyph)
    {
        var slots = new string[256];
        for (int i = 0; i < 256; i++) slots[i] = "/.notdef";
        slots[code] = "/" + glyph;
        return "/" + arrayName + " [ " + string.Join(" ", slots) + " ] def ";
    }

    /// <summary>Re-encodes the captured program twice and shows through each.</summary>
    private static string TwoEncodingsOfOneProgram() =>
        FontProgram("SharedProgram") +
        EncodingArray("EncA", 0xE1, "alpha") +
        EncodingArray("EncB", 0xE1, "beta") +
        "/SharedProgram findfont dup length dict begin " +
        "{1 index /FID ne {def} {pop pop} ifelse} forall /Encoding EncA def " +
        "currentdict end /FontA exch definefont pop " +
        "/SharedProgram findfont dup length dict begin " +
        "{1 index /FID ne {def} {pop pop} ifelse} forall /Encoding EncB def " +
        "currentdict end /FontB exch definefont pop " +
        "/FontA findfont 12 scalefont setfont newpath 0 0 moveto (\\341) show " +
        "/FontB findfont 12 scalefont setfont newpath 0 20 moveto (\\341) show ";

    /// <summary>
    /// <c>GetOrAddEmbeddedFont</c> keys its resource cache on (font name, encoding) and built a
    /// brand-new <c>PdfStream</c> over the same <c>byte[]</c> on every miss.
    /// <c>ResourcePromoter</c> memoises by <b>reference identity</b> of the stream, so N encodings
    /// of one program became N promoted objects and the writer emitted the program N times —
    /// bounded only by the 256-font cap times the captured program size, since the emitted-bytes
    /// cap never sees resources.
    ///
    /// <para>Reference equality is the assertion because reference identity is exactly what the
    /// promoter keys on; two structurally equal streams would still be written twice.</para>
    /// </summary>
    [Fact]
    public void TwoEncodingsOfOneEmbeddedProgram_ShareOneFontFileStream()
    {
        var result = Run(TwoEncodingsOfOneProgram());

        var programs = new List<PdfStream>();
        foreach (var entry in FontDictionaries(result.Resources))
            if (entry[PdfName.FontDescriptor] is PdfDictionary descriptor &&
                descriptor[PdfName.FontFile] is PdfStream program)
                programs.Add(program);

        Assert.Equal(2, programs.Count);
        Assert.Same(programs[0], programs[1]);
    }

    /// <summary>
    /// The end-to-end consequence, on the document path where the amplification was actually
    /// billed to the caller's output stream: the captured program's bytes appear <b>once</b>.
    /// </summary>
    [Fact]
    public void TwoEncodingsOfOneEmbeddedProgram_WriteTheProgramBytesOnce()
    {
        using var source = new System.IO.MemoryStream(
            Encoding.Latin1.GetBytes("%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 200\n" +
                                     TwoEncodingsOfOneProgram() + "showpage\n"));
        using var output = new System.IO.MemoryStream();
        PostScriptConversion.ConvertDocument(source, output);

        // The clear-text header line is unique to the captured program and survives verbatim,
        // because the program is passed through byte for byte.
        string pdf = PdfStreamText.Decoded(output.ToArray());
        Assert.Equal(1, CountOccurrences(pdf, "/FontName /SharedProgram def"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static IEnumerable<PdfDictionary> FontDictionaries(PdfDictionary resources)
    {
        if (resources?[PdfName.Font] is not PdfDictionary fonts) yield break;
        foreach (var key in fonts.Keys)
            if (fonts[key] is PdfDictionary font) yield return font;
    }

    // ==================================================================
    //  A truncated eexec section is refused, not embedded
    // ==================================================================

    /// <summary>
    /// <c>CaptureEexecSection</c> produced <c>Length3 == 0</c> both when the program legitimately
    /// omits the 512-zero trailer and when the scan simply ran off the end of the buffer, and
    /// <c>IsWellFormed</c> accepted <c>Length3 &gt;= 0</c> — so the two were indistinguishable and a
    /// truncated program passed the gate that exists to reject exactly this. The reader then got a
    /// <c>/FontFile</c> cut mid-charstring, failed to build the font and drew nothing, while the
    /// caller was told the program had been "embedded verbatim".
    /// </summary>
    [Fact]
    public void TruncatedEexecSection_IsNeitherEmbeddedNorReportedAsASuccess()
    {
        // Header, eexec, 200 bytes of binary, then end of input: no zero trailer, no cleartomark.
        string ps = "%!PS-AdobeFont-1.0: Cut 001.000\n/FontName /Cut def\n" +
                    "/FontMatrix [0.001 0 0 0.001 0 0] readonly def\ncurrentfile eexec\n" +
                    new string('A', 200);

        var (result, reports) = RunCollecting(ps);

        Assert.Contains(reports, r => r.Contains("not well formed") &&
                                      r.Contains("not terminated"));
        Assert.DoesNotContain(reports, r => r.Contains("embedded the document's own font program"));

        foreach (var font in FontDictionaries(result.Resources))
            Assert.Null(font[PdfName.FontDescriptor]);
    }

    /// <summary>
    /// The companion that makes the one above mean something: a program that <i>does</i> end at a
    /// terminator is still embedded. Without this, deleting the whole embedding path would leave
    /// the refusal test green.
    /// </summary>
    [Fact]
    public void ProperlyTerminatedEexecSection_IsStillEmbedded()
    {
        var (result, reports) = RunCollecting(
            FontProgram("Whole") + "/Whole 12 dict definefont pop " +
            "/Whole findfont 12 scalefont setfont newpath 0 0 moveto (A) show");

        Assert.Contains(reports, r => r.Contains("embedded the document's own font program") &&
                                      r.Contains("Whole"));

        var descriptor = (PdfDictionary)FirstFont(result.Resources)[PdfName.FontDescriptor];
        Assert.NotNull(descriptor);
        Assert.NotEmpty(((PdfStream)descriptor[PdfName.FontFile]).RawData);
    }

    // ==================================================================
    //  PLRM colour readback
    // ==================================================================

    /// <summary>
    /// <c>PsColor.ToRgb</c> / <c>ToGray</c> are <b>not</b> the emission path — a CMYK fill goes out
    /// as <c>k</c> and the reader converts it. They are only what the PostScript program reads back
    /// through <c>currentrgbcolor</c> / <c>currentgray</c>, and there PLRM §4.8.3 specifies the
    /// <b>additive</b> rule, not PDF §8.6.4.4's multiplicative one.
    ///
    /// <para>Observed through the emitted stream rather than by reaching into the interpreter:
    /// reading the colour back and immediately setting it makes the numbers the program saw the
    /// operands of the emitted <c>rg</c> / <c>g</c>. An EPS prologue deriving a tint from the
    /// current colour is doing exactly this.</para>
    /// </summary>
    /// <remarks>
    /// <b>Every row discriminates.</b> The first draft carried <c>1 0 0 0.5</c> and <c>0 0 0 0</c>,
    /// and both give the same answer under either rule — <c>(1−1)(1−0.5)</c> and
    /// <c>1−min(1, 1+0.5)</c> are both 0 — so the mutation survived them. A theory row that cannot
    /// tell the two formulas apart is decoration; these four differ in at least one channel, and
    /// the last two exercise the <c>min</c> that the multiplicative form does not have.
    /// </remarks>
    [Theory]
    // c m y k                  PLRM 1−min(1, colorant+k)      PDF (1−colorant)(1−k)
    [InlineData("0.5 0 0 0.5",   new[] { 0.0,  0.5,  0.5  })]  // 0.25, 0.5,  0.5
    [InlineData("0.25 0 0 0.25", new[] { 0.5,  0.75, 0.75 })]  // 0.5625, 0.75, 0.75
    [InlineData("0.2 0.4 0.6 0.1", new[] { 0.7, 0.5, 0.3 })]   // 0.72, 0.54, 0.36
    [InlineData("0.8 0 0 0.5",   new[] { 0.0,  0.5,  0.5  })]  // 0.1,  0.5,  0.5  — the clamp
    public void CurrentRgbColor_FollowsPlrmNotThePdfConversion(string cmyk, double[] expected)
    {
        var result = Run($"{cmyk} setcmykcolor currentrgbcolor setrgbcolor " +
                         "newpath 0 0 moveto 10 10 lineto 0 10 lineto closepath fill");

        double[] rg = ContentStreamAssert.FirstOperandsOf(result.Content, "rg");

        Assert.Equal(expected[0], rg[0], 3);
        Assert.Equal(expected[1], rg[1], 3);
        Assert.Equal(expected[2], rg[2], 3);
    }

    /// <summary>
    /// PLRM's CMYK-to-gray uses 0.3/0.59/0.11 <i>additively</i> — <c>1 0 0 0.5 currentgray</c> is
    /// 0.2, where the PDF rule gave 0.3505. The weights are PLRM's own two-decimal ones, not
    /// BT.601's, which is why the <c>Luma</c> helper is not shared with this arm.
    /// </summary>
    /// <remarks>
    /// <c>0 0 0 1</c> was dropped from this theory after it survived the mutation: both rules give
    /// 0 there, so the row proved nothing. Every row below differs under the two formulas.
    /// </remarks>
    [Theory]
    // c m y k                 PLRM 1−min(1, .3c+.59m+.11y+k)   PDF (1−k)(1−BT.601 luma)
    [InlineData("1 0 0 0.5",    0.2 )]   // 0.3505
    [InlineData("0 1 0 0",      0.41)]   // 0.413
    [InlineData("0.6 0.6 0.6 0.5", 0.0)] // 0.2   — the clamp
    public void CurrentGray_FollowsPlrmNotThePdfConversion(string cmyk, double expected)
    {
        var result = Run($"{cmyk} setcmykcolor currentgray setgray " +
                         "newpath 0 0 moveto 10 10 lineto 0 10 lineto closepath fill");

        Assert.Equal(expected, ContentStreamAssert.FirstOperandsOf(result.Content, "g")[0], 3);
    }

    // ==================================================================
    //  exit / stop do not cross a top-level object boundary
    // ==================================================================

    /// <summary>
    /// Both flags are only ever read inside <c>DrainExecStack</c>'s <c>while</c> body, so a
    /// <b>top-level</b> <c>exit</c> — a defensive shape generated prologues really emit — set the
    /// flag, found an empty exec stack and left it set. The next object to push a frame consumed
    /// it, so the <c>repeat</c> below ran its body <b>zero</b> times with no diagnostic at all,
    /// because <c>HandleExit</c> reports only when it finds no loop.
    ///
    /// <para>The loop counter is turned into a coordinate so the emitted stream carries the answer:
    /// three iterations means a line to (30, 0), none means a line to (0, 0), and the second is not
    /// a degenerate version of the first — it is a different operand.</para>
    /// </summary>
    [Fact]
    public void ATopLevelExit_DoesNotUnwindTheNextLoopTheProgramEnters()
    {
        var (result, reports) = RunCollecting(
            "exit /n 0 def 3 { /n n 1 add def } repeat " +
            "newpath 0 0 moveto n 10 mul 0 lineto stroke");

        double[] lineTo = ContentStreamAssert.FirstOperandsOf(result.Content, "l");
        Assert.Equal(30, lineTo[0], 3);

        Assert.Contains(reports, r => r.Contains("invalidexit"));
    }

    /// <summary>The <c>stop</c> twin: a stale flag made <c>DrainExecStack</c> return early.</summary>
    [Fact]
    public void ATopLevelStop_DoesNotEndTheNextProcedureTheProgramRuns()
    {
        var (result, reports) = RunCollecting(
            "stop /n 0 def 4 { /n n 1 add def } repeat " +
            "newpath 0 0 moveto n 10 mul 0 lineto stroke");

        Assert.Equal(40, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
        Assert.Contains(reports, r => r.Contains("top-level 'stop'"));
    }

    /// <summary>
    /// The behaviour the flags exist for is untouched: an <c>exit</c> <i>inside</i> a loop still
    /// ends that loop. Without this the fix could be "clear the flag unconditionally, everywhere".
    /// </summary>
    [Fact]
    public void ExitInsideALoop_StillEndsThatLoop()
    {
        var result = Run("/n 0 def { /n n 1 add def n 3 ge { exit } if } loop " +
                         "newpath 0 0 moveto n 10 mul 0 lineto stroke");

        Assert.Equal(30, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    // ==================================================================
    //  The colour-component count is part of the graphics state
    // ==================================================================

    /// <summary>
    /// PLRM §4.8 makes the current colour space part of the graphics state, but the component count
    /// that decides <c>setcolor</c>'s arity lived on the interpreter, where <c>gsave</c>/
    /// <c>grestore</c> could not reach it. After the <c>grestore</c> below the space is DeviceGray
    /// again, but the count was still 4, so <c>setcolor</c> popped four operands — and because a
    /// real EPS page body is one top-level procedure, the <c>stackunderflow</c> abandoned the whole
    /// procedure and the rest of the page was never drawn.
    ///
    /// <para>Wrapped in a procedure for exactly that reason: as bare top-level tokens the error
    /// costs one object and the stroke still appears, which would make the test pass unfixed.</para>
    /// </summary>
    [Fact]
    public void SetColorAfterAGrestore_PopsTheRestoredSpacesArity()
    {
        var result = Run(
            "/page { gsave /DeviceCMYK setcolorspace 0 0 0 1 setcolor grestore " +
            "0.5 setcolor newpath 10 10 moveto 100 100 lineto stroke } def page");

        Assert.Equal(1, ContentStreamAssert.Count(result.Content, "S"));
        double[] lineTo = ContentStreamAssert.FirstOperandsOf(result.Content, "l");
        Assert.Equal(100, lineTo[0], 3);
        Assert.Equal(100, lineTo[1], 3);
    }

    /// <summary>
    /// The mirror, which fails silently rather than loudly: <c>gsave /DeviceGray setcolorspace
    /// grestore</c> back into a CMYK context left the count at 1, so <c>setcolor</c> consumed one
    /// of its four operands and the next <c>]</c>, <c>counttomark</c> or <c>stopped</c> unwind saw
    /// a stack three deep that should have been empty.
    ///
    /// <para><c>counttomark</c> is turned into a coordinate so the leftover depth is an operand in
    /// the emitted stream: 0 strays draws to (100, 0), 3 strays draws to (103, 0).</para>
    /// </summary>
    [Fact]
    public void SetColorAfterAGrestore_LeavesNoOperandsBehindInTheMirrorCase()
    {
        var result = Run(
            "/page { mark 0 0 0 1 setcmykcolor gsave /DeviceGray setcolorspace grestore " +
            "0.1 0.2 0.3 0.4 setcolor counttomark /strays exch def cleartomark " +
            "newpath 0 0 moveto strays 100 add 0 lineto stroke } def page");

        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    /// <summary><c>initgraphics</c> ends with <c>0 setgray</c>, so it resets the count too.</summary>
    [Fact]
    public void InitGraphics_RestoresTheDeviceGrayComponentCount()
    {
        var result = Run(
            "/page { /DeviceCMYK setcolorspace initgraphics mark 0.5 setcolor " +
            "counttomark /strays exch def cleartomark " +
            "newpath 0 0 moveto strays 100 add 0 lineto stroke } def page");

        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    // ==================================================================
    //  A string literal is charged once
    // ==================================================================

    /// <summary>
    /// <c>PostScriptConversion</c> hands one <c>PsAllocationBudget</c> to both the scanner and the
    /// parser. <c>PsScanner</c> charged the finished bytes and <c>PsParser.MakeString</c> charged
    /// the same array again, so every string literal cost twice its size and the effective ceiling
    /// was halved for string-heavy input. Because <c>Exhausted</c> is sticky, the first over-budget
    /// moment poisoned the whole conversion: the very first <c>def</c> then raised
    /// <c>limitcheck</c>.
    ///
    /// <para>The ceiling is set just above the literal's real size, so the budget is the thing
    /// under test: one charge fits, two do not. The fixture also <c>def</c>s and draws afterwards,
    /// because "the budget survived" is only interesting if the program that follows it ran.</para>
    /// </summary>
    [Fact]
    public void AStringLiteral_IsChargedOnceNotTwice()
    {
        const int Literal = 400_000;
        var options = new PostScriptConversionOptions
        {
            // Room for one charge of the literal plus the interpreter's own small allocations,
            // and not enough for two.
            MaxCompositeAllocationBytes = Literal + 100_000
        };

        var result = Run("/s (" + new string('x', Literal) + ") def " +
                         "newpath 0 0 moveto 100 0 lineto stroke", options);

        Assert.False(result.BudgetExhausted,
            "one 400 KB literal must not exhaust a 500 KB composite-allocation budget.");
        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    /// <summary>
    /// The budget still bites when the input genuinely exceeds it — otherwise the test above would
    /// pass against a budget that had simply been removed.
    /// </summary>
    [Fact]
    public void AnOversizedStringLiteral_StillExhaustsTheBudget()
    {
        const int Literal = 400_000;
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 100_000 };

        var result = Run("/s (" + new string('x', Literal) + ") def " +
                         "newpath 0 0 moveto 100 0 lineto stroke", options);

        Assert.True(result.BudgetExhausted);
    }

    // ==================================================================
    //  bind
    // ==================================================================

    /// <summary>
    /// <c>bind</c> was a no-op, justified by a comment claiming "nothing can redefine a systemdict
    /// operator mid-conversion in a way that would differ". <c>def</c> writes into <c>userdict</c>
    /// and <c>TryLookup</c> walks the stack top-down, so a <c>def</c> of an operator's name shadows
    /// systemdict for every later lookup — and a procedure bound before the redefinition picked the
    /// redefinition up.
    ///
    /// <para>Below, <c>draw</c> resolved <c>moveto</c> at call time to <c>{ pop pop }</c>, so no
    /// current point was established, <c>lineto</c> raised <c>undefined</c>, and the whole top-level
    /// object was abandoned: the page drew <b>nothing</b>. Shadowing a painting operator and
    /// reaching the original through a bound procedure is a common idiom in generated
    /// PostScript.</para>
    /// </summary>
    [Fact]
    public void ABoundProcedure_KeepsTheOperatorItBoundWhenTheNameIsLaterRedefined()
    {
        var result = Run(
            "/draw { moveto } bind def /moveto { pop pop } def " +
            "newpath 100 100 draw 200 200 lineto stroke");

        ContentStreamAssert.Contains(result.Content, "100 100 m");
        ContentStreamAssert.Contains(result.Content, "200 200 l");
        Assert.Equal(1, ContentStreamAssert.Count(result.Content, "S"));
    }

    /// <summary>
    /// Without <c>bind</c> the redefinition <b>does</b> apply — that is what the operator is for,
    /// and asserting only the bound case would pass against a <c>bind</c> that resolved names
    /// eagerly everywhere.
    /// </summary>
    [Fact]
    public void AnUnboundProcedure_StillSeesALaterRedefinition()
    {
        var result = Run(
            "/draw { moveto } def /moveto { pop pop } def " +
            "newpath 100 100 draw 200 200 lineto stroke");

        ContentStreamAssert.DoesNotContainOperator(result.Content, "m");
    }

    /// <summary>
    /// PLRM substitutes operators only. A name resolving to a <b>procedure</b> stays a name, so a
    /// program that redefines one of its own procedures after binding still sees the new one.
    /// </summary>
    [Fact]
    public void BindDoesNotFreezeANameThatResolvesToAProcedure()
    {
        var result = Run(
            "/step { 10 } def /go { step } bind def /step { 70 } def " +
            "newpath 0 0 moveto go 0 lineto stroke");

        Assert.Equal(70, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    /// <summary>Nested procedures are bound too, and the walk is charged and depth-capped.</summary>
    [Fact]
    public void BindReachesANestedProcedure()
    {
        var result = Run(
            "/draw { true { moveto } if } bind def /moveto { pop pop } def " +
            "newpath 100 100 draw 200 200 lineto stroke");

        ContentStreamAssert.Contains(result.Content, "100 100 m");
    }

    /// <summary>
    /// <b>The adversarial input for a recursive walk is a cycle, not depth.</b> A deeply nested
    /// procedure <i>literal</i> cannot be written past a few hundred levels and 500 CLR frames
    /// overflow nothing, so a nesting test here would pass with the cap deleted — this one asserted
    /// exactly that before it was rewritten. <c>getinterval</c>/<c>put</c> aliasing lets a program
    /// build a procedure that <b>contains itself</b>, which is unbounded recursion in one object
    /// and would take the host down with an uncatchable <c>StackOverflowException</c>.
    ///
    /// <para>The evidence is the guard's own: the conversion returns, the drawing that follows the
    /// <c>bind</c> is still emitted, and the process is still alive to assert it.</para>
    /// </summary>
    [Fact]
    public void BindOnASelfReferentialProcedure_IsDepthCappedRatherThanRecursingForever()
    {
        // `/a load` pushes the array without running it; the put makes a[0] the array itself.
        var (result, reports) = RunCollecting(
            "/a [ null ] cvx def /a load 0 /a load put /a load bind pop " +
            "newpath 0 0 moveto 100 0 lineto stroke");

        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);

        // The cap is named, not silent: below it the names stay names, which is the behaviour
        // `bind` exists to prevent.
        Assert.Contains(reports, r => r.Contains("'bind' stopped at"));
    }

    /// <summary>The cap's report is one-shot, so a wide adversarial graph cannot amplify it.</summary>
    [Fact]
    public void RepeatedBindDepthRefusals_AreReportedOnce()
    {
        var (_, reports) = RunCollecting(
            "/a [ null ] cvx def /a load 0 /a load put " +
            "5 { /a load bind pop } repeat " +
            "newpath 0 0 moveto 100 0 lineto stroke");

        int reported = 0;
        foreach (string r in reports)
            if (r.Contains("'bind' stopped at")) reported++;

        Assert.Equal(1, reported);
    }

    // ==================================================================
    //  A non-finite CTM is refused, not flattened to 0 0 0 0 0 0 cm
    // ==================================================================

    /// <summary>
    /// <c>PsMatrix.IsSingular</c> is <c>Math.Abs(Determinant) &lt; MinMatrixDeterminant</c>, which
    /// is <b>false</b> for a NaN or infinite determinant — so the refusal that
    /// <c>MinMatrixDeterminant</c>'s own doc promises ("the operator is refused and reported rather
    /// than emitted") never fired. The matrix reached the emitter, whose <c>F()</c> substitutes
    /// <c>0</c> for a non-finite component at the write boundary, and the emitted
    /// <c>0 0 0 0 0 0 cm</c> is a genuinely singular PDF matrix that collapses every later path,
    /// image and text run to a point.
    /// </summary>
    [Theory]
    [InlineData("1e200 1e200 mul dup scale")]
    [InlineData("1e200 1e200 mul 0 translate")]
    [InlineData("[1e200 1e200 mul 0 0 1 0 0] concat")]
    [InlineData("[1e200 1e200 mul 0 0 1 0 0] setmatrix")]
    public void ANonFiniteMatrix_IsRefusedSoLaterMarksStillDraw(string transform)
    {
        var (result, reports) = RunCollecting(
            transform + " newpath 0 0 moveto 100 100 lineto stroke");

        // The blanking matrix never reached the stream…
        foreach (var op in ContentStreamAssert.Parse(result.Content))
            Assert.False(op.Operator == "cm" && Array.TrueForAll(op.Operands, o => o == "0"),
                         "a 0 0 0 0 0 0 cm reached the stream and blanks everything after it.");

        // …and the drawing the transform preceded is still there, under a usable transform.
        Assert.Equal(1, ContentStreamAssert.Count(result.Content, "S"));
        ContentStreamAssert.Contains(result.Content, "100 100 l");

        Assert.Contains(reports, r => r.Contains("non-finite matrix was refused"));
    }

    /// <summary>
    /// A finite transform is still applied — otherwise the fix could be "refuse every matrix".
    /// </summary>
    [Fact]
    public void AFiniteTransform_IsStillApplied()
    {
        var result = Run("2 3 scale newpath 0 0 moveto 100 100 lineto stroke");

        ContentStreamAssert.Contains(result.Content, "2 0 0 3 0 0 cm");
    }

    /// <summary>
    /// The report is one-shot: a loop at the cap must not compose a message and fire every
    /// <c>OnEvent</c> subscriber per iteration. Asserted as a <b>count</b>, which is the guard's own
    /// evidence, not as a wall clock.
    /// </summary>
    [Fact]
    public void RepeatedNonFiniteMatrices_AreReportedOnce()
    {
        var (_, reports) = RunCollecting(
            "200 { 1e200 1e200 mul dup scale } repeat newpath 0 0 moveto 10 10 lineto stroke");

        int reported = 0;
        foreach (string r in reports)
            if (r.Contains("non-finite matrix was refused")) reported++;

        Assert.Equal(1, reported);
    }

    // ==================================================================
    //  cvi / cvr / cvs charge what they convert
    // ==================================================================

    /// <summary>
    /// <c>ParseNumericString</c> called <c>s.AsLatin1().Trim()</c> behind a comment claiming
    /// "bounded: callers pass a number-sized buffer". Its callers are <c>cvi</c> and <c>cvr</c>,
    /// which pop whatever string the <i>program</i> put on the stack: <c>AsLatin1</c> allocates two
    /// bytes of UTF-16 per input byte, all outside <c>PsAllocationBudget</c> and without a
    /// <c>Tick</c>, so the conversion reported itself as within budget throughout.
    ///
    /// <para>A <b>bounded</b> <c>repeat</c> rather than <c>{ } loop</c>, deliberately: the evidence
    /// is that 50 conversions of a 100 KB string exceed a 4 MB ceiling, which is fast to check in
    /// both directions. The unbounded form would need the op budget to stop it, which is millions
    /// of full-length parses and minutes of wall clock — and a wall-clock assertion is the banned
    /// shape anyway.</para>
    /// </summary>
    [Theory]
    [InlineData("cvi")]
    [InlineData("cvr")]
    public void ConvertingAStringOperand_IsChargedToTheAllocationBudget(string op)
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 4_000_000 };

        // 100 000 ASCII '1' bytes: parses cleanly, so no exception path throttles the loop.
        var result = Run("/s 100000 string def 0 1 99999 { s exch 49 put } for " +
                         $"50 {{ s {op} pop }} repeat", options);

        Assert.True(result.BudgetExhausted,
            $"50 x 100 KB of '{op}' conversion must be visible to the 4 MB allocation budget.");
    }

    /// <summary>
    /// <c>cvs</c>'s <c>rangecheck</c> on the destination buffer fires <b>after</b> the render, so
    /// the source string is materialised as UTF-16 before anything checks it can fit. That fourth
    /// site is charged too.
    /// </summary>
    [Fact]
    public void CvsOfALongStringOperand_IsChargedToTheAllocationBudget()
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 4_000_000 };

        // `cvs` into a 10-byte buffer always raises rangecheck; the render happened regardless.
        var result = Run("/s 100000 string def /b 10 string def " +
                         "50 { { s b cvs pop } stopped pop } repeat", options);

        Assert.True(result.BudgetExhausted,
            "the render cvs performs before its rangecheck must be charged.");
    }

    /// <summary>
    /// A short conversion is not charged into oblivion — the ceiling has to discriminate, not
    /// simply always trip.
    /// </summary>
    [Fact]
    public void ConvertingAShortStringOperand_StaysWithinTheBudget()
    {
        var options = new PostScriptConversionOptions { MaxCompositeAllocationBytes = 4_000_000 };

        var result = Run("50 { (42) cvi pop } repeat " +
                         "newpath 0 0 moveto 100 0 lineto stroke", options);

        Assert.False(result.BudgetExhausted);
        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    private static PdfDictionary FirstFont(PdfDictionary resources)
    {
        var fonts = (PdfDictionary)resources[PdfName.Font];
        foreach (var key in fonts.Keys) return (PdfDictionary)fonts[key];
        throw new Xunit.Sdk.XunitException("no font resource was registered.");
    }
}
