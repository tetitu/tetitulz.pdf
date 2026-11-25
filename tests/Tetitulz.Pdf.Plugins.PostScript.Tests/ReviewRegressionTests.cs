// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Nine rules of this plugin, each tested against a build that broke it and seen to fail there,
/// which is the only way to know a test asserts the behaviour its name claims.
/// </summary>
public sealed class ReviewRegressionTests
{
    private static PostScriptConversionResult Run(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps));

    private static string Text(string ps) => Encoding.ASCII.GetString(Run(ps).Content ?? []);

    // ==================================================================
    //  1. arc / arcn sweep normalisation: an unbounded `while` loop
    // ==================================================================

    /// <summary>
    /// Runs a conversion on a background thread so a hang fails the test instead of wedging the
    /// whole test run. The thread is a background one, so an unkillable spin dies with the process.
    /// </summary>
    private static bool CompletesWithin(string ps, int millis)
    {
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try { Run(ps); } catch { /* terminating with an error is still terminating */ }
            done.Set();
        }) { IsBackground = true };
        t.Start();
        return done.Wait(millis);
    }

    /// <summary>
    /// The angles reach the flattener as unbounded doubles. <c>while (sweep &gt; 0) sweep -= 360</c>
    /// never advances once 360 is below the value's ULP, and the spin is inside one operator where
    /// the step budget cannot see it — so this hung the thread outright.
    /// </summary>
    [Theory]
    [InlineData("newpath 0 0 1 0 1e300 arcn")]
    [InlineData("newpath 0 0 1 1e300 0 arc")]
    [InlineData("newpath 0 0 1 -1e300 1e300 arc")]
    [InlineData("newpath 0 0 1 0 1000000000 arcn")]
    public void ArcWithExtremeAngle_Terminates(string ps)
        => Assert.True(CompletesWithin(ps, 5000), $"'{ps}' did not terminate");

    /// <summary>A full turn or more still draws the whole circle — four quadrant curves.</summary>
    [Theory]
    [InlineData("newpath 0 0 100 0 360 arc fill")]
    [InlineData("newpath 0 0 100 0 720 arc fill")]
    [InlineData("newpath 0 0 100 0 1e300 arc fill")]
    public void ArcOfAFullTurnOrMore_EmitsAWholeCircle(string ps)
    {
        // Four 90-degree Beziers. Clamping by `sweep % 360` instead would collapse the 720 and
        // 1e300 cases to a zero sweep and emit no curve at all.
        Assert.Equal(4, CountOperator(Text(ps), "c"));
    }

    /// <summary>
    /// A raw angle difference past 360 in the direction <i>opposite</i> the operator's is not a
    /// multi-turn request, and clamping on its magnitude silently made it one. <c>400 0 arc</c> is
    /// 40 degrees counter-clockwise to 360 — a 320-degree sweep ending at (100, 0), not a full
    /// circle ending back where it started.
    /// </summary>
    [Fact]
    public void ArcCounterClockwise_FromAnAngleAbove360_SweepsToTheEndAngle()
    {
        string content = Text("newpath 0 0 100 400 0 arc fill");
        Assert.Equal((100.0, 0.0), FinalCurveEndPoint(content));
    }

    /// <summary>The mirror case for <c>arcn</c>: 0 to 400 clockwise is -320, ending at 40 degrees.</summary>
    [Fact]
    public void ArcnClockwise_ToAnAngleAbove360_SweepsToTheEndAngle()
    {
        var (x, y) = FinalCurveEndPoint(Text("newpath 0 0 100 0 400 arcn fill"));
        Assert.Equal(76.6, x, 1);   // 100 * cos(40 degrees)
        Assert.Equal(64.3, y, 1);   // 100 * sin(40 degrees)
    }

    /// <summary>The last <c>c</c> operator's endpoint — the third coordinate pair on the line.</summary>
    private static (double X, double Y) FinalCurveEndPoint(string content)
    {
        string last = null;
        foreach (string line in content.Split('\n'))
            if (line.EndsWith(" c", System.StringComparison.Ordinal)) last = line;

        Assert.NotNull(last);
        string[] parts = last.Split(' ');
        return (double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The short-way-round rule the PLRM specifies, which the clamp must not disturb.</summary>
    [Fact]
    public void ArcAcrossZero_SweepsTheShortWayForwards()
    {
        // 350 -> 10 counter-clockwise is 20 degrees: one Bezier, not the four of a near-full turn.
        Assert.Equal(1, CountOperator(Text("newpath 0 0 100 350 10 arc fill"), "c"));
    }

    [Fact]
    public void ArcnAcrossZero_SweepsTheShortWayBackwards()
    {
        // 10 -> 350 clockwise is -20 degrees.
        Assert.Equal(1, CountOperator(Text("newpath 0 0 100 10 350 arcn fill"), "c"));
    }

    /// <summary>A non-finite operand would put NaN into every emitted coordinate.</summary>
    [Fact]
    public void ArcWithNonFiniteOperand_IsRefusedAndEmitsNoCoordinates()
    {
        // 1 0 div raises undefinedresult, so build the infinity by overflowing a real instead.
        string content = Text("newpath 0 0 1e200 1e200 mul 0 90 arc fill");

        Assert.DoesNotContain("NaN", content);
        Assert.DoesNotContain("Inf", content);
        Assert.DoesNotContain("∞", content);
    }

    // ==================================================================
    //  2. A clip taken at save depth 0 could never be escaped
    // ==================================================================

    private const string ClipTo10 =
        "newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath clip newpath ";
    private const string PaintBig =
        " newpath 0 0 moveto 100 0 lineto 100 100 lineto closepath fill";

    /// <summary>
    /// PDF escapes a clip only by restoring past a <c>q</c>, and PostScript's <c>clip</c> opens no
    /// scope. With no bracket, <c>initclip</c> found nothing to close and then opened a level, so
    /// the <c>W n</c> stayed in force and the fill after it was still clipped.
    /// </summary>
    [Fact]
    public void InitClip_AfterATopLevelClip_RestoresPastTheClip()
        => AssertClipIsEscapedBeforeTheFill(Text(ClipTo10 + "initclip" + PaintBig));

    /// <summary>The ordinary idiom, kept so the rule cannot regress it.</summary>
    [Fact]
    public void Grestore_AfterAClipInsideGsave_RestoresPastTheClip()
        => AssertClipIsEscapedBeforeTheFill(Text("gsave " + ClipTo10 + " grestore" + PaintBig));

    /// <summary>PLRM: initgraphics is initmatrix + newpath + <b>initclip</b> + the state defaults.</summary>
    [Fact]
    public void InitGraphics_AfterATopLevelClip_RestoresPastTheClip()
        => AssertClipIsEscapedBeforeTheFill(Text(ClipTo10 + "initgraphics" + PaintBig));

    private static void AssertClipIsEscapedBeforeTheFill(string content)
    {
        int clip = content.IndexOf("W n", System.StringComparison.Ordinal);
        int restore = content.IndexOf("\nQ", System.StringComparison.Ordinal);
        int fill = content.LastIndexOf("\nf", System.StringComparison.Ordinal);

        Assert.True(clip >= 0, $"expected a clip to have been emitted.\n---\n{content}---");
        Assert.True(restore > clip && restore < fill,
            $"expected a Q between the clip and the fill.\n---\n{content}---");
    }

    /// <summary>
    /// Consecutive clips at the same depth intersect, which is what <c>clip</c> means — so they
    /// share one bracket. Opening one per clip would walk a clipping loop into the emitter's
    /// graphics-state depth cap.
    /// </summary>
    [Fact]
    public void RepeatedClipsAtTheSameDepth_ShareOneBracket()
    {
        string content = Text(ClipTo10 + ClipTo10 + ClipTo10 + PaintBig);

        Assert.Equal(3, CountOperator(content, "W n"));
        Assert.Equal(1, CountOperator(content, "q"));
    }

    /// <summary>An empty path emits no clip, so it must not leave an unjustified bracket either.</summary>
    [Fact]
    public void ClipWithAnEmptyPath_EmitsNeitherClipNorBracket()
    {
        string content = Text("newpath clip" + PaintBig);

        Assert.Equal(0, CountOperator(content, "W n"));
        Assert.Equal(0, CountOperator(content, "q"));
    }

    /// <summary>Whatever the unwinding does, the result must still be a balanced stream.</summary>
    [Theory]
    [InlineData(ClipTo10 + "initclip" + PaintBig)]
    [InlineData(ClipTo10 + "initgraphics" + PaintBig)]
    [InlineData("gsave gsave " + ClipTo10 + "initclip" + PaintBig)]
    [InlineData(ClipTo10 + ClipTo10 + "initclip" + PaintBig)]
    public void ClipUnwinding_LeavesQAndBigQBalanced(string ps)
    {
        string content = Text(ps);
        Assert.Equal(CountOperator(content, "q"), CountOperator(content, "Q"));
    }

    // ==================================================================
    //  3. rand carried its position across conversions
    // ==================================================================

    /// <summary>
    /// The generator is seeded fixed so a conversion is byte-reproducible. It was
    /// <c>[ThreadStatic]</c> but never re-seeded, and per-thread storage is not per-document
    /// scope — so the second conversion of a program continued the first one's sequence.
    /// </summary>
    [Fact]
    public void Rand_ProducesTheSameContentOnEveryConversionOfOneProgram()
    {
        const string ps = "newpath 0 0 moveto rand 1000 mod 0 rlineto 10 0 rlineto fill";

        string first = Text(ps);
        string second = Text(ps);
        string third = Text(ps);

        Assert.Equal(first, second);
        Assert.Equal(first, third);

        // Falsifiability: the program must actually depend on rand, or "they match" would hold
        // just as well for a program that ignores it. A seed other than the default one — the
        // default is 1, so `1 srand` would prove nothing — has to move the output.
        Assert.NotEqual(first, Text("newpath 0 0 moveto 2 srand rand 1000 mod 0 rlineto " +
                                    "10 0 rlineto fill"));
    }

    /// <summary>An explicit srand still overrides the per-conversion seed within one conversion.</summary>
    [Fact]
    public void SRand_StillSelectsTheSequenceWithinOneConversion()
    {
        string seeded1 = Text("newpath 0 0 moveto 7 srand rand 1000 mod 0 rlineto 10 0 rlineto fill");
        string seeded2 = Text("newpath 0 0 moveto 9 srand rand 1000 mod 0 rlineto 10 0 rlineto fill");

        Assert.NotEqual(seeded1, seeded2);
        Assert.Equal(seeded1,
            Text("newpath 0 0 moveto 7 srand rand 1000 mod 0 rlineto 10 0 rlineto fill"));
    }

    // ==================================================================
    //  4. A singular image matrix embedded an image nothing drew
    // ==================================================================

    /// <summary>
    /// The XObject was registered before the matrix was inverted, so an image that could not be
    /// placed still shipped every sample byte in /Resources and spent a slot from the image cap.
    /// </summary>
    [Fact]
    public void ImageWithASingularMatrix_IsNeitherDrawnNorEmbedded()
    {
        var result = Run("2 2 8 [0 0 0 0 0 0] <00FF00FF> image");

        Assert.DoesNotContain(" Do", Encoding.ASCII.GetString(result.Content ?? []));
        Assert.Equal(0, XObjectCount(result));
    }

    /// <summary>The same image with an invertible matrix must still be drawn and embedded.</summary>
    [Fact]
    public void ImageWithAnInvertibleMatrix_IsDrawnAndEmbedded()
    {
        var result = Run("2 2 8 [2 0 0 2 0 0] <00FF00FF> image");

        Assert.Contains(" Do", Encoding.ASCII.GetString(result.Content ?? []));
        Assert.Equal(1, XObjectCount(result));
    }

    private static int XObjectCount(PostScriptConversionResult result)
        => (result.Resources?[PdfName.XObject] as PdfDictionary)?.Keys.Count ?? 0;

    // ==================================================================
    //  5. setcolor left its operands on the stack
    // ==================================================================

    /// <summary>
    /// <c>setcolor</c> was registered with a fixed arity of zero, so its components stayed on the
    /// operand stack and every count-sensitive operator after it saw the wrong depth. Asserted
    /// through <c>counttomark</c>, which is what a real prologue uses.
    /// </summary>
    [Theory]
    [InlineData("/DeviceGray setcolorspace 0.5 setcolor", 0)]
    [InlineData("/DeviceRGB setcolorspace 1 0 0 setcolor", 0)]
    [InlineData("/DeviceCMYK setcolorspace 0 0 0 1 setcolor", 0)]
    [InlineData("[/Separation /Spot /DeviceGray {} ] setcolorspace 0.4 setcolor", 0)]
    [InlineData("[/DeviceN [/A /B] /DeviceGray {} ] setcolorspace 0.1 0.2 setcolor", 0)]
    public void SetColor_ConsumesTheComponentsOfTheCurrentSpace(string ps, int expectedLeftOver)
    {
        // mark ... counttomark leaves the number of operands the sequence left behind.
        string content = Text($"mark {ps} counttomark " +
                              "newpath 0 0 moveto 0 rlineto 10 0 rlineto fill");

        // The leftover count becomes the x of the first rlineto, so it lands in the emitted path.
        Assert.Contains($"\n{expectedLeftOver} 0 l\n", content);
    }

    /// <summary>
    /// An ICCBased space carries its component count in a stream dictionary this interpreter never
    /// builds, so the count is unknowable and the fallback pops the numbers that are actually
    /// present. It must stop at the first operand that is not one.
    /// </summary>
    [Fact]
    public void SetColor_InAnUnsizableSpace_StopsAtTheFirstNonNumber()
    {
        string content = Text("mark /Marker [/ICCBased 0] setcolorspace 0.1 0.2 0.3 setcolor " +
                              "counttomark newpath 0 0 moveto 0 rlineto 10 0 rlineto fill");

        // The three components go; /Marker below them stays. One operand left before the mark.
        Assert.Contains("\n1 0 l\n", content);
    }

    /// <summary>
    /// And it never pops more than the widest device space, so a mis-sized guess cannot run away
    /// down the stack.
    /// </summary>
    [Fact]
    public void SetColor_InAnUnsizableSpace_PopsAtMostFourNumbers()
    {
        string content = Text("mark 1 2 [/ICCBased 0] setcolorspace 3 4 5 6 setcolor " +
                              "counttomark newpath 0 0 moveto 0 rlineto 10 0 rlineto fill");

        // Four popped, the 1 and 2 left. This is the honest limit of the fallback: with the count
        // unknown it cannot tell a component from an operand that merely looks like one, so a
        // program keeping bare numbers under a setcolor in an ICCBased space would lose them.
        // Popping nothing would desynchronise every such program instead of only that one.
        Assert.Contains("\n2 0 l\n", content);
    }

    // ==================================================================
    //  6. An embedded font's missing /Widths is reported, not hidden
    // ==================================================================

    /// <summary>
    /// Table 111 requires /Widths, /FirstChar and /LastChar for a non-standard-14 Type 1 font.
    /// Deriving them needs the charstrings, which this path never decrypts — so the omission is a
    /// known deviation and has to be reported rather than passing as a clean conversion.
    /// </summary>
    [Fact]
    public void EmbeddedFont_ReportsThatItOmitsTheRequiredWidths()
    {
        var reports = ConvertCollecting(EmbeddedFontProgram +
            "/Custom 12 dict definefont pop " +
            "/Custom findfont 12 scalefont setfont newpath 0 0 moveto (Hi) show");

        // The font really was embedded — otherwise "no /Widths" would be true of a font that was
        // never emitted, and the test would pass for the wrong reason.
        Assert.Contains(reports, r => r.Contains("verbatim as a /FontFile"));
        Assert.Contains(reports, r => r.Contains("/Widths"));
    }

    /// <summary>
    /// A minimal but structurally real Type 1 program: the <c>%!PS-AdobeFont</c> header the capture
    /// keys on, a clear-text dictionary, <c>eexec</c>, a stand-in encrypted body and the
    /// conventional 512-zero trailer.
    /// </summary>
    private static readonly string EmbeddedFontProgram =
        "%!PS-AdobeFont-1.0: Custom 001.000\n" +
        "/FontName /Custom def\n" +
        "/FontType 1 def\n" +
        "/Encoding StandardEncoding def\n" +
        "currentfile eexec\n" +
        "A1B2C3D4E5F60718293A4B5C6D7E8F90\n" +
        new string('0', 512) + "\ncleartomark\n";

    /// <summary>
    /// Every diagnostic, not just the last. <c>LastError</c> is exactly that, and a conversion that
    /// embeds a font goes on to report other things afterwards — so a test asking whether something
    /// was reported at all has to subscribe.
    /// </summary>
    private static List<string> ConvertCollecting(string ps)
    {
        // Thread-filtered: OnEvent is process-wide, so an unfiltered handler collects whatever
        // other test classes are converting in parallel.
        using var collector = TestInfrastructure.DiagnosticCollector.Start();
        Run(ps);
        return new List<string>(collector.Messages);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>
    /// How many times <paramref name="op"/> appears as a whole operator line. Counting substrings
    /// would make "q" match the "q" inside a name and "c" match every "cm".
    /// </summary>
    private static int CountOperator(string content, string op)
    {
        int n = 0;
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed == op) { n++; continue; }

            // Operators that carry operands: match the trailing token.
            int space = trimmed.LastIndexOf(' ');
            if (space >= 0 && trimmed.Substring(space + 1) == op) n++;
        }
        return n;
    }
}
