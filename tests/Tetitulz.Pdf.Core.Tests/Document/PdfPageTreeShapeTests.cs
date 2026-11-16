// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// §7.7.3's leaf-page test — the complete truth table of the predicate four page-tree walks now
/// share.
/// </summary>
/// <remarks>
/// <para><b>The rule existed four times and gave two answers.</b> <c>Core.PdfPageTree</c> and
/// <c>Plugins.Maintenance.PageTreeShape</c> required an untyped childless node to be page-<i>shaped</i>
/// — to carry <c>/Contents</c> or its own <c>/MediaBox</c> — before counting it as a page.
/// <c>PdfUaChecker</c> and <c>PdfDeepAccessibilityChecker</c> omitted that clause, so a node with
/// neither counted as a page and their own checks then ran against a non-page. Each of those two
/// carries a comment describing that exact failure for the <c>/Type /Pages</c> case; this is the
/// same failure one case further out.</para>
///
/// <para><b>It is a four-input predicate, so the table is small enough to state completely</b> —
/// which is the point. The clause that drifted historically (excluding a node that positively
/// declares <c>/Type /Pages</c>) and the clause that differed between the two families (the
/// page-shaped evidence) are each pinned by a case that fails without it.</para>
/// </remarks>
public class PdfPageTreeShapeTests
{
    private static readonly PdfArray SomeKids = new PdfArray();
    private static readonly PdfName Unknown = new PdfName("Bogus");

    /// <summary>An explicit <c>/Type /Page</c> wins outright, whatever else the node carries.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnExplicitPageTypeIsAlwaysALeaf(bool hasContents, bool hasMediaBox)
        => Assert.True(PdfPageTreeShape.IsLeafPage(PdfName.Page, null, hasContents, hasMediaBox));

    /// <summary>
    /// <b>The emptied branch.</b> <c>&lt;&lt; /Type /Pages /Count 0 /MediaBox […] &gt;&gt;</c> with
    /// no <c>/Kids</c> is what a page-removal tool leaves behind when it hoists the box. Counting it
    /// as a page shifts every later page index. This is the clause that drifted for two commits.
    /// </summary>
    [Fact]
    public void ANodeDeclaringPagesIsNeverALeafEvenWithNoKids()
        => Assert.False(PdfPageTreeShape.IsLeafPage(PdfName.Pages, null, hasContents: false, hasMediaBox: true));

    /// <summary>
    /// A leaf that forgot <c>/Type /Page</c> is still a leaf when it is page-shaped — §7.7.3.2
    /// requires the key but real producers omit it, and this library reads those files.
    /// </summary>
    [Theory]
    [InlineData(true, false)]    // /Contents alone
    [InlineData(false, true)]    // its own /MediaBox alone
    [InlineData(true, true)]
    public void AnUntypedChildlessNodeIsALeafWhenPageShaped(bool hasContents, bool hasMediaBox)
        => Assert.True(PdfPageTreeShape.IsLeafPage(null, null, hasContents, hasMediaBox));

    /// <summary>
    /// <b>The clause the two conformance checkers lacked.</b> An untyped childless node carrying
    /// neither <c>/Contents</c> nor its own <c>/MediaBox</c> is not page-shaped and is not a page —
    /// counting it as one runs UA checks against a node that is not a page at all.
    /// </summary>
    [Fact]
    public void AnUntypedChildlessNodeWithNoPageEvidenceIsNotALeaf()
        => Assert.False(PdfPageTreeShape.IsLeafPage(null, null, hasContents: false, hasMediaBox: false));

    /// <summary>
    /// A node with <c>/Kids</c> is intermediate however page-shaped it looks — an inherited
    /// <c>/MediaBox</c> on a <c>/Pages</c> node is entirely ordinary, so the structural clause must
    /// not fire while there are children to walk.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ANodeWithKidsIsNeverALeaf(bool hasContents, bool hasMediaBox)
    {
        Assert.False(PdfPageTreeShape.IsLeafPage(null, SomeKids, hasContents, hasMediaBox));
        Assert.False(PdfPageTreeShape.IsLeafPage(Unknown, SomeKids, hasContents, hasMediaBox));
    }

    /// <summary>
    /// An unrecognised <c>/Type</c> behaves as an absent one — some producers write it as a literal
    /// string, which resolves to no <see cref="PdfName"/> at all.
    /// </summary>
    [Fact]
    public void AnUnrecognisedTypeIsTreatedStructurally()
    {
        Assert.True(PdfPageTreeShape.IsLeafPage(Unknown, null, hasContents: true, hasMediaBox: false));
        Assert.False(PdfPageTreeShape.IsLeafPage(Unknown, null, hasContents: false, hasMediaBox: false));
    }
}
