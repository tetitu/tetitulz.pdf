// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Four unrelated defects, three of them sharing one shape: a bound that <i>looks</i> present and
/// is evaluated in the wrong arithmetic, in the wrong place, or on the wrong quantity.
///
/// <list type="number">
///   <item><b>32-bit bounds on a file-controlled index.</b> <c>getinterval</c>/<c>putinterval</c>
///     testing <c>index + count &gt; Length</c> unchecked would let a saturated <c>int.MaxValue</c>
///     index wrap the sum negative and pass — turning a conforming <c>rangecheck</c> into an
///     <c>IndexOutOfRangeException</c> that escapes <c>PsInterpreter.Run</c> and discards the
///     whole conversion.</item>
///   <item><b>A validity test that admits infinity.</b> <c>Width &gt; 0 &amp;&amp; Height &gt; 0</c>
///     alone is true of <c>Infinity</c>, so <c>%%BoundingBox: 0 0 1e400 1e400</c> would reach
///     <c>/MediaBox</c> as the literal <c>Infinity</c> — a page this library's own parser cannot
///     read back.</item>
///   <item><b>A diagnostic charged per byte.</b> One uncapped <c>PostScriptDiagnostics.Report</c>
///     per stray <c>&gt;</c>, per unbalanced <c>)</c> and per non-hex byte would be the exact
///     amplification <c>ReportOversizedNumericToken</c>'s doc comment names as "making the
///     diagnostics themselves the attack".</item>
///   <item><b>A required entry.</b> The CJK descendant CIDFont carries a <c>/FontDescriptor</c>,
///     which Table 117 lists as Required for every CIDFont.</item>
/// </list>
/// </summary>
public sealed class FourthReviewRegressionTests
{
    static FourthReviewRegressionTests()
    {
        // Idempotent: the plugin skips a CMap that is already registered.
        new AdobeCMapPlugin().Initialize();
    }

    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions o = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), o);

    private static string Text(string ps) => Encoding.Latin1.GetString(Run(ps).Content ?? []);

    // ==================================================================
    //  1. Interval bounds in 32-bit arithmetic
    // ==================================================================

    /// <summary>
    /// The index is saturated at <c>int.MaxValue</c> by <c>PsObject.AsInt</c>, so
    /// <c>index + count</c> wraps to <c>int.MinValue</c> and the guard admits a view pointing far
    /// past the backing array.
    ///
    /// <para>The assertion is the guard's own evidence twice over: a <c>rangecheck</c> was reported,
    /// and the marks the program made <i>before</i> the bad operator are still in the output. The
    /// second half is the one that matters — an escaping <c>IndexOutOfRangeException</c> is caught
    /// only by <c>ConvertFragment</c>'s catch-all, which leaves <c>Content</c> null and loses every
    /// path already emitted.</para>
    /// </summary>
    [Theory]
    [InlineData("(abc) 2147483647 1 getinterval 0 get")]
    [InlineData("(abcd) 2147483647 (x) putinterval")]
    [InlineData("[1 2 3] 2147483647 1 getinterval 0 get")]
    [InlineData("[1 2 3 4] 2147483647 [9] putinterval")]
    public void IntervalIndexOverflow_IsARangecheckAndKeepsTheArtworkAlreadyEmitted(string overflowing)
    {
        using var diag = DiagnosticCollector.Start();

        var result = Run("newpath 10 10 moveto 100 100 lineto stroke " + overflowing);
        string content = Encoding.Latin1.GetString(result.Content ?? []);

        Assert.Contains("100 100 l", content);
        Assert.Contains("\nS\n", content);
        Assert.True(diag.Any("rangecheck"),
            "expected a conforming 'rangecheck'; got: " + string.Join(" | ", diag.Messages));
    }

    /// <summary>
    /// The overflow must not be "fixed" by refusing every large index: a legal in-range interval on
    /// the same operators still has to work, or the guard is indistinguishable from a stub.
    /// </summary>
    [Fact]
    public void GetInterval_InRange_StillAliasesTheSourceString()
    {
        string s = Text("newpath 0 0 moveto (abcdef) 2 3 getinterval show");
        Assert.Contains("(cde) Tj", s);
    }

    // ==================================================================
    //  2. A non-finite %%BoundingBox
    // ==================================================================

    /// <summary>
    /// <c>double.TryParse(NumberStyles.Float, InvariantCulture)</c> accepts both the literal
    /// <c>Infinity</c> and any overflowing exponent, and <c>(double.PositiveInfinity)
    /// .ToString("F6")</c> is the word "Infinity" — which is not a PDF number. It reached
    /// <c>/MediaBox</c> through <c>PageSize</c> and the origin-shift <c>cm</c> through
    /// <c>Origin</c>, so both are asserted.
    /// </summary>
    [Theory]
    [InlineData("0 0 1e400 1e400")]
    [InlineData("-1e400 -1e400 100 100")]
    [InlineData("0 0 Infinity Infinity")]
    public void ADeclaredBoundingBoxThatIsNotFinite_NeverReachesTheOutput(string box)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\n%%BoundingBox: " + box +
            "\nnewpath 0 0 moveto 10 10 lineto stroke showpage\n"));

        PostScriptConversion.ConvertDocument(input, output);
        string pdf = Encoding.Latin1.GetString(output.ToArray());

        Assert.DoesNotContain("Infinity", pdf);
        Assert.DoesNotContain("NaN", pdf);

        // Falling back is only correct if it falls back to something usable: the declared box is
        // discarded, so the page is sized by the drawn extent or Letter, and a /MediaBox is present.
        Assert.Contains("/MediaBox", pdf);
    }

    /// <summary>A finite declared box must still win, or the guard above is a blanket refusal.</summary>
    [Fact]
    public void AFiniteDeclaredBoundingBox_StillSizesThePage()
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 300\n" +
            "newpath 0 0 moveto 10 10 lineto stroke showpage\n"));

        PostScriptConversion.ConvertDocument(input, output);
        string pdf = Encoding.Latin1.GetString(output.ToArray());

        Assert.Contains("/MediaBox [0 0 200 300]", pdf);
    }

    // ==================================================================
    //  3. Scanner diagnostics reported per byte
    // ==================================================================

    /// <summary>
    /// A file of stray delimiters cost one composed message and one <c>OnEvent</c> invocation per
    /// byte. The assertion counts the <b>scanner's own</b> message, which is the guard's evidence —
    /// not a wall clock, which a fast machine passes with the defect present.
    ///
    /// <para><b>The delimiter is space-separated on purpose.</b> A solid run of <c>&gt;</c> bytes
    /// pairs into <c>&gt;&gt;</c> <i>dict-close</i> tokens, which never reach the stray-<c>&gt;</c>
    /// branch at all — the first draft of this test measured <c>PsInterpreter.Run</c>'s
    /// one-report-per-abandoned-top-level-object recovery instead, and that one is charged to the
    /// operation budget and reports something genuinely different each time.</para>
    /// </summary>
    [Theory]
    [InlineData("> ", "stray '>'")]
    [InlineData(") ", "unbalanced ')'")]
    public void AdversarialRunOfStrayDelimiters_ReportsOnceNotOncePerByte(string stray, string fragment)
    {
        using var diag = DiagnosticCollector.Start();
        Run(string.Concat(System.Linq.Enumerable.Repeat(stray, 20000)));

        // Two messages, not 20 000: the first occurrence, plus a counted summary at end of input.
        // The summary is required by AssemblyScanRegressionTests and is the other half of the
        // claim — suppressing everything after the first would hide the difference between a typo
        // and a bomb — so this asserts the count is bounded AND that the total survived.
        Assert.InRange(Count(diag, fragment), 1, 2);
        Assert.True(diag.Any("20000 " + fragment),
            "the summary must carry the count. Reported: " + string.Join(" | ", diag.Messages));
    }

    /// <summary>The same for a hex string whose every byte is rejected.</summary>
    [Fact]
    public void AdversarialRunOfNonHexBytesInAHexString_ReportsOnceNotOncePerByte()
    {
        using var diag = DiagnosticCollector.Start();
        Run("<" + new string('z', 20000) + ">");

        Assert.InRange(Count(diag, "non-hex character"), 1, 2);
        Assert.True(diag.Any("20000 non-hex character(s)"),
            "the summary must carry the count. Reported: " + string.Join(" | ", diag.Messages));
    }

    /// <summary>How many collected messages contain <paramref name="fragment"/>.</summary>
    private static int Count(DiagnosticCollector diag, string fragment)
    {
        int n = 0;
        foreach (string m in diag.Messages)
            if (m.Contains(fragment, System.StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// Suppressing the repeats must not suppress the <i>first</i> one: a file with a single stray
    /// delimiter still has to say so, which is what <c>PsScannerTests</c> already relies on.
    /// </summary>
    [Fact]
    public void ASingleStrayDelimiter_IsStillReported()
    {
        using var diag = DiagnosticCollector.Start();
        Run("newpath > 0 0 moveto");

        Assert.True(diag.Any("stray '>'"),
            "the first occurrence must still be reported: " + string.Join(" | ", diag.Messages));
    }

    // ==================================================================
    //  4. Table 117: a CIDFont requires a /FontDescriptor
    // ==================================================================

    /// <summary>
    /// No CJK program is embedded, so the reader has to substitute — and with no <c>/Flags</c>,
    /// <c>/FontBBox</c>, <c>/ItalicAngle</c>, <c>/StemV</c> or <c>/MissingWidth</c> it has nothing to
    /// substitute from. Strict readers and every preflight validator reject the font outright and
    /// the whole CJK run draws as nothing.
    /// </summary>
    [Fact]
    public void TheCjkDescendantCidFont_CarriesTheRequiredFontDescriptor()
    {
        var result = Run("/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont " +
                         @"newpath 0 0 moveto (\202\240) show");

        var fonts = (PdfDictionary)result.Resources[PdfName.Font];
        PdfDictionary font = null;
        foreach (var kv in fonts) { font = (PdfDictionary)kv.Value; break; }

        var descendant = (PdfDictionary)((PdfArray)font[PdfName.DescendantFonts])[0];
        var descriptor = Assert.IsType<PdfDictionary>(descendant[PdfName.FontDescriptor]);

        Assert.Equal("KozMinPro-Regular", ((PdfName)descriptor[PdfName.FontName]).Value);
        Assert.Equal("FontDescriptor", ((PdfName)descriptor[PdfName.Type]).Value);

        // The five entries a reader actually substitutes from.
        Assert.Equal(4, (int)((PdfNumber)descriptor[PdfName.Flags]).DoubleValue);
        Assert.Equal(4, ((PdfArray)descriptor[PdfName.FontBBox]).Count);
        Assert.NotNull(descriptor[PdfName.ItalicAngle]);
        Assert.True(((PdfNumber)descriptor[PdfName.StemV]).DoubleValue > 0);
        Assert.Equal(1000, (int)((PdfNumber)descriptor[PdfName.MissingWidth]).DoubleValue);
    }

    /// <summary>
    /// Table 117 also requires the entry to be an <b>indirect</b> reference. The document path is
    /// the only one that owns an object-number space, and <c>ResourcePromoter</c> already promotes
    /// anything under a <c>/FontDescriptor</c> key — so the new entry has to arrive there promoted
    /// rather than inline.
    /// </summary>
    [Fact]
    public void OnTheDocumentPath_TheDescendantsDescriptorIsIndirect()
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\n/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont\n" +
            "newpath 0 0 moveto (\\202\\240) show showpage\n"));

        PostScriptConversion.ConvertDocument(input, output);
        string pdf = Encoding.Latin1.GetString(output.ToArray());

        Assert.Contains("/CIDFontType0", pdf);
        Assert.Matches(@"/FontDescriptor \d+ 0 R", pdf);
    }

    // ==================================================================
    //  5. Text operators whose cost scales with the operand
    // ==================================================================

    /// <summary>
    /// <c>stringwidth</c>, <c>show</c> and <c>charpath</c> each do one metric lookup per byte plus a
    /// full array copy, charged as a single tick — the defect class the third review closed for
    /// <c>search</c>, <c>copy</c>, <c>roll</c>, <c>astore</c> and <c>putinterval</c> and left in
    /// these three.
    ///
    /// <para>Deterministic rather than timed: with the cost charged, a budget that permits fewer
    /// ticks than the loop needs bytes is exhausted, which <c>BudgetExhausted</c> states. With it
    /// uncharged the loop is bounded only by <c>MaxTotalOps</c>, so a budget this small finishes
    /// clean.</para>
    /// </summary>
    [Theory]
    [InlineData("/s 20000 string def 0 1 40 { pop s stringwidth pop pop } for")]
    [InlineData("/s 20000 string def newpath 0 0 moveto 0 1 40 { pop s show } for")]
    [InlineData("/s 20000 string def newpath 0 0 moveto 0 1 40 { pop s false charpath } for")]
    public void PerByteTextWorkIsChargedToTheOperationBudget(string ps)
    {
        var result = Run(ps, new PostScriptConversionOptions { MaxTotalOps = 100_000 });

        Assert.True(result.BudgetExhausted,
            "the per-byte work was not charged, so the budget never noticed 800 000 bytes of it");
    }

    /// <summary>
    /// And the charge must not be so eager that ordinary text exhausts the default budget — a guard
    /// that refuses real input is a worse defect than the one it replaced.
    /// </summary>
    [Fact]
    public void OrdinaryTextIsUnaffectedByTheCharge()
    {
        var result = Run("newpath 0 0 moveto (Hello, world) show (Hello, world) stringwidth pop pop");

        Assert.False(result.BudgetExhausted);
        Assert.Contains("(Hello, world) Tj", Encoding.Latin1.GetString(result.Content ?? []));
    }
}
