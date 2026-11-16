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

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The LOW findings of the assembly-wide scan (2026-08-13) that belong to this plugin.
///
/// <para>Five of the six were confirmed and fixed. The sixth — memoising
/// <c>PdfResourceCollector.EncodingKey</c> by encoding-instance identity — is an explicitly
/// unmeasured performance hypothesis, and both this repo's rule ("measure before you rank") and the
/// finding's own instruction ("if the delta is in the noise beside the interpreter loop, leave it
/// alone and record the measurement") make guessing the wrong move. It is not fixed here and has no
/// test.</para>
/// </summary>
public sealed class LowScanRegressionTests
{
    private static PostScriptConversionResult Run(string ps, PostScriptConversionOptions options = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps), options);

    private static (byte[] Pdf, List<string> Reports) ConvertDocumentCollecting(string ps)
    {
        using var collector = DiagnosticCollector.Start();
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(ps));
        using var output = new MemoryStream();
        PostScriptConversion.ConvertDocument(source, output);
        return (output.ToArray(), new List<string>(collector.Messages));
    }

    // ==================================================================
    //  The nested-stream counter is finally read
    // ==================================================================

    /// <summary>
    /// A minimal but structurally real Type 1 program, terminated properly so it embeds.
    /// </summary>
    private static string FontProgram(string name)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-AdobeFont-1.0: ").Append(name).Append(" 001.000\n");
        sb.Append("/FontName /").Append(name).Append(" def\n");
        sb.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n");
        sb.Append("/FontType 1 def\n/Encoding StandardEncoding def\ncurrentfile eexec\n");
        sb.Append("A1B2C3D4E5F60718293A4B5C6D7E8F90\n");
        sb.Append(new string('0', 512)).Append("\ncleartomark\n");
        return sb.ToString();
    }

    /// <summary>
    /// <c>PdfWriter.NestedStreamsWrittenWithoutData</c> was added <b>for this caller</b> — its own
    /// doc names <c>ResourcePromoter in the PostScript plugin</c> — and <c>Write</c> never read it.
    /// So the exact defect it exists to catch, an embedded <c>/FontFile</c> serialised as dictionary
    /// entries with the program dropped, would have recurred silently while the writer went on
    /// returning a page count as though the file were complete.
    ///
    /// <para>The condition is unreachable through the public API while the promoter works, so what
    /// is asserted is the pair: the program's bytes really are in the output, <b>and</b> nothing was
    /// reported lost. Breaking the promoter flips both — the bytes vanish and the new check fires —
    /// which is what makes this falsifiable rather than a check on a check.</para>
    /// </summary>
    [Fact]
    public void AnEmbeddedFontProgram_ReachesTheOutputAndNoStreamIsReportedLost()
    {
        var (pdf, reports) = ConvertDocumentCollecting(
            "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 200 200\n" +
            FontProgram("Promoted") + "/Promoted 12 dict definefont pop " +
            "/Promoted findfont 12 scalefont setfont newpath 0 0 moveto (A) show showpage\n");

        // Asserted FIRST, on purpose. With the byte check first, breaking the promoter fails on the
        // missing bytes and says nothing about whether the counter check works — which would leave
        // the two new lines untested behind a red test. This order makes the diagnostic the thing
        // that goes red.
        Assert.DoesNotContain(reports, r => r.Contains("lost their data"));

        // And the program really is in the file: the captured bytes are passed through verbatim, so
        // the header line can only appear if the stream was promoted to an indirect object. Without
        // this, the assertion above would pass against a conversion that embedded nothing at all.
        Assert.Contains("/FontName /Promoted def", PdfStreamText.Decoded(pdf));
    }

    // The CIDSystemInfo /Supplement finding is pinned in CjkCompositeFontTests instead: the CJK
    // path needs Tetitulz.Pdf.Plugins.CMap registered, that class already does it in its static
    // constructor, and it is the file the finding names as asserting /Ordering and never
    // /Supplement. Duplicating the registration here would be a second place to keep in step.

    // ==================================================================
    //  FindMark is charged before it scans
    // ==================================================================

    /// <summary>
    /// <c>FindMark</c> walks the operand stack to <c>MaxOperandStackDepth</c> (10 000) entries, and
    /// all four callers charged only what they <i>found</i>: <c>]</c> and <c>&gt;&gt;</c> tick the
    /// item count once the index is known, and <c>cleartomark</c>/<c>counttomark</c> charged
    /// nothing. The full-depth scan — including the not-found path, which has no result to charge
    /// afterwards — was invisible to the budget, so a loop over a deep stack performed thousands of
    /// comparisons per iteration for about four ticks.
    ///
    /// <para>The evidence is the budget's own verdict against a stated op ceiling, on a
    /// <b>bounded</b> repeat: charged, 200 scans of a ~3 000-deep stack exceed 100 000 ops;
    /// uncharged, the whole program costs on the order of ten thousand. Fast either way, and no
    /// wall clock is involved.</para>
    /// </summary>
    [Fact]
    public void ScanningForAMark_IsChargedToTheOperationBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };

        var result = Run("mark 0 1 2999 { } for 200 { counttomark pop } repeat", options);

        Assert.True(result.BudgetExhausted,
            "200 scans of a 3 000-deep operand stack must be visible to a 100 000-op budget.");
    }

    /// <summary>
    /// A shallow stack does not trip the same ceiling — otherwise the test above would pass against
    /// a charge that had simply been made enormous.
    /// </summary>
    [Fact]
    public void ScanningForAMarkOnAShallowStack_StaysWithinTheOperationBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };

        var result = Run("mark 1 2 3 200 { counttomark pop } repeat cleartomark " +
                         "newpath 0 0 moveto 100 0 lineto stroke", options);

        Assert.False(result.BudgetExhausted);
        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    // ==================================================================
    //  filter and setcacheparams consume what the PLRM says
    // ==================================================================

    /// <summary>
    /// <c>("filter", 0, 1)</c> in the fixed-arity table popped nothing and pushed one object, which
    /// is the desynchronisation that table's own doc says it exists to prevent.
    /// <c>currentfile /ASCII85Decode filter</c> is a routine prologue line: it left <b>three</b>
    /// objects where the program expects one file, and the next <c>]</c>, <c>counttomark</c>,
    /// <c>stopped</c> unwind or <c>image</c> read the two strays as its own operands.
    ///
    /// <para>The leftover depth is turned into a coordinate, so the emitted stream carries the
    /// answer: one object draws to (100, 0), three draw to (300, 0).</para>
    /// </summary>
    [Fact]
    public void Filter_ConsumesItsParameterAndItsDataSource()
    {
        var result = Run(
            "/page { mark currentfile /ASCII85Decode filter counttomark /n exch def cleartomark " +
            "newpath 0 0 moveto n 100 mul 0 lineto stroke } def page");

        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    /// <summary>
    /// <c>filter</c> over a <b>file</b> is implemented as of 2026-08-19 — it is how an EPS embeds
    /// image data — so it is no longer reported. The operand-balance contract this test was written
    /// for still holds and is what it now asserts: one file object comes back, whatever the filter.
    ///
    /// <para>Renamed rather than edited in place. The old name said "is still reported as
    /// unimplemented", and leaving that name on an assertion about something else is the exact
    /// name/body disagreement CLAUDE.md records as the failure no guard can catch.</para>
    /// </summary>
    [Fact]
    public void Filter_OverAFile_YieldsOneFileObject()
    {
        var result = Run("clear currentfile /ASCII85Decode filter count 0 " +
                         "newpath moveto 10 10 lineto stroke");

        // One object on the stack — the file — not the two the unbalanced version left.
        Assert.Equal(1.0, ContentStreamAssert.FirstOperandsOf(result.Content, "m")[0]);
        Assert.DoesNotContain("filter", result.UnimplementedOperators);
    }

    /// <summary>
    /// …but a filter over something that is no data source — not a file, a string or a procedure
    /// (PLRM §3.13.1) — still reports; a string is one (the control).
    /// </summary>
    [Fact]
    public void Filter_OverSomethingThatIsNoDataSource_IsStillReportedAsUnimplemented()
    {
        Assert.Contains("filter", Run("5 /ASCII85Decode filter pop").UnimplementedOperators);
        Assert.DoesNotContain("filter", Run("(data) /ASCII85Decode filter pop").UnimplementedOperators);
    }

    /// <summary>
    /// <c>setcacheparams</c> consumes everything back to the topmost mark, so <c>("setcacheparams",
    /// 0, 0)</c> left the whole parameter list on the stack. Two marks are used so
    /// <c>counttomark</c> still has one to count to afterwards: consumed, the inner mark is gone and
    /// the count is 0; left behind, the inner mark is still there and the count is 3.
    /// </summary>
    [Fact]
    public void SetCacheParams_ConsumesBackToTheMark()
    {
        var result = Run(
            "/page { mark mark 1 2 3 setcacheparams counttomark /n exch def cleartomark " +
            "newpath 0 0 moveto n 100 add 0 lineto stroke } def page");

        Assert.Equal(100, ContentStreamAssert.FirstOperandsOf(result.Content, "l")[0], 3);
    }

    /// <summary>
    /// With no mark on the stack a conforming interpreter raises <c>unmatchedmark</c> — and raising
    /// it is what stops the operator from clearing a stack that never had one. <c>stopped</c>
    /// pushing true is the guard's own evidence.
    /// </summary>
    [Fact]
    public void SetCacheParamsWithNoMark_RaisesUnmatchedMark()
    {
        var result = Run(
            "/page { 40 50 60 { setcacheparams } stopped " +
            "{ newpath 0 0 moveto 100 0 lineto stroke } if } def page");

        Assert.Equal(1, ContentStreamAssert.Count(result.Content, "S"));
    }

    private static PdfDictionary FirstFont(PdfDictionary resources)
    {
        var fonts = (PdfDictionary)resources[PdfName.Font];
        foreach (var key in fonts.Keys) return (PdfDictionary)fonts[key];
        throw new Xunit.Sdk.XunitException("no font resource was registered.");
    }
}
