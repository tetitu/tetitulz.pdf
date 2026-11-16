// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The document-level script tree is read within <see cref="PdfLimits.MaxNameTreeEntries"/>, the
/// limit a host sets, and a cut tree is reported.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class ActionListLimitTests
{
    [Fact]
    public void ADocumentScriptTreeCutAtTheEntryLimitIsReported()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /Names << /JavaScript << /Names ["
            + "(a) << /S /JavaScript /JS (first) >> (b) << /S /JavaScript /JS (second) >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>");

        try
        {
            PdfLimits.MaxNameTreeEntries = 1;

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            PdfAction only = Assert.Single(PdfActionDispatcher.ExtractActions(doc));

            Assert.Equal("first", only.Target);
            Assert.Contains("/Names /JavaScript", PdfActionDispatcher.LastChainDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A field tree deeper than <see cref="PdfLimits.MaxTreeDepth"/> was cut without a word, so a
    /// caller reading <see cref="PdfActionDispatcher.LastChainDiagnostic"/> as "the list is
    /// complete" (the PDF/A and PDF/X validators do) took the cut list as whole. With the limit at 2
    /// the third level's action is not listed, and the cut is reported; at the default it is listed.
    /// </summary>
    [Fact]
    public void AFieldTreeCutAtTheDepthLimitIsReported()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /T (a) /Kids [5 0 R] >>",
            "<< /T (b) /Kids [6 0 R] >>",
            "<< /T (c) /FT /Tx /A << /S /URI /URI (deep) >> >>");

        using (var ms = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(ms))
        {
            Assert.Equal("a.b.c", Assert.Single(PdfActionDispatcher.ExtractActions(doc)).FieldName);
            Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
        }

        try
        {
            PdfLimits.MaxTreeDepth = 2;

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);

            Assert.Empty(PdfActionDispatcher.ExtractActions(doc));
            Assert.Contains("AcroForm field tree", PdfActionDispatcher.LastChainDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The outline's recursion was cut the same silent way.</summary>
    [Fact]
    public void AnOutlineCutAtTheDepthLimitIsReported()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /First 5 0 R /Last 5 0 R >>",
            "<< /Title (1) /Parent 4 0 R /First 6 0 R /Last 6 0 R >>",
            "<< /Title (2) /Parent 5 0 R /First 7 0 R /Last 7 0 R >>",
            "<< /Title (3) /Parent 6 0 R /A << /S /URI /URI (deep) >> >>");

        try
        {
            PdfLimits.MaxTreeDepth = 2;

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);

            Assert.Empty(PdfActionDispatcher.ExtractActions(doc));
            Assert.Contains("outline", PdfActionDispatcher.LastChainDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The control for both: a leaf at the limit loses nothing and is not a cut. An outline item
    /// with no children, and a field with an empty <c>/Kids</c>, recurse once more and find nothing.
    /// </summary>
    [Fact]
    public void ALeafAtTheDepthLimitIsNotACut()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /AcroForm << /Fields [7 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /First 5 0 R /Last 5 0 R >>",
            "<< /Title (1) /Parent 4 0 R /First 6 0 R /Last 6 0 R >>",
            "<< /Title (2) /Parent 5 0 R /A << /S /URI /URI (leaf) >> >>",
            "<< /T (a) /Kids [8 0 R] >>",
            "<< /T (b) /Kids [] /A << /S /URI /URI (field) >> >>");

        try
        {
            PdfLimits.MaxTreeDepth = 2;

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);

            Assert.Equal(2, PdfActionDispatcher.ExtractActions(doc).Count);
            Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: at the default limit both scripts are listed and nothing is reported.</summary>
    [Fact]
    public void AtTheDefaultLimitTheWholeTreeIsListed()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /Names << /JavaScript << /Names ["
            + "(a) << /S /JavaScript /JS (first) >> (b) << /S /JavaScript /JS (second) >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>");

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(2, PdfActionDispatcher.ExtractActions(doc).Count);
        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }
}
