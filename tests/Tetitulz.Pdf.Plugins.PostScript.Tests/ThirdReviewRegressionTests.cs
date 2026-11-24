// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A <b>class</b> of defect, not one: an operator whose real cost scales with attacker-controlled
/// data while <c>WorkBudget</c> charges one tick per operator is invisible to the guard that is
/// documented as terminating <c>{ } loop</c> in bounded time. Each operator here is charged its
/// real cost.
/// </summary>
public sealed class ThirdReviewRegressionTests
{
    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions o = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), o);

    private static string Text(string ps) => Encoding.Latin1.GetString(Run(ps).Content ?? []);

    /// <summary>
    /// A budget small enough that an honestly-charged operator exhausts it quickly. Any of these
    /// programs ran for hours-to-days before the charging was added, whatever the budget was,
    /// because the cost was never charged at all.
    /// </summary>
    private static PostScriptConversionOptions Bounded(long ops = 200_000) =>
        new() { MaxTotalOps = ops };


    // ==================================================================
    //  Unbudgeted work inside one operator
    // ==================================================================

    /// <summary>
    /// The decisive assertion for <c>clip</c>, and a deterministic one rather than a stopwatch.
    ///
    /// <para><c>clip</c> retains the path and re-emits it, so each iteration writes an ever-longer
    /// path. If the budget is charged for that, it runs out <i>before</i> the emitted stream fills:
    /// the output is proportional to the budget. If it is not — the defect — the loop always runs
    /// far enough to hit the 8 MiB truncation cap, whatever the budget. Measured: 1.8 / 3.7 /
    /// 7.7 MB across three budgets when charged, and a flat 8388608 at every budget when not.</para>
    /// </summary>
    [Theory]
    [InlineData(200_000)]
    [InlineData(400_000)]
    public void ClipInALoop_ExhaustsTheBudgetBeforeFillingTheStream(long ops)
    {
        var result = Run("0 0 moveto { 1 1 rlineto clip } loop", Bounded(ops));

        Assert.True(result.BudgetExhausted, "the operation budget must be what stops this");
        Assert.False(result.ContentTruncated,
            "the byte cap stopped it before the budget did, so the path scan is still uncharged");
    }

    /// <summary>
    /// The deterministic counterpart for <c>pathbbox</c>, and the one a stopwatch cannot fake.
    /// </summary>
    /// <remarks>
    /// <c>pathbbox</c> re-reads the whole path, which grows one segment per iteration. Charged, the
    /// <b>operation</b> budget runs out first: n²/2 + 16n = ops, so a 2e7 budget stops at roughly
    /// 6 300 segments, far under <see cref="PsLimits.MaxPathSegments"/> (100 000). Uncharged, each
    /// iteration costs a flat ~16 ticks, so the loop reaches that cap after only 1.6M of them and
    /// <c>rlineto</c> raises <c>limitcheck</c> — a <i>different</i> guard, leaving
    /// <c>BudgetExhausted</c> false. <b>Which guard fired is the evidence.</b>
    /// <para>The budget is 2e7 rather than the 2e6 the stopwatch version used, because at 2e6 the
    /// uncharged side clears the segment cap by only ~25%; here both sides have a 12–16× margin.</para>
    /// </remarks>
    [Fact]
    public void PathBBoxInALoop_ExhaustsTheBudgetBeforeTheSegmentCap()
    {
        var result = Run("newpath 0 0 moveto { 1 1 rlineto pathbbox pop pop pop pop } loop",
                         Bounded(20_000_000));

        Assert.True(result.BudgetExhausted,
            $"the operation budget must be what stops this; last error was '{result.LastError}'");
        Assert.DoesNotContain("limitcheck", result.LastError ?? string.Empty,
                              StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>search</c> is a naive O(n·m) scan and the needle here never matches, so it does the full
    /// 1e10 byte comparisons, which one tick per operator would not charge. <c>anchorsearch</c> is a
    /// prefix test, and scanning the whole haystack would answer an O(m) question at O(n) cost.
    /// </summary>
    /// <remarks>
    /// <b>The budget is the evidence, not a stopwatch, and it discriminates because this fixture
    /// has no <c>loop</c>.</b> It is fifteen straight-line tokens, and <c>string</c> charges the
    /// <i>allocation</i> budget only — it never ticks — so nothing but the search charge itself can
    /// spend an operation budget. Charged, <c>search</c> ticks 2e10 and <c>anchorsearch</c> ticks
    /// 100 000, either of which exhausts this budget on the spot. Uncharged, the whole program
    /// costs about fifteen ticks and <c>BudgetExhausted</c> is <b>false</b>.
    /// <para>The budget must sit below <c>anchorsearch</c>'s 100 000-tick charge, which is why it
    /// is not the 200 000 default: at the default, <c>anchorsearch</c> charged correctly would
    /// still not exhaust it and the row would pass for the wrong reason.</para>
    /// </remarks>
    [Theory]
    [InlineData("search")]
    [InlineData("anchorsearch")]
    public void StringSearchIsChargedToTheBudget(string op)
    {
        // A 100 000-byte needle ending in a byte the 200 000-byte haystack never contains.
        string ps = "/h 200000 string def /n 100000 string def n 99999 1 put " + $"h n {op}";

        var result = Run(ps, Bounded(50_000));

        Assert.True(result.BudgetExhausted,
            $"'{op}' must be charged for the bytes it scans; last error was '{result.LastError}'");
    }

    /// <summary>The path itself is now bounded, so the memory behind that work is too.</summary>
    [Fact]
    public void AnUnboundedPath_RaisesLimitCheck()
    {
        var result = Run("newpath 0 0 moveto { 1 1 rlineto } loop", Bounded(2_000_000));

        Assert.True(result.BudgetExhausted || (result.LastError ?? string.Empty).Contains("limitcheck",
                        StringComparison.OrdinalIgnoreCase),
            $"expected a bounded path; last error was '{result.LastError}'");
    }

    /// <summary>
    /// <c>findfont</c> tests a hyphenated name's suffixes against the CMap registry, copying each.
    /// A program can build a 500 KB font name without a single long token in its source.
    /// </summary>
    /// <remarks>
    /// <b>Not a wall-clock ceiling:</b> one like <c>ms &lt; 15_000</c> is under the ~30 s threshold
    /// below which ceilings flake in a full-solution run.
    /// <para>The guard is <c>PsLimits.MaxCMapNameChars</c> — a <b>cap</b> rather than a charge —
    /// so unlike its siblings there is no budget to interrogate: past the cap the name is simply not
    /// split, and a correctly-capped run and an uncapped-but-fast one are indistinguishable in the
    /// result. Termination is genuinely the whole claim, so this takes the dedicated-thread remedy
    /// rather than a bigger number: a bigger number leaves the same race with a longer fuse.</para>
    /// </remarks>
    [Fact]
    public void ACompositeFontNameSplitIsNotQuadratic()
    {
        // 200 000 hyphens, built at run time.
        const string ps = "/t 1000 string def 0 1 999 { t exch 45 put } for " +
                          "/s 200000 string def 0 1000 199000 { s exch t putinterval } for " +
                          "s cvn findfont pop";

        var worker = new Thread(() => Run(ps, Bounded(20_000_000))) { IsBackground = true };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromMinutes(2)),
            "findfont did not return on a 200 000-hyphen name — the split is still quadratic");
    }

    /// <summary><c>scalefont</c> copies the whole dictionary and is charged the copy, not a flat two entries.</summary>
    /// <remarks>
    /// <para><b>Termination is the claim, and these two are the only cases in this file with no
    /// other evidence for it.</b> Both budgets end in an allocation <c>limitcheck</c> whether the
    /// copy is charged or not — charged after ~70 iterations, uncharged after ~280 000 — so
    /// <c>BudgetExhausted</c>, <c>LastError</c> and the (empty) output are identical, and only the
    /// duration differs: ~200 ms against tens of minutes uncharged. Squeezing the
    /// budgets cannot separate them, because the ops-to-allocation ratio per iteration moves
    /// together with the charge.</para>
    ///
    /// <para>A dedicated thread, not <c>Stopwatch</c> and not <c>Task.Run</c>: a full-solution run's
    /// test assemblies share the cores, so elapsed time measures the machine's backlog.
    /// <c>PtBypassRegressionTests</c>' class comment records the measurement — a bomb that aborts
    /// in ~0.5 s idle takes 12.7 s under a parallel whole-solution run, <b>22×</b> with the code
    /// unchanged, so a 6 s ceiling (6 s / 22 = 273 ms against a ~200 ms charged run) would leave no
    /// margin at all. Two minutes here is a hang-guard against a ~600× margin, not a performance
    /// assertion — the same shape and the same number as
    /// <c>PtBypassRegressionTests.HangCeilingMs</c>.</para>
    /// </remarks>
    [Theory]
    [InlineData("scalefont")]
    [InlineData("makefont")]
    public void FontDictionaryCopiesAreChargedForTheirRealSize(string op)
    {
        string operand = op == "makefont" ? "[1 0 0 1 0 0]" : "1";
        PostScriptConversionResult result = null;

        var worker = new Thread(() => result = Run(
            "/D 20000 dict def 0 1 19999 { D exch dup put } for " +
            $"{{ D {operand} {op} pop }} loop", Bounded(20_000_000))) { IsBackground = true };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromMinutes(2)),
            $"'{op}' over a large dictionary must be charged for its real size; uncharged it copies " +
            "20 000 entries per iteration for hundreds of thousands of iterations and does not return");
        Assert.True(result.BudgetExhausted,
            $"'{op}' over a large dictionary must exhaust a budget, not copy it for free");
    }

    /// <summary><c>cvn</c> retained ~600 MB against a 64 MiB budget that never saw a byte of it.</summary>
    /// <remarks>
    /// <b>Two assertions, and the pair is the point.</b> <c>cvn</c> charges through
    /// <c>LatinFromOperand</c>, which calls <c>ChargeAllocation</c> and never <c>Tick</c>s — so
    /// charged, the <b>allocation</b> budget is what stops it and <c>LastError</c> says
    /// <c>limitcheck</c>. Uncharged, the <b>operation</b> budget stops it instead and says so.
    /// Both set <c>BudgetExhausted</c>; only the pair says the allocation budget saw the string.
    /// The idiom is <c>AssemblyScanRegressionTests.TextOperatorWork_IsChargedByTheStringLength</c>'s.
    /// Both budgets are squeezed so the uncharged case fails in about a second rather than in hours
    /// — which is what replaces the 6 s stopwatch this row used to carry.
    /// </remarks>
    [Fact]
    public void CvnIsChargedForTheStringItConverts()
    {
        var options = new PostScriptConversionOptions
        {
            MaxCompositeAllocationBytes = 100_000,
            MaxTotalOps = 300_000,
        };

        var result = Run("/s 4000 string def { s cvn pop } loop", options);

        Assert.Contains("limitcheck", result.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.True(result.BudgetExhausted,
            "the allocation budget must be what stopped it; last error was " + result.LastError);
    }

    /// <summary>
    /// Distinct encodings mint distinct font resources, each permanently retained. 172 bytes
    /// retained ~226 MB — 3.5x the whole composite-allocation ceiling.
    /// </summary>
    [Fact]
    public void FontResourcesAreCapped()
    {
        var sb = new StringBuilder("/buf 12 string def newpath 0 0 moveto ");
        for (int i = 0; i < PsLimits.MaxFontResources + 100; i++)
            sb.Append("/Helvetica findfont dup /Encoding [ /g").Append(i)
              .Append(" ] put setfont (A) show ");

        var result = Run(sb.ToString(), Bounded(20_000_000));

        int fonts = FontCount(result);
        Assert.True(fonts <= PsLimits.MaxFontResources,
            $"{fonts} font resources were minted, past the {PsLimits.MaxFontResources} cap");
    }

    private static int FontCount(PostScriptConversionResult r)
        => (r.Resources?[Core.Objects.PdfName.Font] as Core.Objects.PdfDictionary)?.Keys.Count ?? 0;

    // ==================================================================
    //  Wrong output
    // ==================================================================

    /// <summary>
    /// <c>arct</c> rounds a corner. It emitted the 270-degree complementary arc — three cubics
    /// bulging outside the corner — because the cross product was taken against the vector pointing
    /// BACK along the incoming segment.
    /// </summary>
    [Fact]
    public void Arct_EmitsTheMinorFillet()
    {
        string s = Text("newpath 0 0 moveto 100 0 100 100 50 arct fill");

        // Both arcs share their endpoints, so the endpoint alone cannot tell them apart. What does:
        // the 90-degree fillet needs at most two cubics (a sweep of exactly 90 rounds a hair above
        // it in floating point, so the flattener may split once), while the 270-degree arc the
        // broken version drew always needs three — and it swings right around the circle, so it
        // emits coordinates outside the corner box that the fillet never touches.
        Assert.InRange(CountCubics(s), 1, 2);

        // Curve coordinates only: the path legitimately starts at the pre-arc current point, and
        // arct draws a line to the first tangent point before the fillet begins.
        foreach (var (x, y) in CurveCoordinates(s))
        {
            Assert.InRange(x, 49.9, 100.1);   // centre (50,50), r=50: the fillet stays in this box
            Assert.InRange(y, -0.1, 50.1);
        }
    }

    /// <summary>
    /// arct is tangent to the two LINES (PLRM): a radius whose tangent points lie past the segments'
    /// ends still rounds the corner — the fillet at the corner (10,0) of radius 50 touches the first
    /// line at x = −40 and the second at y = 50, both beyond 10. It was refused and drew a straight
    /// corner.
    /// </summary>
    [Fact]
    public void Arct_TangentPointsPastTheSegmentEnds_StillRound()
    {
        string s = Text("newpath 0 0 moveto 10 0 10 10 50 arct fill");

        Assert.InRange(CountCubics(s), 1, 2);
        foreach (var (x, y) in CurveCoordinates(s))
        {
            Assert.InRange(x, -40.1, 10.1);   // centre (-40, 50), r = 50
            Assert.InRange(y, -0.1, 50.1);
        }
    }

    /// <summary>The mirror turn must round the same way.</summary>
    [Fact]
    public void Arct_MirrorCorner_AlsoEmitsTheMinorFillet()
    {
        string s = Text("newpath 0 0 moveto 100 0 100 -100 50 arct fill");

        Assert.InRange(CountCubics(s), 1, 2);

        // Curve coordinates only: the path legitimately starts at the pre-arc current point, and
        // arct draws a line to the first tangent point before the fillet begins.
        foreach (var (x, y) in CurveCoordinates(s))
        {
            Assert.InRange(x, 49.9, 100.1);
            Assert.InRange(y, -50.1, 0.1);
        }
    }

    private static int CountCubics(string content)
    {
        int n = 0;
        foreach (string line in content.Split('\n'))
            if (line.TrimEnd().EndsWith(" c", StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Every coordinate pair on every cubic — the two control points and the endpoint.</summary>
    private static System.Collections.Generic.IEnumerable<(double X, double Y)> CurveCoordinates(string content)
    {
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.EndsWith(" c", StringComparison.Ordinal)) continue;

            string[] parts = line.Split(' ');
            for (int k = 0; k + 1 < parts.Length - 1; k += 2)
                yield return (double.Parse(parts[k], System.Globalization.CultureInfo.InvariantCulture),
                              double.Parse(parts[k + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A non-finite number is not a PDF number and must never reach the stream.</summary>
    [Theory]
    [InlineData("newpath 1e200 1e200 mul dup moveto 10 10 lineto stroke")]
    [InlineData("newpath 0 0 moveto 1e200 1e200 mul 0 lineto stroke")]
    public void NonFiniteCoordinatesNeverReachTheStream(string ps)
    {
        string s = Text(ps);

        Assert.DoesNotContain("Infinity", s);
        Assert.DoesNotContain("NaN", s);
    }

    /// <summary>...and it must not reach /MediaBox either, which would make the page unopenable.</summary>
    [Fact]
    public void NonFiniteCoordinatesNeverReachTheMediaBox()
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\nnewpath 1e200 1e200 mul dup moveto 10 10 lineto stroke showpage\n"));
        PostScriptConversion.ConvertDocument(input, output);

        string pdf = Encoding.Latin1.GetString(output.ToArray());
        Assert.DoesNotContain("Infinity", pdf);
        Assert.DoesNotContain("NaN", pdf);
    }

    /// <summary>
    /// PLRM 4.10: a stencil is painted in the current colour. None was emitted, so a red stencil
    /// rendered black — or worse, in whatever unrelated fill colour preceded it.
    /// </summary>
    [Fact]
    public void ImageMask_IsPaintedInTheCurrentColour()
    {
        string s = Text("1 0 0 setrgbcolor gsave 8 0 0 8 0 0 concat " +
                        "8 8 true [8 0 0 -8 0 8] <FFFFFFFFFFFFFFFF> imagemask grestore");

        Assert.Contains("1 0 0 rg", s);
    }

    /// <summary>
    /// The colour must be emitted for the stencil specifically, not inherited: a preceding fill in
    /// a different colour is the case that tells "emitted black" from "emitted nothing".
    /// </summary>
    [Fact]
    public void ImageMask_AfterADifferentFill_StillEmitsItsOwnColour()
    {
        string s = Text("0 0 1 setrgbcolor newpath 0 0 moveto 5 0 lineto 5 5 lineto fill " +
                        "1 0 0 setrgbcolor gsave 8 0 0 8 0 0 concat " +
                        "8 8 true [8 0 0 -8 0 8] <FFFFFFFFFFFFFFFF> imagemask grestore");

        int blue = s.IndexOf("0 0 1 rg", StringComparison.Ordinal);
        int red  = s.IndexOf("1 0 0 rg", StringComparison.Ordinal);
        int draw = s.IndexOf(" Do", StringComparison.Ordinal);

        Assert.True(red > blue && red < draw,
            $"the stencil's own colour must be emitted between the fill and the Do.\n---\n{s}---");
    }

    /// <summary>An ordinary image carries its own colour and must NOT get a fill operator.</summary>
    [Fact]
    public void OrdinaryImage_DoesNotEmitAFillColour()
    {
        string s = Text("1 0 0 setrgbcolor gsave 8 0 0 8 0 0 concat " +
                        "2 2 8 [2 0 0 -2 0 2] <00FF7F40> image grestore");

        Assert.DoesNotContain("1 0 0 rg", s);
    }

    /// <summary>PLRM: the <c>for</c> control value is an integer only if BOTH operands were.</summary>
    [Theory]
    [InlineData("1 1 1", "integertype")]
    [InlineData("1.0 1 1", "realtype")]
    [InlineData("1 1.0 1", "realtype")]
    public void ForControlValueTakesItsTypeFromTheOperands(string operands, string expected)
    {
        // `type` pushes a name; compare it and let the result reach the emitted path.
        string s = Text($"newpath 0 0 moveto {operands} {{ type /{expected} eq {{ 1 }} {{ 0 }} ifelse }} for " +
                        "0 rlineto 10 0 rlineto fill");

        Assert.Contains("\n1 0 l\n", s);
    }

    // ==================================================================
    //  Diagnostics and conformance
    // ==================================================================

    /// <summary>
    /// A declining plugin call must not leave the previous XObject's diagnostics in the sink — a
    /// host logging LastError to explain the placeholder was told a fact about other content.
    /// </summary>
    [Fact]
    public void DecliningPluginCall_ClearsThePreviousDiagnostics()
    {
        var plugin = new PostScriptXObjectPlugin();

        plugin.TryConvertToPdfContent(
            new Core.Plugins.PostScriptContentRequest
            {
                PostScript = Encoding.ASCII.GetBytes("bogusoperator")
            }, out _);

        Assert.Contains("bogusoperator", PostScriptDiagnostics.LastError ?? string.Empty);

        // Now decline. The sink must describe THIS call, not the last one.
        plugin.TryConvertToPdfContent(
            new Core.Plugins.PostScriptContentRequest { PostScript = Array.Empty<byte>() }, out _);

        Assert.DoesNotContain("bogusoperator", PostScriptDiagnostics.LastError ?? string.Empty);
    }

    /// <summary>Table 111: a font's /FontDescriptor shall be an indirect reference.</summary>
    [Fact]
    public void DocumentImport_WritesTheFontDescriptorAsAnIndirectReference()
    {
        const string marker = "A1B2C3D4E5F60718293A4B5C6D7E8F90";
        string program =
            "%!PS-AdobeFont-1.0: Custom 001.000\n" +
            "/FontName /Custom def\n/FontType 1 def\n/Encoding StandardEncoding def\n" +
            "currentfile eexec\n" + marker + "\n" + new string('0', 512) + "\ncleartomark\n";

        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 200\n" + program +
            "/Custom 12 dict definefont pop /Custom findfont 12 scalefont setfont " +
            "newpath 20 20 moveto (Hi) show showpage\n"));
        PostScriptConversion.ConvertDocument(input, output);

        Assert.Matches(@"/FontDescriptor \d+ 0 R", Encoding.Latin1.GetString(output.ToArray()));
    }
}
