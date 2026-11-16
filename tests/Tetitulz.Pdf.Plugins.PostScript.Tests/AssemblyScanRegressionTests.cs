// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Document;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Regressions from the assembly-wide scan (2026-08-13). Four defects in this plugin, three of them
/// hostile-input and all three of the same family: something a <b>file</b> controls — a DSC number,
/// an operand index, a string length — reaching arithmetic or a budget that was not written for it.
///
/// <para>Every assertion here is on the guard's own evidence: the typed PostScript error the spec
/// prescribes, the marks that survived it, a bounded diagnostic count, an exhausted budget. None is
/// on a wall clock, and none is of the form "it did not throw" — a conversion that produces nothing
/// at all does not throw either.</para>
/// </summary>
public sealed class AssemblyScanRegressionTests
{
    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), options);

    private static (byte[] Pdf, PostScriptDocumentResult Result) ConvertDocument(string ps)
    {
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(ps));
        using var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(source, output);
        return (output.ToArray(), result);
    }

    /// <summary>A blue square from (10,10) to (90,90) — a measurable extent, and visible in the stream.</summary>
    private const string Square =
        "0 0 1 setrgbcolor newpath 10 10 moveto 90 10 lineto 90 90 lineto 10 90 lineto closepath fill";

    // ==================================================================
    //  A non-finite %%BoundingBox
    // ==================================================================

    /// <summary>
    /// <c>double.TryParse(NumberStyles.Float, InvariantCulture)</c> returns <b>true</b> and
    /// <c>+infinity</c> for an overflowing literal on .NET Core 3.0 and later, and
    /// <c>PsBoundingBox.IsValid</c> only asked for <c>Width &gt; 0</c> — which infinity satisfies. So
    /// the box passed as valid and <c>PostScriptDocumentWriter</c> wrote
    /// <c>/MediaBox [0 0 Infinity Infinity]</c>, which this repo's own parser reads back as null: the
    /// page is unopenable. The second case is the mirror, and worse — it makes the <b>origin</b>
    /// infinite, so <c>1 0 0 1 Infinity Infinity cm</c> is prepended to the content stream and the
    /// stream itself will not parse.
    /// </summary>
    [Theory]
    [InlineData("0 0 1e400 1e400")]
    [InlineData("-1e400 -1e400 100 100")]
    [InlineData("0 0 Infinity Infinity")]
    [InlineData("-1e308 0 1e308 100")]   // finite corners, a width past double's range
    public void NonFiniteBoundingBox_NeverReachesTheMediaBoxOrTheOriginShift(string declared)
    {
        var (pdf, result) = ConvertDocument(
            "%!PS-Adobe-3.0\n%%BoundingBox: " + declared + "\n" + Square + " showpage\n");

        string text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("Infinity", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);

        // The decisive check: the file still opens and its page still has a real size. Asserting
        // only on the absence of the word would pass on an empty file.
        Assert.Equal(1, result.PageCount);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.NotNull(doc);

        var box = doc.Pages[0].MediaBox;
        Assert.NotNull(box);

        double N(int i) => ((PdfNumber)box[i]).DoubleValue;
        double width  = N(2) - N(0);
        double height = N(3) - N(1);

        // The refused box falls through to the measured extent of the square, which is 80x80.
        Assert.Equal(80, width,  3);
        Assert.Equal(80, height, 3);
    }

    /// <summary>
    /// The refusal is reported, not silent: a file whose stated extent was discarded is a fact its
    /// caller needs, and this plugin's rule is that what it cannot use is named rather than hidden.
    /// </summary>
    [Fact]
    public void NonFiniteBoundingBox_IsReported()
    {
        using var log = DiagnosticCollector.Start();
        ConvertDocument("%!PS-Adobe-3.0\n%%BoundingBox: 0 0 1e400 1e400\n" + Square + "\n");

        Assert.True(log.Any("non-finite"),
            "the discarded %%BoundingBox should be reported. Reported: " +
            string.Join(" | ", log.Messages));
    }

    /// <summary>A finite box is still honoured — the guard must not refuse ordinary input.</summary>
    [Fact]
    public void FiniteBoundingBox_IsStillUsedForThePageSize()
    {
        var (pdf, _) = ConvertDocument(
            "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 300\n" + Square + " showpage\n");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var box = doc.Pages[0].MediaBox;

        Assert.Equal(200, ((PdfNumber)box[2]).DoubleValue, 3);
        Assert.Equal(300, ((PdfNumber)box[3]).DoubleValue, 3);
    }

    /// <summary>
    /// The second half of that guard, tested where it lives. <c>DscScanner</c> refuses the comment
    /// and <see cref="PsBoundingBox.IsValid"/> refuses the value, and the two are independent: the
    /// box also reaches <c>PostScriptConversionResult.BoundingBox</c> and
    /// <c>PostScriptDocumentWriter</c> through paths that consult only the second.
    ///
    /// <para>NaN was never the problem — every comparison against it is false, so <c>Width &gt; 0</c>
    /// already refused it. Infinity is, because <c>infinity &gt; 0</c> is true.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0, double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NegativeInfinity, 100, 100)]
    [InlineData(0, 0, double.NaN, double.NaN)]
    public void PsBoundingBox_IsNotValidWhenACornerIsNotANumber(double llx, double lly,
                                                                double urx, double ury)
    {
        var box = new PsBoundingBox(llx, lly, urx, ury);

        Assert.False(box.IsFinite);
        Assert.False(box.IsValid);
    }

    /// <summary>A real rectangle is still valid — the guard must not refuse ordinary geometry.</summary>
    [Fact]
    public void PsBoundingBox_IsValidForAnOrdinaryRectangle()
    {
        var box = new PsBoundingBox(0, 0, 612, 792);

        Assert.True(box.IsFinite);
        Assert.True(box.IsValid);
    }

    // ==================================================================
    //  Overflowing interval indices
    // ==================================================================

    /// <summary>
    /// <c>PsObject.AsInt()</c> saturates at <c>int.MaxValue</c> instead of throwing, so a file can
    /// name that index — and <c>index + count</c> in 32-bit arithmetic then wraps to
    /// <c>int.MinValue</c>, which is not <c>&gt; Length</c>. The range check passed, the returned
    /// view carried an offset far outside its backing array, and the CLR's own bounds check was the
    /// only thing left. <c>IndexOutOfRangeException</c> is neither a <c>PsErrorException</c> nor a
    /// <c>PsBudgetExhaustedException</c>, so it escaped <c>PsInterpreter.Run</c> and cost the caller
    /// <b>every mark the program had already emitted</b>.
    ///
    /// <para>Both halves are asserted, because the second is the expensive one: a conforming
    /// <c>rangecheck</c> costs one operator, and the artwork before it survives.</para>
    /// </summary>
    [Theory]
    [InlineData("{ (abc) 2147483647 1 getinterval 0 get } exec")]
    [InlineData("{ [1 2 3] 2147483647 1 getinterval 0 get } exec")]
    [InlineData("{ (abcd) 2147483647 (x) putinterval } exec")]
    [InlineData("{ [1 2 3 4] 2147483647 [9] putinterval } exec")]
    public void OverflowingIntervalIndex_RaisesRangecheckAndKeepsTheArtwork(string bomb)
    {
        var result = Run(Square + " " + bomb);

        Assert.Contains("rangecheck", result.LastError ?? string.Empty, StringComparison.Ordinal);

        // The marks emitted before the bad operator are still there. Pre-fix `result.Content` was
        // null, because the escaping CLR exception discarded the whole conversion.
        ContentStreamAssert.Contains(result.Content, "0 0 1 rg");
        ContentStreamAssert.Contains(result.Content, "90 90 l");
    }

    /// <summary>
    /// The widened check must not refuse a legal interval — the fix is to the arithmetic, not to the
    /// rule, and PLRM §3.3.2 requires the view to alias its parent's storage.
    /// </summary>
    [Fact]
    public void OrdinaryGetIntervalStillAliasesItsParent()
    {
        // `putinterval` through the view must be visible in the parent, which is only true of a
        // shared-storage view. The result is shown so the assertion reads the emitted text.
        var result = Run("/s (ABCDEF) def s 2 3 getinterval 0 (xyz) putinterval " +
                         "/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto s show");

        string content = Encoding.Latin1.GetString(result.Content ?? Array.Empty<byte>());
        Assert.Contains("(ABxyzF)", content, StringComparison.Ordinal);
    }

    // ==================================================================
    //  Scanner diagnostics
    // ==================================================================

    /// <summary>
    /// <c>PostScriptDiagnostics.Report</c> is neither deduplicated nor capped — its own doc comment
    /// says so — and the scanner called it once per stray byte, before a single PostScript object
    /// executed and with no budget ticking. A stream of <c>'&gt;'</c> bytes therefore composed one
    /// message and fired every <c>OnEvent</c> subscriber per byte, which turns a host's logger into
    /// the amplifier.
    ///
    /// <para>The count in the summary is the other half of the claim: suppressing everything after
    /// the first would hide the difference between a typo and a bomb.</para>
    /// </summary>
    [Fact]
    public void MalformedStrayAngleBrackets_AreReportedOnceWithACount()
    {
        const int Strays = 200_000;
        var source = new StringBuilder(Strays * 2 + 8);
        for (int i = 0; i < Strays; i++) source.Append("> ");

        using var log = DiagnosticCollector.Start();
        var result = Run(source.ToString());

        Assert.InRange(result.DiagnosticCount, 1, 8);
        Assert.True(log.Any(Strays + " stray '>'"),
            "the summary should carry the count. Reported: " + string.Join(" | ", log.Messages));
    }

    /// <summary>The same amplifier, one layer down: every non-hex byte inside a hex string.</summary>
    [Fact]
    public void MalformedNonHexBytes_AreReportedOnceWithACount()
    {
        const int Bad = 100_000;

        using var log = DiagnosticCollector.Start();
        var result = Run("<" + new string('z', Bad) + ">");

        Assert.InRange(result.DiagnosticCount, 1, 8);
        Assert.True(log.Any(Bad + " non-hex character(s)"),
            "the summary should carry the count. Reported: " + string.Join(" | ", log.Messages));
    }

    /// <summary>
    /// Deduplication must not cost the diagnostic itself. One stray byte is still reported in full,
    /// with its offset — the case a developer is actually debugging.
    /// </summary>
    [Fact]
    public void ASingleStrayByte_IsStillReportedInFull()
    {
        var result = Run("> 5");

        Assert.Contains("stray '>' at byte 0 ignored", result.LastError ?? string.Empty,
                        StringComparison.Ordinal);
        Assert.Equal(1, result.DiagnosticCount);
    }

    // ==================================================================
    //  Text operators charged by string length
    // ==================================================================

    /// <summary>
    /// <c>show</c>, <c>stringwidth</c> and <c>charpath</c> each do work proportional to the operand's
    /// length — a byte-array copy plus one metric lookup per byte — and each cost the budget one
    /// tick, the same as <c>pop</c>. <c>PositionedShow</c> already ticked its byte count, so this was
    /// an inconsistency rather than a design.
    ///
    /// <para>Two assertions, and both are needed. <c>limitcheck</c> alone would also pass for
    /// <c>charpath</c> uncharged, because the path cap eventually stops that one on its own; and
    /// <c>BudgetExhausted</c> alone would also pass for <c>show</c> uncharged, because it eventually
    /// runs out of operations. Only the pair says "the <i>allocation</i> budget saw the work".</para>
    ///
    /// <para><c>xshow</c> is deliberately <b>not</b> in this set. It was already charged its byte
    /// count, so only the ordering of that charge changed here — and it was measured to still stop
    /// on this same evidence with the charge mutated away, because its per-glyph emission reaches
    /// the allocation budget by another route. A case that passes with the fix removed proves
    /// nothing, whatever its name says.</para>
    /// </summary>
    [Theory]
    [InlineData("/s 10000 string def { s stringwidth pop pop } loop")]
    [InlineData("/s 10000 string def newpath 0 0 moveto { s show } loop")]
    [InlineData("/s 10000 string def newpath 0 0 moveto { s true charpath } loop")]
    public void TextOperatorWork_IsChargedByTheStringLength(string ps)
    {
        var options = new PostScriptConversionOptions
        {
            MaxCompositeAllocationBytes = 100_000,
            MaxTotalOps = 300_000
        };

        var result = Run(ps, options);

        Assert.Contains("limitcheck", result.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.True(result.BudgetExhausted,
            "the allocation budget must be what stopped it; last error was " + result.LastError);
    }

    /// <summary>
    /// The deterministic counterpart, and the one a stopwatch cannot fake: if <c>show</c> is charged
    /// for its operand, the operation budget runs out <b>before</b> the emitted stream fills, so the
    /// output is proportional to the budget. Uncharged, the loop always reaches the 8 MiB truncation
    /// cap whatever the budget is. Same shape as <c>ClipInALoop_ExhaustsTheBudgetBeforeFillingTheStream</c>.
    /// </summary>
    [Fact]
    public void ShowInALoop_ExhaustsTheBudgetBeforeFillingTheStream()
    {
        var result = Run("/s 10000 string def newpath 0 0 moveto { s show } loop",
                         new PostScriptConversionOptions { MaxTotalOps = 150_000 });

        Assert.True(result.BudgetExhausted, "the operation budget must be what stops this");
        Assert.False(result.ContentTruncated,
            "the byte cap stopped it before the budget did, so `show` is still charged one tick");
    }

    /// <summary>
    /// The charge must not refuse ordinary text. A page of labels converts clean under the default
    /// budget and still reaches the stream — the check that keeps this guard from being a regression
    /// in disguise.
    /// </summary>
    [Fact]
    public void OrdinaryText_IsUnaffectedByTheCharge()
    {
        var source = new StringBuilder("/Helvetica findfont 12 scalefont setfont ");
        for (int i = 0; i < 200; i++)
            source.Append("newpath 10 ").Append(i * 3).Append(" moveto (Label ")
                  .Append(i).Append(") show ");

        var result = Run(source.ToString());

        Assert.Equal(200, ContentStreamAssert.Count(result.Content, "Tj"));
        Assert.True(result.IsClean, "ordinary text must convert clean: " + result.LastError);
    }

    // ==================================================================
    //  The emitter's own depth-cap report
    // ==================================================================

    /// <summary>
    /// <c>ContentStreamEmitter.Save</c> reported once per refused <c>q</c>, so anything that kept
    /// asking at the cap was a diagnostic amplifier — the same defect the scanner had.
    ///
    /// <para><c>PsGraphicsEngine</c> now raises <c>limitcheck</c> one level below this, so the
    /// program that used to reach it (<c>{ gsave } loop</c>) no longer can, which is why this is
    /// asserted on the emitter directly rather than through a conversion. A guard that is currently
    /// unreachable is exactly the one that quietly becomes reachable again.</para>
    /// </summary>
    [Fact]
    public void EmitterDepthCap_IsReportedOncePerStreamNotOncePerRefusedSave()
    {
        var emitter = new Tetitulz.Pdf.Plugins.PostScript.Emit.ContentStreamEmitter();

        using var log = DiagnosticCollector.Start();
        for (int i = 0; i < Core.Constants.PdfLimits.MaxGraphicsStateStackDepth + 200; i++)
            emitter.Save();

        int reports = 0;
        foreach (string m in log.Messages)
            if (m.Contains("gsave nesting reached", StringComparison.Ordinal)) reports++;

        Assert.Equal(1, reports);

        // …and the cap itself still holds: exactly the allowed number of `q` reached the stream.
        Assert.Equal(Core.Constants.PdfLimits.MaxGraphicsStateStackDepth,
                     ContentStreamAssert.Count(emitter.ToBytes(), "q"));
    }

    /// <summary>
    /// <c>charpath</c> reported on every call, so the same loop that the tick now bounds still
    /// composed a message per iteration. One report per conversion, like the unrepresentable-text
    /// warning — and still one, so the information is not lost.
    /// </summary>
    [Fact]
    public void CharPath_WithNoFace_IsReportedOncePerConversionNotOncePerCall()
    {
        var source = new StringBuilder("/Helvetica findfont 12 scalefont setfont newpath 0 0 moveto ");
        for (int i = 0; i < 500; i++) source.Append("(AV) false charpath ");

        var result = Run(source.ToString(), new PostScriptConversionOptions { OutlineFontProvider = _ => null });

        Assert.Contains("charpath", result.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.InRange(result.DiagnosticCount, 1, 4);
    }
}
