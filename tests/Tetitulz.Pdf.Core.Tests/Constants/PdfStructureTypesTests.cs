// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Constants;

/// <summary>
/// <see cref="PdfStructureTypes.IsStandardInIso32000Part1"/> (ISO 32000-1 Table 333) and
/// <see cref="PdfStructureTypes.IsStandardInPdf14"/> (the PDF Reference 1.4's §9.7.4): both read in one
/// test per name, so the two vocabularies are pinned against each other.
/// </summary>
public class PdfStructureTypesTests
{
    [Theory]
    // In both: PDF 1.4's set.
    [InlineData("Document", true, true)]
    [InlineData("Part", true, true)]
    [InlineData("Art", true, true)]
    [InlineData("Sect", true, true)]
    [InlineData("Div", true, true)]
    [InlineData("BlockQuote", true, true)]
    [InlineData("Caption", true, true)]
    [InlineData("TOC", true, true)]
    [InlineData("TOCI", true, true)]
    [InlineData("Index", true, true)]
    [InlineData("NonStruct", true, true)]
    [InlineData("Private", true, true)]
    [InlineData("P", true, true)]
    [InlineData("H", true, true)]
    [InlineData("H1", true, true)]
    [InlineData("H6", true, true)]
    [InlineData("L", true, true)]
    [InlineData("LI", true, true)]
    [InlineData("Lbl", true, true)]
    [InlineData("LBody", true, true)]
    [InlineData("Table", true, true)]
    [InlineData("TR", true, true)]
    [InlineData("TH", true, true)]
    [InlineData("TD", true, true)]
    [InlineData("Span", true, true)]
    [InlineData("Quote", true, true)]
    [InlineData("Note", true, true)]
    [InlineData("Reference", true, true)]
    [InlineData("BibEntry", true, true)]
    [InlineData("Code", true, true)]
    [InlineData("Link", true, true)]
    [InlineData("Figure", true, true)]
    [InlineData("Formula", true, true)]
    [InlineData("Form", true, true)]
    // PDF 1.5's additions: Table 333's, not PDF 1.4's.
    [InlineData("THead", true, false)]
    [InlineData("TBody", true, false)]
    [InlineData("TFoot", true, false)]
    [InlineData("Annot", true, false)]
    [InlineData("Ruby", true, false)]
    [InlineData("RB", true, false)]
    [InlineData("RT", true, false)]
    [InlineData("RP", true, false)]
    [InlineData("Warichu", true, false)]
    [InlineData("WT", true, false)]
    [InlineData("WP", true, false)]
    // Neither: PDF 2.0's additions, a heading past H6, Artifact, case, a producer's own.
    [InlineData("Aside", false, false)]
    [InlineData("Title", false, false)]
    [InlineData("Em", false, false)]
    [InlineData("H7", false, false)]
    [InlineData("Artifact", false, false)]
    [InlineData("p", false, false)]
    [InlineData("Heading1", false, false)]
    [InlineData(null, false, false)]
    public void EachName_IsStandardInTheVocabulariesThatListIt(string? role, bool iso32000Part1, bool pdf14)
    {
        Assert.Equal(iso32000Part1, PdfStructureTypes.IsStandardInIso32000Part1(role!));
        Assert.Equal(pdf14, PdfStructureTypes.IsStandardInPdf14(role!));
    }
}
