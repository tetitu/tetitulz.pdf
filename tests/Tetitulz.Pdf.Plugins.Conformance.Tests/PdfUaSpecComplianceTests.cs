// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Spec-conformance tests for the PDF/UA plugin surface (<see cref="PdfUaChecker"/> and
/// <see cref="PdfAccessibilityFixer"/>), pinning down the defects that a nine-point review
/// of the accessibility stack turned up:
///
/// <list type="number">
///   <item><b>/Tabs, not /TabOrder.</b> ISO 32000-1 Table 30 names the page tab-order key
///     <c>/Tabs</c>. <c>/TabOrder</c> is not a PDF key at all, so the old check could never be
///     satisfied by a file any other producer wrote, and the old "fix" wrote a key no viewer
///     reads — the document looked repaired and behaved exactly as before.</item>
///   <item><b>Indirect references.</b> Every value in a PDF dictionary may be
///     <c>n g R</c>. Bare casts turned the legal, common <c>/MarkInfo 9 0 R</c> into a
///     fabricated UA01 (and the same class of bug hid real violations behind an indirect
///     /AcroForm or /Fields).</item>
///   <item><b>Encryption.</b> PDF/UA-1 permits encryption; what it requires is that the
///     security handler allow extraction for assistive technology (/P bit 10). The old
///     checker reported UA06 for any encrypted file and then <i>returned</i>, so an encrypted
///     document got a one-line report and every other defect went unmentioned.</item>
///   <item><b>Unicode safety.</b> The fixer's hand-rolled writer encoded objects through code
///     page 28591 (ISO-8859-1), replacing every character above U+00FF with '?' — a Hebrew or
///     CJK tooltip was destroyed by the very operation that claimed to add it.</item>
///   <item><b>Cycle safety.</b> The structure-tree and form-field walks were depth-capped but
///     not cycle-guarded, so a /K or /Kids graph pointing back at an ancestor cost
///     branching^depth visits.</item>
/// </list>
///
/// <para>
/// Assembly boundary: this is the <c>Tetitulz.Pdf.Plugins.Conformance</c> test project, so
/// everything here goes through the plugin's own public API. The
/// <c>Tetitulz.Pdf.Accessibility</c> feature is deliberately not referenced — plugin → feature
/// would invert the dependency direction CLAUDE.md fixes.
/// </para>
///
/// <para>Every PDF is built in memory as a <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class PdfUaSpecComplianceTests
{
    // ==================================================================
    //  Builders — objects are numbered 1..N in the order supplied.
    // ==================================================================

    private static byte[] BuildPdf(params string[] objects)
        => BuildPdfWithTrailer(string.Empty, objects);

    private static byte[] BuildPdfWithTrailer(string extraTrailerEntries, params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n{1}\nendobj\n", i + 1, objects[i]);
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objects.Length + 1);
        foreach (int off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R{1} >>\n",
            objects.Length + 1, extraTrailerEntries);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Body of a stream object: the dictionary plus a correctly-declared /Length.</summary>
    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>A well-formed XMP packet declaring <c>pdfuaid:part</c>.</summary>
    private static string XmpPacket(string partValue)
        => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
         + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n"
         + " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n"
         + "  <rdf:Description rdf:about=\"\""
         + " xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n"
         + "   <pdfuaid:part>" + partValue + "</pdfuaid:part>\n"
         + "  </rdf:Description>\n"
         + " </rdf:RDF>\n"
         + "</x:xmpmeta>\n"
         + "<?xpacket end=\"w\"?>";

    /// <summary>
    /// Catalog + one page carrying a widget annotation that is also the (untitled) form field,
    /// which is how every real AcroForm is shaped. The page deliberately has no tab-order key.
    /// </summary>
    private static byte[] BuildFormPdf(string fieldEntries = "/FT /Tx /T (field1)")
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget " + fieldEntries + " /Rect [0 0 100 20] >>");

    /// <summary>One page carrying one annotation, with whatever page entries the caller wants.</summary>
    private static byte[] BuildAnnotatedPagePdf(string pageExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] "
                + pageExtras + " >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>");

    /// <summary>A page whose /Resources declare exactly one font.</summary>
    /// <summary>
    /// A page that draws text with the font as <c>/F1</c> (object 4), the font's own objects
    /// following it. The page draws, because UA25 asks ISO 14289-1 7.21.4.1's question about fonts
    /// <i>used</i> for rendering, and a font a resource dictionary merely names is not; the content
    /// stream is the last object, so the font objects keep the numbers the callers give them.
    /// </summary>
    private static byte[] BuildFontPdf(params string[] fontObjects)
        => BuildFontPdfDrawing("BT /F1 12 Tf 72 700 Td (x) Tj ET", fontObjects);

    /// <summary><see cref="BuildFontPdf"/> with the page's content given.</summary>
    private static byte[] BuildFontPdfDrawing(string content, params string[] fontObjects)
    {
        int contentNumber = 4 + fontObjects.Length;
        var objects = new string[4 + fontObjects.Length];
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        objects[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                   + "/Contents " + contentNumber + " 0 R /Resources << /Font << /F1 4 0 R >> >> >>";
        Array.Copy(fontObjects, 0, objects, 3, fontObjects.Length);
        objects[3 + fontObjects.Length] = StreamObject(string.Empty, content);
        return BuildPdf(objects);
    }

    // ==================================================================
    //  Harness
    // ==================================================================

    private static PdfUaValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms);
    }

    private static byte[] RunFix(byte[] input, AccessibilityFixOptions? options = null)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        PdfAccessibilityFixer.Fix(inStream, outStream, options);
        return outStream.ToArray();
    }

    private static AccessibilityFixResult RunFix(
        byte[] input, out byte[] output, AccessibilityFixOptions? options = null)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(inStream, outStream, options);
        output = outStream.ToArray();
        return result;
    }

    /// <summary>
    /// Re-opens a produced PDF and returns its first page dictionary. Assertions about what
    /// the fixer wrote go through this rather than through a byte-level string search: a
    /// substring proves only that some bytes exist somewhere, not that a conforming reader
    /// resolves the key on the page it belongs to (and it cannot distinguish the new revision
    /// from the original bytes an incremental update carries along).
    /// </summary>
    private static PdfDictionary ReadFirstPage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.Single(doc.Pages);
        return doc.Pages[0].Dictionary;
    }

    /// <summary>Re-opens a produced PDF and returns the /TU of the first AcroForm field.</summary>
    private static PdfString? ReadFirstFieldTooltip(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var acroForm = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acroForm);
        var fields = doc.ResolveObject(acroForm![PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        var field = doc.ResolveObject(fields![0]) as PdfDictionary;
        Assert.NotNull(field);
        return field![PdfName.TU] as PdfString;
    }

    private static string Describe(PdfUaValidationResult result)
        => result.Violations.Count == 0
            ? "(no violations)"
            : string.Join(" | ", result.Violations.Select(v => v.ToString()));

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread and fails if it has not finished within
    /// the budget. Used for the adversarial cycle inputs: without a visited set those walks do
    /// branching^depth visits (3^100 for the graphs below), so "it finished at all" is the
    /// assertion. A hung walk fails the test instead of hanging the run.
    ///
    /// <para><b>A dedicated <see cref="Thread"/>, not <c>Task.Run</c>.</b> A pool work item is not
    /// scheduled promptly when 26 test assemblies share the same cores, so the elapsed time would
    /// measure the machine's backlog rather than the walk — the mechanism behind three measured
    /// flakes on 2026-08-23. A dedicated thread starts regardless of pool pressure, and blocking on
    /// <see cref="Thread.Join(TimeSpan)"/> cannot deadlock a test the way <c>Task.Wait</c> can.</para>
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
        { IsBackground = true, Name = "ua-spec-guard" };

        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(budgetSeconds));
        Assert.True(finished,
            $"{what} did not finish within {budgetSeconds}s — the cycle guard is not holding.");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    // ==================================================================
    //  1. UA09 — the page tab-order key is /Tabs (ISO 32000-1 Table 30)
    // ==================================================================

    [Fact]
    public void Validate_AnnotatedPageDeclaringTabsS_ReportsNoTabOrderViolation()
    {
        // The whole point of the rename: a file produced by any conforming producer declares
        // /Tabs, and used to be reported non-conforming because the checker wanted /TabOrder.
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /S"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA09");
    }

    [Fact]
    public void Validate_AnnotatedPageWithNoTabKeyAtAll_ReportsUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf(string.Empty));
        Assert.Contains(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// /Tabs is present but names row order, not structure order. PDF/UA-1 requires the tab
    /// order to follow the structure tree, so only the value /S satisfies the check —
    /// "the key exists" is not enough.
    /// </summary>
    [Fact]
    public void Validate_AnnotatedPageWithTabsRowOrder_ReportsUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /R"));
        Assert.Contains(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// Earlier versions of this library emitted the non-existent /TabOrder key. Tolerating it on
    /// read looked charitable and was the one thing a conformance checker must never do: /TabOrder
    /// is not in ISO 32000-1 Table 30, no reader honours it, and a page carrying only it genuinely
    /// fails ISO 14289-1 §7.18.3 — an independent checker, the published profiles and this library's own SetStructureTabOrder all
    /// say so. It is reported, and the message names the legacy key so the fix is obvious.
    /// </summary>
    [Fact]
    public void Validate_LegacyTabOrderKey_IsReportedAndNamedInTheMessage()
    {
        var result = Validate(BuildAnnotatedPagePdf("/TabOrder /S"));

        var ua09 = Assert.Single(result.Violations, v => v.Code == "UA09");
        Assert.Contains("/TabOrder", ua09.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_PageWithoutAnnotations_DoesNotRequireATabKey()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA09");
    }

    /// <summary>An /Annots array that is present but empty presents no annotation to tab to.</summary>
    [Fact]
    public void Validate_PageWithEmptyAnnotsArray_DoesNotRequireATabKey()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [] >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// The fix must be visible to a reader, not just to a text search: re-open the output and
    /// read the key off the page dictionary the parser resolves.
    /// </summary>
    [Fact]
    public void Fix_AnnotatedPage_WritesTabsSIntoTheReopenedPageDictionary()
    {
        byte[] output = RunFix(BuildFormPdf());
        var page = ReadFirstPage(output);

        var tabs = page[PdfName.Tabs] as PdfName;
        Assert.NotNull(tabs);
        Assert.Equal("S", tabs!.Value);

        // And the key that does not exist in the specification is not invented here.
        Assert.Null(page[PdfName.TabOrder]);
    }

    /// <summary>
    /// A file this library produced before the correction carries /TabOrder /S. The checker
    /// tolerates it, but the fixer must still add the real key — otherwise a document that no
    /// viewer tabs correctly would be considered already repaired.
    /// </summary>
    [Fact]
    public void Fix_PageCarryingLegacyTabOrder_StillGainsARealTabsKey()
    {
        byte[] input = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /TabOrder /S >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field1) /Rect [0 0 100 20] >>");

        var result = RunFix(input, out byte[] output);

        var tabs = ReadFirstPage(output)[PdfName.Tabs] as PdfName;
        Assert.NotNull(tabs);
        Assert.Equal("S", tabs!.Value);
        Assert.Contains(result.FixesApplied, f => f.Contains("/Tabs"));
    }

    [Fact]
    public void Fix_PageWithoutAnnotations_DoesNotGainATabsKey()
    {
        byte[] input = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        byte[] output = RunFix(input);
        Assert.Null(ReadFirstPage(output)[PdfName.Tabs]);
    }

    /// <summary>
    /// End-to-end: the checker must accept what the fixer produced. This is the assertion the
    /// /TabOrder-era code could never satisfy — it wrote a key its own checker did not read
    /// through the page tree of a re-parsed incremental update.
    /// </summary>
    [Fact]
    public void Fix_Output_ClearsTheViolationsItClaimsToFix()
    {
        var fixes = RunFix(BuildFormPdf(), out byte[] output);
        var after = Validate(output);

        Assert.DoesNotContain(after.Violations, v => v.Code == "UA09"); // /Tabs /S written
        Assert.DoesNotContain(after.Violations, v => v.Code == "UA02"); // /Lang written
        Assert.DoesNotContain(after.Violations, v => v.Code == "UA08"); // /TU written
        Assert.DoesNotContain(after.Violations,
            v => v.Code == "UA04" && v.Message.Contains("DisplayDocTitle"));

        // …and only those. The fixer does not invent a document title, so that half of UA04
        // must still be reported: a repaired-looking file that silently lost a real finding
        // would be worse than no fixer at all.
        Assert.Contains(after.Violations,
            v => v.Code == "UA04" && v.Message.Contains("/Info"));
        Assert.True(fixes.AnyFixApplied, "The fixer reported no fixes for a document missing all four.");
    }

    // ==================================================================
    //  2. Indirect references — every entry the checker reads may be `n g R`
    // ==================================================================

    [Fact]
    public void Validate_IndirectMarkInfo_ReportsNoUa01()
    {
        // `/MarkInfo 4 0 R` is legal and extremely common; the bare cast that preceded the
        // fix reported "the /MarkInfo entry is not a dictionary" for every such file.
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Marked true >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    /// <summary>
    /// Resolution has to be recursive, not one level deep: the /Marked boolean inside an
    /// indirect /MarkInfo may itself be stored indirectly.
    /// </summary>
    [Fact]
    public void Validate_IndirectMarkInfoWithIndirectMarkedBoolean_ReportsNoUa01()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Marked 5 0 R >>",
            "true");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    /// <summary>
    /// Control for the two tests above: resolving must not become "assume tagged". An
    /// indirect /MarkInfo that genuinely says <c>/Marked false</c> is still a UA01.
    /// </summary>
    [Fact]
    public void Validate_IndirectMarkInfoDeclaringMarkedFalse_StillReportsUa01()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Marked 5 0 R >>",
            "false");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    [Fact]
    public void Validate_IndirectViewerPreferences_SatisfiesTheDisplayDocTitleCheck()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /DisplayDocTitle true >>");

        Assert.DoesNotContain(Validate(pdf).Violations,
            v => v.Code == "UA04" && v.Message.Contains("DisplayDocTitle"));
    }

    [Fact]
    public void Validate_ViewerPreferencesWithoutDisplayDocTitle_ReportsUa04()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /HideToolbar true >>");

        Assert.Contains(Validate(pdf).Violations,
            v => v.Code == "UA04" && v.Message.Contains("DisplayDocTitle"));
    }

    [Fact]
    public void Validate_IndirectStructTreeRoot_ReportsNoUa05()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /Document >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA05");
    }

    /// <summary>
    /// Resolving must not be confused with accepting: a /StructTreeRoot that resolves to
    /// something other than a dictionary gives the document no usable structure tree.
    /// </summary>
    [Fact]
    public void Validate_StructTreeRootResolvingToANonDictionary_ReportsUa05()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "[1 2 3]");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA05");
    }

    /// <summary>
    /// The document title lives in the trailer's /Info, which is an indirect reference in
    /// essentially every real file.
    /// </summary>
    [Fact]
    public void Validate_IndirectInfoDictionaryWithTitle_SatisfiesTheTitleCheck()
    {
        byte[] pdf = BuildPdfWithTrailer(" /Info 4 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Title (Quarterly Report) >>");

        Assert.DoesNotContain(Validate(pdf).Violations,
            v => v.Code == "UA04" && v.Message.Contains("/Info"));
    }

    /// <summary>An empty /Title is the same as having none — presence is not a title.</summary>
    [Fact]
    public void Validate_IndirectInfoDictionaryWithEmptyTitle_ReportsUa04()
    {
        byte[] pdf = BuildPdfWithTrailer(" /Info 4 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Title () >>");

        Assert.Contains(Validate(pdf).Violations,
            v => v.Code == "UA04" && v.Message.Contains("/Info"));
    }

    /// <summary>
    /// An indirect /AcroForm used to make the whole form invisible to the audit, which
    /// reported a clean bill of health for a file full of unlabelled fields.
    /// </summary>
    [Fact]
    public void Validate_IndirectAcroForm_StillFindsTheUnlabelledField()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Fields [5 0 R] >>",
            "<< /FT /Tx /T (field1) >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA08");
    }

    [Fact]
    public void Validate_IndirectAcroFormWithLabelledField_ReportsNoUa08()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Fields [5 0 R] >>",
            "<< /FT /Tx /T (field1) /TU (Your full name) >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA08");
    }

    /// <summary>/Lang is read through the same resolver, so an indirect string must count.</summary>
    [Fact]
    public void Validate_IndirectLanguageString_ReportsNoUa02()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Lang 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "(en-US)");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA02");
    }

    [Fact]
    public void Validate_IndirectMetadataStreamDeclaringPart1_ReportsNoUa03()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("1")));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA03");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    [Fact]
    public void Validate_MetadataDeclaringADifferentPart_ReportsUa03()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("2")));

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA03");
    }

    /// <summary>
    /// XMP Part 1 §7.9.2.2 lets a simple property be written as an attribute of the
    /// <c>rdf:Description</c> rather than as a child element; UA03 accepts the declaration either way.
    /// </summary>
    [Fact]
    public void Validate_MetadataDeclaringPart1AsAnAttribute_ReportsNoUa03()
    {
        const string packet =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
          + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n"
          + " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n"
          + "  <rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\""
          + " pdfuaid:part=\"1\"/>\n"
          + " </rdf:RDF>\n"
          + "</x:xmpmeta>\n"
          + "<?xpacket end=\"w\"?>";

        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", packet));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA03");
    }

    /// <summary>
    /// Integration: a document in which <i>every</i> entry the checker reads is an indirect
    /// reference — /MarkInfo (and its /Marked boolean), /Lang, /ViewerPreferences (and its
    /// /DisplayDocTitle boolean), /StructTreeRoot, /Metadata and the trailer's /Info — and
    /// which is otherwise correct, must come back conforming. Before the resolver fixes this
    /// exact file produced a fistful of fabricated violations.
    ///
    /// <para>
    /// The structure tree here is deliberately a <em>connected</em> one: the Document element
    /// carries an MCID, the page carries the matching /StructParents, and the /StructTreeRoot
    /// carries a /ParentTree joining them. An earlier version of this fixture had a bare
    /// <c>&lt;&lt; /Type /StructElem /S /Document &gt;&gt;</c> with no /K and no /ParentTree —
    /// i.e. a hollow tree, which UA29 now correctly reports. That made this test assert that a
    /// hollow document conforms, which is the opposite of what it is for; the fixture, not the
    /// check, was wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void Validate_FullyIndirectButConformingDocument_ReportsNoViolations()
    {
        byte[] pdf = BuildPdfWithTrailer(" /Info 7 0 R",
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R /Lang 5 0 R "
                + "/ViewerPreferences 6 0 R /StructTreeRoot 8 0 R /Metadata 10 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Contents 14 0 R /StructParents 0 >>",
            "<< /Marked 11 0 R >>",
            "(en-US)",
            "<< /DisplayDocTitle 12 0 R >>",
            "<< /Title (Quarterly Report) >>",
            "<< /Type /StructTreeRoot /K 9 0 R /ParentTree 13 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 8 0 R /Pg 3 0 R /K 0 >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("1")),
            "true",
            "true",
            "<< /Nums [0 [9 0 R]] >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Hello) Tj ET\nEMC"));

        var result = Validate(pdf);
        Assert.True(result.IsConforming, Describe(result));
    }

    // ==================================================================
    //  2b. UA28 / UA29 — a hollow structure tree is not conformance
    // ==================================================================

    /// <summary>
    /// The single worst outcome an accessibility checker can produce is calling a hollow file
    /// conformant: a /StructTreeRoot, some /StructElem objects that point at nothing, no
    /// /ParentTree, and no marked content anywhere. Everything a dictionary-level check can see
    /// looks right, and a screen reader gets nothing at all.
    /// </summary>
    [Fact]
    public void Validate_HollowStructureTree_IsReportedAndNotConforming()
    {
        byte[] pdf = BuildHollowTaggedPdf();

        var result = Validate(pdf);

        Assert.False(result.IsConforming, Describe(result));
        Assert.Contains(result.Violations, v => v.Code == "UA29");
    }

    /// <summary>
    /// UA29's message must name how many of how many elements are unconnected, so the finding
    /// can be acted on without re-running anything.
    /// </summary>
    [Fact]
    public void Validate_HollowStructureTree_Ua29CountsTheUnconnectedElements()
    {
        var violation = Assert.Single(Validate(BuildHollowTaggedPdf()).Violations, v => v.Code == "UA29");

        // Two elements (P and H1), both empty; the /Document parent reaches them, so it is not.
        Assert.Contains("2 of 3", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart control: the same document with its elements pointing at real MCIDs and a
    /// /ParentTree in place must report neither UA28 nor UA29. Without this, UA29 firing on
    /// everything would look like a pass.
    /// </summary>
    [Fact]
    public void Validate_ConnectedStructureTree_ReportsNeitherUa28NorUa29()
    {
        var violations = Validate(BuildConnectedTaggedPdf()).Violations;

        Assert.DoesNotContain(violations, v => v.Code == "UA28");
        Assert.DoesNotContain(violations, v => v.Code == "UA29");
    }

    /// <summary>
    /// A tree whose elements <i>do</i> carry MCIDs but which has no /ParentTree is reported by
    /// UA28: the MCIDs name content that can never be resolved back to an element
    /// (ISO 32000-1 Table 322).
    /// </summary>
    [Fact]
    public void Validate_ConnectedTreeWithoutParentTree_ReportsUa28()
    {
        byte[] pdf = BuildTaggedPdf(
            structTreeEntries: "/K 9 0 R",                     // no /ParentTree
            documentKids: "/K [11 0 R 12 0 R]",
            firstElement: "<< /Type /StructElem /S /H1 /P 9 0 R /Pg 3 0 R /K 0 >>",
            secondElement: "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K 1 >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA28");
    }

    /// <summary>
    /// An empty &lt;TD&gt; keeps a table grid rectangular and is explicitly permitted (/K is
    /// Optional, ISO 32000-1 Table 323), so it must not be counted as an unconnected element.
    /// </summary>
    [Fact]
    public void Validate_EmptyTableCell_IsNotReportedAsUnconnected()
    {
        byte[] pdf = BuildPdfWithTrailer(" /Info 7 0 R",
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R /Lang 5 0 R "
                + "/ViewerPreferences 6 0 R /StructTreeRoot 8 0 R /Metadata 10 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Contents 14 0 R /StructParents 0 >>",
            "<< /Marked true >>",
            "(en-US)",
            "<< /DisplayDocTitle true >>",
            "<< /Title (Grid) >>",
            "<< /Type /StructTreeRoot /K 9 0 R /ParentTree 13 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /TR /P 8 0 R /K [11 0 R 12 0 R] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("1")),
            "<< /Type /StructElem /S /TD /P 9 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /TD /P 9 0 R >>",          // the deliberately empty cell
            "<< /Nums [0 [11 0 R]] >>",
            StreamObject("", "/TD <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (x) Tj ET\nEMC"));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA29");
    }

    /// <summary>
    /// A tagged document skeleton whose structure elements and /ParentTree can be varied.
    /// Everything else (title, language, XMP, DisplayDocTitle) is correct so the only violations
    /// a test can see are the ones it set up.
    /// </summary>
    private static byte[] BuildTaggedPdf(
        string structTreeEntries, string documentKids, string firstElement, string secondElement,
        string parentTree = "<< /Nums [0 [11 0 R 12 0 R]] >>")
        => BuildPdfWithTrailer(" /Info 7 0 R",
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R /Lang 5 0 R "
                + "/ViewerPreferences 6 0 R /StructTreeRoot 8 0 R /Metadata 10 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Contents 14 0 R /StructParents 0 >>",
            "<< /Marked true >>",
            "(en-US)",
            "<< /DisplayDocTitle true >>",
            "<< /Title (Report) >>",
            "<< /Type /StructTreeRoot " + structTreeEntries + " /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 8 0 R " + documentKids + " >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("1")),
            firstElement,
            secondElement,
            parentTree,
            StreamObject("", "/H1 <</MCID 0>> BDC\nBT /F1 24 Tf 72 700 Td (Title) Tj ET\nEMC\n"
                          + "/P <</MCID 1>> BDC\nBT /F1 12 Tf 72 650 Td (Body) Tj ET\nEMC"));

    /// <summary>Structure elements that reference nothing at all, and no /ParentTree.</summary>
    private static byte[] BuildHollowTaggedPdf()
        => BuildTaggedPdf(
            structTreeEntries: "/K 9 0 R",
            documentKids: "/K [11 0 R 12 0 R]",
            firstElement: "<< /Type /StructElem /S /H1 /P 9 0 R >>",
            secondElement: "<< /Type /StructElem /S /P /P 9 0 R >>");

    /// <summary>The same document, correctly connected to its content.</summary>
    private static byte[] BuildConnectedTaggedPdf()
        => BuildTaggedPdf(
            structTreeEntries: "/K 9 0 R /ParentTree 13 0 R",
            documentKids: "/K [11 0 R 12 0 R]",
            firstElement: "<< /Type /StructElem /S /H1 /P 9 0 R /Pg 3 0 R /K 0 >>",
            secondElement: "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K 1 >>");

    // ==================================================================
    //  3. UA06 — encryption is allowed; denying accessibility is not
    // ==================================================================

    /// <summary>
    /// The checker only reads /P, /R and /Filter out of the encryption dictionary, so a
    /// hand-built dictionary exercises it exactly as a real one would — no key derivation
    /// is involved.
    /// </summary>
    private static byte[] BuildEncryptedAnnotatedPdf(
        string permissionEntry, int revision, string filter = "/Standard")
    {
        string encrypt =
            "<< /Filter " + filter + " /V 2 /R " + revision + " /Length 128 " + permissionEntry
            + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
            + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

        return BuildPdfWithTrailer(" /Encrypt 5 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // Annotated, and with no tab key: a page-level defect that the old early-return
            // hid completely.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>",
            encrypt);
    }

    /// <summary>
    /// /P = -4 has every permission bit set, including bit 10 (value 512, "extract for
    /// assistive technology"). ISO 14289-1 §7.1 asks for exactly that, not for the absence
    /// of encryption.
    /// </summary>
    [Fact]
    public void Validate_EncryptedButPermittingAccessibilityExtraction_ReportsNoUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf("/P -4", revision: 3));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>-516 is -4 with bit 10 (0x200) cleared: assistive technology is locked out.</summary>
    [Fact]
    public void Validate_EncryptedAndDenyingAccessibilityExtraction_ReportsUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf("/P -516", revision: 3));
        Assert.Contains(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// The unsigned spelling of the same bits, which is what a common viewer and most production writers
    /// emit: §7.6.3 Table 22 defines <c>/P</c> as a signed 32-bit integer, and producers print the
    /// non-negative integer that pattern denotes.
    ///
    /// <para><b>Why these two exist.</b> The checker reads <c>/P</c> through
    /// <c>Core.Document.PdfPermissionValue.FromNumber</c> rather than <c>PdfNumber.IntValue</c>,
    /// because the latter is a <c>double</c>→<c>int</c> cast of an out-of-range value and
    /// <i>saturates</i> to <c>int.MaxValue</c> on .NET Core 3.0+ — every bit set, so bit 10 reads
    /// as "permitted" for every such document. That was already correct here and <b>nothing
    /// asserted it</b>: every existing fixture writes the signed spelling, where the two readings
    /// agree, so reverting this call site to <c>IntValue</c> left the whole suite green. Found by
    /// mutating the shared reader and running each consumer's suite against it; the deep checker's
    /// half of the same audit really had been reverted, and had been reporting a false pass.</para>
    /// </summary>
    [Fact]
    public void Validate_DenyingAccessibilityExtraction_InTheUnsignedSpellingOfP_ReportsUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf(UnsignedP(-516), revision: 3));
        Assert.Contains(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// The acceptance control: without it, the test above passes for a checker that reports UA06 on
    /// every encrypted document, which is the behaviour UA06 was rewritten to stop doing.
    /// </summary>
    [Fact]
    public void Validate_PermittingAccessibilityExtraction_InTheUnsignedSpellingOfP_ReportsNoUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf(UnsignedP(-4), revision: 3));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// The <c>/P</c> entry for <paramref name="signedPermissions"/>, printed in its unsigned
    /// spelling. Derived rather than typed, so the two spellings of one bitmask cannot drift apart
    /// in the fixture and leave the tests above asserting something other than they claim.
    /// </summary>
    private static string UnsignedP(int signedPermissions)
        => "/P " + ((uint)signedPermissions).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The regression that mattered most: the old checker returned as soon as it saw
    /// /Encrypt, so an encrypted document's report contained one line and nothing else —
    /// a completely untagged, unlabelled file looked like it had a single problem.
    /// Names, numbers, booleans and dictionary structure are never encrypted, so every
    /// structural check remains meaningful and must still run.
    /// </summary>
    [Fact]
    public void Validate_EncryptedDocument_StillRunsCatalogAndPageLevelChecks()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf("/P -4", revision: 3));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA06");
        Assert.Contains(result.Violations, v => v.Code == "UA01"); // not tagged
        Assert.Contains(result.Violations, v => v.Code == "UA02"); // no /Lang
        Assert.Contains(result.Violations, v => v.Code == "UA05"); // no structure tree
        // UA09 proves the *page tree* was walked too: for an encrypted document the parser
        // never loads the catalog, so the checker has to enumerate the pages itself.
        Assert.Contains(result.Violations, v => v.Code == "UA09");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    [Fact]
    public void Validate_EncryptedWithoutPermissionsEntry_ReportsUa06()
    {
        // No /P at all: it cannot be established that extraction is permitted.
        var result = Validate(BuildEncryptedAnnotatedPdf(string.Empty, revision: 3));
        Assert.Contains(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// Revision 2 reserves permission bits 7–12 and requires them to be 1, so accessibility
    /// extraction is permitted there whatever /P happens to contain.
    /// </summary>
    [Fact]
    public void Validate_EncryptedRevision2_ReportsNoUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf("/P -516", revision: 2));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// ISO 32000-2 deprecates bit 10 for revision 6 (AES-256) and tells readers to treat it
    /// as set, so a cleared bit is not a defect there either.
    /// </summary>
    [Fact]
    public void Validate_EncryptedRevision6_ReportsNoUa06()
    {
        var result = Validate(BuildEncryptedAnnotatedPdf("/P -516", revision: 6));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// A non-standard security handler has no /P bits to inspect, so the permission cannot be
    /// established — that genuinely is a UA06.
    /// </summary>
    [Fact]
    public void Validate_NonStandardSecurityHandler_ReportsUa06()
    {
        var result = Validate(
            BuildEncryptedAnnotatedPdf("/P -4", revision: 3, filter: "/AcmeCrypt"));

        Assert.Contains(result.Violations, v => v.Code == "UA06");
    }

    // ==================================================================
    //  4. Unicode safety — the fixer's writer used to go through Latin-1
    // ==================================================================

    // Written as escapes rather than literals so the assertions cannot be weakened by
    // source-file encoding damage — a test whose "Hebrew" expectation has itself been
    // mangled would agree with a mangled output. shin-dalet-he = "field";
    // shin-final-mem = "name".
    private const string HebrewFieldWord = "\u05E9\u05D3\u05D4";
    private const string HebrewNameWord  = "\u05E9\u05DD";

    /// <summary>
    /// Code page 28591 maps every character above U+00FF to '?', so the previous writer
    /// turned a Hebrew tooltip into question marks — the accessible name the fix added was
    /// less useful than the missing one it replaced. Equality, not "contains", is the
    /// assertion: '?' passes any weaker check.
    /// </summary>
    [Fact]
    public void Fix_HebrewTooltipTemplate_RoundTripsCharacterForCharacter()
    {
        string template = HebrewFieldWord + " {name}";
        string expected = HebrewFieldWord + " field1";

        byte[] output = RunFix(BuildFormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = template });

        var tooltip = ReadFirstFieldTooltip(output);
        Assert.NotNull(tooltip);
        Assert.Equal(expected, tooltip!.GetText());
        Assert.DoesNotContain("?", tooltip.GetText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mechanism, not just the outcome: ISO 32000-1 §7.9.2.2 defines exactly one encoding
    /// for text outside PDFDocEncoding — UTF-16BE introduced by a U+FEFF byte-order mark.
    /// </summary>
    [Fact]
    public void Fix_HebrewTooltip_IsStoredAsAUtf16BeTextString()
    {
        byte[] output = RunFix(BuildFormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = HebrewFieldWord });

        var tooltip = ReadFirstFieldTooltip(output);
        Assert.NotNull(tooltip);

        byte[] raw = tooltip!.RawBytes;
        Assert.True(raw.Length >= 2, "The tooltip string is empty.");
        Assert.Equal((byte)0xFE, raw[0]);
        Assert.Equal((byte)0xFF, raw[1]);
        // BOM + two bytes per code unit — i.e. no lossy narrowing happened on the way out.
        Assert.Equal(2 + HebrewFieldWord.Length * 2, raw.Length);
    }

    /// <summary>
    /// The field's own partial name may be non-Latin-1 too — it is substituted into the
    /// template, so it has to survive both the read and the write.
    /// </summary>
    [Fact]
    public void Fix_HebrewFieldName_IsSubstitutedIntoTheTooltipIntact()
    {
        // /T given as a UTF-16BE hex string, which is how a Hebrew field name is stored.
        byte[] input = BuildFormPdf("/FT /Tx /T <FEFF05E905DD>");

        byte[] output = RunFix(input); // default template is "{name}"

        var tooltip = ReadFirstFieldTooltip(output);
        Assert.NotNull(tooltip);
        Assert.Equal(HebrewNameWord, tooltip!.GetText());
    }

    /// <summary>
    /// A Hebrew tooltip must also satisfy the checker afterwards. If the bytes were mangled
    /// into '?' the file would still "pass" UA08 — which is precisely why the round-trip
    /// equality assertion above exists as well as this one.
    /// </summary>
    [Fact]
    public void Fix_HebrewTooltip_SatisfiesUa08OnReValidation()
    {
        byte[] output = RunFix(BuildFormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = HebrewFieldWord + " {name}" });

        Assert.DoesNotContain(Validate(output).Violations, v => v.Code == "UA08");
    }

    [Fact]
    public void Fix_AsciiTooltip_IsStoredAsAPlainLiteralNotUtf16()
    {
        byte[] output = RunFix(BuildFormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = "Enter your {name}" });

        var tooltip = ReadFirstFieldTooltip(output);
        Assert.NotNull(tooltip);
        Assert.Equal("Enter your field1", tooltip!.GetText());
        // No BOM: pure ASCII stays a plain literal, so the common case gains no bloat.
        Assert.NotEqual((byte)0xFE, tooltip.RawBytes[0]);
    }

    // ==================================================================
    //  5. Cycle safety — adversarial /K and /Kids graphs
    // ==================================================================

    /// <summary>
    /// Four structure elements in a ring, each naming the next three times. With only a depth
    /// cap this is 3^100 visits; with the visited set it is four. Both walkers that consume a
    /// structure tree (UA07's figure scan and UA27's &lt;Link&gt; scan) share this traversal.
    /// </summary>
    private static byte[] BuildCyclicStructTreePdf()
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /Sect /K [6 0 R 6 0 R 6 0 R] >>",
            "<< /Type /StructElem /S /Sect /K [7 0 R 7 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Sect /K [8 0 R 8 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /Sect /K [5 0 R 5 0 R 5 0 R] >>");

    /// <summary>
    /// The same shape in the AcroForm field tree, which UA08 and the fixer both walk.
    ///
    /// <para>
    /// Object 8 is a genuine <b>terminal</b> field hanging off the ring: it has no /Kids and
    /// inherits its /FT from object 7 through /Parent (ISO 32000-1 Table 220 marks the field type
    /// inheritable). It is there so "the guard stops the repeats, not the work" is still testable
    /// — the four ring nodes are all <i>non-terminal</i> (each of their kids is itself a field),
    /// and a non-terminal node is not something a user tabs to and cannot be given an accessible
    /// name by /TU, which is not inheritable downward.
    /// </para>
    /// </summary>
    private static byte[] BuildCyclicFieldTreePdf()
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx /T (a) /Kids [5 0 R 5 0 R 5 0 R] >>",
            "<< /FT /Tx /T (b) /Kids [6 0 R 6 0 R 6 0 R] >>",
            "<< /FT /Tx /T (c) /Kids [7 0 R 7 0 R 7 0 R] >>",
            "<< /FT /Tx /T (d) /Kids [4 0 R 4 0 R 8 0 R] >>",
            "<< /T (leaf) /Parent 7 0 R >>");

    [Fact]
    public void Validate_StructureTreeCycle_CompletesWithinTheTimeout()
    {
        var result = RunWithinTimeout(
            () => Validate(BuildCyclicStructTreePdf()), "PdfUaChecker.Validate on a /K cycle");

        Assert.NotNull(result);
        // Terminating by throwing would also be "fast"; UA99 marks an aborted check, so its
        // absence proves the walk finished normally.
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    [Fact]
    public void Validate_AcroFormKidsCycle_CompletesWithinTheTimeout()
    {
        var result = RunWithinTimeout(
            () => Validate(BuildCyclicFieldTreePdf()), "PdfUaChecker.Validate on a /Kids cycle");

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
        // The guard must stop the *repeats*, not the work: the unlabelled terminal field at the
        // end of the ring is still found and reported.
        Assert.Contains(result.Violations, v => v.Code == "UA08");
    }

    [Fact]
    public void Fix_AcroFormKidsCycle_CompletesWithinTheTimeout()
    {
        byte[] input = BuildCyclicFieldTreePdf();

        var fixes = RunWithinTimeout(() =>
        {
            using var inStream  = new MemoryStream(input);
            using var outStream = new MemoryStream();
            return PdfAccessibilityFixer.Fix(inStream, outStream);
        }, "PdfAccessibilityFixer.Fix on a /Kids cycle");

        Assert.NotNull(fixes);
        // Again: terminating by doing nothing would be no fix at all.
        Assert.Contains(fixes.FixesApplied, f => f.Contains("/TU"));
    }

    /// <summary>
    /// Both cycles in one file, fixed and then re-validated, so the guards are exercised on
    /// the write path and the read path of the same document.
    /// </summary>
    [Fact]
    public void FixThenValidate_DocumentWithBothCycles_CompletesWithinTheTimeout()
    {
        byte[] input = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /AcroForm << /Fields [8 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Sect /K [7 0 R 7 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Sect /K [6 0 R 6 0 R 6 0 R] >>",
            "<< /FT /Tx /T (a) /Kids [9 0 R 9 0 R 9 0 R] >>",
            "<< /FT /Tx /T (b) /Kids [8 0 R 8 0 R 8 0 R] >>");

        var after = RunWithinTimeout(() => Validate(RunFix(input)),
            "Fix followed by Validate on a document with a /K and a /Kids cycle");

        Assert.NotNull(after);
        Assert.DoesNotContain(after.Violations, v => v.Code == "UA99");
    }

    // ==================================================================
    //  6. UA25 — every font must be embedded (ISO 14289-1 §7.21.4.1)
    // ==================================================================

    [Fact]
    public void Validate_FontWithoutAnEmbeddedFontFile_ReportsUa25()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA25" && v.Message.Contains("Helvetica"));
    }

    /// <summary>
    /// ISO 14289-1 7.21.4.1 asks about a font used for rendering: one nothing draws with is not
    /// used, and one drawn only in render mode 3 is exempted by NOTE 2. The same non-embedded
    /// Helvetica, drawn visibly, is the control the case above already is.
    /// </summary>
    [Theory]
    [InlineData("0 0 m 10 10 l S")]                         // the page draws, but no text
    [InlineData("BT /F1 12 Tf 3 Tr 72 700 Td (x) Tj ET")]   // text, invisible
    [InlineData("q BT /F1 12 Tf 3 Tr (x) Tj ET Q BT 7 Tr /F1 12 Tf (y) Tj ET")] // invisible, and clip-only
    public void Validate_NonEmbeddedFontThatPaintsNothing_ReportsNoUa25(string content)
    {
        byte[] pdf = BuildFontPdfDrawing(content,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA25");
    }

    /// <summary>
    /// When which fonts are painted cannot be established, every non-embedded font is reported —
    /// "could not see it used" is not "unused". Here a form XObject shows text without selecting a
    /// font, in the one it inherits from the page, which reading the form on its own cannot name;
    /// the page itself draws Helvetica only invisibly, so without the fallback nothing is reported.
    /// </summary>
    [Fact]
    public void Validate_TextShownInAnInheritedFont_ReportsEveryNonEmbeddedFont()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /X0 4 0 R >> >> >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "BT (x) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject(string.Empty, "BT /F1 12 Tf 3 Tr (a) Tj ET /X0 Do"));

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA25" && v.Message.Contains("Helvetica"));
    }

    /// <summary>
    /// The same fallback when the content scan stops at a ceiling: the invisible-only Helvetica that
    /// is not reported under the default ceilings is reported when the page's content cannot be read
    /// within one byte.
    /// </summary>
    [Fact]
    public void Validate_AContentScanCutShort_ReportsEveryNonEmbeddedFont()
    {
        byte[] pdf = BuildFontPdfDrawing("BT /F1 12 Tf 3 Tr 72 700 Td (x) Tj ET",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA25");

        using var ms = new MemoryStream(pdf);
        var cutShort = PdfUaChecker.Validate(ms, maxPageContentBytes: 1, maxDocumentContentBytes: long.MaxValue);
        Assert.Contains(cutShort.Violations, v => v.Code == "UA25" && v.Message.Contains("Helvetica"));
    }

    [Fact]
    public void Validate_FontWithFontFile3_ReportsNoUa25()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Minion /Encoding /WinAnsiEncoding "
                + "/FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Minion /Flags 32 /FontFile3 6 0 R >>",
            StreamObject("/Subtype /Type1C", " "));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA25");
    }

    /// <summary>
    /// Fonts are not only declared on the page: a Form XObject brings its own /Resources, and
    /// a check that stops at the page misses every font used inside one.
    /// </summary>
    [Fact]
    public void Validate_UnembeddedFontInsideAFormXObject_ReportsUa25()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R "
                + "/Resources << /XObject << /X0 4 0 R >> >> >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /Font << /F1 5 0 R >> >>", "BT /F1 1 Tf (x) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /NestedGaramond "
                + "/Encoding /WinAnsiEncoding >>",
            "<< >>",
            StreamObject(string.Empty, "/X0 Do"));

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA25" && v.Message.Contains("NestedGaramond"));
    }

    [Fact]
    public void Validate_EmbeddedFontInsideAFormXObject_ReportsNoUa25()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R "
                + "/Resources << /XObject << /X0 4 0 R >> >> >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 10 10] "
                + "/Resources << /Font << /F1 5 0 R >> >>", "BT /F1 1 Tf (x) Tj ET"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+NestedGaramond "
                + "/Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+NestedGaramond /Flags 32 "
                + "/FontFile2 7 0 R >>",
            StreamObject(string.Empty, " "),
            StreamObject(string.Empty, "/X0 Do"));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA25");
    }

    /// <summary>
    /// For a composite font the font program hangs off the descendant CIDFont, so checking
    /// the Type0 dictionary alone would report every well-formed CJK document as unembedded —
    /// and miss the genuinely unembedded one below.
    /// </summary>
    [Fact]
    public void Validate_Type0FontWhoseDescendantIsNotEmbedded_ReportsUa25()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type0 /BaseFont /Mincho /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Mincho /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Mincho /Flags 4 >>",
            StreamObject(string.Empty, " "));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA25" && v.Message.Contains("Mincho"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA26"); // /ToUnicode is present
    }

    [Fact]
    public void Validate_Type0FontFullyEmbeddedWithToUnicode_ReportsNeitherUa25NorUa26()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Mincho /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Mincho "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Mincho /Flags 4 /FontFile2 7 0 R >>",
            StreamObject(string.Empty, " "),
            StreamObject(string.Empty, " "));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA25");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA26");
    }

    // ==================================================================
    //  7. UA26 — every font must map to Unicode (ISO 14289-1 §7.21.7)
    // ==================================================================

    /// <summary>
    /// Identity-H maps codes straight to glyph indices. Without /ToUnicode nothing can read
    /// the text out or extract it — the classic "copying this PDF yields garbage"
    /// accessibility failure.
    /// </summary>
    [Fact]
    public void Validate_IdentityHWithoutToUnicode_ReportsUa26()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Mincho /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Mincho "
                + "/FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Mincho /Flags 4 /FontFile2 7 0 R >>",
            StreamObject(string.Empty, " "));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA26");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA25"); // it *is* embedded
    }

    /// <summary>
    /// ISO 14289-1 §7.21.7 exempts a Type 0 font "whose descendant CIDFont uses the Adobe-GB1,
    /// Adobe-CNS1, Adobe-Japan1 or Adobe-Korea1 character collections" — the collection, whatever
    /// the CMap: Identity-H over Adobe-Japan1 is exempt (the conformance corpus 7.21.7-t01-pass-a), and a
    /// predefined CMap over Adobe-Identity is not, its CIDs being glyphs.
    /// </summary>
    [Theory]
    [InlineData("/Identity-H", "Adobe", "Japan1", false)]
    [InlineData("/UniJIS-UCS2-H", "Adobe", "Japan1", false)]
    [InlineData("/UniGB-UCS2-H", "Adobe", "GB1", false)]
    [InlineData("/UniJIS-UCS2-H", "Adobe", "Identity", true)]
    [InlineData("/Identity-H", "Other", "Japan1", true)]
    public void Validate_Ua26TypeZeroExemptionIsTheDescendantsCharacterCollection(
        string cmap, string registry, string ordering, bool reportsUa26)
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Mincho /Encoding " + cmap
                + " /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Mincho /CIDSystemInfo << /Registry ("
                + registry + ") /Ordering (" + ordering + ") /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Mincho /Flags 4 /FontFile2 7 0 R >>",
            StreamObject(string.Empty, " "));

        Assert.Equal(reportsUa26, Validate(pdf).Violations.Any(v => v.Code == "UA26"));
    }

    /// <summary>
    /// A symbolic font with no /Encoding uses the font program's private built-in encoding,
    /// which nothing outside that program can interpret.
    /// </summary>
    [Fact]
    public void Validate_SymbolicFontWithNoEncodingAndNoToUnicode_ReportsUa26()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Wingding "
                + "/FontDescriptor 5 0 R >>",
            // Flags bit 3 (value 4) = symbolic.
            "<< /Type /FontDescriptor /FontName /ABCDEF+Wingding /Flags 4 /FontFile2 6 0 R >>",
            StreamObject(string.Empty, " "));

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA26" && v.Message.Contains("Wingding"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA25");
    }

    /// <summary>
    /// The same font declared non-symbolic falls back to StandardEncoding, which is
    /// Unicode-recoverable — the check must distinguish the two.
    /// </summary>
    [Fact]
    public void Validate_NonSymbolicFontWithNoEncoding_ReportsNoUa26()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Bookman "
                + "/FontDescriptor 5 0 R >>",
            // Flags bit 6 (value 32) = non-symbolic.
            "<< /Type /FontDescriptor /FontName /ABCDEF+Bookman /Flags 32 /FontFile2 6 0 R >>",
            StreamObject(string.Empty, " "));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA26");
    }

    /// <summary>
    /// /Differences names glyphs (/aacute, …) which the Adobe Glyph List maps to Unicode,
    /// so an encoding dictionary carrying them is recoverable.
    /// </summary>
    [Fact]
    public void Validate_EncodingDictionaryWithDifferences_ReportsNoUa26()
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Bookman "
                + "/Encoding << /Type /Encoding /Differences [65 /aacute /eacute] >> "
                + "/FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Bookman /Flags 4 /FontFile2 6 0 R >>",
            StreamObject(string.Empty, " "));

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA26");
    }

    /// <summary>
    /// §7.21.7 names MacRomanEncoding, MacExpertEncoding and WinAnsiEncoding as encodings that need
    /// no /ToUnicode; a symbolic font under a name outside the set does.
    /// </summary>
    [Theory]
    [InlineData("/MacExpertEncoding", false)]
    [InlineData("/MacRomanEncoding", false)]
    [InlineData("/Identity", true)]
    public void Validate_Ua26NamedEncodingsTheClauseExempts(string encoding, bool reportsUa26)
    {
        byte[] pdf = BuildFontPdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Bookman /Encoding " + encoding
                + " /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Bookman /Flags 4 /FontFile2 6 0 R >>",
            StreamObject(string.Empty, " "));

        Assert.Equal(reportsUa26, Validate(pdf).Violations.Any(v => v.Code == "UA26"));
    }

    // ==================================================================
    //  8. UA27 — annotations need an accessible description
    // ==================================================================

    private static byte[] BuildAnnotationPdf(string annotEntries)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot " + annotEntries + " /Rect [0 0 20 20] >>");

    [Fact]
    public void Validate_MarkupAnnotationWithoutContents_ReportsUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /FreeText"));
        Assert.Contains(result.Violations, v => v.Code == "UA27");
    }

    [Fact]
    public void Validate_MarkupAnnotationWithContents_ReportsNoUa27()
    {
        var result = Validate(
            BuildAnnotationPdf("/Subtype /FreeText /Contents (Reviewer comment)"));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>An empty /Contents describes nothing, so presence alone must not satisfy it.</summary>
    [Fact]
    public void Validate_AnnotationWithEmptyContents_ReportsUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /FreeText /Contents ()"));
        Assert.Contains(result.Violations, v => v.Code == "UA27");
    }

    [Fact]
    public void Validate_LinkAnnotationWithoutAnyDescription_ReportsUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /Link"));
        Assert.Contains(result.Violations,
            v => v.Code == "UA27" && v.Message.Contains("/Link"));
    }

    [Fact]
    public void Validate_LinkAnnotationWithContents_ReportsNoUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /Link /Contents (Annual report)"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// ISO 14289-1 §7.18.5 lets the enclosing &lt;Link&gt; structure element supply the
    /// description instead of the annotation, via an /OBJR kid pointing back at it.
    /// </summary>
    [Fact]
    public void Validate_LinkAnnotationDescribedByItsLinkStructureElement_ReportsNoUa27()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Link /Alt (Link to the annual report) /K 7 0 R >>",
            "<< /Type /OBJR /Obj 4 0 R >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// A &lt;Link&gt; element without /Alt supplies no description, so the OBJR it owns must
    /// not excuse the annotation — otherwise merely being tagged would silence the check.
    /// </summary>
    [Fact]
    public void Validate_LinkStructureElementWithoutAlt_StillReportsUa27()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Link /K 7 0 R >>",
            "<< /Type /OBJR /Obj 4 0 R >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// /F bit 6 (value 32) = NoView: the annotation is never presented on screen, so there is
    /// nothing for assistive technology to describe.
    /// </summary>
    [Fact]
    public void Validate_NoViewAnnotationWithoutContents_ReportsNoUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /FreeText /F 32"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>A /Popup only echoes its parent markup annotation; it carries no content of its own.</summary>
    [Fact]
    public void Validate_PopupAnnotationWithoutContents_ReportsNoUa27()
    {
        var result = Validate(BuildAnnotationPdf("/Subtype /Popup"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// Widgets are covered by UA08 (/TU on the field). Reporting them under UA27 as well
    /// would double-count one defect and make the report's counts meaningless.
    /// </summary>
    [Fact]
    public void Validate_WidgetAnnotationWithoutContents_ReportsNoUa27()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /S"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    // ==================================================================
    //  9. UA04 must be encryption-aware, exactly as UA03 already is
    // ==================================================================
    //
    //  A document title may legitimately live only in XMP dc:title. On an encrypted document
    //  that packet is ciphertext to this checker, which does not decrypt — so "I found no
    //  title" was an assertion about something the checker had never been able to read, and
    //  every such (valid) file came back with a fabricated UA04.
    //
    //  The rule the tests below pin down is the one UA03 already follows: an unreadable packet
    //  on an encrypted document downgrades to UA99 "could not be verified", and *only* that
    //  case does. Two checks in one file disagreeing about the same condition is the class of
    //  bug this whole effort has been eliminating, so the exemption is deliberately narrow —
    //  a packet that is absent, empty, or parses fine still yields a plain UA04.

    /// <summary>
    /// /P -4 sets every permission bit, including bit 10, so UA06 stays quiet and the
    /// encryption is a pure "the streams are ciphertext" signal. No key derivation is
    /// involved: the checker only reads /Filter, /V, /R and /P.
    /// </summary>
    private const string StandardEncryptDict =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
      + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
      + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    /// <summary>A well-formed XMP packet whose dc:title carries <paramref name="title"/>.</summary>
    private static string DcTitleXmpPacket(string title)
        => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
         + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
         + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
         + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
         + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" + title + "</rdf:li></rdf:Alt>"
         + "</dc:title></rdf:Description></rdf:RDF></x:xmpmeta>"
         + "<?xpacket end=\"w\"?>";

    /// <summary>A well-formed XMP packet that declares everything except a title.</summary>
    private static string TitlelessXmpPacket()
        => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
         + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
         + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
         + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
         + "<dc:creator><rdf:Seq><rdf:li>A. Author</rdf:li></rdf:Seq></dc:creator>"
         + "</rdf:Description></rdf:RDF></x:xmpmeta>"
         + "<?xpacket end=\"w\"?>";

    /// <summary>
    /// A one-page document with <b>no /Info dictionary at all</b>, so XMP is the only place a
    /// title could come from — which is precisely the shape that produced the false UA04.
    /// Pass <c>null</c> for <paramref name="metadataPacket"/> to omit /Metadata entirely.
    /// The page carries no annotations, so UA09 stays out of the way.
    /// </summary>
    private static byte[] BuildXmpTitlePdf(
        string? metadataPacket, bool encrypted, bool displayDocTitle = true)
    {
        string catalog =
            "<< /Type /Catalog /Pages 2 0 R"
          + (displayDocTitle ? " /ViewerPreferences << /DisplayDocTitle true >>" : string.Empty)
          + (metadataPacket == null ? string.Empty : " /Metadata 4 0 R")
          + " >>";
        const string pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        const string page  = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

        if (metadataPacket == null)
            return encrypted
                ? BuildPdfWithTrailer(" /Encrypt 4 0 R", catalog, pages, page, StandardEncryptDict)
                : BuildPdfWithTrailer(string.Empty, catalog, pages, page);

        string metadata = StreamObject("/Type /Metadata /Subtype /XML", metadataPacket);
        return encrypted
            ? BuildPdfWithTrailer(
                " /Encrypt 5 0 R", catalog, pages, page, metadata, StandardEncryptDict)
            : BuildPdfWithTrailer(string.Empty, catalog, pages, page, metadata);
    }

    /// <summary>The half of UA04 that asserts no document title exists anywhere.</summary>
    private static bool IsMissingTitleUa04(PdfUaViolation v)
        => v.Code == "UA04" && v.Message.Contains("/Info", StringComparison.Ordinal);

    /// <summary>
    /// The UA99 that says the title half of UA04 could not be decided. Matched on "dc:title"
    /// as well as the code so a generic "the UA04 check threw" UA99 cannot satisfy it —
    /// an aborted check and a deliberately undecidable one are different outcomes.
    /// </summary>
    private static bool IsUa04Unverifiable(PdfUaViolation v)
        => v.Code == "UA99"
        && v.Message.Contains("UA04", StringComparison.Ordinal)
        && v.Message.Contains("dc:title", StringComparison.Ordinal);

    /// <summary>
    /// The defect: an encrypted file whose title is in XMP is <i>valid</i>, and was reported as
    /// having no document title — a violation asserted about bytes the checker never read.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithUnreadableXmp_DoesNotAssertTheTitleIsMissing()
    {
        // Ciphertext is what a checker without the key sees; any non-XML byte run models it.
        var result = Validate(BuildXmpTitlePdf("this packet is not XML", encrypted: true));

        Assert.DoesNotContain(result.Violations, IsMissingTitleUa04);
    }

    /// <summary>
    /// …and it does not fall silent either. Saying nothing would leave "does this document have
    /// the title PDF/UA-1 requires?" unanswered while the report read as clean, which is the
    /// same failure mode UA03's Ua03Unverifiable exists to prevent.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithUnreadableXmp_ReportsTheTitleAsUnverifiable()
    {
        var result = Validate(BuildXmpTitlePdf("this packet is not XML", encrypted: true));

        var unverified = Assert.Single(result.Violations, IsUa04Unverifiable);

        // It has to say *why* it could not decide, or the reader is no better off than with
        // the silence it replaces — and "encrypted" is the one thing they can act on.
        Assert.Contains("encrypted", unverified.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control in the other direction: the identical file without /Encrypt is decidable —
    /// the packet really is malformed, the title really is absent, and UA04 must be asserted
    /// outright rather than excused. The exemption is about ciphertext, not about XML errors.
    /// </summary>
    [Fact]
    public void Validate_UnencryptedWithUnreadableXmp_ReportsUa04Outright()
    {
        var result = Validate(BuildXmpTitlePdf("this packet is not XML", encrypted: false));

        Assert.Contains(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    /// <summary>
    /// The plain case that must keep working: nothing carries a title anywhere — no /Info, no
    /// /Metadata — and the document is not encrypted. UA04 fires.
    /// </summary>
    [Fact]
    public void Validate_UnencryptedWithNoTitleAnywhere_StillReportsUa04()
    {
        var result = Validate(BuildXmpTitlePdf(metadataPacket: null, encrypted: false));

        Assert.Contains(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    /// <summary>
    /// Encryption must not become a blanket amnesty. A missing /Metadata entry is a fact about
    /// the document's <i>dictionaries</i>, and those are never ciphertext — so the title is
    /// genuinely absent and UA04 is asserted, encrypted or not.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithNoMetadataStreamAtAll_StillReportsUa04()
    {
        var result = Validate(BuildXmpTitlePdf(metadataPacket: null, encrypted: true));

        Assert.Contains(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    /// <summary>
    /// Nor is it an amnesty for a packet the checker <i>could</i> read. Well-formed XML does not
    /// come out of a decryption the checker never performed, so a packet that parses is
    /// plaintext (legal: /EncryptMetadata false) and "it declares no dc:title" is a trustworthy
    /// answer even here.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithReadableXmpCarryingNoTitle_StillReportsUa04()
    {
        var result = Validate(BuildXmpTitlePdf(TitlelessXmpPacket(), encrypted: true));

        Assert.Contains(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    /// <summary>
    /// The same plaintext-packet reasoning, with a title present: found, accepted, no UA04 and
    /// nothing left unverified.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithReadableXmpCarryingDcTitle_ReportsNoUa04()
    {
        var result = Validate(BuildXmpTitlePdf(DcTitleXmpPacket("Quarterly Report"), true));

        Assert.DoesNotContain(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    /// <summary>
    /// The ordinary unencrypted case the whole XMP branch exists for: a title in dc:title and
    /// no /Info dictionary satisfies UA04 (valid, and common in PDF 2.0 / PDF/UA workflows).
    /// </summary>
    [Fact]
    public void Validate_UnencryptedWithDcTitleInXmp_ReportsNoUa04()
    {
        var result = Validate(BuildXmpTitlePdf(DcTitleXmpPacket("Quarterly Report"), false));

        Assert.DoesNotContain(result.Violations, IsMissingTitleUa04);
    }

    /// <summary>
    /// An empty dc:title is the same as having none — matching the /Info /Title rule, where
    /// presence is not a title.
    /// </summary>
    [Fact]
    public void Validate_UnencryptedWithEmptyDcTitle_ReportsUa04()
    {
        var result = Validate(BuildXmpTitlePdf(DcTitleXmpPacket("   "), encrypted: false));

        Assert.Contains(result.Violations, IsMissingTitleUa04);
    }

    /// <summary>
    /// Only the <i>title</i> half of UA04 is undecidable under encryption. /DisplayDocTitle is a
    /// boolean in the catalog, encryption never touches those, and suppressing it along with the
    /// title would have traded one false negative for another.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithUnreadableXmp_StillReportsTheDisplayDocTitleHalf()
    {
        var result = Validate(BuildXmpTitlePdf(
            "this packet is not XML", encrypted: true, displayDocTitle: false));

        Assert.Contains(result.Violations,
            v => v.Code == "UA04" && v.Message.Contains("DisplayDocTitle", StringComparison.Ordinal));
    }

    /// <summary>
    /// And an /Info title still settles the question outright: its bytes are ciphertext on an
    /// encrypted document, but UA04 asks only whether a title is <i>present</i>, and presence is
    /// legible without the key. Nothing to downgrade here.
    /// </summary>
    [Fact]
    public void Validate_EncryptedWithInfoTitle_ReportsNeitherUa04NorUnverifiable()
    {
        byte[] pdf = BuildPdfWithTrailer(" /Info 4 0 R /Encrypt 5 0 R",
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Title (Quarterly Report) >>",
            StandardEncryptDict);

        var result = Validate(pdf);

        Assert.DoesNotContain(result.Violations, IsMissingTitleUa04);
        Assert.DoesNotContain(result.Violations, IsUa04Unverifiable);
    }

    // ==================================================================
    //  10. UA09 — the legacy /TabOrder spelling is named, never accepted
    // ==================================================================

    /// <summary>
    /// The message must tell the owner of a legacy file <i>what to write instead</i>. "N pages
    /// have no /Tabs /S" is true and useless to someone looking at a page that visibly declares
    /// a tab order; naming both the key to delete and the key to add is what makes the finding
    /// actionable.
    /// </summary>
    [Fact]
    public void Validate_LegacyTabOrderKey_MessageNamesBothTheLegacyKeyAndTheRemedy()
    {
        var result = Validate(BuildAnnotatedPagePdf("/TabOrder /S"));

        var ua09 = Assert.Single(result.Violations, v => v.Code == "UA09");
        Assert.Contains("/TabOrder", ua09.Message, StringComparison.Ordinal);
        Assert.Contains("/Tabs /S", ua09.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// /Tabs is present but names row order, and the legacy key is there too. The page is
    /// non-conformant either way, and the legacy key is still named — the advice ("remove
    /// /TabOrder, write /Tabs /S") is what the user needs in this shape as well.
    /// </summary>
    [Fact]
    public void Validate_LegacyTabOrderAlongsideANonStructureTabsValue_IsReportedAndNamed()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /R /TabOrder /S"));

        var ua09 = Assert.Single(result.Violations, v => v.Code == "UA09");
        Assert.Contains("/TabOrder", ua09.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The converse: /Tabs /S conforms, and the presence of a stale /TabOrder alongside it
    /// cannot manufacture a violation. Only the real key decides.
    /// </summary>
    [Fact]
    public void Validate_TabsSAlongsideALegacyTabOrder_ReportsNoUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /S /TabOrder /R"));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// A page that is missing the key outright must not be described as carrying the legacy
    /// one — the legacy sentence is only appended when a page really declares /TabOrder /S.
    /// </summary>
    [Fact]
    public void Validate_PageWithNoTabKey_MessageDoesNotMentionTheLegacyKey()
    {
        var result = Validate(BuildAnnotatedPagePdf(string.Empty));

        var ua09 = Assert.Single(result.Violations, v => v.Code == "UA09");
        Assert.DoesNotContain("/TabOrder", ua09.Message, StringComparison.Ordinal);
    }
}
