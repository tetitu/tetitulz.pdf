// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The two PDF/UA page-tree walks — <see cref="PdfDeepAccessibilityChecker"/>'s and the Conformance
/// plugin's <see cref="PdfUaChecker"/> — each carry their own private 200 000-<c>/Kids</c>-slot
/// budget, and <c>PdfAccessibility.Audit</c> runs both over the same bytes. If the two ceilings ever
/// drift apart, one combined report describes two different subsets of one file's pages.
///
/// <remarks>
/// <para><b>What this replaces, and why.</b> A class of this name lived in
/// <c>Tetitulz.Pdf.Plugins.Conformance.Tests</c> and invoked only <see cref="PdfUaChecker"/> — the
/// deep checker is not visible from there, since <c>Accessibility → Conformance</c> is the
/// sanctioned edge and not the reverse. So the "parity" it claimed was never asserted: the deep
/// checker's budget could have been 20 or 20 million and it would have stayed green. Its two flat-tree
/// cases survive as <c>PluginPageTreeSlotBudgetTests</c>, which no longer claims to be about both.</para>
///
/// <para><b>Why the assertion is not <c>budgetA == budgetB</c>.</b> CLAUDE.md: a parity test that
/// compares a constant with itself is a tautology, and one such test is exactly why a charge-set
/// divergence went unnoticed here before. Both budgets are <c>private const</c> in any case. So each
/// walk is driven through its real public entry point, over the same two documents, and each is
/// asserted to refuse — with <i>its own</i> evidence — on the one that exceeds the ceiling, and not
/// to refuse on the one that does not.</para>
///
/// <para><b>The two fixtures bracket the ceiling to under half a percent</b>, which is what makes
/// drift detectable rather than merely conceivable. <see cref="SharedKidsPageTree"/> costs
/// <i>n</i>² + <i>n</i> + 1 slots, so <i>n</i> = 446 is 199 363 (under) and <i>n</i> = 447 is
/// 200 257 (over). A budget that moved to 190 000 or to 210 000 reddens one of the four assertions.
/// The shape also keeps the files at ~900 objects instead of the quarter-million a flat tree would
/// need for the same slot count.</para>
///
/// <para><b>Both fixtures are encrypted, and that is not incidental.</b> Each checker walks the page
/// tree by hand only when <c>PdfDocument</c> declined to load the catalog, which in practice means
/// an encrypted document; PDF/UA-1 expressly permits encryption, so it is a supported audit target.
/// An unencrypted fixture takes the parser's own page walk and never reaches either budget. Nothing
/// here is really enciphered — the <c>/Encrypt</c> dictionary exists to select the code path, and it
/// grants accessibility extraction so no permission finding is raised.</para>
///
/// <para><b>Every case also asserts that the walk enumerated the page</b> (a UA09 from the plugin, a
/// UA14 from the deep checker). Without that, "no refusal was reported" would be satisfied just as
/// well by a walk that never ran — the trap CLAUDE.md names for a control fixture.</para>
///
/// <para><b>What is deliberately NOT asserted here: the exact boundary.</b> A tree costing precisely
/// 200 000 slots is the one input on which the two walks still differ, and pinning that would record
/// a defect as a contract. The plugin re-tests the budget on <i>entry</i> to a node
/// (<c>budget &lt;= 0</c>) while the deep checker's entry test is strict (<c>budget &lt; 0</c>), so a
/// node reached with exactly nothing left is dropped-and-reported by one and collected by the other.
/// Measured on a two-page tree costing exactly 200 000 slots: the plugin raised UA99 and audited
/// <b>one</b> page, the deep checker raised nothing and audited <b>two</b> — and the UA99 says the
/// walk "stopped", which on a tree that ended exactly on budget it did not. Both fixtures below stay
/// clear of that point on purpose (637 slots under, 257 over). See the round-16 M7 notes.</para>
/// </remarks>
/// </summary>
public class PageTreeBudgetParityTests
{
    /// <summary>446² + 446 + 1 = 199 363 slots — 637 short of the 200 000 both walks allow.</summary>
    private const int UnderBudget = 446;

    /// <summary>447² + 447 + 1 = 200 257 slots — 257 past it.</summary>
    private const int OverBudget = 447;

    /// <summary>
    /// Converts a page-tree parameter into the slot count both walks charge for it, so the two
    /// constants above are checked arithmetic rather than a comment.
    /// </summary>
    private static int Slots(int n) => n * n + n + 1;

    // =====================================================================
    //  Over the ceiling — both walks must refuse, each in its own words
    // =====================================================================

    /// <summary>
    /// <b>The ceiling.</b> One document, both walks, both refusing. The plugin's refusal travels in
    /// the result as a UA99 violation; the deep checker's travels through
    /// <see cref="AccessibilityDiagnostics"/>. Those are two different channels on purpose — asserting
    /// each on its own terms is what makes this a claim about two implementations rather than one.
    /// </summary>
    [Fact]
    public void APageTreePastTheBudgetIsRefusedByBothWalks()
    {
        Assert.True(Slots(OverBudget) > 200_000, "fixture must exceed the ceiling");
        byte[] pdf = SharedKidsPageTree(OverBudget);

        IReadOnlyList<PdfUaViolation> plugin = ValidateWithPlugin(pdf);
        Assert.Contains(plugin, x =>
            string.Equals(x.Code, "UA99", StringComparison.Ordinal) &&
            x.Message.IndexOf("/Kids slots", StringComparison.Ordinal) >= 0);
        AssertPluginWalkEnumeratedThePage(plugin);

        IReadOnlyList<string> deep = DeepCheck(pdf, out IReadOnlyList<AccessibilityIssue> issues);
        Assert.Contains(deep, m =>
            m != null &&
            m.IndexOf("page-tree walk stopped", StringComparison.Ordinal) >= 0 &&
            m.IndexOf("/Kids slots", StringComparison.Ordinal) >= 0);
        AssertDeepWalkEnumeratedThePage(issues);
    }

    // =====================================================================
    //  Under the ceiling — neither walk may refuse
    // =====================================================================

    /// <summary>
    /// <b>The floor.</b> A ceiling that fires 0.3 % early is as wrong as one that never fires, and a
    /// budget that had drifted downward would show up here rather than in the test above.
    /// </summary>
    [Fact]
    public void APageTreeUnderTheBudgetIsRefusedByNeitherWalk()
    {
        Assert.True(Slots(UnderBudget) < 200_000, "fixture must stay under the ceiling");
        byte[] pdf = SharedKidsPageTree(UnderBudget);

        IReadOnlyList<PdfUaViolation> plugin = ValidateWithPlugin(pdf);
        Assert.DoesNotContain(plugin, x =>
            string.Equals(x.Code, "UA99", StringComparison.Ordinal) &&
            x.Message.IndexOf("/Kids slots", StringComparison.Ordinal) >= 0);
        AssertPluginWalkEnumeratedThePage(plugin);

        IReadOnlyList<string> deep = DeepCheck(pdf, out IReadOnlyList<AccessibilityIssue> issues);
        Assert.DoesNotContain(deep, m =>
            m != null && m.IndexOf("page-tree walk stopped", StringComparison.Ordinal) >= 0);
        AssertDeepWalkEnumeratedThePage(issues);
    }

    // =====================================================================
    //  "The walk really ran" — the control every case shares
    // =====================================================================

    /// <summary>
    /// The one real page carries annotations and no <c>/Tabs /S</c>, so the plugin reports UA09 for
    /// exactly one page. Only a page the hand-written walk put in its list can produce it.
    /// </summary>
    private static void AssertPluginWalkEnumeratedThePage(IReadOnlyList<PdfUaViolation> violations)
    {
        PdfUaViolation tabs = Assert.Single(violations.Where(x =>
            string.Equals(x.Code, "UA09", StringComparison.Ordinal)));
        Assert.Contains("1 page(s)", tabs.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same page names a <c>/StructParents</c> key the (empty) <c>/ParentTree</c> never defines,
    /// which is a UA14 the deep checker can only raise for a page it collected.
    /// </summary>
    private static void AssertDeepWalkEnumeratedThePage(IReadOnlyList<AccessibilityIssue> issues)
    {
        Assert.Contains(issues, i =>
            string.Equals(i.Code, "UA14", StringComparison.Ordinal) &&
            i.Message.IndexOf("/StructParents", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  Driving the two walks
    // =====================================================================

    private static IReadOnlyList<PdfUaViolation> ValidateWithPlugin(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations;
    }

    /// <summary>
    /// Runs the deep checker on a dedicated thread and returns every
    /// <see cref="AccessibilityDiagnostics"/> message that thread raised.
    ///
    /// <para>The dedicated thread makes the worker's managed thread id knowable, which is what lets
    /// the handler discard events raised by test classes running in parallel: the event is
    /// process-wide even though the <c>LastError</c> slot behind it is <c>[ThreadStatic]</c>. Same
    /// shape as <c>CheckerWalkBudgetTests.Audit</c>, for the same reason.</para>
    /// </summary>
    private static IReadOnlyList<string> DeepCheck(byte[] pdf, out IReadOnlyList<AccessibilityIssue> issues)
    {
        var messages = new List<string>();
        IReadOnlyList<AccessibilityIssue> captured = Array.Empty<AccessibilityIssue>();
        Exception failure = null;
        int workerThreadId = 0;

        void Handler(string message)
        {
            if (Volatile.Read(ref workerThreadId) != Environment.CurrentManagedThreadId) return;
            lock (messages) messages.Add(message);
        }

        AccessibilityDiagnostics.OnEvent += Handler;

        var worker = new Thread(() =>
        {
            Volatile.Write(ref workerThreadId, Environment.CurrentManagedThreadId);
            AccessibilityDiagnostics.Clear();
            try { captured = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf)); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "page-tree-budget-parity",
        };

        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(120));
        AccessibilityDiagnostics.OnEvent -= Handler;

        // Not the assertion — the callers' refusal checks are — but a walk that has become unbounded
        // must fail the test rather than wedge the run.
        Assert.True(finished, "the deep audit did not finish; the page-tree walk is unbounded.");
        Assert.True(failure == null,
            "the deep audit threw " + failure?.GetType().Name + ": " + failure?.Message +
            ". A hostile tree must degrade into a partial-but-stated result, not an exception.");

        issues = captured;
        lock (messages) return messages.ToArray();
    }

    // =====================================================================
    //  The fixture
    // =====================================================================

    /// <summary>
    /// One real page plus <paramref name="n"/> <c>/Pages</c> nodes that all name the <b>same</b>
    /// indirect <i>n</i>-entry <c>/Kids</c> array. Legal PDF — <c>/Kids</c> may be an indirect
    /// reference and a node may be shared — and the shape both walks charge per slot for: the
    /// visited set stops each leaf being expanded twice, but every one of the <i>n</i> expansions
    /// still resolves all <i>n</i> slots. So the file is O(<i>n</i>) bytes and the walk is
    /// O(<i>n</i>²) slots, which is how a ~900-object document reaches a 200 000-slot ceiling.
    ///
    /// <para>Charge: 1 + <i>n</i> for the root's own <c>/Kids</c>, then <i>n</i> per intermediate.
    /// The leaves declare <c>/Type /Pages</c> with an empty <c>/Kids</c> — emptied branches, not
    /// pages — so both walks enumerate exactly one page however far they get, and the "the walk ran"
    /// controls read the same on both fixtures.</para>
    /// </summary>
    private static byte[] SharedKidsPageTree(int n)
    {
        const int FirstIntermediate = 8;          // 1–7 are fixed below
        int sharedArray = FirstIntermediate + n;
        int firstLeaf   = sharedArray + 1;

        var bodies = new List<string>(8 + 2 * n)
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) >>",

            // 2 — page-tree root: the one real page, then n intermediate nodes
            "<< /Type /Pages /Kids [3 0 R " + References(FirstIntermediate, n) + "] /Count 1 >>",

            // 3 — the one real page. /Annots with no /Tabs /S is the plugin's UA09; the
            //     /StructParents key the empty /ParentTree never defines is the deep checker's UA14.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/StructParents 42 /Annots [7 0 R] >>",

            // 4 — its content
            StreamObject("BT ET\n"),

            // 5 — structure tree root
            "<< /Type /StructTreeRoot /K [] /ParentTree 6 0 R >>",

            // 6 — a /ParentTree that defines nothing
            "<< /Nums [] >>",

            // 7 — the annotation that makes the page tab-order-relevant
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
        };

        // 8 … 7+n — every intermediate node names the SAME /Kids array, indirectly.
        for (int i = 0; i < n; i++)
        {
            bodies.Add("<< /Type /Pages /Kids " +
                       sharedArray.ToString(CultureInfo.InvariantCulture) + " 0 R /Count 0 >>");
        }

        // 8+n — the shared array.
        bodies.Add("[" + References(firstLeaf, n) + "]");

        // 9+n … 8+2n — emptied branches: they cost a slot each and yield no page.
        for (int i = 0; i < n; i++) bodies.Add("<< /Type /Pages /Kids [] /Count 0 >>");

        return BuildPdf(bodies);
    }

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    /// <summary>"a 0 R b 0 R c 0 R …" for a contiguous run of object numbers.</summary>
    private static string References(int first, int count)
    {
        var sb = new StringBuilder(count * 8);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append((first + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A standard-handler <c>/Encrypt</c> dictionary that grants accessibility extraction (bit 10).
    /// Nothing in the file is actually enciphered; its only job is to stop <c>PdfDocument</c> loading
    /// the catalog, which is what selects the hand-written page walk in both checkers.
    /// </summary>
    private const string EncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3392 "
        + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
        + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var bodies = new List<string>(objectBodies) { EncryptionDictionary };
        string encryptEntry =
            " /Encrypt " + bodies.Count.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + " /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = bodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(encryptEntry).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
