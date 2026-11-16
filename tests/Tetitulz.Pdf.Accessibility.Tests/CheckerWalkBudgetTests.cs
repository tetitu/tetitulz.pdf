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
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Adversarial-input tests for the two hand-written tree walks in
/// <see cref="PdfDeepAccessibilityChecker"/> — the <c>/ParentTree</c> number tree and the page tree
/// — as required by CLAUDE.md for every DoS / recursion guard.
///
/// <para>
/// <b>The shape both tests use, and why the pre-existing guards miss it.</b> Each walk had a visited
/// set, a depth cap and a count of <em>nodes</em>. None of those bounds the cost of expanding
/// <em>one</em> node. A <c>/Kids</c> array may be an indirect reference, and nothing stops
/// <i>n</i> intermediate nodes from naming the same <i>n</i>-entry array: the visited set then stops
/// each child being expanded twice, but every one of the <i>n</i> expansions still resolves all
/// <i>n</i> slots. That is O(n²) work from an O(n)-byte file, with the node counter touched only
/// <i>n</i> times and the depth cap never firing, because the tree is two or three levels deep. It
/// is the same defect <c>StructTreeWalker.DefaultMaxEdges</c> was introduced to stop in the
/// structure tree; neither of these walkers had been updated to match.
/// </para>
///
/// <para>
/// <b>Why these are not timing tests.</b> A quadratic walk is only <i>slow</i> at large <i>n</i>;
/// asserting on a stopwatch would make the tests machine-dependent. The documents below are sized
/// just past each ceiling instead, so the assertion is deterministic — the guard must fire and must
/// say so — and the same file scaled up is what turns into minutes of CPU. Each fixture is a few
/// hundred kilobytes of ASCII built in memory; nothing is read from disk.
/// </para>
/// </summary>
public class CheckerWalkBudgetTests
{
    /// <summary>
    /// Wall-clock ceiling for one audit. Enormous relative to the work these files involve; it is
    /// here only so a walk that has become unbounded fails the test instead of hanging the run.
    /// Minutes, not seconds: a whole-solution run's test hosts stretch a sub-second walk to half
    /// a minute.
    /// </summary>
    private const int TimeBudgetMs = 600_000;

    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// A standard-handler <c>/Encrypt</c> dictionary that grants accessibility extraction (bit 10).
    /// No object below is actually enciphered — see <see cref="PageTreeKidsBomb"/> for why a fixture
    /// wants one at all.
    /// </summary>
    private const string EncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3392 "
        + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
        + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies, bool encrypted = false)
    {
        var bodies = new List<string>(objectBodies);

        string encryptEntry = string.Empty;
        if (encrypted)
        {
            bodies.Add(EncryptionDictionary);
            encryptEntry = " /Encrypt " + bodies.Count.ToString(CultureInfo.InvariantCulture) + " 0 R"
                         + " /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";
        }

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

    // =====================================================================
    //  Diagnostics capture
    // =====================================================================

    /// <summary>
    /// Runs <paramref name="operation"/> on a dedicated thread and returns every
    /// <see cref="AccessibilityDiagnostics"/> message it raised.
    ///
    /// <para>A dedicated thread makes the worker's managed thread id knowable, which is what lets
    /// the handler discard events raised by test classes running in parallel — the event is
    /// process-wide even though the <c>LastError</c> slot behind it is <c>[ThreadStatic]</c>.</para>
    /// </summary>
    private static IReadOnlyList<string> Audit(byte[] pdf, out IReadOnlyList<AccessibilityIssue> issues)
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
            try
            {
                captured = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "walk-budget",
        };

        worker.Start();
        bool finished = worker.Join(TimeBudgetMs);
        AccessibilityDiagnostics.OnEvent -= Handler;

        Assert.True(finished,
            "The audit did not finish within " + TimeBudgetMs.ToString(CultureInfo.InvariantCulture) +
            " ms. These files hold a few thousand tiny objects, so this is not a slow machine: it is " +
            "a tree walk whose cost is quadratic in the number of shared /Kids slots.");

        Assert.True(failure == null,
            "The audit threw " + failure?.GetType().Name + ": " + failure?.Message +
            ". A hostile tree must degrade into a partial-but-stated result, not an exception.");

        issues = captured;
        lock (messages) return messages.ToArray();
    }

    // =====================================================================
    //  /ParentTree — ReadNumberTree
    // =====================================================================

    /// <summary>
    /// A <c>/ParentTree</c> whose <i>n</i> intermediate nodes all point <c>/Kids</c> at one shared
    /// <i>n</i>-entry array. Legal PDF: number-tree nodes may be shared and <c>/Kids</c> may be an
    /// indirect reference. Every leaf carries an empty <c>/Nums</c>, so the entry cap
    /// (200 000 stored entries) is never approached and the tree is two levels deep, so the depth
    /// cap (32) never fires either — the slot ceiling is the only guard that can stop it.
    /// </summary>
    private static byte[] NumberTreeKidsBomb(int n)
    {
        int firstIntermediate = 7;            // 1–6 are fixed below
        int sharedArray       = firstIntermediate + n;
        int firstLeaf         = sharedArray + 1;

        var bodies = new List<string>(7 + 2 * n)
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) >>",

            // 2 — page tree
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

            // 3 — page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 >>",

            // 4 — content
            StreamObject("BT ET\n"),

            // 5 — structure tree root
            "<< /Type /StructTreeRoot /K [] /ParentTree 6 0 R >>",

            // 6 — number tree root: n intermediate kids
            "<< /Kids [" + References(firstIntermediate, n) + "] >>",
        };

        // 7 … 6+n — every intermediate node names the SAME /Kids array, indirectly.
        for (int i = 0; i < n; i++)
            bodies.Add("<< /Kids " + sharedArray.ToString(CultureInfo.InvariantCulture) + " 0 R >>");

        // 7+n — the shared array.
        bodies.Add("[" + References(firstLeaf, n) + "]");

        // 8+n … 7+2n — leaves that contribute no entries at all.
        for (int i = 0; i < n; i++) bodies.Add("<< /Nums [] >>");

        return BuildPdf(bodies);
    }

    /// <summary>
    /// The guard must fire and the report must say the map is partial, because everything UA14
    /// derives from a truncated <c>/ParentTree</c> would otherwise be an accusation about coverage
    /// the walk never reached.
    ///
    /// <para><b>Pre-fix behaviour:</b> the walk ran all 410 240 slot resolves to completion and
    /// returned <c>error == null</c>, so no Info issue was emitted and the audit silently presented
    /// a fully-walked result. At <i>n</i> = 64 000 the same file — a few hundred kilobytes inside an
    /// object stream — is roughly 4·10⁹ resolves.</para>
    /// </summary>
    [Fact]
    public void ParentTreeSharingOneKidsArray_IsTruncatedAndSaidSo()
    {
        // 640² + 640 = 410 240 slots, just past the 400 000 ceiling.
        Audit(NumberTreeKidsBomb(640), out IReadOnlyList<AccessibilityIssue> issues);

        AccessibilityIssue partial = Assert.Single(issues.Where(i =>
            string.Equals(i.Code, "UA14", StringComparison.Ordinal) &&
            i.Message.IndexOf("could only be read partially", StringComparison.Ordinal) >= 0));

        Assert.Equal(IssueSeverity.Info, partial.Severity);
        Assert.Contains("slot ceiling", partial.Message, StringComparison.Ordinal);
        Assert.Contains("400000", partial.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: an ordinary <c>/ParentTree</c> must not be reported as truncated. A
    /// ceiling that fires on real documents would be as useless as no ceiling at all.
    /// </summary>
    [Fact]
    public void WellFormedParentTree_IsNotReportedAsPartiallyRead()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 >>",
            StreamObject("/P <</MCID 0>> BDC BT ET EMC\n"),
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 6 0 R >>",
            "<< /Kids [8 0 R] >>",                                   // one intermediate node
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K [0] >>",
            "<< /Nums [0 [7 0 R]] >>",
        });

        Audit(pdf, out IReadOnlyList<AccessibilityIssue> issues);

        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("could only be read partially", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// A <c>/ParentTree</c> node that cannot be parsed is a tree read in part, and UA14 says so — with
    /// the reason. The parser answers an unparseable object with the null object, as it does an
    /// undefined one, so the walk used to read the node as an empty subtree and the map as whole;
    /// every "no element claims this MCID" it then derived was an accusation about a tree it had
    /// not read. The body is a stray array close, the one shape the tolerant parser refuses.
    /// </summary>
    [Fact]
    public void ParentTreeWithAnUnreadableNode_IsSaidToBePartiallyRead_WithTheReason()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 >>",
            StreamObject("/P <</MCID 0>> BDC BT ET EMC\n"),
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 6 0 R >>",
            "<< /Kids [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K [0] >>",
            "]",                                                      // the leaf, unreadable
        });

        Audit(pdf, out IReadOnlyList<AccessibilityIssue> issues);

        AccessibilityIssue partial = Assert.Single(issues.Where(i =>
            string.Equals(i.Code, "UA14", StringComparison.Ordinal) &&
            i.Message.IndexOf("could only be read partially", StringComparison.Ordinal) >= 0));

        Assert.Equal(IssueSeverity.Info, partial.Severity);
        Assert.Contains("could not be read", partial.Message, StringComparison.Ordinal);
    }
    // =====================================================================
    //  Page tree — CollectPagesFromNode
    // =====================================================================

    /// <summary>
    /// The same shape one level up: <i>n</i> <c>/Pages</c> nodes that all name one shared
    /// <i>n</i>-entry <c>/Kids</c> array of empty <c>/Pages</c> leaves. The document also carries one
    /// real page, so it is a valid file rather than a parser edge case.
    ///
    /// <para><b>Why the fixture is encrypted.</b> The checker has two ways to enumerate pages:
    /// <c>CollectPagesFromParser</c>, which just reads <c>PdfDocument.Pages</c>, and
    /// <c>CollectPagesFromPageTree</c>, the hand-written walk this ceiling lives in. The second one
    /// runs only when <c>ctx.Doc.Catalog</c> is null — i.e. for a document whose catalog the parser
    /// would not load, which is every encrypted document. An unencrypted fixture therefore never
    /// reaches the guarded code at all, so the assertion below would pass or fail for reasons that
    /// have nothing to do with the ceiling. Nothing here is actually enciphered; the
    /// <c>/Encrypt</c> dictionary exists purely to select that code path, and it grants
    /// accessibility extraction so no permission finding is raised.</para>
    /// </summary>
    private static byte[] PageTreeKidsBomb(int n)
    {
        int firstIntermediate = 5;            // 1–4 are fixed below
        int sharedArray       = firstIntermediate + n;
        int firstLeaf         = sharedArray + 1;

        var bodies = new List<string>(5 + 2 * n)
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R >>",

            // 2 — page tree root: one real page, then n intermediate nodes
            "<< /Type /Pages /Kids [3 0 R " + References(firstIntermediate, n) + "] /Count 1 >>",

            // 3 — the one real page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",

            // 4 — its content
            StreamObject("BT ET\n"),
        };

        // 5 … 4+n — every intermediate node names the SAME /Kids array, indirectly.
        for (int i = 0; i < n; i++)
        {
            bodies.Add("<< /Type /Pages /Kids " +
                       sharedArray.ToString(CultureInfo.InvariantCulture) + " 0 R /Count 0 >>");
        }

        // 5+n — the shared array.
        bodies.Add("[" + References(firstLeaf, n) + "]");

        // 6+n … 5+2n — leaves that hold no pages, so the walk gains nothing per slot.
        for (int i = 0; i < n; i++) bodies.Add("<< /Type /Pages /Kids [] /Count 0 >>");

        return BuildPdf(bodies, encrypted: true);
    }

    /// <summary>
    /// The walk must charge per <c>/Kids</c> slot and stop when the ceiling is reached, saying so
    /// through <see cref="AccessibilityDiagnostics"/>.
    ///
    /// <para><b>Pre-fix behaviour:</b> the budget was spent once per node expanded, so this file
    /// touched it 901 times out of 200 000 while performing 202 951 resolves; no diagnostic was
    /// raised and nothing bounded the walk. The message is asserted on the word "slots" precisely
    /// because the old exhaustion message said "nodes" — it could not be produced by the old
    /// code at any <i>n</i> this test could build.</para>
    /// </summary>
    [Fact]
    public void PageTreeSharingOneKidsArray_StopsAtTheSlotCeilingAndReportsIt()
    {
        // 450² + 450 + 1 = 202 951 slots, just past the 200 000 ceiling.
        IReadOnlyList<string> diagnostics = Audit(PageTreeKidsBomb(450), out _);

        Assert.Contains(diagnostics, m =>
            m != null &&
            m.IndexOf("page-tree walk stopped", StringComparison.Ordinal) >= 0 &&
            m.IndexOf("/Kids slots", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// And the ceiling must not fire on an ordinary page tree: a well-formed one holds each node in
    /// exactly one <c>/Kids</c> array, so slots and nodes differ by one there. Encrypted for the
    /// same reason as <see cref="PageTreeKidsBomb"/> — otherwise this control would prove only that
    /// a walk which never ran raised no diagnostic. The two pages it enumerates are asserted, so
    /// the fixture cannot pass by taking a path that collects nothing.
    /// </summary>
    [Fact]
    public void OrdinaryPageTree_DoesNotReachTheSlotCeiling()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",

            // Both pages name a /StructParents key the /ParentTree below never defines, so each one
            // that the walk reaches produces exactly one UA14.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 7 >>",
            StreamObject("BT ET\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /StructParents 8 >>",
            StreamObject("BT ET\n"),
            "<< /Type /StructTreeRoot /K [] /ParentTree 8 0 R >>",
            "<< /Nums [] >>",
        };

        IReadOnlyList<string> diagnostics =
            Audit(BuildPdf(bodies, encrypted: true), out IReadOnlyList<AccessibilityIssue> issues);

        Assert.DoesNotContain(diagnostics, m =>
            m != null && m.IndexOf("page-tree walk stopped", StringComparison.Ordinal) >= 0);

        // Two page-scoped findings prove the hand-written walk really enumerated both pages, so the
        // assertion above is about a ceiling that did not fire rather than a walk that never ran.
        Assert.Equal(2, issues.Count(i =>
            string.Equals(i.Code, "UA14", StringComparison.Ordinal) &&
            i.Message.IndexOf("/StructParents", StringComparison.Ordinal) >= 0));
    }
}
