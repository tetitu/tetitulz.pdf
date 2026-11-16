// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Structure;

/// <summary>
/// One cell of ISO 32000-2 Annex L's containment table for each rule it encodes, read through
/// <see cref="PdfStructureNamespaceInclusion.CanContain"/> — so a digit mapped to the wrong rule,
/// or the table read by the wrong axis, is seen.
/// </summary>
public class StructureContainmentTableTests
{
    [Theory]
    [InlineData("StructTreeRoot", "Document", StructureContainmentRule.RequiredExactlyOnce)]
    [InlineData("Note", "DocumentFragment", StructureContainmentRule.ForbiddenUnlessGroupingLevel)]
    [InlineData("Hn", "Art", StructureContainmentRule.OptionalAtMostOnce)]
    [InlineData("Part", "Document", StructureContainmentRule.SeeElementDescription)]
    [InlineData("Ruby", "RB", StructureContainmentRule.RubySpecific)]
    [InlineData("Warichu", "WT", StructureContainmentRule.WarichuSpecific)]
    [InlineData("WP", "Figure", StructureContainmentRule.Undocumented)]
    [InlineData("Document", "P", StructureContainmentRule.Optional)]
    [InlineData("TR", "P", StructureContainmentRule.Forbidden)]
    public void OneCellPerRule(string parent, string child, StructureContainmentRule rule)
        => Assert.Equal(rule, PdfStructureNamespaceInclusion.CanContain(parent, child));

    /// <summary>A name the table does not list, in either place, is unknown — and so is null.</summary>
    [Theory]
    [InlineData("Paragraph", "P")]
    [InlineData("Document", "Paragraph")]
    [InlineData("document", "P")]
    [InlineData(null, "P")]
    [InlineData("Document", null)]
    public void AnUnlistedName_IsUnknown(string parent, string child)
        => Assert.Equal(StructureContainmentRule.Unknown, PdfStructureNamespaceInclusion.CanContain(parent, child));

    /// <summary>The four rules that permit the child outright are allowed; every other is not.</summary>
    [Theory]
    [InlineData(StructureContainmentRule.Optional, true)]
    [InlineData(StructureContainmentRule.Required, true)]
    [InlineData(StructureContainmentRule.OptionalAtMostOnce, true)]
    [InlineData(StructureContainmentRule.RequiredExactlyOnce, true)]
    [InlineData(StructureContainmentRule.Forbidden, false)]
    [InlineData(StructureContainmentRule.ForbiddenUnlessGroupingLevel, false)]
    [InlineData(StructureContainmentRule.SeeElementDescription, false)]
    [InlineData(StructureContainmentRule.Unknown, false)]
    public void WhichRulesAreDefinitelyAllowed(StructureContainmentRule rule, bool allowed)
        => Assert.Equal(allowed, PdfStructureNamespaceInclusion.IsDefinitelyAllowed(rule));
}
