// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Structure;

/// <summary>
/// <c>PdfOptionalContentRefs</c> — the one answer to "which optional-content groups does the
/// default configuration hide?" (ISO 32000-1 §8.11.4.3).
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> The type is named in CLAUDE.md as a shared decision point and
/// had <b>no direct test of any kind</b>: its two consumers are
/// <c>PdfPageRenderer.BuildHiddenOcgSet</c> (both renderers and the render session) and
/// <c>PdfOcgManager</c> (which feeds <c>PdfOperations.ListLayers</c>), so every rule below was
/// reached only incidentally, through a rendered page or a layer listing.</para>
///
/// <para><b>It was a merge of two disagreeing copies, which makes covering the survivor the point.</b>
/// The renderer honoured <c>/BaseState</c>; the layer listing did not read it at all. A document
/// whose <c>/D</c> says <c>/BaseState /OFF</c> therefore had every layer reported
/// <c>DefaultVisible = true</c> while both renderers correctly drew none of them — a host building
/// a layer UI showed every checkbox ticked over a blank page. CLAUDE.md's rule 5: a merge whose
/// survivor no test reaches is a deletion, not a consolidation.</para>
///
/// <para><b>The rules are order-dependent and are asserted separately for that reason.</b>
/// <c>/BaseState</c> initialises every group named by the top-level <c>/OCGs</c>; <c>/ON</c> and
/// <c>/OFF</c> then override individual groups; and <c>/ON</c> wins for a group named by both,
/// because re-enabling a layer is the explicit act.</para>
/// </remarks>
public sealed class OptionalContentHiddenSetTests
{
    // Object numbers standing in for optional-content groups.
    private const int GroupA = 11;
    private const int GroupB = 12;
    private const int GroupC = 13;

    private static PdfArray Refs(params int[] objectNumbers)
    {
        var a = new PdfArray();
        foreach (int n in objectNumbers) a.Add(new PdfReference(n, 0));
        return a;
    }

    /// <summary>An <c>/OCProperties</c> whose <c>/D</c> is <paramref name="config"/>.</summary>
    private static PdfDictionary OcProperties(PdfDictionary config, PdfArray? allGroups = null)
    {
        var ocp = new PdfDictionary();
        if (allGroups != null) ocp.Set(PdfName.OCGs, allGroups);
        ocp.Set(PdfName.D, config);
        return ocp;
    }

    private static PdfDictionary Config(PdfArray? on = null, PdfArray? off = null, string? baseState = null)
    {
        var d = new PdfDictionary();
        if (on != null) d.Set(PdfName.ON, on);
        if (off != null) d.Set(PdfName.OFF, off);
        if (baseState != null) d.Set(PdfName.BaseState, new PdfName(baseState));
        return d;
    }

    // ── Collect ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Elements are collected <b>as references</b>. A group is identified by object number, so
    /// resolving an element would throw away the identity being recorded.
    /// </summary>
    [Fact]
    public void Collect_ReadsTheObjectNumbersOfADirectArray()
    {
        HashSet<int> set = PdfOptionalContentRefs.Collect(null, Refs(GroupA, GroupC));

        Assert.Equal(new HashSet<int> { GroupA, GroupC }, set);
    }

    /// <summary>A non-reference element is skipped rather than guessed at, and the rest survive.</summary>
    [Fact]
    public void Collect_SkipsElementsThatAreNotReferences()
    {
        var array = new PdfArray();
        array.Add(new PdfNumber(7));
        array.Add(new PdfReference(GroupB, 0));
        array.Add(PdfNull.Instance);

        Assert.Equal(new HashSet<int> { GroupB }, PdfOptionalContentRefs.Collect(null, array));
    }

    /// <summary>
    /// An absent or non-array value yields an <b>empty set, never null</b> — every caller treats
    /// the result as a set to test membership in.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Collect_AnAbsentOrNonArrayValue_YieldsAnEmptySet(bool absent)
    {
        PdfObject? value = absent ? null : new PdfNumber(3);

        HashSet<int> set = PdfOptionalContentRefs.Collect(null, value);

        Assert.NotNull(set);
        Assert.Empty(set);
    }

    // ── BuildHiddenSet: the three rules, in order ────────────────────────────────────

    /// <summary><c>/OFF</c> hides the groups it names, whatever the base state.</summary>
    [Fact]
    public void OffHidesTheGroupsItNames()
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null, OcProperties(Config(off: Refs(GroupB))));

        Assert.Equal(new HashSet<int> { GroupB }, hidden);
    }

    /// <summary>
    /// <b><c>/ON</c> wins for a group named by both.</b> Re-enabling a layer is the explicit act,
    /// so a producer that lists a group in both arrays is asking for it.
    /// </summary>
    [Fact]
    public void OnWinsOverOffForAGroupInBoth()
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null, OcProperties(Config(on: Refs(GroupA), off: Refs(GroupA, GroupB))));

        Assert.Equal(new HashSet<int> { GroupB }, hidden);
    }

    /// <summary>
    /// <c>/BaseState /OFF</c> switches off <i>every</i> group the top-level <c>/OCGs</c> names, and
    /// <c>/ON</c> brings back the ones it lists. This is the rule the layer listing never read.
    /// </summary>
    [Fact]
    public void BaseStateOff_HidesEveryGroupExceptThoseSwitchedOn()
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null,
            OcProperties(Config(on: Refs(GroupA), baseState: "OFF"), Refs(GroupA, GroupB, GroupC)));

        Assert.Equal(new HashSet<int> { GroupB, GroupC }, hidden);
    }

    /// <summary>
    /// The default base state is <c>/ON</c>, and an <b>unrecognised</b> value falls back to it —
    /// there is no prior state for the default configuration to leave "unchanged".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("ON")]
    [InlineData("Unchanged")]
    public void BaseStateOtherThanOff_HidesNothingByItself(string? baseState)
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null, OcProperties(Config(baseState: baseState), Refs(GroupA, GroupB)));

        Assert.Null(hidden);
    }

    /// <summary>
    /// <b>There is deliberately no <c>/OCGs</c> gate.</b> The top-level array is spec-required and
    /// real producers omit it; using it as an early return once showed every hidden layer of such a
    /// document.
    /// </summary>
    [Fact]
    public void OffIsHonouredEvenWithNoTopLevelOcgsArray()
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null, OcProperties(Config(off: Refs(GroupC)), allGroups: null));

        Assert.Equal(new HashSet<int> { GroupC }, hidden);
    }

    // ── null, so a caller can skip the lookup entirely ───────────────────────────────

    [Fact]
    public void NoOcProperties_IsNull() =>
        Assert.Null(PdfOptionalContentRefs.BuildHiddenSet(null, null));

    [Fact]
    public void NoDefaultConfiguration_IsNull() =>
        Assert.Null(PdfOptionalContentRefs.BuildHiddenSet(null, new PdfDictionary()));

    [Fact]
    public void NothingHidden_IsNullRatherThanAnEmptySet()
    {
        var hidden = PdfOptionalContentRefs.BuildHiddenSet(
            null, OcProperties(Config(on: Refs(GroupA))));

        Assert.Null(hidden);
    }

    // ── §7.3.10: a common viewer writes /D as a reference, and /ON and /OFF routinely are ────

    /// <summary>
    /// An indirect <c>/D</c>, <c>/OFF</c> and <c>/OCGs</c> are all followed. Passing the document
    /// is what makes this read the configuration at all — a copy that took the document and never
    /// used it is the defect this type was extracted to fix.
    /// </summary>
    [Fact]
    public void IndirectConfigurationAndLists_AreFollowed()
    {
        using PdfDocument doc = OpenLayeredPdf();

        var ocProperties = (PdfDictionary)doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("OCProperties")]);

        var hidden = PdfOptionalContentRefs.BuildHiddenSet(doc, ocProperties);

        // Object 6 is /OFF; object 5 is /ON. Both arrays and the /D config are indirect.
        Assert.NotNull(hidden);
        Assert.Contains(6, hidden!);
        Assert.DoesNotContain(5, hidden!);
    }

    /// <summary>
    /// The control for the test above: with no document the indirect entries cannot be followed, so
    /// nothing is read. It proves the assertion is about resolution rather than about the fixture.
    /// </summary>
    [Fact]
    public void WithoutTheDocument_TheIndirectConfigurationIsUnreadable()
    {
        using PdfDocument doc = OpenLayeredPdf();

        var ocProperties = (PdfDictionary)doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("OCProperties")]);

        Assert.Null(PdfOptionalContentRefs.BuildHiddenSet(null, ocProperties));
    }

    /// <summary>
    /// A catalog whose <c>/OCProperties</c> names two groups, with <c>/D</c>, <c>/ON</c> and
    /// <c>/OFF</c> all written as indirect references — the shape a common viewer produces.
    /// </summary>
    private static PdfDocument OpenLayeredPdf()
    {
        const int Last = 10;
        var sb = new StringBuilder();
        var offsets = new int[Last + 1];
        sb.Append("%PDF-1.5\n");

        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties 4 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Obj(4, "<< /OCGs 7 0 R /D 8 0 R >>");
        Obj(5, "<< /Type /OCG /Name (Visible) >>");
        Obj(6, "<< /Type /OCG /Name (Hidden) >>");
        Obj(7, "[5 0 R 6 0 R]");
        // /D, and both of its lists, indirect — every hop this type exists to follow.
        Obj(8, "<< /ON 9 0 R /OFF 10 0 R >>");
        Obj(9, "[5 0 R]");
        Obj(10, "[6 0 R]");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", Last + 1);
        for (int i = 1; i <= Last; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", Last + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }
}
