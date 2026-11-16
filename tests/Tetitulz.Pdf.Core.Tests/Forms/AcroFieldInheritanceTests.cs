// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="AcroFieldInheritance.Find(PdfDictionary, PdfName, AncestorStep, System.Func{PdfObject, PdfObject}, System.Func{PdfObject, bool}, int, ref int, out bool)"/>
/// called directly, on a two-node <c>/Parent</c> chain: what keeps the climb going, and where each
/// ceiling stops it. <c>FieldInheritanceChainSplitTests</c> pins which chain a caller walks; these
/// pin the climb itself.
/// </summary>
public class AcroFieldInheritanceTests
{
    private static readonly PdfName Key = PdfName.DA;
    private static readonly PdfString ParentValue = new PdfString("/Helv 12 Tf 0 g");

    /// <summary>A child holding <paramref name="childValue"/> (or nothing) under a parent holding the real value.</summary>
    private static PdfDictionary Child(PdfObject childValue)
    {
        var parent = new PdfDictionary();
        parent.Set(Key, ParentValue);
        var child = new PdfDictionary();
        if (childValue != null) child.Set(Key, childValue);
        child.Set(PdfName.Parent, parent);
        return child;
    }

    private static PdfDictionary Up(PdfDictionary d) => d[PdfName.Parent] as PdfDictionary;

    private static PdfObject Find(PdfDictionary start, System.Func<PdfObject, bool> accept, int maxNodes, int budget, out bool capExhausted)
        => AcroFieldInheritance.Find(start, Key, Up, null, accept, maxNodes, ref budget, out capExhausted);

    [Fact]
    public void APdfNullOnTheChild_DoesNotEndTheClimb()
    {
        Assert.Same(ParentValue, Find(Child(PdfNull.Instance), null, 8, int.MaxValue, out _));
        var own = new PdfString("/Cour 9 Tf");
        Assert.Same(own, Find(Child(own), null, 8, int.MaxValue, out _));     // control: a real value wins
    }

    /// <summary>A value the caller cannot use must not hide the parent's usable one.</summary>
    [Fact]
    public void AValueTheCallerRejects_DoesNotEndTheClimb()
    {
        var child = Child(new PdfNumber(5));
        Assert.Same(ParentValue, Find(child, v => v is PdfString, 8, int.MaxValue, out _));
        Assert.IsType<PdfNumber>(Find(child, null, 8, int.MaxValue, out _));   // control: accepted when anything goes
    }

    /// <summary>The shared budget is charged once per node and tested before the charge: one unit reads the child only.</summary>
    [Fact]
    public void TheBudget_IsTestedBeforeItIsSpent()
    {
        Assert.Null(Find(Child(null), null, 8, 1, out bool cap));
        Assert.False(cap);                                                    // budget exhaustion is silent by contract
        Assert.Same(ParentValue, Find(Child(null), null, 8, 2, out _));
    }

    /// <summary><c>maxNodes</c> counts the start node: one examines the child only and reports the cap.</summary>
    [Fact]
    public void MaxNodes_IsAnExactCeiling_AndReportsWhatItLeftUnread()
    {
        Assert.Null(Find(Child(null), null, 1, int.MaxValue, out bool capped));
        Assert.True(capped);
        Assert.Same(ParentValue, Find(Child(null), null, 2, int.MaxValue, out bool notCapped));
        Assert.False(notCapped);
    }
}
