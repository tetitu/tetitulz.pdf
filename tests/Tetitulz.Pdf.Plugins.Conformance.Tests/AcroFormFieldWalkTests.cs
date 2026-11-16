// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Tests for <see cref="AcroFormFieldWalk"/> — the single AcroForm field traversal shared by
/// <see cref="PdfUaChecker"/> (which decides a document is broken) and
/// <see cref="PdfAccessibilityFixer"/> (which decides what to repair).
///
/// <para>
/// The last class of tests here is the reason the type exists. A checker and the producer it
/// checks that share no code agree only by coincidence, and this repository has paid for that
/// twice: the fixer once demanded a <i>direct</i> <c>/FT</c> while the checker resolved the
/// inheritable one through <c>/Parent</c>, so the checker reported fields the fixer would never
/// touch; and both once honoured an invented <c>/TabOrder</c> key that no PDF reader has ever
/// read, agreeing perfectly with each other and helping nobody. Asserting that the two produce
/// the <b>same set</b> of fields is what keeps the extraction honest.
/// </para>
/// </summary>
public class AcroFormFieldWalkTests
{
    // ==================================================================
    //  Hand-built object graphs — no file, no parser, no I/O
    // ==================================================================

    private static PdfString Str(string s) => new PdfString(Encoding.UTF8.GetBytes(s));

    private static PdfArray Array(params PdfObject[] items)
    {
        var a = new PdfArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    /// <summary>A catalog whose AcroForm <c>/Fields</c> array holds <paramref name="fields"/>.</summary>
    private static PdfDictionary CatalogWithFields(params PdfObject[] fields)
    {
        var acroForm = new PdfDictionary();
        acroForm[PdfName.Fields] = Array(fields);

        var catalog = new PdfDictionary();
        catalog[PdfName.AcroForm] = acroForm;
        return catalog;
    }

    /// <summary>Resolver for a graph built entirely from direct objects.</summary>
    private static AcroFormResolver Direct => obj => obj;

    // ==================================================================
    //  Inheritable /FT (ISO 32000-1 Table 220)
    // ==================================================================

    /// <summary>
    /// /FT is inheritable, so the field the user actually tabs to may carry none of its own. A walk
    /// that tested for a direct /FT would classify the leaf as "not a field" and demand the tooltip
    /// from a group node instead — where /TU is not inheritable and reaches nobody.
    /// </summary>
    [Fact]
    public void Walk_TerminalFieldInheritingFieldTypeTwoLevelsUp_IsFound()
    {
        var leaf = new PdfDictionary();
        leaf[PdfName.T] = Str("leaf");

        var mid = new PdfDictionary();
        mid[PdfName.T]    = Str("mid");
        mid[PdfName.Kids] = Array(leaf);
        leaf[PdfName.Parent] = mid;

        var root = new PdfDictionary();
        root[PdfName.FT]   = new PdfName("Btn");   // two levels above the leaf
        root[PdfName.T]    = Str("root");
        root[PdfName.Kids] = Array(mid);
        mid[PdfName.Parent] = root;

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(root), Direct);

        Assert.Equal(3, walk.Fields.Count);

        AcroFormField leafField = Assert.Single(walk.Fields, f => f.Dictionary == leaf);
        Assert.True(leafField.IsTerminal);
        Assert.Equal("Btn", leafField.FieldType?.Value);
        Assert.True(leafField.NeedsAccessibleName);

        // Exactly one node owes an accessible name, and it is the one the user tabs to.
        Assert.Single(walk.Fields.Where(f => f.NeedsAccessibleName));
    }

    /// <summary>
    /// A node with field kids is non-terminal (ISO 32000-1 §12.7.3.1). Its /TU would be announced
    /// to nobody, so it must never be reported as owing one — not even though it is the node that
    /// carries /FT.
    /// </summary>
    [Fact]
    public void Walk_NonTerminalGroupNode_IsNotTreatedAsAFieldThatOwesAName()
    {
        var kid = new PdfDictionary();
        kid[PdfName.T] = Str("option1");

        var group = new PdfDictionary();
        group[PdfName.FT]   = new PdfName("Btn");
        group[PdfName.T]    = Str("group");
        group[PdfName.Kids] = Array(kid);
        kid[PdfName.Parent] = group;

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(group), Direct);

        AcroFormField groupField = Assert.Single(walk.Fields, f => f.Dictionary == group);
        Assert.False(groupField.IsTerminal);
        Assert.False(groupField.NeedsAccessibleName);

        // "Not asked" rather than "answered no": the walk does not spend budget resolving a type
        // nobody consumes.
        Assert.Null(groupField.FieldType);
        Assert.Null(groupField.HasAccessibleName);

        AcroFormField kidField = Assert.Single(walk.Fields, f => f.Dictionary == kid);
        Assert.True(kidField.IsTerminal);
        Assert.True(kidField.NeedsAccessibleName);
    }

    /// <summary>
    /// A radio group's kids are bare widget annotations — its on-page appearances, not fields.
    /// Counting one as a field would report (and write) the same tooltip once per appearance, and
    /// would move the tooltip off the node that owns it.
    /// </summary>
    [Fact]
    public void Walk_BareWidgetKid_IsNotAField_SoItsParentIsTerminal()
    {
        var widget = new PdfDictionary();                       // no /FT, no /T, no /Kids
        widget[PdfName.Subtype] = new PdfName("Widget");

        var field = new PdfDictionary();
        field[PdfName.FT]   = new PdfName("Tx");
        field[PdfName.T]    = Str("field1");
        field[PdfName.Kids] = Array(widget);
        widget[PdfName.Parent] = field;

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(field), Direct);

        AcroFormField only = Assert.Single(walk.Fields);
        Assert.Same(field, only.Dictionary);
        Assert.True(only.IsTerminal);
        Assert.True(only.NeedsAccessibleName);
    }

    /// <summary>A non-blank /TU is an accessible name; a blank one is the same as having none.</summary>
    [Theory]
    [InlineData("Your full name", false)]
    [InlineData("   ", true)]
    [InlineData("", true)]
    public void Walk_TooltipPresence_DecidesWhetherAnAccessibleNameIsOwed(string tooltip, bool owed)
    {
        var field = new PdfDictionary();
        field[PdfName.FT] = new PdfName("Tx");
        field[PdfName.T]  = Str("field1");
        field[PdfName.TU] = Str(tooltip);

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(field), Direct);

        Assert.Equal(owed, Assert.Single(walk.Fields).NeedsAccessibleName);
    }

    /// <summary>A document with no form is not a broken document.</summary>
    [Fact]
    public void Walk_NoAcroForm_YieldsNoFields()
    {
        var walk = AcroFormFieldWalk.Walk(new PdfDictionary(), Direct);

        Assert.Empty(walk.Fields);
        Assert.False(walk.BudgetExhausted);
    }

    [Fact]
    public void Walk_NullCatalog_YieldsNoFields()
    {
        Assert.Empty(AcroFormFieldWalk.Walk(null, Direct).Fields);
    }

    // ==================================================================
    //  Adversarial input — every guard gets a test that would have hung
    // ==================================================================

    /// <summary>
    /// Resolving the inheritable /FT walks /Parent, and a hostile file can make that a ring. Before
    /// the hop cap this spun forever. Asserting a bounded amount of work — rather than merely
    /// "it returned" — is what makes the guard, not luck, the reason the test passes.
    /// </summary>
    [Fact]
    public void Walk_ParentCycle_TerminatesAfterABoundedNumberOfLookups()
    {
        var a = new PdfDictionary();
        var b = new PdfDictionary();
        a[PdfName.T] = Str("a");
        b[PdfName.T] = Str("b");
        a[PdfName.Parent] = b;
        b[PdfName.Parent] = a;   // ring, and no /FT anywhere in it

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(a), Direct);

        AcroFormField field = Assert.Single(walk.Fields);
        Assert.True(field.IsTerminal);

        // No /FT is reachable, so this is not a form field and no tooltip may be invented for it.
        Assert.Null(field.FieldType);
        Assert.False(field.NeedsAccessibleName);

        Assert.False(walk.BudgetExhausted);

        // One array entry plus 51 /Parent hops, at PdfLimits.MaxFieldInheritanceDepth = 50. A
        // spinning walk would have burned all 200 000.
        //
        // A LITERAL, and that is the whole point. This asserted
        // `<= AcroFormFieldWalk.MaxFieldDepth + 2` while that symbol meant both the /Kids descent
        // and the /Parent climb, so splitting the constant left it testing a bound this loop no
        // longer has. The obvious repair — `Assert.Equal(PdfLimits.MaxFieldInheritanceDepth + 2,
        // …)` — is WORSE: the walk and the expectation read the same const, so they move together
        // and the assertion passes for every value of it. Mutating the cap to 49 and to 51 left it
        // green both times, which is how that was caught. Only a literal fixes the number the name
        // claims, and changing the cap must then be a conscious edit here.
        Assert.Equal(52, walk.NodesExamined);
    }

    /// <summary>
    /// /Kids pointing back at an ancestor is a cheap loop bomb: without the visited set the walk
    /// costs branching^depth visits and never ends.
    /// <para>
    /// It must also not fall silent. A ring in which every node has a field kid contains no
    /// terminal field under the bare ISO reading, so a walk that stopped there would report
    /// nothing at all on hostile input. The "kid that has not already been visited" clause is what
    /// makes the last unvisited node on the branch the tab stop that owes the tooltip.
    /// </para>
    /// </summary>
    [Fact]
    public void Walk_KidsCycle_TerminatesPromptly_AndStillNamesATabStop()
    {
        var a = new PdfDictionary();
        var b = new PdfDictionary();

        a[PdfName.FT]   = new PdfName("Tx");
        a[PdfName.T]    = Str("a");
        a[PdfName.Kids] = Array(b);

        b[PdfName.T]      = Str("b");
        b[PdfName.Kids]   = Array(a);   // back at the ancestor
        b[PdfName.Parent] = a;

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(a), Direct);

        Assert.False(walk.BudgetExhausted);
        Assert.True(walk.NodesExamined < 100,
            $"a /Kids ring cost {walk.NodesExamined} lookups; the visited set should have bounded it.");

        // Each node is visited exactly once.
        Assert.Equal(2, walk.Fields.Count);

        AcroFormField outer = Assert.Single(walk.Fields, f => f.Dictionary == a);
        Assert.False(outer.IsTerminal);

        AcroFormField inner = Assert.Single(walk.Fields, f => f.Dictionary == b);
        Assert.True(inner.IsTerminal);
        Assert.Equal("Tx", inner.FieldType?.Value);   // inherited through /Parent
        Assert.True(inner.NeedsAccessibleName);       // silence on adversarial input is not allowed
    }

    /// <summary>
    /// The control for <see cref="Walk_DeepAcyclicTree_StopsAtTheDepthCap_WithoutStackOverflow"/>'s
    /// truncation flag: an ordinary shallow tree is walked to the end and must NOT claim it was cut
    /// short.
    /// </summary>
    /// <remarks>
    /// Without this, <c>DepthExceeded = true</c> hard-coded satisfies the deep case, and every
    /// conformant document would report UA08 as partial — a channel that fires on correct input is
    /// one a caller learns to ignore, which is worse than never having reported.
    /// </remarks>
    [Fact]
    public void Walk_ShallowTree_DoesNotClaimTheDescentWasTruncated()
    {
        var leaf = new PdfDictionary();
        leaf[PdfName.T] = Str("leaf");
        leaf[PdfName.TU] = Str("Leaf tooltip");

        var root = new PdfDictionary();
        root[PdfName.T] = Str("root");
        root[PdfName.FT] = new PdfName("Tx");
        root[PdfName.Kids] = Array(leaf);
        leaf[PdfName.Parent] = root;

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(root), Direct);

        Assert.Equal(2, walk.Fields.Count);
        Assert.False(walk.DepthExceeded);
        Assert.False(walk.BudgetExhausted);
    }

    /// <summary>
    /// A deep but perfectly acyclic tree defeats a visited set — nothing repeats — so only the
    /// depth cap stands between the recursion and a StackOverflowException, which no
    /// <c>catch</c> can recover from.
    /// </summary>
    [Fact]
    public void Walk_DeepAcyclicTree_StopsAtTheDepthCap_WithoutStackOverflow()
    {
        const int Levels = 400;   // eight times the cap

        var nodes = new PdfDictionary[Levels];
        for (int i = 0; i < Levels; i++)
        {
            nodes[i] = new PdfDictionary();
            nodes[i][PdfName.T] = Str("n" + i);
        }
        nodes[0][PdfName.FT] = new PdfName("Tx");
        for (int i = 0; i < Levels - 1; i++)
        {
            nodes[i][PdfName.Kids]      = Array(nodes[i + 1]);
            nodes[i + 1][PdfName.Parent] = nodes[i];
        }

        AcroFormFieldWalkResult? walk = null;
        Exception? ex = Record.Exception(
            () => walk = AcroFormFieldWalk.Walk(CatalogWithFields(nodes[0]), Direct));

        Assert.Null(ex);
        Assert.NotNull(walk);

        // Depths 0..50 are recorded, at AcroFormFieldWalk.MaxFieldDepth = 50; the frame below the
        // cap returns immediately.
        //
        // LITERALS, for the same reason the /Parent-ring case above uses one. Written as
        // `MaxFieldDepth + 1` the walk and the expectation read the same const and move together,
        // so the assertion passes for every value of it — mutating the descent cap to 51 left this
        // green, which is how that was measured. The cap is a claim about a NUMBER, and only a
        // literal can be wrong about it.
        Assert.Equal(51, walk!.Fields.Count);
        Assert.Equal(50, walk.Fields[walk.Fields.Count - 1].Depth);

        // The walk stopped because of the cap, not because the tree ran out: the deepest node it
        // recorded still has field kids it did not descend into.
        Assert.False(walk.Fields[walk.Fields.Count - 1].IsTerminal);
        Assert.False(walk.BudgetExhausted);

        // And it SAYS SO. The walk used to return silently at the cap, so this result was
        // byte-for-byte what a fully-walked 51-node tree produces — and a consumer's "no field
        // needs a tooltip" then meant "none of the ones I reached", indistinguishably. UA08
        // reports it now, for the same reason it reports the node budget.
        Assert.True(walk.DepthExceeded);
    }

    /// <summary>
    /// The node budget is spent per <i>edge examined</i>, not per distinct node reached: N fields
    /// that all name one shared N-entry /Kids array cost N² resolve-and-probe operations while a
    /// per-node counter moves only N times. Exhaustion must be reported, never silently truncated.
    /// </summary>
    [Fact]
    public void Walk_NodeBudgetExhausted_IsReported_NotSilentlyTruncated()
    {
        var fields = new PdfObject[20];
        for (int i = 0; i < fields.Length; i++)
        {
            var f = new PdfDictionary();
            f[PdfName.FT] = new PdfName("Tx");
            f[PdfName.T]  = Str("f" + i);
            fields[i] = f;
        }

        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(fields), Direct, nodeBudget: 5);

        Assert.True(walk.BudgetExhausted, "a truncated walk must say so; a partial answer that " +
                                          "looks complete is how a broken document passes.");
        Assert.True(walk.Fields.Count < fields.Length);

        // With the budget left alone the same graph completes.
        var full = AcroFormFieldWalk.Walk(CatalogWithFields(fields), Direct);
        Assert.False(full.BudgetExhausted);
        Assert.Equal(fields.Length, full.Fields.Count);
    }

    /// <summary>
    /// Every value in a PDF dictionary may be an indirect reference. Resolution is the caller's
    /// (so failures land in the caller's own diagnostic slot), but the walk must go through it
    /// everywhere — a bare cast would classify a legally-stored field as "not there".
    /// </summary>
    [Fact]
    public void Walk_IndirectReferences_AreResolvedThroughTheCallersResolver()
    {
        byte[] pdf = InheritedFieldTypePdf();

        using var ms  = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var walk = AcroFormFieldWalk.Walk(doc.Catalog!.Dictionary, ResolverFor(doc));

        // /AcroForm, /Fields, the field itself and its /Kids are all indirect in this file.
        Assert.Equal(2, walk.Fields.Count);
        AcroFormField owed = Assert.Single(walk.Fields.Where(f => f.NeedsAccessibleName));
        Assert.True(owed.IsTerminal);
        Assert.Equal("option1", owed.PartialName?.GetText());
    }

    // ==================================================================
    //  The property the extraction exists to guarantee
    // ==================================================================

    /// <summary>
    /// For every document: the fields <see cref="PdfUaChecker"/> reports as missing an accessible
    /// name are <b>exactly</b> the fields <see cref="PdfAccessibilityFixer"/> writes one onto.
    ///
    /// <para>
    /// Measured from the outside, not from the shared code: the checker's answer is the count in
    /// its public UA08 violation, and the fixer's answer is the set of objects whose /TU actually
    /// changed in the bytes it produced. Both are then compared against the walk's own set. When
    /// the two walks were separate copies this test would have failed on the "inherited /FT" and
    /// "two-level inheritance" rows, where the checker reported a violation the fixer could not
    /// see.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("direct-ft")]
    [InlineData("inherited-ft")]
    [InlineData("two-level-inherited-ft")]
    [InlineData("already-named")]
    [InlineData("blank-tooltip")]
    [InlineData("unnamed")]
    [InlineData("widget-kid")]
    [InlineData("mixed")]
    [InlineData("no-form")]
    public void CheckerAndFixer_AgreeExactlyOnWhichFieldsOweAnAccessibleName(string shape)
    {
        byte[] input = FormShape(shape);

        SortedSet<int> owed = FieldsOwingAName(input);

        // ---- the checker's public answer -------------------------------------------------
        PdfUaViolation? ua08 = Ua08(input);
        if (owed.Count == 0)
        {
            Assert.Null(ua08);
        }
        else
        {
            Assert.NotNull(ua08);
            Assert.StartsWith($"{owed.Count} interactive form field(s)", ua08!.Message,
                StringComparison.Ordinal);
        }

        // ---- the fixer's public answer ---------------------------------------------------
        Dictionary<int, bool> before = TooltipByObject(input);
        byte[] output = RunTooltipFix(input);
        Dictionary<int, bool> after = TooltipByObject(output);

        var actuallyFixed = new SortedSet<int>(
            before.Where(kv => !kv.Value && after.TryGetValue(kv.Key, out bool now) && now)
                  .Select(kv => kv.Key));

        Assert.True(owed.SetEquals(actuallyFixed),
            $"the checker says objects [{string.Join(", ", owed)}] owe an accessible name, but the " +
            $"fixer wrote /TU onto [{string.Join(", ", actuallyFixed)}]. These two sets must be " +
            "identical: a violation the fixer cannot see is one it can never close, and a field it " +
            "fixes unasked is one the checker never reported.");

        // No field outside that set was touched, and nothing is left owing afterwards.
        Assert.Equal(before.Count, after.Count);
        Assert.Empty(FieldsOwingAName(output));
        Assert.Null(Ua08(output));
    }

    /// <summary>
    /// The same property stated the other way round, on the one shape that historically broke it:
    /// the fixer must not "fix" the non-terminal group node the checker never reported.
    /// </summary>
    [Fact]
    public void Fixer_DoesNotWriteATooltipOntoANodeTheCheckerNeverReported()
    {
        byte[] input  = FormShape("inherited-ft");
        byte[] output = RunTooltipFix(input);

        using var ms  = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);

        var walk = AcroFormFieldWalk.Walk(doc.Catalog!.Dictionary, ResolverFor(doc));

        foreach (AcroFormField group in walk.Fields.Where(candidate => !candidate.IsTerminal))
        {
            Assert.Null(group.Dictionary[PdfName.TU]);
        }
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>Mirrors the production resolvers: a reference resolves, /Null becomes absent.</summary>
    private static AcroFormResolver ResolverFor(PdfDocument doc) => obj =>
    {
        if (obj == null) return null;
        if (!(obj is PdfReference)) return obj;
        PdfObject resolved = doc.ResolveObject(obj);
        return resolved is PdfNull ? null : resolved;
    };

    private static SortedSet<int> FieldsOwingAName(byte[] pdf)
    {
        using var ms  = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var owed = new SortedSet<int>();
        foreach (AcroFormField f in AcroFormFieldWalk.Walk(doc.Catalog!.Dictionary, ResolverFor(doc)).Fields)
        {
            if (!f.NeedsAccessibleName) continue;
            Assert.True(f.TryGetObjectId(out int objNum, out _),
                "every field in these fixtures is a top-level indirect object.");
            owed.Add(objNum);
        }
        return owed;
    }

    /// <summary>Object number → "has a non-blank /TU", for every field node in the document.</summary>
    private static Dictionary<int, bool> TooltipByObject(byte[] pdf)
    {
        using var ms  = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        AcroFormResolver resolve = ResolverFor(doc);

        var map = new Dictionary<int, bool>();
        foreach (AcroFormField f in AcroFormFieldWalk.Walk(doc.Catalog!.Dictionary, resolve).Fields)
        {
            Assert.True(f.TryGetObjectId(out int objNum, out _),
                "every field in these fixtures is a top-level indirect object.");

            string? tu = (resolve(f.Dictionary[PdfName.TU]) as PdfString)?.GetText();
            map[objNum] = !string.IsNullOrWhiteSpace(tu);
        }
        return map;
    }

    private static PdfUaViolation? Ua08(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations.FirstOrDefault(v => v.Code == "UA08");
    }

    /// <summary>
    /// Runs only the tooltip fix. Placeholders are enabled so that "the fixer declined to invent a
    /// name" cannot masquerade as "the fixer did not find the field" — this test is about which
    /// fields are found, and that distinction is covered separately in PdfAccessibilityFixerTests.
    /// </summary>
    private static byte[] RunTooltipFix(byte[] input)
    {
        using var inStream  = new MemoryStream(input);
        using var outStream = new MemoryStream();

        PdfAccessibilityFixer.Fix(inStream, outStream, new AccessibilityFixOptions
        {
            AddLanguage        = false,
            SetDisplayDocTitle = false,
            FixTabOrder        = false,
            AddMissingTooltips = true,
            AddPlaceholderTooltipsForUnnamedFields = true,
        });

        return outStream.ToArray();
    }

    private static byte[] FormShape(string shape) => shape switch
    {
        // A plain terminal field carrying its own /FT.
        "direct-ft" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx /T (field1) >>"),

        // Radio-group shape: the group owns /FT, the terminal kid inherits it.
        "inherited-ft" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>",
            "<< /FT /Btn /T (group) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T (option1) /Rect [0 0 20 20] >>"),

        // /FT two levels above the field the user tabs to.
        "two-level-inherited-ft" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Btn /T (root) /Kids [5 0 R] >>",
            "<< /T (mid) /Parent 4 0 R /Kids [6 0 R] >>",
            "<< /T (leaf) /Parent 5 0 R >>"),

        "already-named" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx /T (field1) /TU (Your full name) >>"),

        // A whitespace-only /TU is not an accessible name.
        "blank-tooltip" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx /T (field1) /TU (   ) >>"),

        // Terminal field with no /T to derive a name from.
        "unnamed" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx >>"),

        // The kid is a bare widget annotation, so the parent is the terminal field.
        "widget-kid" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>",
            "<< /FT /Tx /T (group) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 20 20] >>"),

        // One field owes a name, its sibling does not.
        "mixed" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Tx /T (needs) >>",
            "<< /FT /Tx /T (has) /TU (Postcode) >>"),

        "no-form" => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture"),
    };

    private static byte[] InheritedFieldTypePdf() => FormShape("inherited-ft");

    /// <summary>
    /// Builds a minimal PDF in memory from object bodies numbered 1..N, object 1 being the
    /// catalog. Self-contained by design: no file on disk, so nothing here can go green by
    /// skipping.
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
}
