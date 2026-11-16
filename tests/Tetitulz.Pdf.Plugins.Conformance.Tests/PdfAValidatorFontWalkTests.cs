// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A06 — "all fonts must be embedded" — over the graph the walk actually has to cross.
///
/// <para>Two defects lived here together, and the first hid the second. The resource recursion
/// passed the form XObject <b>stream</b> where a resource dictionary was expected, so the next
/// level read <c>xobj[/Font]</c> — a key a form never carries (ISO 32000-1 Table 95 puts its
/// fonts under <c>/Resources</c>) — and A06 examined page-level fonts only. And the walk was
/// guarded by a depth cap alone, which bounds the stack but not the fan-out: two edges per node
/// cost 2^50 visits, a hang rather than a crash.</para>
///
/// <para>Every "reports A06" case here is paired with an embedded control, so none of them can
/// pass by A06 firing unconditionally.</para>
/// </summary>
public class PdfAValidatorFontWalkTests
{
    // ------------------------------------------------------------------
    //  A06 through a form XObject
    // ------------------------------------------------------------------

    /// <summary>
    /// The page names no <c>/Font</c> at all; the text is drawn inside placed artwork, which is
    /// the ordinary shape for it. Before the fix this validated clean and
    /// <see cref="PdfAConverter"/> stamped <c>pdfaid:part=1</c> on it.
    /// </summary>
    [Fact]
    public void Validate_NonEmbeddedFontReachableOnlyThroughAFormXObject_ReportsA06()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /XObject << /Fm0 5 0 R >> >>",
            (5, Form("/Resources << /Font << /F1 6 0 R >> >>")),
            (6, "<< /Type /Font /Subtype /TrueType /BaseFont /HiddenInsideForm >>"));

        var result = PdfAValidator.Validate(new MemoryStream(pdf));

        Assert.Contains(result.Violations,
            v => v.Code == "A06" && v.Message.Contains("HiddenInsideForm"));
        Assert.False(result.IsConformant);
    }

    /// <summary>
    /// The control. Same graph, same nesting — but the font carries a <c>/FontFile2</c>, so the
    /// walk that now reaches it must stay quiet. Without this the test above would pass with the
    /// embedding test deleted.
    /// </summary>
    [Fact]
    public void Validate_EmbeddedFontReachableOnlyThroughAFormXObject_DoesNotReportA06()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /XObject << /Fm0 5 0 R >> >>",
            (5, Form("/Resources << /Font << /F1 6 0 R >> >>")),
            (6, "<< /Type /Font /Subtype /TrueType /BaseFont /ReallyEmbedded " +
                "/FontDescriptor 7 0 R >>"),
            (7, "<< /Type /FontDescriptor /FontName /ReallyEmbedded /Flags 32 " +
                "/FontFile2 8 0 R >>"),
            (8, "<< /Length 1 >>\nstream\n \nendstream"));

        var result = PdfAValidator.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "A06");
    }

    /// <summary>
    /// A font two levels down, to pin that the recursion keeps descending rather than only
    /// stepping once from the page.
    /// </summary>
    [Fact]
    public void Validate_NonEmbeddedFontTwoFormXObjectsDeep_ReportsA06()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /XObject << /Fm0 5 0 R >> >>",
            (5, Form("/Resources << /XObject << /Fm1 6 0 R >> >>")),
            (6, Form("/Resources << /Font << /F1 7 0 R >> >>")),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /TwoLevelsDown >>"));

        var result = PdfAValidator.Validate(new MemoryStream(pdf));

        Assert.Contains(result.Violations,
            v => v.Code == "A06" && v.Message.Contains("TwoLevelsDown"));
    }

    /// <summary>
    /// A06 judges fonts used for rendering (ISO 19005-2 6.2.11.4.1, the published profile's 6.2.11.4.1-1): a font
    /// the resources name and the content never selects, or selects only to show text in mode 3,
    /// needs no program; shown in a painting mode it does (the control).
    /// </summary>
    [Theory]
    [InlineData("q Q", false)]
    [InlineData("BT /F1 1 Tf 3 Tr (x) Tj ET", false)]
    [InlineData("BT /F1 1 Tf 0 Tr (x) Tj ET", true)]
    [InlineData("BT /F1 1 Tf 3 Tr (x) Tj 0 Tr (y) Tj ET", true)]
    public void Validate_OnlyAFontUsedForRendering_IsReportedUnembedded(string content, bool reported)
    {
        byte[] pdf = BuildPdfWithContent(content, "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /MaybeRendered >>"));

        Assert.Equal(reported, PdfAValidator.Validate(new MemoryStream(pdf)).Violations
            .Any(v => v.Code == "A06" && v.Message.Contains("MaybeRendered")));
    }

    /// <summary>
    /// Text shown before any font is selected leaves the painted fonts unknown, and unknown means
    /// every named font is in scope — never none.
    /// </summary>
    [Fact]
    public void Validate_WhenThePaintedFontsCannotBeTold_EveryNamedFontIsJudged()
    {
        byte[] pdf = BuildPdfWithContent("BT (x) Tj ET", "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /CannotTell >>"));

        Assert.Contains(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06" && v.Message.Contains("CannotTell"));
    }

    /// <summary>Two font dictionaries of one name, both painted and unembedded: one A06, not two.</summary>
    [Fact]
    public void Validate_TwoUnembeddedFontsOfOneName_AreOneFinding()
    {
        byte[] pdf = BuildPdfWithContent("BT /F1 1 Tf (x) Tj /F2 1 Tf (y) Tj ET", "<< /Font << /F1 5 0 R /F2 6 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Twin >>"),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Twin >>"));

        Assert.Single(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06" && v.Message.Contains("Twin"));
    }

    // ------------------------------------------------------------------
    //  Hostile shapes the depth cap alone did not bound
    // ------------------------------------------------------------------

    /// <summary>
    /// The form's own resources name it twice, so the graph has branching factor two at every
    /// level. A depth cap of 50 turns that into 2^50 visits — the walk never returns. The
    /// evidence asserted is the guard's, not a stopwatch: the validation completes and still
    /// reports the font that lives inside the cycle.
    /// </summary>
    [Fact]
    public void Validate_FormXObjectResourceCycle_TerminatesAndStillReportsTheFontInside()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /XObject << /Fm0 5 0 R >> >>",
            (5, Form("/Resources << /Font << /F1 6 0 R >> " +
                     "/XObject << /A 5 0 R /B 5 0 R >> >>")),
            (6, "<< /Type /Font /Subtype /TrueType /BaseFont /InsideTheCycle >>"));

        var result = RunWithinTimeout(
            () => PdfAValidator.Validate(new MemoryStream(pdf)),
            "A06 over a two-edge form-XObject cycle");

        Assert.Contains(result.Violations,
            v => v.Code == "A06" && v.Message.Contains("InsideTheCycle"));
    }

    /// <summary>
    /// The same shape one layer down: a composite font whose <c>/DescendantFonts</c> names itself
    /// alongside a real CIDFont. Unguarded this is 2^50 visits again (and with a single
    /// self-reference, an uncatchable <see cref="StackOverflowException"/> that kills the host
    /// process rather than failing the validation). The real descendant is still reported.
    /// </summary>
    [Fact]
    public void Validate_Type0DescendantFontCycle_TerminatesAndStillReportsTheDescendant()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type0 /BaseFont /Composite " +
                "/DescendantFonts [5 0 R 6 0 R] >>"),
            (6, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /NakedCidFont >>"));

        var result = RunWithinTimeout(
            () => PdfAValidator.Validate(new MemoryStream(pdf)),
            "A06 over a self-referential /DescendantFonts array");

        Assert.Contains(result.Violations,
            v => v.Code == "A06" && v.Message.Contains("NakedCidFont"));
    }

    /// <summary>
    /// The control for the case above: an <i>embedded</i> descendant reached through the same
    /// cycle is not reported, so the cycle guard is not simply declaring everything unembedded on
    /// its way out.
    /// </summary>
    [Fact]
    public void Validate_Type0DescendantFontCycleWithEmbeddedDescendant_DoesNotReportA06()
    {
        byte[] pdf = BuildPdf(
            pageResources: "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type0 /BaseFont /Composite " +
                "/DescendantFonts [5 0 R 6 0 R] >>"),
            (6, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /EmbeddedCidFont " +
                "/FontDescriptor 7 0 R >>"),
            (7, "<< /Type /FontDescriptor /FontName /EmbeddedCidFont /Flags 4 " +
                "/FontFile2 8 0 R >>"),
            (8, "<< /Length 1 >>\nstream\n \nendstream"));

        var result = RunWithinTimeout(
            () => PdfAValidator.Validate(new MemoryStream(pdf)),
            "A06 over a self-referential /DescendantFonts array with an embedded descendant");

        Assert.DoesNotContain(result.Violations, v => v.Code == "A06");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread and fails if it has not finished in time.
    /// Copied in shape from <c>PdfUaSpecComplianceTests.RunWithinTimeout</c>: for the adversarial
    /// cycle inputs the un-guarded walk is branching^depth visits, so "it finished at all" is part
    /// of the claim, and a hung walk must fail the test rather than hang the run.
    ///
    /// <para>A dedicated <see cref="Thread"/> rather than <c>Task.Run</c>, for the reason spelled
    /// out on the original: a pool work item is not scheduled promptly under a whole-solution run,
    /// so the wall clock would measure the backlog instead of the walk.</para>
    /// </summary>
    private static T RunWithinTimeout<T>(Func<T> work, string what)
    {
        const int budgetSeconds = 30;

        T result = default;
        Exception failure = null;

        var worker = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "pdfa-font-walk-guard" };

        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(budgetSeconds));
        Assert.True(finished,
            $"{what} did not finish within {budgetSeconds}s — the cycle guard is not holding.");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    /// <summary>
    /// A form XObject stream with the given extra dictionary entries, whose body shows text in its
    /// <c>/F1</c> when it names one: A06 judges fonts used for rendering, so a font only named is
    /// not one.
    /// </summary>
    private static string Form(string extraEntries)
    {
        string body = extraEntries.Contains("/Font << /F1", StringComparison.Ordinal) ? DrawF1 : "";
        return "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] " + extraEntries +
               $" /Length {body.Length} >>\nstream\n{body}\nendstream";
    }

    /// <summary>Text shown in the resource <c>/F1</c>, in a painting mode.</summary>
    private const string DrawF1 = "BT /F1 1 Tf (x) Tj ET";

    /// <summary>
    /// Objects 1-4 are the catalog, page tree, page and content stream; everything else is
    /// emitted verbatim in <paramref name="extra"/>, which must be numbered from 5 upward.
    /// </summary>
    private static byte[] BuildPdf(string pageResources, params (int Num, string Body)[] extra)
        => BuildPdfWithContent(pageResources.Contains("/Font << /F1", StringComparison.Ordinal) ? DrawF1 : "q Q",
            pageResources, extra);

    /// <summary><see cref="BuildPdf"/> with the page's content given.</summary>
    private static byte[] BuildPdfWithContent(string content, string pageResources, params (int Num, string Body)[] extra)
    {
        var bytes = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        void Obj(int n, string body)
        {
            offsets[n] = bytes.Count;
            Emit($"{n} 0 obj\n{body}\nendobj\n");
        }


        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
               $"/Resources {pageResources} >>");
        Obj(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        foreach (var (num, body) in extra) Obj(num, body);

        int highest = extra.Length == 0 ? 4 : extra.Max(e => e.Num);
        int xref = bytes.Count;
        Emit($"xref\n0 {highest + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= highest; i++)
            Emit(offsets.TryGetValue(i, out int off)
                ? $"{off:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {highest + 1} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return bytes.ToArray();
    }
}
