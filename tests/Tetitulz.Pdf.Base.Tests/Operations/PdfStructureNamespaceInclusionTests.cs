// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfStructureNamespaceInclusion"/> — ISO/TS 32005 Table 5's hierarchical inclusion
/// rules, extracted from the PDF Association's own published "Errata" spreadsheet (the PDF/UA
/// TWG-approved corrected version) rather than the ISO text itself, which this environment cannot
/// render cleanly (see the class's own remarks for the full verification chain). Every value
/// asserted here was independently read back from the raw extracted spreadsheet rows during
/// development, not merely "the code agrees with itself."
/// </summary>
public class PdfStructureNamespaceInclusionTests
{
    // ------------------------------------------------------------------
    //  Cross-checked against the source data directly (not just internal consistency)
    // ------------------------------------------------------------------

    [Fact]
    public void Document_UnderStructTreeRoot_IsRequiredExactlyOnce()
    {
        // The one relationship using the bare "1" value — cross-verified against TS 32005's own
        // prose ("1 StructTreeRoot" under Document's Parents), independently of the megatable.
        var rule = PdfStructureNamespaceInclusion.CanContain("StructTreeRoot", "Document");
        Assert.Equal(StructureContainmentRule.RequiredExactlyOnce, rule);
    }

    [Fact]
    public void Document_UnderDocument_IsOptional()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Document", "Document");
        Assert.Equal(StructureContainmentRule.Optional, rule);
    }

    [Fact]
    public void DocumentFragment_UnderStructTreeRoot_IsForbidden()
    {
        // Only Document may be a direct child of StructTreeRoot.
        var rule = PdfStructureNamespaceInclusion.CanContain("StructTreeRoot", "DocumentFragment");
        Assert.Equal(StructureContainmentRule.Forbidden, rule);
    }

    [Fact]
    public void Part_UnderDocument_IsOptional()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Document", "Part");
        Assert.Equal(StructureContainmentRule.Optional, rule);
    }

    [Fact]
    public void RB_UnderRuby_IsRubySpecific()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Ruby", "RB");
        Assert.Equal(StructureContainmentRule.RubySpecific, rule);
    }

    [Fact]
    public void WT_UnderWarichu_IsWarichuSpecific()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Warichu", "WT");
        Assert.Equal(StructureContainmentRule.WarichuSpecific, rule);
    }

    /// <summary>The one cell in the whole table carrying the undocumented "c" marker (PDF
    /// Association GitHub issue #440) — Figure as a child of WP.</summary>
    [Fact]
    public void Figure_UnderWP_IsUndocumented()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("WP", "Figure");
        Assert.Equal(StructureContainmentRule.Undocumented, rule);
    }

    /// <summary>The single cell the Errata worksheet corrects relative to the as-published one
    /// (confirmed by diffing both worksheets directly during development, not by trusting the
    /// "changes marked in red" note) — pinned so a future re-sync against a newer source notices
    /// if this specific correction regresses.</summary>
    [Fact]
    public void Sub_UnderCode_IsOptional_TheErrataCorrection()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Code", "Sub");
        Assert.Equal(StructureContainmentRule.Optional, rule);
    }

    // ------------------------------------------------------------------
    //  Unknown handling
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("StructTreeRoot", "NotARealType")]
    [InlineData("NotARealType", "Document")]
    [InlineData(null, "Document")]
    [InlineData("Document", null)]
    public void UnrecognisedOrNullNames_ReturnUnknown(string parent, string child)
    {
        var rule = PdfStructureNamespaceInclusion.CanContain(parent, child);
        Assert.Equal(StructureContainmentRule.Unknown, rule);
    }

    /// <summary>"Content Item" is a child-only pseudo-type (a marked-content leaf, not a structure
    /// element) — it must never be usable as a parent.</summary>
    [Fact]
    public void ContentItem_AsAParent_ReturnsUnknown()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Content Item", "Document");
        Assert.Equal(StructureContainmentRule.Unknown, rule);
    }

    /// <summary>"StructTreeRoot" is a parent-only pseudo-type — nothing may contain it.</summary>
    [Fact]
    public void StructTreeRoot_AsAChild_ReturnsUnknown()
    {
        var rule = PdfStructureNamespaceInclusion.CanContain("Document", "StructTreeRoot");
        Assert.Equal(StructureContainmentRule.Unknown, rule);
    }

    // ------------------------------------------------------------------
    //  IsDefinitelyAllowed
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(StructureContainmentRule.Optional, true)]
    [InlineData(StructureContainmentRule.Required, true)]
    [InlineData(StructureContainmentRule.OptionalAtMostOnce, true)]
    [InlineData(StructureContainmentRule.RequiredExactlyOnce, true)]
    [InlineData(StructureContainmentRule.Forbidden, false)]
    [InlineData(StructureContainmentRule.ForbiddenUnlessGroupingLevel, false)]
    [InlineData(StructureContainmentRule.SeeElementDescription, false)]
    [InlineData(StructureContainmentRule.RubySpecific, false)]
    [InlineData(StructureContainmentRule.WarichuSpecific, false)]
    [InlineData(StructureContainmentRule.Undocumented, false)]
    [InlineData(StructureContainmentRule.Unknown, false)]
    public void IsDefinitelyAllowed_MatchesTheDocumentedSet(StructureContainmentRule rule, bool expected)
    {
        Assert.Equal(expected, PdfStructureNamespaceInclusion.IsDefinitelyAllowed(rule));
    }

    // ------------------------------------------------------------------
    //  Structural invariants of the embedded table itself
    // ------------------------------------------------------------------

    [Fact]
    public void ParentTypes_StartsWithStructTreeRootAndHasNoDuplicates()
    {
        Assert.Equal("StructTreeRoot", PdfStructureNamespaceInclusion.ParentTypes[0]);
        Assert.Equal(
            PdfStructureNamespaceInclusion.ParentTypes.Count,
            PdfStructureNamespaceInclusion.ParentTypes.Distinct().Count());
    }

    [Fact]
    public void ChildTypes_EndsWithContentItemAndHasNoDuplicates()
    {
        Assert.Equal("Content Item", PdfStructureNamespaceInclusion.ChildTypes[^1]);
        Assert.Equal(
            PdfStructureNamespaceInclusion.ChildTypes.Count,
            PdfStructureNamespaceInclusion.ChildTypes.Distinct().Count());
    }

    [Fact]
    public void EveryChildTypeExceptContentItem_IsAlsoAParentType()
    {
        var parents = new HashSet<string>(PdfStructureNamespaceInclusion.ParentTypes);
        foreach (string child in PdfStructureNamespaceInclusion.ChildTypes)
        {
            if (child == "Content Item") continue;
            Assert.Contains(child, parents);
        }
    }

    /// <summary>Every known type combination resolves to a real rule — the table has no gaps,
    /// which would otherwise surface as a silent <c>Unknown</c> indistinguishable from a typo in a
    /// caller's own type name.</summary>
    [Fact]
    public void EveryKnownCombination_NeverReturnsUnknown()
    {
        foreach (string parent in PdfStructureNamespaceInclusion.ParentTypes)
        foreach (string child in PdfStructureNamespaceInclusion.ChildTypes)
        {
            var rule = PdfStructureNamespaceInclusion.CanContain(parent, child);
            Assert.NotEqual(StructureContainmentRule.Unknown, rule);
        }
    }
}
