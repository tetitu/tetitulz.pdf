// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// §12.7.3.1's widget-vs-field question — the one predicate behind <c>PdfFormField.IsTerminal</c>
/// and the Conformance plugin's AcroForm walk.
/// </summary>
/// <remarks>
/// <para><b>The <c>/Kids</c> half had no coverage.</b> A 2026-08-29 liveness sweep replaced the raw
/// presence test with a resolved <c>is PdfArray</c> one and nothing reddened — not in Core, not in
/// Base, not in the Conformance plugin, the three assemblies that reach it.</para>
///
/// <para><b>Why raw is the correct reading, and the mutation is not.</b> The method's own comment
/// says it: "<c>/Kids</c> written as a reference is still <c>/Kids</c>, and a caller with no
/// resolver must not read that as 'no children'." A null resolver is a shape callers can build —
/// <c>PdfFormField</c>'s constructor is public and takes the document as a plain parameter — so
/// resolving first turns an indirect <c>/Kids</c> into a <see cref="PdfReference"/> that fails an
/// <c>is PdfArray</c> test, and a parent node is misclassified as a terminal field. That moves which
/// nodes the walk treats as fields, which moves what UA08 reports and what the accessibility fixer
/// touches.</para>
///
/// <para>Both overloads are exercised, because the resolver is where the two differ and the defect
/// only appears when there is none.</para>
/// </remarks>
public class AcroFieldClassificationTests
{
    private static PdfDictionary Node(PdfName key, PdfObject value)
    {
        var d = new PdfDictionary();
        if (key != null) d.Set(key, value);
        return d;
    }

    /// <summary>
    /// The case the mutation exposed: <c>/Kids</c> written indirectly, asked with <b>no</b> resolver.
    /// </summary>
    [Fact]
    public void AnIndirectKidsIsAFieldNodeEvenWithNoResolver()
    {
        var node = Node(PdfName.Kids, new PdfReference(9, 0));

        Assert.True(AcroFieldClassification.IsFieldNode(node, (System.Func<PdfObject, PdfObject>)null));
        Assert.True(AcroFieldClassification.IsFieldNode(node, (PdfDocument)null));
    }

    /// <summary>A direct <c>/Kids</c> array is a field node too — the ordinary case.</summary>
    [Fact]
    public void ADirectKidsArrayIsAFieldNode()
    {
        var kids = new PdfArray();
        kids.Add(new PdfReference(9, 0));

        Assert.True(AcroFieldClassification.IsFieldNode(Node(PdfName.Kids, kids),
                                                        (System.Func<PdfObject, PdfObject>)null));
    }

    /// <summary>
    /// A node with a non-empty <c>/T</c> is a field even with no <c>/Kids</c> — the other half of
    /// the predicate, and the control that stops the <c>/Kids</c> assertions passing against an
    /// implementation that simply answers true.
    /// </summary>
    [Fact]
    public void ANodeWithANameIsAFieldNode()
    {
        var t = new PdfString("Name");
        Assert.True(AcroFieldClassification.IsFieldNode(Node(PdfName.T, t),
                                                        (System.Func<PdfObject, PdfObject>)null));
    }

    /// <summary>
    /// <b>An empty <c>/T</c> does not make a field.</b> §12.7.3.1 identifies a field by a name, and
    /// a zero-length one names nothing — a widget carrying <c>/T ()</c> is still just a widget.
    /// </summary>
    [Fact]
    public void ANodeWithAnEmptyNameIsNotAFieldNode()
    {
        Assert.False(AcroFieldClassification.IsFieldNode(Node(PdfName.T, new PdfString("")),
                                                         (System.Func<PdfObject, PdfObject>)null));
    }

    /// <summary>
    /// An indirect <c>/T</c> <b>does</b> need the resolver — unlike <c>/Kids</c>, whose presence
    /// alone answers the question. Without one the text cannot be read, so the node is not a field
    /// on this evidence; with one it is. The asymmetry is deliberate and is what the two assertions
    /// below pin.
    /// </summary>
    [Fact]
    public void AnIndirectNameNeedsAResolverToCount()
    {
        var node = Node(PdfName.T, new PdfReference(11, 0));

        Assert.False(AcroFieldClassification.IsFieldNode(node, (System.Func<PdfObject, PdfObject>)null));

        PdfObject Resolve(PdfObject o) =>
            o is PdfReference r && r.ReferenceObjectNumber == 11 ? new PdfString("Name") : o;

        Assert.True(AcroFieldClassification.IsFieldNode(node, Resolve));
    }

    [Fact]
    public void ANodeWithNeitherKidsNorNameIsNotAFieldNode()
    {
        Assert.False(AcroFieldClassification.IsFieldNode(new PdfDictionary(),
                                                         (System.Func<PdfObject, PdfObject>)null));
        Assert.False(AcroFieldClassification.IsFieldNode(null,
                                                         (System.Func<PdfObject, PdfObject>)null));
    }
}
