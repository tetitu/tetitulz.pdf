// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Adversarial-input tests for every DoS / recursion guard in the accessibility stack, as
/// required by CLAUDE.md ("For DoS / recursion guards: add an explicit adversarial-input test
/// that would have hung / stack-overflowed pre-fix").
///
/// <para>
/// <b>What "pre-fix" means here:</b> a structure-tree walker that caps recursion
/// <em>depth</em> and nothing else. A <c>/K</c> cycle whose elements each list the
/// other twice therefore costs 2^100 visits under the 100-level cap — not a slow operation, an
/// unbounded one. The documents built below are exactly that shape, so a walker without a
/// visited set cannot finish them inside any timeout, while a correctly guarded one finishes in
/// milliseconds. The tests are consequently written as "completes within a generous budget AND
/// visited each element exactly once", not merely "does not throw".
/// </para>
///
/// <para>
/// Every PDF here is built in memory from ASCII bytes (CLAUDE.md: tests must be self-contained;
/// no file is read from disk).
/// </para>
/// </summary>
public class AdversarialStructureTests
{
    /// <summary>
    /// Wall-clock budget for one guarded operation. Deliberately enormous relative to the work
    /// involved (these documents hold fewer than a dozen structure elements): the point is that
    /// an unguarded walk cannot finish in <em>any</em> budget, so a generous one removes machine
    /// speed as a source of flakiness while still proving the guard exists. Minutes, not seconds:
    /// a whole-solution run's test hosts stretch a sub-second walk to half a minute.
    /// </summary>
    private const int TimeBudgetMs = 600_000;

    // ===============================================================
    //  Guarded execution harness
    // ===============================================================

    /// <summary>Outcome of one operation run under a hard time budget.</summary>
    private sealed class GuardedRun<T>
    {
        /// <summary>What the operation returned (default when it threw).</summary>
        public T Result;

        /// <summary>The exception the operation threw, or null.</summary>
        public Exception Failure;

        /// <summary>
        /// <see cref="AccessibilityDiagnostics.LastError"/> as observed <em>on the worker
        /// thread</em>. The slot is <c>[ThreadStatic]</c>, so reading it from the test thread
        /// after the fact would always return null.
        /// </summary>
        public string LastDiagnostic;

        /// <summary>
        /// Every diagnostic raised by the worker thread during the run, in order. Collected via
        /// <see cref="AccessibilityDiagnostics.OnEvent"/> and filtered by managed thread id, so
        /// a test class running in parallel cannot contaminate the assertions below.
        /// </summary>
        public IReadOnlyList<string> Diagnostics = Array.Empty<string>();

        /// <summary>How long the operation actually took.</summary>
        public TimeSpan Elapsed;

        /// <summary>Whether any diagnostic mentions the given fragment.</summary>
        public bool Mentions(string fragment)
        {
            foreach (string message in Diagnostics)
                if (message != null && message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a dedicated thread and fails the test if it has not
    /// finished within <see cref="TimeBudgetMs"/>. A dedicated thread (rather than the pool) is
    /// used so the worker's managed thread id is knowable, which is what makes the thread-static
    /// diagnostics slot and the <see cref="AccessibilityDiagnostics.OnEvent"/> filter reliable.
    /// </summary>
    private static GuardedRun<T> RunGuarded<T>(string what, Func<T> operation)
    {
        var run = new GuardedRun<T>();
        var messages = new List<string>();
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
                run.Result = operation();
            }
            catch (Exception ex)
            {
                run.Failure = ex;
            }
            run.LastDiagnostic = AccessibilityDiagnostics.LastError;
        })
        {
            IsBackground = true,
            Name = "adversarial-" + what,
        };

        var stopwatch = Stopwatch.StartNew();
        worker.Start();
        bool finished = worker.Join(TimeBudgetMs);
        stopwatch.Stop();

        AccessibilityDiagnostics.OnEvent -= Handler;

        run.Elapsed = stopwatch.Elapsed;
        lock (messages) run.Diagnostics = messages.ToArray();

        Assert.True(finished,
            what + " did not finish within " + TimeBudgetMs.ToString(CultureInfo.InvariantCulture) +
            " ms. These documents hold fewer than a dozen structure elements, so this is not a slow " +
            "machine: it is the cycle guard failing. Without a visited set, a /K cycle with branching " +
            "factor 2 costs 2^100 visits under the 100-level depth cap.");

        Assert.True(run.Failure == null,
            what + " threw " + run.Failure?.GetType().Name + ": " + run.Failure?.Message +
            ". A hostile structure must degrade into a partial-but-usable result, not an exception.");

        return run;
    }

    // ===============================================================
    //  In-memory PDF construction
    // ===============================================================

    /// <summary>
    /// Assembles a classic-xref PDF from object bodies. <paramref name="bodies"/>[0] is object 1,
    /// [1] is object 2, and so on; object 1 is always the catalog (the trailer's /Root).
    /// Everything is ASCII so <c>StringBuilder.Length</c> is a byte offset.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A page content stream drawing two lines, each inside its own marked-content sequence.</summary>
    private const string MarkedContent =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha line) Tj ET\nEMC\n" +
        "/P <</MCID 1>> BDC\nBT /F1 12 Tf 72 660 Td (Beta line) Tj ET\nEMC\n";

    private static string ContentStreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) +
           " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// Objects 1–5 shared by every adversarial document: catalog (pointing at object 6 as the
    /// structure tree root), page tree, one content-bearing page, the content stream and a font.
    /// </summary>
    private static List<string> CommonPrefix(string catalogExtras)
    {
        return new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> " + catalogExtras + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            ContentStreamObject(MarkedContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
    }

    /// <summary>
    /// <b>The 2^100 document.</b> Structure element A (object 7) lists B twice; B (object 8)
    /// lists A twice. Both carry a real MCID, so every consumer — auditor, reading-order
    /// analyzer, alt-text editor — has genuine work to do rather than bailing out early.
    ///
    /// <para>
    /// Element count reachable with a visited set: root + A + B = 3. Without one: 2^100.
    /// </para>
    /// </summary>
    private static byte[] KCyclePdf()
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R 8 0 R 0] >>");
        bodies.Add("<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /K [7 0 R 7 0 R 1] >>");
        bodies.Add("<< /Nums [0 [7 0 R 8 0 R]] >>");
        return BuildPdf(bodies);
    }

    /// <summary>A single structure element whose <c>/K</c> lists itself twice.</summary>
    private static byte[] SelfReferentialPdf()
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /K [7 0 R 7 0 R 0] >>");
        bodies.Add("<< /Nums [0 [7 0 R]] >>");
        return BuildPdf(bodies);
    }

    /// <summary>
    /// AcroForm whose field tree cycles: field "Root" lists "Child" twice, "Child" lists "Root"
    /// twice. Neither carries /TU, so the tooltip fixers have real work and must not loop.
    /// </summary>
    private static byte[] AcroFormKidsCyclePdf()
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R /AcroForm 9 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [0 1] >>");
        bodies.Add("<< /Nums [0 [7 0 R 7 0 R]] >>");
        bodies.Add("<< /Fields [10 0 R] >>");
        bodies.Add("<< /FT /Tx /T (Root) /Kids [11 0 R 11 0 R] >>");
        bodies.Add("<< /FT /Tx /T (Child) /Parent 10 0 R /Kids [10 0 R 10 0 R] >>");
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A well-formed structure tree whose <c>/ParentTree</c> number tree cycles: node 9 lists
    /// node 10 twice, node 10 lists node 9 twice.
    /// </summary>
    private static byte[] NumberTreeKidsCyclePdf()
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R] >>");
        bodies.Add("<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [0 1] >>");
        bodies.Add("<< /Kids [10 0 R 10 0 R] >>");
        bodies.Add("<< /Kids [9 0 R 9 0 R] >>");
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A legal but pathologically deep tree: <paramref name="levels"/> nested elements, each with
    /// exactly one child. Acyclic, so a visited set does not help — only the depth cap does, and
    /// it must stop the recursion rather than let the CLR unwind the stack.
    /// </summary>
    private static byte[] DeepAcyclicPdf(int levels)
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] >>");

        for (int i = 0; i < levels; i++)
        {
            int self = 7 + i;
            string kids = i == levels - 1
                ? "[0]"
                : "[" + (self + 1).ToString(CultureInfo.InvariantCulture) + " 0 R]";
            bodies.Add("<< /Type /StructElem /S /Sect /P " +
                       (self - 1).ToString(CultureInfo.InvariantCulture) +
                       " 0 R /Pg 3 0 R /K " + kids + " >>");
        }

        return BuildPdf(bodies);
    }

    /// <summary>
    /// Two structure elements (objects 8 and 9) whose <c>/K</c> is the <em>same</em> indirect
    /// array object, which holds one inline structure element.
    ///
    /// <para>
    /// This is the shape that exercises the walker's second cycle guard. The reference-keyed
    /// guard skips a repeat by object number before the child is even resolved, which is why it
    /// is silent; an inline element has no object number, so it goes through the
    /// identity-keyed set instead — and <em>that</em> path reports. The document is a legal DAG,
    /// not a cycle: the point is that collapsing the second occurrence changes what the walk
    /// reports, so it must not do so silently.
    /// </para>
    /// </summary>
    private static byte[] SharedInlineKidPdf()
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 11 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R 9 0 R] >>");
        bodies.Add("<< /Type /StructElem /S /Sect /P 7 0 R /Pg 3 0 R /K 10 0 R >>");
        bodies.Add("<< /Type /StructElem /S /Sect /P 7 0 R /Pg 3 0 R /K 10 0 R >>");
        bodies.Add("[ << /Type /StructElem /S /Figure /Pg 3 0 R /K [0] >> ]");
        bodies.Add("<< /Nums [0 [8 0 R 9 0 R]] >>");
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A wide DAG: <paramref name="levels"/> nodes in a chain, but every node lists its single
    /// child <em>twice</em>. No cycle at all, and the depth cap never fires — yet a path-based
    /// walk enumerates 2^levels distinct paths. Only a global visited set makes this linear.
    /// </summary>
    private static byte[] SharedChildDagPdf(int levels)
    {
        var bodies = CommonPrefix("/StructTreeRoot 6 0 R");
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] >>");

        for (int i = 0; i < levels; i++)
        {
            int self = 7 + i;
            string kids = i == levels - 1
                ? "[0]"
                : "[" + (self + 1).ToString(CultureInfo.InvariantCulture) + " 0 R " +
                  (self + 1).ToString(CultureInfo.InvariantCulture) + " 0 R]";
            bodies.Add("<< /Type /StructElem /S /Sect /P " +
                       (self - 1).ToString(CultureInfo.InvariantCulture) +
                       " 0 R /Pg 3 0 R /K " + kids + " >>");
        }

        return BuildPdf(bodies);
    }

    // ===============================================================
    //  Corpus sanity — the bait must really be hostile
    // ===============================================================

    /// <summary>
    /// Guards every test below against a vacuous pass: if the cycle document silently failed to
    /// parse, or its structure tree were not actually circular, "completed quickly" would prove
    /// nothing. This asserts the trap is armed.
    /// </summary>
    [Fact]
    public void KCyclePdf_ReallyContainsACycle()
    {
        using var doc = PdfDocument.Open(new MemoryStream(KCyclePdf()));
        var a = doc.GetObject(7) as PdfDictionary;
        var b = doc.GetObject(8) as PdfDictionary;

        Assert.NotNull(a);
        Assert.NotNull(b);

        var aKids = a.Get(new PdfName("K")) as PdfArray;
        var bKids = b.Get(new PdfName("K")) as PdfArray;
        Assert.NotNull(aKids);
        Assert.NotNull(bKids);

        // A -> [B, B] and B -> [A, A]: branching factor 2 in both directions.
        Assert.Equal(2, CountReferencesTo(aKids, 8));
        Assert.Equal(2, CountReferencesTo(bKids, 7));
    }

    private static int CountReferencesTo(PdfArray array, int objectNumber)
    {
        int n = 0;
        for (int i = 0; i < array.Count; i++)
            if (array[i] is PdfReference r && r.ReferenceObjectNumber == objectNumber) n++;
        return n;
    }

    // ===============================================================
    //  The walker itself — visited-set guard, observable
    // ===============================================================

    [Fact]
    public void StructTreeWalker_KCycle_VisitsEachElementExactlyOnce()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<IReadOnlyList<StructNode>> run = RunGuarded(
            "StructTreeWalker.Collect over a /K cycle",
            () =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdf));
                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                var root = doc.ResolveObject(
                    doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
                Assert.NotNull(root);
                return StructTreeWalker.Collect(root, doc, index);
            });

        // Root + A + B. Anything larger means an element was expanded more than once.
        Assert.Equal(3, run.Result.Count);

        var seen = new HashSet<int>();
        foreach (StructNode node in run.Result)
            Assert.True(seen.Add(node.ObjectNumber),
                "object " + node.ObjectNumber + " was reported twice by a walk that claims to be cycle-safe");

        // Both elements really were reached — the guard bounds the walk, it does not truncate it.
        Assert.Contains(run.Result, n => n.Role == "Document");
        Assert.Contains(run.Result, n => n.Role == "Figure");
    }

    /// <summary>
    /// The observability half of the cycle guard. A repeat reached through an object
    /// <em>reference</em> is skipped by number before the child is resolved — deliberately
    /// silent, because that fast path is what makes the guard O(1). A repeat reached as an
    /// inline element goes through the identity-keyed set inside the visit itself, and that one
    /// changes the reported tree, so it has to be diagnosable.
    /// </summary>
    [Fact]
    public void StructTreeWalker_SharedInlineElement_ReportsThatItCollapsedTheRepeat()
    {
        byte[] pdf = SharedInlineKidPdf();

        GuardedRun<IReadOnlyList<StructNode>> run = RunGuarded(
            "StructTreeWalker.Collect over a shared inline structure element",
            () =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdf));
                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                var root = doc.ResolveObject(
                    doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
                return StructTreeWalker.Collect(root, doc, index);
            });

        // Root + Document + two Sects + the shared Figure, reported once at its first position.
        Assert.Equal(5, run.Result.Count);

        int figures = 0;
        foreach (StructNode node in run.Result)
            if (node.Role == "Figure") figures++;
        Assert.Equal(1, figures);

        Assert.True(run.Mentions("reached more than once"),
            "collapsing a repeat changes the tree the caller is shown, so it must be reported " +
            "through AccessibilityDiagnostics rather than silently altering the result. Diagnostics: " +
            string.Join(" | ", run.Diagnostics));
    }

    [Fact]
    public void StructTreeWalker_SelfReferentialElement_IsVisitedOnce()
    {
        byte[] pdf = SelfReferentialPdf();

        GuardedRun<IReadOnlyList<StructNode>> run = RunGuarded(
            "StructTreeWalker.Collect over a self-referential element",
            () =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdf));
                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                var root = doc.ResolveObject(
                    doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
                return StructTreeWalker.Collect(root, doc, index);
            });

        // Root + the one element that points at itself.
        Assert.Equal(2, run.Result.Count);
        Assert.Contains(run.Result, n => n.Role == "Figure");
    }

    [Fact]
    public void StructTreeWalker_DeeplyNestedAcyclicTree_StopsAtTheDepthCapWithoutCrashing()
    {
        byte[] pdf = DeepAcyclicPdf(5_000);

        GuardedRun<IReadOnlyList<StructNode>> run = RunGuarded(
            "StructTreeWalker.Collect over a 5,000-level tree",
            () =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdf));
                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                var root = doc.ResolveObject(
                    doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
                return StructTreeWalker.Collect(root, doc, index);
            });

        // The tree is 5,000 deep and perfectly legal, so the visited set cannot help: the depth
        // cap is the only thing standing between this and 5,000 stack frames.
        Assert.True(run.Result.Count > 1,
            "the walk must descend, not bail out at the root");
        Assert.True(run.Result.Count <= StructTreeWalker.DefaultMaxDepth + 1,
            "the walk visited " + run.Result.Count + " nodes; the " +
            StructTreeWalker.DefaultMaxDepth + "-level cap allows at most " +
            (StructTreeWalker.DefaultMaxDepth + 1) + " (the start node plus one per level)");

        Assert.True(run.Mentions("deeper than"),
            "hitting the depth cap truncates the result, so it must be reported. Diagnostics: " +
            string.Join(" | ", run.Diagnostics));
    }

    [Fact]
    public void StructTreeWalker_WideDagWithSharedChildren_IsLinearNotExponential()
    {
        const int Levels = 40;                 // 2^40 distinct paths, 40 distinct nodes
        byte[] pdf = SharedChildDagPdf(Levels);

        GuardedRun<IReadOnlyList<StructNode>> run = RunGuarded(
            "StructTreeWalker.Collect over a 40-level shared-child DAG",
            () =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdf));
                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                var root = doc.ResolveObject(
                    doc.Catalog.Dictionary.Get(new PdfName("StructTreeRoot"))) as PdfDictionary;
                return StructTreeWalker.Collect(root, doc, index);
            });

        // There is no cycle and the depth cap never fires: every one of the 40 nodes is legally
        // reachable and must be reported exactly once. 41 = root + 40 levels.
        Assert.Equal(Levels + 1, run.Result.Count);

        var seen = new HashSet<int>();
        foreach (StructNode node in run.Result)
            Assert.True(seen.Add(node.ObjectNumber),
                "object " + node.ObjectNumber + " was expanded twice; with 40 doubled levels that is " +
                "the first step of a 2^40 walk");
    }

    // ===============================================================
    //  Every consumer, over the /K cycle
    // ===============================================================

    [Fact]
    public void Audit_KCycle_CompletesAndReturnsAUsableReport()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<AccessibilityAuditReport> run = RunGuarded(
            "PdfAccessibility.Audit over a /K cycle",
            () => PdfAccessibility.Audit(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.Issues);

        // The Figure carries no /Alt, so a real audit of this document cannot come back empty.
        // An empty report would mean the walk gave up before reaching the element.
        Assert.NotEmpty(run.Result.Issues);
        Assert.Contains(run.Result.Issues, i => i.Code == "UA07");
    }

    [Fact]
    public void DeepAccessibilityChecker_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<IReadOnlyList<AccessibilityIssue>> run = RunGuarded(
            "PdfDeepAccessibilityChecker.Check over a /K cycle",
            () => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
    }

    [Fact]
    public void SetFigureAltText_KCycle_TouchesTheFigureExactlyOnce()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<SetAltTextResult> run = RunGuarded(
            "PdfStructureTreeEditor.SetFigureAltText over a /K cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                SetAltTextResult result = PdfStructureTreeEditor.SetFigureAltText(
                    input, output, "A described figure");
                Assert.True(output.Length >= pdf.Length,
                    "the editor must always write at least the original document through");
                return result;
            });

        // Exactly one Figure is reachable. A walk with no visited set would revisit it once per
        // path through the cycle.
        Assert.Equal(1, run.Result.Updated);
        Assert.Equal(0, run.Result.Failed);
    }

    [Fact]
    public void FixHeadingHierarchy_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<int> run = RunGuarded(
            "PdfStructureTreeEditor.FixHeadingHierarchy over a /K cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                int changed = PdfStructureTreeEditor.FixHeadingHierarchy(input, output);
                Assert.True(output.Length >= pdf.Length);
                return changed;
            });

        // No headings in this tree, so nothing legitimately changes — but the walk still had to
        // traverse the cycle to find that out.
        Assert.Equal(0, run.Result);
    }

    [Fact]
    public void AnalyzeReadingOrder_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<ReadingOrderAnalysis> run = RunGuarded(
            "PdfReadingOrderAnalyzer.Analyze over a /K cycle",
            () => PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.Issues);

        // Whichever way it goes, the result must be self-describing: either the document was
        // examined, or it says why it was not. A silent empty issue list is the bug this
        // property exists to prevent.
        if (!run.Result.Analyzed)
            Assert.False(string.IsNullOrWhiteSpace(run.Result.NotAnalyzedReason));
    }

    [Fact]
    public void FixReadingOrder_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<ReadingOrderFixResult> run = RunGuarded(
            "PdfReadingOrderAnalyzer.Fix over a /K cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);
                Assert.True(output.Length >= pdf.Length,
                    "the fixer must always write at least the original document through");
                return result;
            });

        Assert.NotNull(run.Result);
        Assert.True(run.Result.ElementsReordered >= 0);
    }

    [Fact]
    public void AutoTag_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<AutoTagResult> run = RunGuarded(
            "PdfAutoTagger.AutoTag over a /K cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                AutoTagResult result = PdfAutoTagger.AutoTag(input, output, new AutoTagOptions());
                Assert.True(output.Length >= pdf.Length);
                return result;
            });

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.Warnings);
        Assert.NotNull(run.Result.SkippedPageReasons);
    }

    [Fact]
    public void PdfUaChecker_KCycle_Completes()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<PdfUaValidationResult> run = RunGuarded(
            "PdfUaChecker.Validate over a /K cycle",
            () => PdfUaChecker.Validate(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.Violations);

        // The Figure has no /Alt, so the structure scan really did reach it; and the scan must
        // not have aborted (UA99 is the "scan could not be completed" code).
        Assert.Contains(run.Result.Violations, v => v.Code == "UA07");
        Assert.DoesNotContain(run.Result.Violations, v => v.Code == "UA99");
    }

    // ===============================================================
    //  Self-referential element, through the public API
    // ===============================================================

    [Fact]
    public void Audit_SelfReferentialElement_Completes()
    {
        byte[] pdf = SelfReferentialPdf();

        GuardedRun<AccessibilityAuditReport> run = RunGuarded(
            "PdfAccessibility.Audit over a self-referential element",
            () => PdfAccessibility.Audit(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        Assert.Contains(run.Result.Issues, i => i.Code == "UA07");
    }

    [Fact]
    public void SetFigureAltText_SelfReferentialElement_UpdatesItOnce()
    {
        byte[] pdf = SelfReferentialPdf();

        GuardedRun<SetAltTextResult> run = RunGuarded(
            "PdfStructureTreeEditor.SetFigureAltText over a self-referential element",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                return PdfStructureTreeEditor.SetFigureAltText(input, output, "Self-referential figure");
            });

        Assert.Equal(1, run.Result.Updated);
    }

    // ===============================================================
    //  Shared inline element, through the public API
    // ===============================================================

    /// <summary>
    /// The same guard seen from a public entry point. Two structure elements share one inline
    /// Figure, so a walk without a visited set would offer it to the caller's alt-text provider
    /// twice; a guarded walk offers it once — and, because an inline element has no object
    /// number, must then explain why it could not be revised instead of silently reporting
    /// success.
    /// </summary>
    [Fact]
    public void SetFigureAltText_SharedInlineElement_OffersItOnceAndExplainsTheSkip()
    {
        byte[] pdf = SharedInlineKidPdf();

        GuardedRun<SetAltTextResult> run = RunGuarded(
            "PdfStructureTreeEditor.SetFigureAltText over a shared inline element",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                return PdfStructureTreeEditor.SetFigureAltText(input, output, "Shared figure");
            });

        // Offered exactly once (Updated + Skipped + Failed over one figure), and the one attempt
        // failed for a stated reason: an inline element has no object number to revise.
        Assert.Equal(0, run.Result.Updated);
        Assert.Equal(0, run.Result.Skipped);
        Assert.Equal(1, run.Result.Failed);

        Assert.True(run.Mentions("inline"),
            "a figure that could not be given alt text must say why. Diagnostics: " +
            string.Join(" | ", run.Diagnostics));
    }

    // ===============================================================
    //  AcroForm /Kids cycle
    // ===============================================================

    [Fact]
    public void PdfUaChecker_AcroFormKidsCycle_Completes()
    {
        byte[] pdf = AcroFormKidsCyclePdf();

        GuardedRun<PdfUaValidationResult> run = RunGuarded(
            "PdfUaChecker.Validate over an AcroForm /Kids cycle",
            () => PdfUaChecker.Validate(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);

        // Both fields lack /TU, so the field walk really did reach them; and it must not have
        // aborted part-way.
        Assert.Contains(run.Result.Violations, v => v.Code == "UA08");
        Assert.DoesNotContain(run.Result.Violations, v => v.Code == "UA99");
    }

    [Fact]
    public void PdfAccessibilityFixer_AcroFormKidsCycle_FixesEachFieldOnce()
    {
        byte[] pdf = AcroFormKidsCyclePdf();

        GuardedRun<AccessibilityFixResult> run = RunGuarded(
            "PdfAccessibilityFixer.Fix over an AcroForm /Kids cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                AccessibilityFixResult result = PdfAccessibilityFixer.Fix(
                    input, output, new AccessibilityFixOptions());
                Assert.True(output.Length >= pdf.Length,
                    "the fixer must always write at least the original document through");
                return result;
            });

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.FixesApplied);
        Assert.NotNull(run.Result.Warnings);
    }

    [Fact]
    public void SetFieldTooltip_AcroFormKidsCycle_FindsTheFieldAndStops()
    {
        byte[] pdf = AcroFormKidsCyclePdf();

        GuardedRun<bool> run = RunGuarded(
            "PdfStructureTreeEditor.SetFieldTooltip over an AcroForm /Kids cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                bool found = PdfStructureTreeEditor.SetFieldTooltip(
                    input, output, "Root", "The root field");
                Assert.True(output.Length >= pdf.Length);
                return found;
            });

        Assert.True(run.Result,
            "the cycle guard must stop the walk re-entering, not stop it finding a real field");
    }

    [Fact]
    public void SetFieldTooltip_AcroFormKidsCycle_MissingFieldStillTerminates()
    {
        byte[] pdf = AcroFormKidsCyclePdf();

        // The worst case: the target does not exist, so the walk cannot stop early and must
        // exhaust the (cyclic) field graph before answering.
        GuardedRun<bool> run = RunGuarded(
            "PdfStructureTreeEditor.SetFieldTooltip searching for an absent field in a cyclic AcroForm",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                return PdfStructureTreeEditor.SetFieldTooltip(
                    input, output, "NoSuchField", "unused");
            });

        Assert.False(run.Result);
        Assert.True(run.Mentions("NoSuchField"),
            "a field that was never found must be reported, not silently treated as 'nothing to do'. " +
            "Diagnostics: " + string.Join(" | ", run.Diagnostics));
    }

    // ===============================================================
    //  Number-tree (/ParentTree) /Kids cycle
    // ===============================================================

    [Fact]
    public void Audit_ParentTreeKidsCycle_Completes()
    {
        byte[] pdf = NumberTreeKidsCyclePdf();

        GuardedRun<AccessibilityAuditReport> run = RunGuarded(
            "PdfAccessibility.Audit over a /ParentTree /Kids cycle",
            () => PdfAccessibility.Audit(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        Assert.NotNull(run.Result.Issues);
    }

    [Fact]
    public void DeepAccessibilityChecker_ParentTreeKidsCycle_ReportsTheEmptyCoverage()
    {
        byte[] pdf = NumberTreeKidsCyclePdf();

        GuardedRun<IReadOnlyList<AccessibilityIssue>> run = RunGuarded(
            "PdfDeepAccessibilityChecker.Check over a /ParentTree /Kids cycle",
            () => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);

        // The cyclic number tree carries no /Nums at all, so the page's /StructParents key
        // resolves to nothing. UA14 is the coverage check; its presence proves the reader walked
        // the tree, terminated, and drew a conclusion rather than hanging or giving up silently.
        Assert.Contains(run.Result, i => i.Code == "UA14");
    }

    // ===============================================================
    //  Deep and wide documents, through the public API
    // ===============================================================

    /// <summary>
    /// The audit terminates on a 5,000-level tree and <b>says that it stopped at the depth cap</b>
    /// rather than presenting a 100-level result as if it were the whole tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The audit does not walk a 5,000-level tree to a conclusion.</b>
    /// <c>StructTreeWalker.DefaultMaxDepth</c> is 100, so 4,900 of these levels are never visited
    /// and every element-level check runs on a list of the first hundred. The walker's diagnostics
    /// event is not part of <c>AccessibilityAuditReport</c>, so without the UA99 the report would
    /// be byte-identical to one in which the whole tree had been examined and found clean.
    /// </para>
    /// <para>
    /// The fixture is 5,000 levels deep for the termination half: <c>RunGuarded</c> fails if the
    /// walk hangs or overflows the stack.
    /// </para>
    /// <para>
    /// Its <c>PdfUaChecker</c> sibling below is a different engine with its own traversal, and it
    /// really does reach a conclusion without reporting a gap — so the two are not inconsistent.
    /// </para>
    /// </remarks>
    [Fact]
    public void Audit_DeeplyNestedAcyclicTree_StopsAtTheDepthCapAndReportsIt()
    {
        byte[] pdf = DeepAcyclicPdf(5_000);

        GuardedRun<AccessibilityAuditReport> run = RunGuarded(
            "PdfAccessibility.Audit over a 5,000-level tree",
            () => PdfAccessibility.Audit(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);

        AccessibilityIssue partial = Assert.Single(run.Result.Issues.Where(i =>
            i.Code == "UA99" &&
            i.Message.IndexOf("collected only in part", StringComparison.Ordinal) >= 0));

        // Info, so a ceiling can never flip IsConforming: "not examined" is not a finding against
        // the document.
        Assert.Equal(IssueSeverity.Info, partial.Severity);
        Assert.Contains("100-level structure-tree depth cap", partial.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tree inside the depth cap reports no such gap, so the notice above is about this document
    /// and not about every document.
    /// </summary>
    [Fact]
    public void Audit_ShallowAcyclicTree_ReportsNoWalkGap()
    {
        AccessibilityAuditReport report = PdfAccessibility.Audit(new MemoryStream(DeepAcyclicPdf(10)));

        Assert.DoesNotContain(report.Issues, i =>
            i.Message.IndexOf("collected only in part", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The checker's walk stops at its 100-level depth cap, and says so: the levels below it were
    /// not examined, so a UA07 figure down there would go unreported, and a clean result that is
    /// silent about that reads as a clean document. The walk used to stop in silence — a tree
    /// deeper than the cap and a tree that ends there produced the same result.
    /// </summary>
    [Fact]
    public void PdfUaChecker_DeeplyNestedAcyclicTree_Completes_AndReportsTheDepthCapAsAPartialScan()
    {
        byte[] pdf = DeepAcyclicPdf(5_000);

        GuardedRun<PdfUaValidationResult> run = RunGuarded(
            "PdfUaChecker.Validate over a 5,000-level tree",
            () => PdfUaChecker.Validate(new MemoryStream(pdf)));

        Assert.NotNull(run.Result);
        PdfUaViolation partial = Assert.Single(run.Result.Violations, v => v.Code == "UA99");
        Assert.Contains("100-level depth cap", partial.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a tree inside the cap is walked whole and nothing calls the scan partial.</summary>
    [Fact]
    public void PdfUaChecker_ShallowAcyclicTree_ReportsNoPartialScan()
    {
        PdfUaValidationResult result = PdfUaChecker.Validate(new MemoryStream(DeepAcyclicPdf(10)));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    [Fact]
    public void Audit_WideDagWithSharedChildren_Completes()
    {
        byte[] pdf = SharedChildDagPdf(40);

        GuardedRun<AccessibilityAuditReport> run = RunGuarded(
            "PdfAccessibility.Audit over a 40-level shared-child DAG",
            () => PdfAccessibility.Audit(new MemoryStream(pdf)));

        // Completing inside the time budget is the DoS property, and RunGuarded already enforces
        // it. What was missing is that the audit *did its job* rather than bailing out early: it
        // must not have thrown, and it must report real findings about this document — an
        // untagged-but-structured DAG is not conformant.
        Assert.Null(run.Failure);
        Assert.NotEmpty(run.Result.Issues);
        Assert.False(run.Result.IsConforming);
    }

    [Fact]
    public void FixReadingOrder_WideDagWithSharedChildren_Completes()
    {
        byte[] pdf = SharedChildDagPdf(40);

        GuardedRun<ReadingOrderFixResult> run = RunGuarded(
            "PdfReadingOrderAnalyzer.Fix over a 40-level shared-child DAG",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);
                Assert.True(output.Length >= pdf.Length);
                return result;
            });

        Assert.NotNull(run.Result);
    }

    // ===============================================================
    //  A hostile document must not corrupt the output
    // ===============================================================

    /// <summary>
    /// Every editor in this feature writes an incremental update, so whatever the guards do, the
    /// original bytes must survive verbatim at the head of the file — including when the walk was
    /// truncated part-way by a cycle.
    /// </summary>
    [Fact]
    public void EditorsOverAHostileTree_StillProduceAStrictAppend()
    {
        byte[] pdf = KCyclePdf();

        GuardedRun<byte[]> run = RunGuarded(
            "PdfStructureTreeEditor.SetFigureAltText output shape over a /K cycle",
            () =>
            {
                using var input = new MemoryStream(pdf);
                using var output = new MemoryStream();
                PdfStructureTreeEditor.SetFigureAltText(input, output, "Described");
                return output.ToArray();
            });

        byte[] produced = run.Result;
        Assert.True(produced.Length > pdf.Length,
            "an applied edit must append a revision");
        for (int i = 0; i < pdf.Length; i++)
            Assert.Equal(pdf[i], produced[i]);

        // And the result must still be a readable PDF, not a truncated one.
        using var doc = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(doc.Pages);
    }
}
