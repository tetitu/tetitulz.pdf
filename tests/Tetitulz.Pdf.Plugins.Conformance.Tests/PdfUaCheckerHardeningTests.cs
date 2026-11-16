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
/// Second hardening pass over <see cref="PdfUaChecker"/>. Every test here fails against the
/// previous implementation, and each pins one of the defects a deep review of the accessibility
/// stack confirmed:
///
/// <list type="number">
///   <item><b>Budgets spent per node instead of per edge.</b> Three of the four walkers
///     (<c>WalkStructTree</c>, <c>CollectPagesFrom</c>) decremented once per <i>newly visited</i>
///     node while iterating that node's whole child array, and the fourth
///     (<c>CollectFonts</c>) had no budget at all. A visited set stops re-expansion, not the cost
///     of one expansion, so N nodes sharing one indirect N-entry array cost N² resolves while the
///     counter moved N times — a 200 000-node ceiling that could never fire on the only graph it
///     existed for. Adversarial-input tests, as CLAUDE.md requires for any DoS guard.</item>
///   <item><b>A skipped check reported as a pass.</b> UA03 was silently omitted for every
///     encrypted document, and an omitted check is indistinguishable from one that ran and found
///     nothing: a file that cannot possibly be PDF/UA-1 came back <c>IsConforming</c>.</item>
///   <item><b>/Type is Optional on /MCR and /OBJR</b> (ISO 32000-1 Tables 324 and 325). Requiring
///     it produced a false UA29 on a legal file and disabled the ISO 14289-1 §7.18.1 escape hatch
///     that lets a &lt;Link&gt; element's /Alt describe its annotation.</item>
///   <item><b>/FT is inheritable</b> (Table 220). "Has a direct /FT" is not "is a terminal field",
///     so every terminal field that inherits its type — the standard radio-group shape — went
///     unreported, while the non-terminal parent, whose /TU nobody reads, was reported instead.</item>
///   <item><b>/TabOrder is not a PDF key.</b> Accepting it made this checker the one component in
///     the stack that called a page conformant which an independent checker, the published profiles and the library's own fixer all
///     treat as broken.</item>
/// </list>
///
/// <para>Every fixture is an in-memory <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class PdfUaCheckerHardeningTests
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

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>
    /// A hand-built standard-handler encryption dictionary. Nothing in these fixtures is actually
    /// enciphered — the checker's whole interest here is <c>/Filter</c>, <c>/R</c> and <c>/P</c>,
    /// plus the fact that <c>PdfDocument</c> declines to load a catalog for an encrypted file.
    /// <para>/P = -4 has every permission bit set, including bit 10 ("extract for assistive
    /// technology"), so UA06 can never fire and confuse an assertion about another check.</para>
    /// </summary>
    private const string StandardEncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
        + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
        + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    private static PdfUaValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms);
    }

    private static string Describe(PdfUaValidationResult result)
        => result.Violations.Count == 0
            ? "(no violations)"
            : string.Join(" | ", result.Violations.Select(v => v.ToString()));

    /// <summary>
    /// Validates on a worker thread with a wall-clock ceiling, and returns the diagnostic the
    /// walk left behind.
    ///
    /// <para><see cref="PdfUaChecker.LastValidationError"/> is <c>[ThreadStatic]</c>, so it is read
    /// <i>inside</i> the worker: reading it from the test thread would always see null and every
    /// budget assertion below would pass vacuously.</para>
    ///
    /// <para>The worker is a dedicated <see cref="Thread"/>, not a pool work item. That keeps the
    /// wall clock a measure of the walk rather than of the thread pool's backlog under a
    /// whole-solution run, and it also guarantees the <c>[ThreadStatic]</c> slot starts empty
    /// instead of carrying whatever a recycled pool thread last wrote.</para>
    /// </summary>
    private static (PdfUaValidationResult Result, string Diagnostic) ValidateWithin(
        byte[] pdf, string what)
    {
        const int budgetSeconds = 60;

        (PdfUaValidationResult Result, string Diagnostic) captured = default;
        Exception failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(pdf);
                var result = PdfUaChecker.Validate(ms);
                captured = (Result: result, Diagnostic: PdfUaChecker.LastValidationError);
            }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "ua-checker-guard" };

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(budgetSeconds)),
            $"{what} did not finish within {budgetSeconds}s — the traversal budget is not holding.");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return captured;
    }

    // ==================================================================
    //  1. Adversarial input: a shared /K array in the structure tree
    // ==================================================================

    /// <summary>
    /// N structure elements whose <c>/K</c> all name <i>one</i> indirect array of N references.
    /// Every element is visited exactly once (the visited set works), but each visit iterates the
    /// whole N-entry array and resolves twice per entry, so the walk costs N² operations while a
    /// per-node counter moves only N times. At N = 600 that is ~360 000 edges against a 200 000
    /// ceiling: the guard must fire, and before the fix it could not.
    /// </summary>
    private static byte[] BuildSharedKidArrayStructTree(int n)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
        };

        var shared = new StringBuilder("[");
        for (int i = 0; i < n; i++) shared.Append(6 + i).Append(" 0 R ");
        shared.Append(']');
        objects.Add(shared.ToString());

        for (int i = 0; i < n; i++)
            objects.Add("<< /Type /StructElem /S /P /K 5 0 R >>");

        return BuildPdf(objects.ToArray());
    }

    [Fact]
    public void Validate_StructureTreeSharingOneKidArray_SpendsTheBudgetAndSaysSo()
    {
        var (result, diagnostic) = ValidateWithin(
            BuildSharedKidArrayStructTree(600), "PdfUaChecker.Validate on a shared /K array");

        Assert.NotNull(result);

        // The assertion that fails without the fix: with the budget spent per node the counter
        // moved 601 times against a 200 000 ceiling, so it never fired and no diagnostic was left
        // — the checker quietly did 360 000 edge expansions and called the result complete.
        Assert.False(string.IsNullOrEmpty(diagnostic),
            "the structure-tree budget never fired on a graph with 360 000 edges.");
        Assert.Contains("structure-tree traversal stopped", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>
    /// Truncating the walk must not be mistaken for a crash: UA99 means "a check threw", and this
    /// walk completes normally, just partially. The partiality is carried by the diagnostic above.
    /// </summary>
    [Fact]
    public void Validate_StructureTreeSharingOneKidArray_DoesNotReportAnAbortedCheck()
    {
        var (result, _) = ValidateWithin(
            BuildSharedKidArrayStructTree(600), "PdfUaChecker.Validate on a shared /K array");

        Assert.DoesNotContain(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("could not be completed"));
    }

    /// <summary>
    /// The shared array pointing at one <b>leaf</b>, so the depth stays at two and only the slot
    /// ceiling can stop the walk. The sibling test above shares an array <i>of the elements</i>, and
    /// each descends into the next, so that tree is 600 deep and the depth cap fires first — since the
    /// walk started reporting a depth-capped scan, that test no longer tells whether the slot ceiling
    /// exists. 500 elements over a 500-entry array is 250 000 slots against a 200 000 ceiling.
    /// </summary>
    [Fact]
    public void Validate_StructureTreeSharingOneFlatKidArray_StopsAtTheSlotCeilingAndSaysSo()
    {
        var (result, diagnostic) = ValidateWithin(
            BuildSharedLeafArrayStructTree(500), "PdfUaChecker.Validate on a flat shared /K array");

        Assert.NotNull(result);
        Assert.Contains("structure-tree traversal stopped at the 200000-slot /K ceiling", diagnostic ?? "",
            StringComparison.Ordinal);
        Assert.Contains(result.Violations, v => v.Code == "UA99" && v.Message.Contains("200000-slot", StringComparison.Ordinal));
    }

    /// <summary>The control: the same shape under the ceiling is walked whole and nothing calls the scan partial.</summary>
    [Fact]
    public void Validate_StructureTreeSharingOneFlatKidArrayUnderTheCeiling_ReportsNoPartialScan()
    {
        var (result, diagnostic) = ValidateWithin(
            BuildSharedLeafArrayStructTree(300), "PdfUaChecker.Validate on a flat shared /K array under the ceiling");

        Assert.NotNull(result);
        Assert.DoesNotContain("structure-tree traversal stopped", diagnostic ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    /// <summary>
    /// <paramref name="n"/> paragraphs whose <c>/K</c> all name one shared array of <paramref name="n"/>
    /// references to a single leaf span: n² slots from O(n) bytes, two levels deep.
    /// </summary>
    private static byte[] BuildSharedLeafArrayStructTree(int n)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
        };

        // 5: the root's kids, 6: the shared array, 7: the leaf, 8..: the paragraphs.
        var rootKids = new StringBuilder("[");
        for (int i = 0; i < n; i++) rootKids.Append(8 + i).Append(" 0 R ");
        rootKids.Append(']');
        objects.Add(rootKids.ToString());

        var shared = new StringBuilder("[");
        for (int i = 0; i < n; i++) shared.Append("7 0 R ");
        shared.Append(']');
        objects.Add(shared.ToString());

        objects.Add("<< /Type /StructElem /S /Span /K [0] >>");

        for (int i = 0; i < n; i++)
            objects.Add("<< /Type /StructElem /S /P /K 6 0 R >>");

        return BuildPdf(objects.ToArray());
    }

    // ==================================================================
    //  2. Adversarial input: a shared /XObject dictionary in the resources
    // ==================================================================

    /// <summary>
    /// N pages, each with its own (distinct, so un-deduplicated) <c>/Resources</c>, all naming one
    /// <c>/XObject</c> dictionary of N Form entries. The visited set bounds how many resource
    /// dictionaries are expanded and does nothing about the N entries each expansion probes, so
    /// this is N² resolves behind a depth cap of 32 and — before the fix — no ceiling whatsoever.
    /// </summary>
    private static byte[] BuildSharedXObjectResourceGraph(int n)
    {
        var objects = new List<string>();

        var kids = new StringBuilder("[");
        for (int i = 0; i < n; i++) kids.Append(4 + i).Append(" 0 R ");
        kids.Append(']');

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids " + kids + " /Count " + n + " >>");

        var xobjects = new StringBuilder("<< ");
        for (int i = 0; i < n; i++)
            xobjects.Append("/F").Append(i).Append(' ').Append(4 + n + i).Append(" 0 R ");
        xobjects.Append(">>");
        objects.Add(xobjects.ToString());

        for (int i = 0; i < n; i++)
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                      + "/Resources << /XObject 3 0 R >> >>");

        for (int i = 0; i < n; i++)
            objects.Add(StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", " "));

        return BuildPdf(objects.ToArray());
    }

    [Fact]
    public void Validate_ResourcesSharingOneXObjectDictionary_SpendsTheBudgetAndSaysSo()
    {
        var (result, diagnostic) = ValidateWithin(
            BuildSharedXObjectResourceGraph(500),
            "PdfUaChecker.Validate on a shared /XObject dictionary");

        Assert.NotNull(result);

        // 500 × 500 = 250 000 entries against a 200 000 ceiling. Before the fix CollectFonts had
        // no budget parameter at all, so nothing was ever reported however long it ran.
        Assert.False(string.IsNullOrEmpty(diagnostic),
            "the resource walk has no traversal ceiling: 250 000 entries were probed silently.");
        Assert.Contains("resource traversal stopped", diagnostic, StringComparison.Ordinal);
    }

    // ==================================================================
    //  3. Adversarial input: a shared /Kids array in an encrypted page tree
    // ==================================================================

    /// <summary>
    /// The same shape applied to the page tree. This walker only runs when the catalog could not
    /// be loaded — i.e. for an encrypted document — and PDF/UA-1 expressly permits encryption
    /// (UA06), so encrypted files are a supported audit target for attacker-chosen input.
    /// </summary>
    private static byte[] BuildEncryptedSharedKidsArrayPageTree(int n)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids 3 0 R /Count " + n + " >>",
        };

        var shared = new StringBuilder("[");
        for (int i = 0; i < n; i++) shared.Append(4 + i).Append(" 0 R ");
        shared.Append(']');
        objects.Add(shared.ToString());

        for (int i = 0; i < n; i++)
            objects.Add("<< /Type /Pages /Kids 3 0 R /Count 1 >>");

        objects.Add(StandardEncryptionDictionary);

        return BuildPdfWithTrailer(" /Encrypt " + objects.Count + " 0 R", objects.ToArray());
    }

    [Fact]
    public void Validate_EncryptedPageTreeSharingOneKidsArray_SpendsTheBudgetAndSaysSo()
    {
        var (result, diagnostic) = ValidateWithin(
            BuildEncryptedSharedKidsArrayPageTree(500),
            "PdfUaChecker.Validate on a shared page-tree /Kids array");

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(diagnostic),
            "the page-tree budget never fired on a graph with 250 000 edges.");
        Assert.Contains("page-tree traversal stopped", diagnostic, StringComparison.Ordinal);
    }

    // ==================================================================
    //  4. UA03 on an encrypted document: skipped is not passed
    // ==================================================================

    /// <summary>
    /// A one-page document carrying a /Metadata stream whose bytes are not XML — which is what
    /// every standard-encrypted document's XMP packet looks like to a checker without the key
    /// (unless /EncryptMetadata is false). Everything else the checker reads is correct, so the
    /// only thing it can possibly report is the state of UA03.
    /// </summary>
    private static byte[] BuildTaggedPdfWithUnreadableXmp(bool encrypted)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /Lang (en-US) "
                + "/ViewerPreferences << /DisplayDocTitle true >> /StructTreeRoot 5 0 R "
                + "/Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 >>",
            // Ciphertext is what a checker without the key sees; any non-XML byte run models it.
            StreamObject("/Type /Metadata /Subtype /XML", "this packet is not XML"),
            "<< /Type /StructTreeRoot /K 6 0 R /ParentTree 7 0 R >>",
            "<< /Type /StructElem /S /Document /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [6 0 R]] >>",
            "<< /Title (Quarterly Report) >>",
        };

        string trailer = " /Info 8 0 R";
        if (encrypted)
        {
            objects.Add(StandardEncryptionDictionary);
            trailer += " /Encrypt " + objects.Count + " 0 R";
        }

        return BuildPdfWithTrailer(trailer, objects.ToArray());
    }

    /// <summary>
    /// The defect in one assertion: an encrypted document whose PDF/UA identifier is unreadable
    /// used to come back with <b>zero</b> violations, because the one check that could have said
    /// anything about it returned without recording a thing. A file carrying no confirmable
    /// pdfuaid:part must never be reported as conforming.
    /// </summary>
    [Fact]
    public void Validate_EncryptedDocumentWithUnreadableXmp_IsNotReportedAsConforming()
    {
        var result = Validate(BuildTaggedPdfWithUnreadableXmp(encrypted: true));

        Assert.False(result.IsConforming,
            "an encrypted document whose pdfuaid:part could not be read was called conforming: "
            + Describe(result));
    }

    /// <summary>
    /// And it says <i>which</i> check it could not run, by code. "Some checks were skipped" would
    /// leave a reader no better off than the silence it replaces.
    /// </summary>
    [Fact]
    public void Validate_EncryptedDocumentWithUnreadableXmp_NamesUa03AsUnverified()
    {
        var result = Validate(BuildTaggedPdfWithUnreadableXmp(encrypted: true));

        Assert.Contains(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("UA03", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control that keeps the rule honest in the other direction: the identical document
    /// without an /Encrypt entry is a plain, decidable UA03 — the checker must not answer
    /// "unverifiable" where it can actually see the answer.
    /// </summary>
    [Fact]
    public void Validate_UnencryptedDocumentWithUnreadableXmp_ReportsUa03Outright()
    {
        var result = Validate(BuildTaggedPdfWithUnreadableXmp(encrypted: false));

        Assert.Contains(result.Violations, v => v.Code == "UA03");
        Assert.DoesNotContain(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("UA03", StringComparison.Ordinal));
    }

    // ==================================================================
    //  5. /Type is Optional on /MCR and /OBJR (Tables 324 and 325)
    // ==================================================================

    /// <summary>
    /// A conforming, connected document whose content references omit the Optional <c>/Type</c>:
    /// the &lt;P&gt; owns MCID 0 through a bare <c>&lt;&lt; /Pg … /MCID 0 &gt;&gt;</c> and the
    /// &lt;Link&gt; owns the page's only annotation through a bare
    /// <c>&lt;&lt; /Pg … /Obj … &gt;&gt;</c>. /MCID and /Obj are the sole required entries of
    /// those two forms, so this file is legal and fully tagged.
    /// </summary>
    private static byte[] BuildTypelessContentReferencePdf()
        => BuildPdfWithTrailer(" /Info 10 0 R",
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /Lang (en-US) "
                + "/ViewerPreferences << /DisplayDocTitle true >> /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] "
                + "/Tabs /S /StructParents 0 /Contents 9 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>",
            "<< /Type /StructTreeRoot /K 6 0 R /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /K [7 0 R 11 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /K << /Pg 3 0 R /MCID 0 >> >>",
            "<< /Nums [0 [7 0 R 11 0 R]] >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Hello) Tj ET\nEMC"),
            "<< /Title (Typeless) >>",
            "<< /Type /StructElem /S /Link /P 6 0 R /Alt (Link to the annual report) "
                + "/K << /Pg 3 0 R /Obj 4 0 R >> >>");

    /// <summary>
    /// Before the fix this file produced UA29 with the strongest wording the checker owns —
    /// "no element in this tree is connected to any page content" — against a &lt;P&gt; that owns
    /// MCID 0 and a &lt;Link&gt; that owns the page's only annotation.
    /// </summary>
    [Fact]
    public void Validate_TypelessMarkedContentReference_IsNotReportedAsUnconnected()
    {
        var result = Validate(BuildTypelessContentReferencePdf());
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA29");
    }

    /// <summary>
    /// The same omission silently disabled ISO 14289-1 §7.18.1: a typeless /OBJR never registered
    /// its annotation, so the &lt;Link&gt; element's /Alt stopped standing in for the annotation's
    /// /Contents and UA27 fired on a link that is correctly described.
    /// </summary>
    [Fact]
    public void Validate_TypelessObjectReference_StillLetsTheLinkElementDescribeItsAnnotation()
    {
        var result = Validate(BuildTypelessContentReferencePdf());
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// The control: dropping /Type must not become "assume content". A /K child that is a
    /// dictionary with neither /MCID, /Obj, /Type nor /S references nothing, and is still UA29.
    /// </summary>
    [Fact]
    public void Validate_TypelessKidWithNeitherMcidNorObj_IsStillReportedAsUnconnected()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /P /K << /Foo (bar) >> >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA29");
    }

    // ==================================================================
    //  6. UA08 — /FT is inheritable, so "has /FT" is not "is a field"
    // ==================================================================

    /// <summary>
    /// The shape every generator that hoists /FT onto a group node produces: a non-terminal parent
    /// carrying /FT and /TU, and two terminal fields that inherit the type and carry no tooltip.
    /// Two interactive fields have no accessible name.
    /// </summary>
    private static byte[] BuildInheritedFieldTypeForm(string parentExtras, string kidExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R 6 0 R] /Tabs /S >>",
            "<< /FT /Tx /T (group) " + parentExtras + " /Kids [5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T (first) " + kidExtras
                + " /Rect [0 0 100 20] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T (second) " + kidExtras
                + " /Rect [0 0 100 40] >>");

    [Fact]
    public void Validate_TerminalFieldsInheritingTheirFieldType_AreReportedWhenTheyHaveNoTooltip()
    {
        var result = Validate(BuildInheritedFieldTypeForm(
            parentExtras: "/TU (Group)", kidExtras: string.Empty));

        var ua08 = Assert.Single(result.Violations, v => v.Code == "UA08");
        // Both terminal fields, and only them — the parent is named and is not a field a user
        // tabs to, so counting it as well would report three defects where there are two.
        Assert.Contains("2 interactive form field(s)", ua08.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror case, which the old test reported as a defect on a correct file: the fields the
    /// user tabs to are named; the group node above them is not, and /TU is not inheritable
    /// downward anyway, so its absence there harms nobody.
    /// </summary>
    [Fact]
    public void Validate_NamedTerminalFieldsUnderAnUnnamedGroup_ReportNoUa08()
    {
        var result = Validate(BuildInheritedFieldTypeForm(
            parentExtras: string.Empty, kidExtras: "/TU (Your first name)"));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA08");
    }

    /// <summary>
    /// A radio group: the kids are bare widget annotations (appearance only — no /T, no /FT, no
    /// /Kids), so the group itself <i>is</i> the terminal field. Exactly one accessible name is
    /// missing, and it must be counted once, not once per appearance.
    /// </summary>
    [Fact]
    public void Validate_RadioGroupWithWidgetOnlyKids_ReportsTheGroupOnceAndNotItsWidgets()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R 6 0 R] /Tabs /S >>",
            "<< /FT /Btn /T (delivery) /Ff 32768 /Kids [5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /AS /Off /Rect [0 0 20 20] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /AS /Off /Rect [0 30 20 50] >>");

        var ua08 = Assert.Single(Validate(pdf).Violations, v => v.Code == "UA08");
        Assert.Contains("1 interactive form field(s)", ua08.Message, StringComparison.Ordinal);
    }

    // ==================================================================
    //  7. UA09 — /TabOrder is not a PDF key
    // ==================================================================

    private static byte[] BuildAnnotatedPagePdf(string pageExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] "
                + pageExtras + " >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>");

    /// <summary>
    /// ISO 32000-1 Table 30 defines <c>/Tabs</c> and nothing else; <c>/TabOrder</c> is honoured by
    /// no reader, is failed by independent checkers under ISO 14289-1 §7.18.3, and is treated as unfixed
    /// by this library's own <c>SetStructureTabOrder</c> and <c>PdfAccessibilityFixer.FixTabs</c>.
    /// Accepting it here made the checker the single component that called the page conformant.
    /// </summary>
    [Fact]
    public void Validate_PageCarryingOnlyTheLegacyTabOrderKey_ReportsUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf("/TabOrder /S"));
        Assert.Contains(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// …and names the legacy key, so the finding is actionable rather than a bare repetition of
    /// "this page has no /Tabs".
    /// </summary>
    [Fact]
    public void Validate_PageCarryingOnlyTheLegacyTabOrderKey_NamesItInTheMessage()
    {
        var ua09 = Assert.Single(
            Validate(BuildAnnotatedPagePdf("/TabOrder /S")).Violations, v => v.Code == "UA09");

        Assert.Contains("/TabOrder", ua09.Message, StringComparison.Ordinal);
    }

    /// <summary>Control: the real key still satisfies the check, and says nothing about legacy keys.</summary>
    [Fact]
    public void Validate_PageDeclaringTabsS_ReportsNoUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /S"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// A page that declares both keys is satisfied by the real one — the legacy spelling must not
    /// turn a conforming page into a finding.
    /// </summary>
    [Fact]
    public void Validate_PageDeclaringBothKeys_ReportsNoUa09()
    {
        var result = Validate(BuildAnnotatedPagePdf("/Tabs /S /TabOrder /S"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA09");
    }
}
