// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Accessibility;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="StructureTypes.IsStandard"/>: ISO 32000-1 Table 333 (Core's list, which PDF/A-1 narrows
/// to the PDF Reference 1.4's) widened with ISO 32000-2's additions and the open heading levels —
/// PDF 1.5's table and ruby types included, which PDF/A-1's narrower list leaves out.
/// </summary>
public class StandardStructureTypeTests
{
    [Theory]
    [InlineData("P", true)]
    [InlineData("Form", true)]
    [InlineData("THead", true)]          // PDF 1.5: Table 333's, not PDF 1.4's
    [InlineData("TFoot", true)]
    [InlineData("Annot", true)]
    [InlineData("Ruby", true)]
    [InlineData("WP", true)]
    [InlineData("Aside", true)]          // ISO 32000-2's additions
    [InlineData("Title", true)]
    [InlineData("FENote", true)]
    [InlineData("H7", true)]             // an open heading level
    [InlineData("Artifact", false)]
    [InlineData("Custom", false)]
    [InlineData(null, false)]
    public void IsStandard_ReadsTable333AndPdf20sAdditions(string? role, bool standard)
        => Assert.Equal(standard, StructureTypes.IsStandard(role!));
}
