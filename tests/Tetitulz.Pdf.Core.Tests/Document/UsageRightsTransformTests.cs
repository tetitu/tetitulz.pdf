// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The UR transform parameters (ISO 32000-2 Table 258) in both directions — the one table the
/// signer writes names from and the usage-rights reader reads them back with.
/// </summary>
public class UsageRightsTransformTests
{
    /// <summary>
    /// Every combination of rights survives the trip through the written names and back. The
    /// writer and the reader were two tables; a right renamed on one side would have written a
    /// grant the other could not read.
    /// </summary>
    [Fact]
    public void EveryCombinationOfRightsReadsBackAsWritten()
    {
        const int all = (int)(UsageRights.FormFillIn | UsageRights.FormImportExport | UsageRights.FormSave
                              | UsageRights.Annotation | UsageRights.DocumentSave);

        for (int bits = 0; bits <= all; bits++)
        {
            var rights = (UsageRights)bits;
            PdfDictionary tp = Parse("<<" + UsageRightsTransform.Entries(rights) + " >>");

            Assert.Equal(rights, UsageRightsTransform.Read(tp, o => o));
        }
    }

    /// <summary>
    /// Pinned, because the round trip alone cannot see a change to both halves: the names are
    /// Table 258's, and every form right shares one /Form array (§7.3.7 — a key written twice keeps
    /// one grant and loses the other).
    /// </summary>
    [Fact]
    public void TheWrittenNamesAreTable258s()
    {
        Assert.Equal(
            " /Form [/FillIn /Import /Export /SubmitStandalone]" +
            " /Annots [/Create /Modify /Copy /Import /Export]" +
            " /Document [/FullSave]",
            UsageRightsTransform.Entries(UsageRights.FormFillIn | UsageRights.FormImportExport
                | UsageRights.FormSave | UsageRights.Annotation | UsageRights.DocumentSave));
        Assert.Equal("", UsageRightsTransform.Entries(UsageRights.None));
    }

    /// <summary>Either export or import alone grants the form-data exchange right.</summary>
    [Theory]
    [InlineData("/Import")]
    [InlineData("/Export")]
    public void EitherHalfOfFormDataExchangeGrantsIt(string name)
    {
        PdfDictionary tp = Parse($"<< /Form [{name}] >>");

        Assert.Equal(UsageRights.FormImportExport, UsageRightsTransform.Read(tp, o => o));
    }

    /// <summary>A /Document array without /FullSave grants nothing; a name elsewhere is not the right.</summary>
    [Fact]
    public void FullSaveCountsOnlyInTheDocumentArray()
    {
        Assert.Equal(UsageRights.None, UsageRightsTransform.Read(Parse("<< /Document [/Other] >>"), o => o));
        Assert.Equal(UsageRights.None, UsageRightsTransform.Read(Parse("<< /Form [/FullSave] >>"), o => o));
    }

    [Theory]
    [InlineData("UR", true)]
    [InlineData("UR3", true)]
    [InlineData("DocMDP", false)]
    [InlineData("FieldMDP", false)]
    public void TheUsageRightsMethodIsUrOrUr3(string method, bool expected)
    {
        Assert.Equal(expected, UsageRightsTransform.IsUsageRightsMethod(new PdfName(method)));
    }

    [Fact]
    public void NoMethodIsNotTheUsageRightsMethod()
    {
        Assert.False(UsageRightsTransform.IsUsageRightsMethod(null));
    }

    private static PdfDictionary Parse(string text)
    {
        var parser = new PdfParser(new MemoryStream(Encoding.ASCII.GetBytes(text)));
        return Assert.IsType<PdfDictionary>(parser.ParseDirectObject());
    }
}
