// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfStructureAttributes"/> — ISO 32000-1 §14.7.5 structure-element attributes, in both
/// the inline <c>/A</c> form and the <c>/C</c> + <c>/ClassMap</c> form.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> All four public members had <b>no direct test</b>: the type is
/// exercised only through <c>Base.Operations.Structure.PdfStructureTreeReader</c> and
/// Accessibility's UA-2 checkers, so every rule below was reached incidentally, by a checker
/// reporting or not reporting something several layers up.</para>
///
/// <para><b>Both spellings matter, and one of them used to be unread.</b> While <c>/C</c> went
/// unconsulted, a checker-clean table that declared its merges and header scopes through a
/// ClassMap had every span read as 1 and every <c>/Scope</c> read as absent, and was reported as
/// ragged with unaddressable headers.</para>
///
/// <para><b>The owner rule is deliberately asymmetric</b>, which is the part most likely to be
/// "simplified" by someone who has not read §14.7.5.1: an attribute object declaring a
/// <i>different</i> owner is rejected, and one declaring <b>no</b> owner is accepted. Both arms are
/// asserted, because either alone describes a rule that is not this one.</para>
/// </remarks>
public sealed class StructureAttributeLookupTests
{
    private const string Table = "Table";
    private const string Layout = "Layout";

    private static readonly Dictionary<int, PdfObject> Objects = new();

    private static PdfObject Resolve(PdfObject o)
        => o is PdfReference r && Objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject? t) ? t : o;

    /// <summary>Registers <paramref name="value"/> as an indirect object and returns the reference.</summary>
    private static PdfReference Indirect(PdfObject value)
    {
        int number = Objects.Count + 100;
        Objects[number] = value;
        return new PdfReference(number, 0);
    }

    /// <summary>An attribute object: an owner (or none) plus entries.</summary>
    private static PdfDictionary Attrs(string? owner, params (PdfName Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        if (owner != null) d.Set(PdfName.O, new PdfName(owner));
        foreach (var (key, value) in entries) d.Set(key, value);
        return d;
    }

    private static PdfDictionary Element(PdfObject? a = null, PdfObject? c = null)
    {
        var e = new PdfDictionary();
        if (a != null) e.Set(PdfName.A, a);
        if (c != null) e.Set(PdfName.C, c);
        return e;
    }

    private static Dictionary<string, PdfObject> ClassMapOf(params (string Name, PdfObject Value)[] classes)
    {
        var root = new PdfDictionary();
        var map = new PdfDictionary();
        foreach (var (name, value) in classes) map.Set(new PdfName(name), value);
        root.Set(PdfName.ClassMap, map);
        return PdfStructureAttributes.BuildClassMap(root, Resolve);
    }

    private static int? SpanOf(PdfDictionary element, IReadOnlyDictionary<string, PdfObject>? classMap,
        string? owner = Table)
        => (PdfStructureAttributes.Lookup(element, PdfName.ColSpan, owner, classMap, Resolve)
            as PdfNumber)?.IntValue;

    // ── BuildClassMap ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildClassMap_WithNoClassMapEntry_IsEmptyNotNull()
    {
        var map = PdfStructureAttributes.BuildClassMap(new PdfDictionary(), Resolve);

        Assert.NotNull(map);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildClassMap_WithNoStructTreeRoot_IsEmptyNotNull()
        => Assert.Empty(PdfStructureAttributes.BuildClassMap(null, Resolve));

    /// <summary>
    /// The map is resolved one level, so an indirect attribute object becomes the dictionary it
    /// points at — and is then interpreted exactly like an inline one, because §14.7.5.2 gives the
    /// two the same shape.
    /// </summary>
    [Fact]
    public void BuildClassMap_ResolvesAnIndirectAttributeObject()
    {
        var map = ClassMapOf(("Wide", Indirect(Attrs(Table, (PdfName.ColSpan, new PdfNumber(3))))));

        PdfObject value = Assert.Contains("Wide", map);
        Assert.IsAssignableFrom<PdfDictionary>(value);
    }

    // ── Lookup: /A before /C ──────────────────────────────────────────────────────────

    [Fact]
    public void AnInlineAttribute_IsFound()
    {
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))));

        Assert.Equal(2, SpanOf(element, classMap: null));
    }

    [Fact]
    public void AClassAttribute_IsFoundThroughTheClassMap()
    {
        var map = ClassMapOf(("Wide", Attrs(Table, (PdfName.ColSpan, new PdfNumber(4)))));
        var element = Element(c: new PdfName("Wide"));

        Assert.Equal(4, SpanOf(element, map));
    }

    /// <summary>
    /// <c>/A</c> is consulted first: where an element carries both, the inline attribute is the
    /// more specific statement about that one element.
    /// </summary>
    [Fact]
    public void AnInlineAttribute_BeatsTheClassMap()
    {
        var map = ClassMapOf(("Wide", Attrs(Table, (PdfName.ColSpan, new PdfNumber(9)))));
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))), c: new PdfName("Wide"));

        Assert.Equal(2, SpanOf(element, map));
    }

    /// <summary>The <c>/C</c> array form, taking the first class that carries the key.</summary>
    [Fact]
    public void AClassArray_IsWalkedUntilOneCarriesTheKey()
    {
        var map = ClassMapOf(
            ("Plain", Attrs(Table, (PdfName.Scope, new PdfName("Column")))),
            ("Wide", Attrs(Table, (PdfName.ColSpan, new PdfNumber(5)))));

        var classes = new PdfArray();
        classes.Add(new PdfName("Plain"));
        classes.Add(new PdfName("Wide"));

        Assert.Equal(5, SpanOf(Element(c: classes), map));
    }

    // ── the owner rule, both arms ────────────────────────────────────────────────────

    /// <summary>An attribute object owned by someone else does not answer for this owner.</summary>
    [Fact]
    public void AnAttributeOwnedByAnotherOwner_IsNotReturned()
    {
        var element = Element(a: Attrs(Layout, (PdfName.ColSpan, new PdfNumber(2))));

        Assert.Null(SpanOf(element, classMap: null, owner: Table));
    }

    /// <summary>
    /// An attribute object that declares <b>no</b> owner is accepted either way — the lenient arm,
    /// and the reason the rule cannot be written as a simple equality.
    /// </summary>
    [Fact]
    public void AnAttributeWithNoDeclaredOwner_IsAccepted()
    {
        var element = Element(a: Attrs(owner: null, (PdfName.ColSpan, new PdfNumber(7))));

        Assert.Equal(7, SpanOf(element, classMap: null, owner: Table));
    }

    /// <summary>A null owner accepts anything, which is what a caller that does not care passes.</summary>
    [Fact]
    public void ANullOwner_AcceptsAnyAttributeObject()
    {
        var element = Element(a: Attrs(Layout, (PdfName.ColSpan, new PdfNumber(2))));

        Assert.Equal(2, SpanOf(element, classMap: null, owner: null));
    }

    /// <summary>
    /// <c>/O</c> is <b>resolved</b> before it is compared. Read raw, an indirect owner looks absent
    /// and falls into the lenient branch above — so a <c>/Layout</c> attribute would be silently
    /// accepted as a <c>/Table</c> one, which is the defect this read was changed to close.
    /// </summary>
    [Fact]
    public void AnIndirectOwner_IsResolvedBeforeItIsCompared()
    {
        var attributes = new PdfDictionary();
        attributes.Set(PdfName.O, Indirect(new PdfName(Layout)));
        attributes.Set(PdfName.ColSpan, new PdfNumber(2));

        Assert.Null(SpanOf(Element(a: attributes), classMap: null, owner: Table));
    }

    // ── §14.7.5.4: an array of attribute objects, interleaved with revision numbers ──

    [Fact]
    public void AnArrayOfAttributeObjects_SkipsTheRevisionNumbers()
    {
        var array = new PdfArray();
        array.Add(new PdfNumber(0));                                        // a revision number
        array.Add(Attrs(Layout, (PdfName.Scope, new PdfName("Row"))));
        array.Add(new PdfNumber(1));
        array.Add(Attrs(Table, (PdfName.ColSpan, new PdfNumber(6))));

        Assert.Equal(6, SpanOf(Element(a: array), classMap: null));
    }

    // ── the DoS cap ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>/C</c> array longer than <see cref="PdfStructureAttributes.MaxClassNamesPerElement"/> is
    /// walked only that far. The cap is the only bound on an array whose length a hostile file
    /// chooses, so the entry past it must not be reached.
    /// </summary>
    [Fact]
    public void AnOversizedClassArray_IsNotWalkedPastTheCap()
    {
        int cap = PdfStructureAttributes.MaxClassNamesPerElement;

        var classes = new PdfArray();
        for (int i = 0; i < cap; i++) classes.Add(new PdfName("Filler"));
        classes.Add(new PdfName("Wide"));   // one past the cap — must never be consulted

        var map = ClassMapOf(
            ("Filler", Attrs(Table, (PdfName.Scope, new PdfName("Column")))),
            ("Wide", Attrs(Table, (PdfName.ColSpan, new PdfNumber(8)))));

        Assert.Null(SpanOf(Element(c: classes), map));

        // The control: the same class one slot EARLIER is found, so the test above is measuring
        // the cap rather than a class map that never worked.
        var withinCap = new PdfArray();
        for (int i = 0; i < cap - 1; i++) withinCap.Add(new PdfName("Filler"));
        withinCap.Add(new PdfName("Wide"));

        Assert.Equal(8, SpanOf(Element(c: withinCap), map));
    }

    // ── ReadAll ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadAll_GroupsByOwner_AndExcludesTheOwnerItself()
    {
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))));

        var all = PdfStructureAttributes.ReadAll(element, null, Resolve);

        Dictionary<string, PdfObject> table = Assert.Contains(Table, all);
        Assert.Equal(2, ((PdfNumber)table["ColSpan"]).IntValue);
        Assert.DoesNotContain("O", table.Keys);
    }

    /// <summary>
    /// An attribute object with no <c>/O</c> is filed under the empty string — §14.7.5.1 requires
    /// an owner, so a missing one is reported rather than guessed at.
    /// </summary>
    [Fact]
    public void ReadAll_AnAttributeWithNoOwner_IsFiledUnderTheEmptyString()
    {
        var element = Element(a: Attrs(owner: null, (PdfName.ColSpan, new PdfNumber(2))));

        var all = PdfStructureAttributes.ReadAll(element, null, Resolve);

        Assert.Contains(string.Empty, all.Keys);
    }

    /// <summary>
    /// <c>/C</c> is merged <b>under</b> <c>/A</c>, matching <c>Lookup</c>'s order. Written the other
    /// way round, a ClassMap span overrode the inline one.
    /// </summary>
    [Fact]
    public void ReadAll_TheInlineAttributeWinsOverTheClassOne()
    {
        var map = ClassMapOf(("Wide", Attrs(Table, (PdfName.ColSpan, new PdfNumber(9)))));
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))), c: new PdfName("Wide"));

        var all = PdfStructureAttributes.ReadAll(element, map, Resolve);

        Assert.Equal(2, ((PdfNumber)all[Table]["ColSpan"]).IntValue);
    }

    /// <summary>A class attribute the inline object does not carry is still merged in.</summary>
    [Fact]
    public void ReadAll_ClassAttributesFillInWhatTheInlineObjectOmits()
    {
        var map = ClassMapOf(("Wide", Attrs(Table,
            (PdfName.ColSpan, new PdfNumber(9)), (PdfName.Scope, new PdfName("Column")))));
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))), c: new PdfName("Wide"));

        var all = PdfStructureAttributes.ReadAll(element, map, Resolve);

        Assert.Equal(2, ((PdfNumber)all[Table]["ColSpan"]).IntValue);
        Assert.Equal("Column", ((PdfName)all[Table]["Scope"]).Value);
    }

    [Fact]
    public void ReadAll_WithNoElement_IsEmptyNotNull()
        => Assert.Empty(PdfStructureAttributes.ReadAll(null, null, Resolve));

    // ── no resolver at all ──────────────────────────────────────────────────────────

    /// <summary>
    /// A null resolver means "values are direct", which is what a caller walking an encrypted
    /// document by hand supplies. Direct values still read.
    /// </summary>
    [Fact]
    public void ANullResolver_StillReadsDirectValues()
    {
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, new PdfNumber(2))));

        Assert.Equal(2, (PdfStructureAttributes.Lookup(
            element, PdfName.ColSpan, Table, null, null) as PdfNumber)?.IntValue);
    }

    /// <summary>
    /// An inline <c>/A</c> that is itself an indirect reference is followed before its attribute is
    /// read. Read raw, the reference reads as absent, and the attribute is lost even though the
    /// element carries it.
    /// </summary>
    [Fact]
    public void AnIndirectInlineAttributeObject_IsFollowed()
    {
        var element = Element(a: Indirect(Attrs(Table, (PdfName.ColSpan, new PdfNumber(2)))));

        Assert.Equal(2, SpanOf(element, classMap: null));
    }

    /// <summary>
    /// An entry with no value is not an attribute: it is left out of the element's attributes, while
    /// the entries beside it are kept.
    /// </summary>
    [Fact]
    public void AnEntryWithNoValue_IsLeftOutOfTheElementsAttributes()
    {
        var element = Element(a: Attrs(Table, (PdfName.ColSpan, (PdfObject)null), (PdfName.RowSpan, new PdfNumber(2))));

        var all = PdfStructureAttributes.ReadAll(element, null, Resolve);

        Assert.True(all.TryGetValue(Table, out var entries));
        Assert.DoesNotContain("ColSpan", entries.Keys);
        Assert.Equal(2, ((PdfNumber)entries["RowSpan"]).IntValue);
    }
}
