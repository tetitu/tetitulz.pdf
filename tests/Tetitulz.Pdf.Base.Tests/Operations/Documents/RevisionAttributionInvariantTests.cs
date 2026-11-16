// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// <see cref="PdfRevisionAttribution"/> cannot describe an examination that did not happen.
/// </summary>
/// <remarks>
/// <para>Two properties say whether the signatures were read, and they must not disagree.
/// <see cref="PdfRevisionAttribution.SignaturesExamined"/> was already derived from
/// <see cref="PdfRevisionAttribution.NotExaminedReason"/> for that reason;
/// <see cref="PdfRevisionAttribution.SignaturesCoveringNoRevision"/> was not, and was kept empty
/// instead by a <c>Clear()</c> on the failure path inside <c>TryAttribute</c>. Deleting that call
/// left the whole suite green — the one fixture that reaches the failure path throws before any
/// name is collected, so no test could tell a remembered guarantee from an enforced one.</para>
///
/// <para>These assert the type's contract at the constructor, which is where the guarantee now
/// lives. Going through <c>ListWithSignatures</c> instead would need a document that names a
/// signature and <i>then</i> throws mid-walk — a fixture that pins the fixture rather than the
/// rule, and one that any change to where the walk fails would quietly stop exercising.</para>
/// </remarks>
public class RevisionAttributionInvariantTests
{
    [Fact]
    public void NamesCollectedBeforeAFailureAreNotReportedAsFindings()
    {
        var attribution = new PdfRevisionAttribution(
            Array.Empty<PdfRevisionInfo>(),
            notExaminedReason: "the object graph could not be read: boom",
            signaturesCoveringNoRevision: new[] { "Parent.Sig1" });

        Assert.False(attribution.SignaturesExamined);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    /// <summary>
    /// The acceptance control. Where the walk succeeded the findings are passed through untouched —
    /// without this, the refusal above would also pass on a type that had simply stopped reporting
    /// unattributed signatures at all.
    /// </summary>
    [Fact]
    public void FindingsFromASuccessfulWalkArePassedThrough()
    {
        var attribution = new PdfRevisionAttribution(
            Array.Empty<PdfRevisionInfo>(),
            notExaminedReason: null,
            signaturesCoveringNoRevision: new[] { "Parent.Sig1" });

        Assert.True(attribution.SignaturesExamined);
        Assert.Equal(new[] { "Parent.Sig1" }, attribution.SignaturesCoveringNoRevision);
    }

    /// <summary>
    /// The revision list is a byte-level read and owes nothing to the object graph, so it survives
    /// the failure — only the attribution half is dropped.
    /// </summary>
    [Fact]
    public void TheRevisionListSurvivesAFailedWalk()
    {
        IReadOnlyList<PdfRevisionInfo> revisions = PdfRevisions.List(
            new System.IO.MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf()));

        var attribution = new PdfRevisionAttribution(
            revisions, "the document is encrypted", Array.Empty<string>());

        Assert.Equal(revisions.Count, attribution.Revisions.Count);
        Assert.False(attribution.SignaturesExamined);
    }
}
