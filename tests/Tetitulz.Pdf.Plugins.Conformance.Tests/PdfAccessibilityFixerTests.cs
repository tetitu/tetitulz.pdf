// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>Tests for PdfAccessibilityFixer.</summary>
public class PdfAccessibilityFixerTests
{
private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Catalog + one page carrying a Widget annotation and one untitled form field.</summary>
    private static byte[] FormPdf(string extraTrailerEntries = "")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>\nendobj\n");
        int obj4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (field1) /Rect [0 0 100 20] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("trailer\n<< /Size 5 /Root 1 0 R{0} >>\n", extraTrailerEntries);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] RunFix(byte[] input, AccessibilityFixOptions? options = null)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        PdfAccessibilityFixer.Fix(inStream, outStream, options);
        return outStream.ToArray();
    }

    [Fact]
    public void Fix_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibilityFixer.Fix((Stream)null!, new MemoryStream()));
    }

    [Fact]
    public void Fix_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibilityFixer.Fix(new MemoryStream(MinimalPdf()), null!));
    }

    [Fact]
    /// <summary>
    /// The default fix pass must actually change the document — and the result must name what it
    /// changed. On an untagged, language-less document that is the catalog <c>/Lang</c> and
    /// <c>/ViewerPreferences /DisplayDocTitle</c>; the produced bytes must differ from the input.
    /// "Output was written" is equally true of a passthrough that fixed nothing.
    /// </summary>
    public void Fix_MinimalPdf_AppliesTheLanguageAndDisplayDocTitleFixes()
    {
        byte[] pdf = MinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var result = PdfAccessibilityFixer.Fix(input, output);

        Assert.NotEqual(pdf, output.ToArray());
        Assert.Contains(result.FixesApplied, f => f.Contains("/Lang"));
        Assert.Contains(result.FixesApplied, f => f.Contains("DisplayDocTitle"));
    }

    [Fact]
    public void Fix_AddLanguage_LogsAction()
    {
        byte[] pdf = MinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(input, output,
            new AccessibilityFixOptions { AddLanguage = true, DefaultLanguage = "he-IL" });
        Assert.Contains(result.FixesApplied, f => f.Contains("he-IL") || f.Contains("Lang"));
    }

    [Fact]
    public void Fix_SetDisplayDocTitle_LogsAction()
    {
        byte[] pdf = MinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(input, output,
            new AccessibilityFixOptions { SetDisplayDocTitle = true });
        Assert.Contains(result.FixesApplied,
            f => f.Contains("DisplayDocTitle") || f.Contains("title"));
    }

    [Fact]
    /// <summary>
    /// Omitting the options argument must mean <i>exactly</i> a default-constructed
    /// <see cref="AccessibilityFixOptions"/> — same fixes, same warnings, byte-identical output
    /// (the synthesised /ID is a digest of the input, so the writer is deterministic).
    ///
    /// <para>That is the only claim the overload makes, and it is one a silently different set of
    /// implicit defaults would break. "Fix did not throw" would not notice, and is equally true of
    /// a pass-through that copied the input across.</para>
    /// </summary>
    public void Fix_NoOptions_IsIdenticalToPassingDefaultOptions()
    {
        byte[] pdf = MinimalPdf();

        using var implicitIn  = new MemoryStream(pdf);
        using var implicitOut = new MemoryStream();
        var withoutOptions = PdfAccessibilityFixer.Fix(implicitIn, implicitOut);

        using var explicitIn  = new MemoryStream(pdf);
        using var explicitOut = new MemoryStream();
        var withDefaults = PdfAccessibilityFixer.Fix(
            explicitIn, explicitOut, new AccessibilityFixOptions());

        Assert.Equal(withDefaults.FixesApplied, withoutOptions.FixesApplied);
        Assert.Equal(withDefaults.Warnings,     withoutOptions.Warnings);
        Assert.Equal(explicitOut.ToArray(),     implicitOut.ToArray());

        // …and those shared defaults really fix something, so the comparison is not between
        // two identical no-ops.
        Assert.NotEmpty(withoutOptions.FixesApplied);
        Assert.NotEqual(pdf, implicitOut.ToArray());
    }

    [Fact]
    public void Fix_OutputStartsWithPdf()
    {
        byte[] pdf = MinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfAccessibilityFixer.Fix(input, output);
        output.Position = 0;
        byte[] header = new byte[4];
        output.Read(header, 0, 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(header));
    }

    [Fact]
    public void AccessibilityFixOptions_Defaults_AreReasonable()
    {
        var opts = new AccessibilityFixOptions();
        Assert.Equal("en-US", opts.DefaultLanguage);
        Assert.True(opts.AddLanguage);
        Assert.True(opts.SetDisplayDocTitle);
        Assert.True(opts.AddMissingTooltips);
        Assert.True(opts.FixTabOrder);
    }

    [Fact]
    /// <summary>
    /// The two report collections must describe the run, not merely exist: this document needs
    /// fixes and gets them cleanly, so <c>FixesApplied</c> is non-empty and <c>Warnings</c> is
    /// empty. <c>NotNull</c> on both is satisfied by a run that did nothing and said nothing.
    /// </summary>
    public void AccessibilityFixResult_ReportsTheFixesItMadeAndWarnsAboutNothing()
    {
        byte[] pdf = MinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var result = PdfAccessibilityFixer.Fix(input, output);

        Assert.NotEmpty(result.FixesApplied);
        Assert.Empty(result.Warnings);

        // Every reported fix says what it did.
        Assert.All(result.FixesApplied, f => Assert.False(string.IsNullOrWhiteSpace(f)));
    }

    // ------------------------------------------------------------------
    //  The fixed output must still be a readable PDF
    // ------------------------------------------------------------------

    [Fact]
    public void Fix_Output_IsReparseable_AndKeepsOriginalRevisionReachable()
    {
        // Proves /Prev points at the cross-reference section the parser actually used:
        // object 3 only exists in the original revision, so it is reachable only through
        // the /Prev chain.
        byte[] output = RunFix(MinimalPdf());
        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
        Assert.NotNull(doc.Pages[0].MediaBox);
    }

    // ------------------------------------------------------------------
    //  /Tabs (ISO 32000-1 Table 30) — NOT /TabOrder
    // ------------------------------------------------------------------

    [Fact]
    public void Fix_AnnotatedPage_WritesTabsNotTabOrder()
    {
        byte[] output = RunFix(FormPdf());

        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0].Dictionary;

        var tabs = page[new PdfName("Tabs")] as PdfName;
        Assert.NotNull(tabs);
        Assert.Equal("S", tabs!.Value);

        // /TabOrder is not a PDF key; no viewer honours it and it must never be written.
        Assert.Null(page[new PdfName("TabOrder")]);
        Assert.DoesNotContain("/TabOrder", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_AnnotatedPage_LogsTabsFix()
    {
        using var input  = new MemoryStream(FormPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(input, output);
        Assert.Contains(result.FixesApplied, f => f.Contains("/Tabs"));
    }

    [Fact]
    public void Fix_FixTabOrderDisabled_DoesNotWriteTabs()
    {
        byte[] output = RunFix(FormPdf(), new AccessibilityFixOptions { FixTabOrder = false });
        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);
        Assert.Null(doc.Pages[0].Dictionary[new PdfName("Tabs")]);
    }

    // ------------------------------------------------------------------
    //  Non-Latin-1 text must survive the write (Hebrew / CJK)
    // ------------------------------------------------------------------

    /// <summary>
    /// The previous hand-rolled writer encoded every object through ISO-8859-1, so any
    /// character above U+00FF became '?'. A Hebrew tooltip was silently destroyed.
    /// Escape sequences are used for the literal so the test is immune to source-file
    /// encoding damage.
    /// </summary>
    [Fact]
    public void Fix_HebrewTooltip_SurvivesRoundTrip()
    {
        // Written as escapes, not literals, so the assertion cannot be weakened by
        // source-file encoding damage (this repository has been bitten by that before).
        const string hebrew = "\u05E9\u05D3\u05D4"; // shin-dalet-he, "field"
        string template = hebrew + " {name}";
        string expected = hebrew + " field1";

        byte[] output = RunFix(FormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = template });

        Assert.Equal(expected, ReadFieldTooltip(output));
    }

    [Fact]
    public void Fix_CjkTooltip_SurvivesRoundTrip()
    {
        const string cjk = "\u6587\u5B57\u5217";  // "character string"
        byte[] output = RunFix(FormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = cjk + " {name}" });

        Assert.Equal(cjk + " field1", ReadFieldTooltip(output));
    }

    [Fact]
    public void Fix_AsciiTooltip_SurvivesRoundTrip()
    {
        byte[] output = RunFix(FormPdf(),
            new AccessibilityFixOptions { DefaultTooltipTemplate = "Enter {name} here" });

        Assert.Equal("Enter field1 here", ReadFieldTooltip(output));
    }

    private static string? ReadFieldTooltip(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var acroForm = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acroForm);
        var fields = doc.ResolveObject(acroForm![PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        var field = doc.ResolveObject(fields![0]) as PdfDictionary;
        Assert.NotNull(field);
        return (field![PdfName.TU] as PdfString)?.GetText();
    }

    // ------------------------------------------------------------------
    //  Trailer /ID — required by PDF/UA and PDF/A, dropped by the old writer
    // ------------------------------------------------------------------

    [Fact]
    public void Fix_TrailerGainsFileIdWhenInputHasNone()
    {
        byte[] output = RunFix(FormPdf());
        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        var id = doc.Trailer[PdfName.ID] as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id!.Count);
        Assert.Equal(16, (id[0] as PdfString)!.RawBytes.Length);
        Assert.Equal(16, (id[1] as PdfString)!.RawBytes.Length);
    }

    [Fact]
    public void Fix_TrailerPreservesExistingFileId()
    {
        const string original = "<0102030405060708090A0B0C0D0E0F10>";
        byte[] output = RunFix(FormPdf($" /ID [{original} {original}]"));

        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        var id = doc.Trailer[PdfName.ID] as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            (id![0] as PdfString)!.RawBytes);
    }

    // ------------------------------------------------------------------
    //  Adversarial input
    // ------------------------------------------------------------------

    /// <summary>
    /// A /Kids graph that loops back on itself would cost branching^depth visits with only the
    /// depth capped. The visited set makes it terminate immediately.
    /// </summary>
    [Fact]
    public void Fix_CyclicFieldTree_Terminates()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int obj4 = sb.Length;
        sb.Append("4 0 obj\n<< /FT /Tx /T (a) /Kids [5 0 R 5 0 R 5 0 R] >>\nendobj\n");
        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /FT /Tx /T (b) /Kids [4 0 R 4 0 R 4 0 R] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());
        var ex = Record.Exception(() => RunFix(pdf));
        Assert.Null(ex);
    }

    /// <summary>
    /// <c>PdfAccessibilityFixer.LastFixError</c> has to be a <em>working</em> per-thread
    /// diagnostic, so this test proves the three things that make it one:
    ///
    /// <list type="number">
    ///   <item>a clean run leaves the slot <c>null</c> (it is cleared on entry, and a successful
    ///     fix reports nothing);</item>
    ///   <item>a real, tolerated failure driven through the public API populates it with the
    ///     operation and the cause — and the same fact reaches
    ///     <see cref="AccessibilityFixResult.Warnings"/>, so the two never disagree;</item>
    ///   <item>the slot is genuinely <c>[ThreadStatic]</c>: two threads triggering
    ///     <em>different</em> failures concurrently each read back their own message, and a
    ///     third thread that ran cleanly still reads <c>null</c> afterwards.</item>
    /// </list>
    ///
    /// <para>
    /// The concurrency half is barriered: both threads finish writing before either one reads.
    /// Without that, a process-global slot would still pass whenever the two operations happened
    /// not to overlap — i.e. the test would pass by timing luck rather than by the property it
    /// claims to check. The previous version of this test reflected over the property and
    /// asserted its type, which passes even if nothing ever writes to it.
    /// </para>
    ///
    /// <para>
    /// Both fixtures use the same trick to force a tolerated failure: an object the fixer wants
    /// to revise that is a <em>direct</em> dictionary nested inside another object, so it has no
    /// object number and an incremental update cannot name it. Every fix but the one under test
    /// is switched off, so the update set stays empty, the writer is never invoked, and the only
    /// thing that can touch the slot is the failure being provoked.
    /// </para>
    /// </summary>
    [Fact]
    public void PdfAccessibilityFixer_ExposesThreadStaticDiagnostic()
    {
        // ---- fixtures ---------------------------------------------------------------
        // Named distinctly from the class-level BuildPdf so this method stays self-contained and
        // shadows nothing.
        static byte[] AssemblePdf(string[] bodies)
        {
            var sb = new StringBuilder();
            sb.Append("%PDF-1.7\n");

            var offsets = new int[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                offsets[i] = sb.Length;
                sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
            }

            int xrefOffset = sb.Length;
            int size = bodies.Length + 1;
            sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
            for (int i = 0; i < offsets.Length; i++)
                sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
            sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\n");
            sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        // A text field that lives *directly* in the catalog's /AcroForm /Fields array. It has
        // /FT and no /TU, so the fixer wants to give it a tooltip — and cannot, because a direct
        // dictionary has no object number to revise.
        static byte[] DirectFieldPdf() => AssemblePdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [ << /FT /Tx /T (direct) >> ] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        });

        // An annotated page written *directly* into /Kids rather than as its own indirect object.
        // The fixer wants to write /Tabs /S on it (ISO 32000-1 Table 30) and, again, cannot name it.
        static byte[] DirectPagePdf() => AssemblePdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [ << /Type /Page /Parent 2 0 R "
                + "/MediaBox [0 0 612 792] /Annots [3 0 R] >> ] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
        });

        // Exactly one fix enabled at a time: an empty update set means the incremental writer is
        // never reached, so no other code path can write to the diagnostic slot.
        static AccessibilityFixOptions Only(bool tooltips, bool tabs) => new AccessibilityFixOptions
        {
            AddLanguage        = false,
            SetDisplayDocTitle = false,
            AddMissingTooltips = tooltips,
            FixTabOrder        = tabs,
        };

        static AccessibilityFixResult Run(byte[] input, AccessibilityFixOptions options, out byte[] produced)
        {
            using var inStream  = new MemoryStream(input);
            using var outStream = new MemoryStream();
            AccessibilityFixResult r = PdfAccessibilityFixer.Fix(inStream, outStream, options);
            produced = outStream.ToArray();
            return r;
        }

        const string TooltipFailure = "PdfAccessibilityFixer.FixFieldsTooltips";
        const string TabsFailure    = "PdfAccessibilityFixer.FixTabs";

        // ---- 1. a clean run reports nothing ------------------------------------------
        byte[] cleanInput = DirectFieldPdf();
        AccessibilityFixResult clean = Run(cleanInput, Only(tooltips: false, tabs: false), out byte[] cleanOutput);

        Assert.True(PdfAccessibilityFixer.LastFixError == null,
            "a clean fix must leave the diagnostic slot null, but it held: " +
            PdfAccessibilityFixer.LastFixError);
        Assert.Empty(clean.FixesApplied);
        Assert.Equal(cleanInput, cleanOutput);

        // ---- 2. a real failure populates it, and agrees with the result ---------------
        byte[] fieldInput = DirectFieldPdf();
        AccessibilityFixResult fieldFix = Run(fieldInput, Only(tooltips: true, tabs: false), out byte[] fieldOutput);

        string? afterField = PdfAccessibilityFixer.LastFixError;
        Assert.False(string.IsNullOrEmpty(afterField),
            "a form field the fixer could not revise must be reported, not dropped silently.");
        Assert.Contains(TooltipFailure, afterField!);
        Assert.Contains("object number could not be determined", afterField!);

        // The diagnostic and the caller-visible result must describe the same event.
        Assert.Empty(fieldFix.FixesApplied);
        Assert.False(fieldFix.AnyFixApplied);
        Assert.Contains(fieldFix.Warnings, w => w.IndexOf("/TU", StringComparison.Ordinal) >= 0);
        // Nothing could be revised, so the bytes must come through untouched.
        Assert.Equal(fieldInput, fieldOutput);

        byte[] pageInput = DirectPagePdf();
        AccessibilityFixResult pageFix = Run(pageInput, Only(tooltips: false, tabs: true), out byte[] pageOutput);

        string? afterPage = PdfAccessibilityFixer.LastFixError;
        Assert.False(string.IsNullOrEmpty(afterPage),
            "a page the fixer could not revise must be reported, not dropped silently.");
        Assert.Contains(TabsFailure, afterPage!);
        Assert.Contains("a page's object number could not be determined", afterPage!);
        Assert.Empty(pageFix.FixesApplied);
        Assert.Contains(pageFix.Warnings, w => w.IndexOf("/Tabs /S", StringComparison.Ordinal) >= 0);
        Assert.Equal(pageInput, pageOutput);

        // ---- 3. per-thread isolation --------------------------------------------------
        // Put this thread back into the "clean" state so that any leak from a worker is visible
        // as a non-null value here at the end.
        Run(DirectFieldPdf(), Only(tooltips: false, tabs: false), out _);
        Assert.True(PdfAccessibilityFixer.LastFixError == null,
            "the second clean run must have cleared the slot again.");

        using var barrier = new System.Threading.Barrier(2);
        var seen     = new string?[2];
        var failures = new Exception?[2];

        void Worker(int slot, byte[] pdf, AccessibilityFixOptions options)
        {
            try
            {
                Run(pdf, options, out _);

                // Both writes must land before either read: without this the test would pass on a
                // process-global slot whenever the two calls simply did not overlap.
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("the partner thread never reached the barrier.");

                seen[slot] = PdfAccessibilityFixer.LastFixError;
            }
            catch (Exception ex)
            {
                // Release the partner rather than leaving it to time out on a torn run. A failure
                // to do so is folded into the report instead of being swallowed.
                try
                {
                    failures[slot] = ex;
                    barrier.RemoveParticipant();
                }
                catch (Exception releaseFailure)
                {
                    failures[slot] = new AggregateException(ex, releaseFailure);
                }
            }
        }

        var tabsThread = new System.Threading.Thread(
            () => Worker(0, DirectPagePdf(), Only(tooltips: false, tabs: true)))
        { IsBackground = true, Name = "fixer-diag-tabs" };

        var tooltipThread = new System.Threading.Thread(
            () => Worker(1, DirectFieldPdf(), Only(tooltips: true, tabs: false)))
        { IsBackground = true, Name = "fixer-diag-tooltip" };

        tabsThread.Start();
        tooltipThread.Start();

        Assert.True(tabsThread.Join(TimeSpan.FromSeconds(60)),   "the /Tabs worker did not finish.");
        Assert.True(tooltipThread.Join(TimeSpan.FromSeconds(60)), "the tooltip worker did not finish.");

        Assert.True(failures[0] == null, "the /Tabs worker threw: "   + failures[0]);
        Assert.True(failures[1] == null, "the tooltip worker threw: " + failures[1]);

        Assert.False(string.IsNullOrEmpty(seen[0]), "the /Tabs worker read back no diagnostic.");
        Assert.False(string.IsNullOrEmpty(seen[1]), "the tooltip worker read back no diagnostic.");

        // Each thread must see its OWN failure and nothing of the other's.
        Assert.Contains(TabsFailure, seen[0]!);
        Assert.DoesNotContain(TooltipFailure, seen[0]!);

        Assert.Contains(TooltipFailure, seen[1]!);
        Assert.DoesNotContain(TabsFailure, seen[1]!);

        // And neither worker's write may reach the thread that ran cleanly.
        Assert.True(PdfAccessibilityFixer.LastFixError == null,
            "a worker thread's diagnostic leaked into this thread's slot: " +
            PdfAccessibilityFixer.LastFixError);
    }

    // ==================================================================
    //  Shared builders for the field-tree tests below
    // ==================================================================

    /// <summary>
    /// Assembles a classic-xref PDF from object bodies. <paramref name="bodies"/>[0] becomes
    /// object 1 (and must be the catalog), [1] object 2, and so on, so a body may reference a
    /// later object by its known number.
    /// </summary>
    private static byte[] BuildPdf(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n{1}\nendobj\n", i + 1, bodies[i]);
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", bodies.Length + 1);
        foreach (int off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", bodies.Length + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A radio-group-shaped form: the group node (object 4) carries <c>/FT</c> and the terminal
    /// field (object 5) <b>inherits</b> it through <c>/Parent</c>. ISO 32000-1 Table 220 marks
    /// <c>/FT</c> inheritable, so object 5 is the field the user tabs to and the one that owes a
    /// /TU — object 4 is non-terminal and its /TU would reach nobody (/TU is not inheritable).
    /// </summary>
    private static byte[] InheritedFieldTypePdf() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>",
        "<< /FT /Btn /T (group) /Kids [5 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T (option1) /Rect [0 0 20 20] >>");

    /// <summary>A terminal field with <c>/FT</c> but no <c>/T</c> to derive a tooltip from.</summary>
    private static byte[] UnnamedFieldPdf() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [0 0 100 20] >>");

    private static PdfDictionary? TopLevelField(PdfDocument doc, int index = 0)
    {
        var acroForm = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acroForm);
        var fields = doc.ResolveObject(acroForm![PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        return doc.ResolveObject(fields![index]) as PdfDictionary;
    }

    private static string? TooltipOf(PdfDictionary? field)
        => (field?[PdfName.TU] as PdfString)?.GetText();

    private static bool HasUa08(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var result = PdfUaChecker.Validate(ms);
        return result.Violations.Any(v => v.Code == "UA08");
    }

    // ==================================================================
    //  Inheritable /FT — the fixer must find the same terminal fields
    //  PdfUaChecker.CollectMissingTooltips finds
    // ==================================================================

    /// <summary>
    /// Testing <c>field[/FT] != null</c> — a <b>direct</b> /FT — a terminal field that inherits /FT
    /// through /Parent would match nothing and never be fixed, while the checker (which resolves the
    /// chain) reports it as a UA08 violation.
    /// </summary>
    [Fact]
    public void Fix_TerminalFieldInheritingFieldTypeThroughParent_GetsTooltip()
    {
        byte[] input = InheritedFieldTypePdf();

        // Precondition: the checker sees the missing tooltip. If this ever stops holding, the
        // consistency assertions below would pass vacuously.
        Assert.True(HasUa08(input));

        byte[] output = RunFix(input);

        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        var group = TopLevelField(doc);
        Assert.NotNull(group);
        var kids = doc.ResolveObject(group![PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);
        var terminal = doc.ResolveObject(kids![0]) as PdfDictionary;

        Assert.Equal("option1", TooltipOf(terminal));
    }

    /// <summary>
    /// /TU is not inheritable (ISO 32000-1 Table 220), so a tooltip on a non-terminal group node
    /// is announced to nobody. Writing one there is the "passes the checker, helps no human"
    /// outcome this fixer exists to avoid.
    /// </summary>
    [Fact]
    public void Fix_NonTerminalGroupField_DoesNotGetTooltip()
    {
        byte[] output = RunFix(InheritedFieldTypePdf());

        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(TooltipOf(TopLevelField(doc)));
    }

    /// <summary>The property that matters: after Fix, the checker has nothing left to report.</summary>
    [Fact]
    public void Fix_InheritedFieldType_LeavesNoUa08_CheckerAndFixerAgree()
    {
        Assert.False(HasUa08(RunFix(InheritedFieldTypePdf())));
    }

    [Fact]
    public void Fix_SimpleForm_LeavesNoUa08_CheckerAndFixerAgree()
    {
        Assert.True(HasUa08(FormPdf()));
        Assert.False(HasUa08(RunFix(FormPdf())));
    }

    // ==================================================================
    //  Adversarial /Parent chain
    // ==================================================================

    /// <summary>
    /// Resolving inheritable /FT walks /Parent, and a hostile file can make that a ring. The walk
    /// is capped by hops and by the shared traversal budget, so it costs a fixed number of lookups.
    /// Asserting that the budget was never exhausted is the non-flaky way to say "promptly": a
    /// spinning walk would have burned all 200 000 nodes and warned about it.
    /// </summary>
    [Fact]
    public void Fix_ParentCycle_TerminatesWithoutExhaustingBudget()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /T (a) /Parent 5 0 R >>",
            "<< /T (b) /Parent 4 0 R >>");

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        AccessibilityFixResult? result = null;
        var ex = Record.Exception(() => result = PdfAccessibilityFixer.Fix(input, output));

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Warnings, w => w.Contains("traversal stopped"));

        // No /FT is reachable anywhere in the ring, so neither node is a form field and no
        // tooltip may be invented for it.
        using var ms = new MemoryStream(output.ToArray());
        using var doc = PdfDocument.Open(ms);
        Assert.Null(TooltipOf(TopLevelField(doc)));
    }

    // ==================================================================
    //  Honesty: a fabricated /TU is reported, never banked silently
    // ==================================================================

    /// <summary>
    /// /TU is the field's accessible name. Deriving it from the /T partial name yields text that
    /// satisfies UA08 but was written by a machine, not an author — the caller has to be told.
    /// </summary>
    [Fact]
    public void Fix_TooltipDerivedFromPartialName_EmitsPlaceholderWarning()
    {
        using var input  = new MemoryStream(FormPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(input, output);

        Assert.Contains(result.Warnings,
            w => w.Contains("field1") && w.Contains("placeholder"));
    }

    [Fact]
    public void Fix_TooltipDerivedFromPartialName_WarningNamesTheField()
    {
        using var input  = new MemoryStream(FormPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(input, output);

        List<string> tooltipWarnings = result.Warnings.Where(w => w.Contains("/TU")).ToList();
        Assert.Single(tooltipWarnings);
        Assert.Contains("field1", tooltipWarnings[0], StringComparison.Ordinal);
        Assert.Contains("/T partial name", tooltipWarnings[0], StringComparison.Ordinal);
    }

    // ==================================================================
    //  "FieldN" fallback — opt-in only
    // ==================================================================

    [Fact]
    public void AccessibilityFixOptions_PlaceholderOptIn_DefaultsToFalse()
    {
        Assert.False(new AccessibilityFixOptions().AddPlaceholderTooltipsForUnnamedFields);
    }

    /// <summary>
    /// A /TU of "Field1" would hide the problem: the document passes UA08 and the screen-reader
    /// user still has nothing to hear. By default the honest violation is left standing.
    /// </summary>
    [Fact]
    public void Fix_FieldWithNoPartialName_WritesNoTooltipByDefault()
    {
        byte[] input = UnnamedFieldPdf();
        Assert.True(HasUa08(input));

        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(inStream, outStream);

        byte[] output = outStream.ToArray();
        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(TooltipOf(TopLevelField(doc)));

        // Skipped, not silently ignored — and the violation is deliberately still there.
        Assert.Contains(result.Warnings, w => w.Contains("no /T partial name"));
        Assert.DoesNotContain(result.FixesApplied, f => f.Contains("/TU"));
        Assert.True(HasUa08(output));
    }

    [Fact]
    public void Fix_FieldWithNoPartialName_WritesPlaceholderUnderOptIn()
    {
        using var inStream  = new MemoryStream(UnnamedFieldPdf());
        using var outStream = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(inStream, outStream,
            new AccessibilityFixOptions { AddPlaceholderTooltipsForUnnamedFields = true });

        byte[] output = outStream.ToArray();
        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal("Field1", TooltipOf(TopLevelField(doc)));
        Assert.Contains(result.Warnings, w => w.Contains("synthetic placeholder"));
        Assert.Contains(result.FixesApplied, f => f.Contains("/TU"));
    }

    /// <summary>
    /// The opt-in exists so a caller can trade an honest violation for a hollow pass; it must
    /// actually make the checker go quiet, or the option is useless.
    /// </summary>
    [Fact]
    public void Fix_UnnamedFieldUnderOptIn_LeavesNoUa08()
    {
        using var inStream  = new MemoryStream(UnnamedFieldPdf());
        using var outStream = new MemoryStream();
        PdfAccessibilityFixer.Fix(inStream, outStream,
            new AccessibilityFixOptions { AddPlaceholderTooltipsForUnnamedFields = true });

        Assert.False(HasUa08(outStream.ToArray()));
    }
}
