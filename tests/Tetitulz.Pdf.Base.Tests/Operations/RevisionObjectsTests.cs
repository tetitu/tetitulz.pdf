// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="RevisionObjects"/> — the one place an object-number map becomes the ordered
/// <see cref="IndirectObject"/> list an appended revision writes.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over CLAUDE.md's shared decision points
/// widened the refusal from <c>key &lt;= 0</c> to <c>key &lt; 0</c> — letting object <b>0</b> into a
/// revision — and nothing anywhere reddened: not the Accessibility suite, whose feature holds the
/// type's only callers, and not Base's or Conformance's either. The type had no test of its own at
/// all.</para>
///
/// <para><b>What object 0 costs.</b> §7.5.4 reserves it as the head of the free list, so an object
/// written there is unreachable: the catalog, tag tree or XMP packet a plan meant to add is in the
/// file and addressable by nothing. The refusal is not a tidy-up — it is the difference between a
/// revision that works and one that silently adds dead bytes.</para>
///
/// <para>It cannot happen from the present callers, which mint their numbers from the document's
/// own high-water mark. That is exactly why nothing reached it, and exactly why the guard has to
/// hold for the next caller that computes a number some other way.</para>
/// </remarks>
public sealed class RevisionObjectsTests
{
    private const string Operation = "RevisionObjectsTests.From";

    /// <summary>
    /// A superseded object carries the document's own generation, and a new one carries 0.
    /// </summary>
    /// <remarks>
    /// Not redundant with <c>IncrementalUpdateWriter.KeptGeneration</c>, though the in-tree writer
    /// would correct a 0 itself. The Accessibility feature hands this list, as a plan, to a
    /// caller-supplied <c>RemediationOptions.EncryptedRevisionWriter</c>, which need not be that
    /// writer. The sweep that added the writer's rule found this line surviving against every
    /// in-tree consumer; this is the contract it keeps for the others.
    /// </remarks>
    [Fact]
    public void ASupersededObjectCarriesItsGeneration_ANewOneZero()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .ObjAtGeneration(1, 1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Finish(root: "1 1 R");
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(pdf));

        List<IndirectObject> revision = RevisionObjects.From(
            new Dictionary<int, PdfObject> { [3] = new PdfDictionary() },
            new Dictionary<int, PdfObject> { [1] = new PdfDictionary() },
            PdfObjectIndex.Build(doc), Operation);

        Assert.Equal(new[] { (1, 1), (3, 0) },
            revision.Select(o => (o.ObjectNumber, o.GenerationNumber)).ToArray());
    }

    [Fact]
    public void ObjectZero_IsLeftOutOfTheRevision_AndReported()
    {
        DocumentEditDiagnostics.Clear();

        var created = new Dictionary<int, PdfObject>
        {
            [0] = new PdfDictionary(),
            [5] = new PdfDictionary(),
        };

        List<IndirectObject> revision = RevisionObjects.From(created, null, null, Operation);

        Assert.Equal(new[] { 5 }, revision.Select(o => o.ObjectNumber));
        Assert.Contains("object number 0", DocumentEditDiagnostics.LastError);
    }

    /// <summary>A negative number is refused for the same reason, and says so too.</summary>
    [Fact]
    public void ANegativeObjectNumber_IsLeftOutOfTheRevision_AndReported()
    {
        DocumentEditDiagnostics.Clear();

        var created = new Dictionary<int, PdfObject>
        {
            [-3] = new PdfDictionary(),
            [7] = new PdfDictionary(),
        };

        List<IndirectObject> revision = RevisionObjects.From(created, null, null, Operation);

        Assert.Equal(new[] { 7 }, revision.Select(o => o.ObjectNumber));
        Assert.Contains("object number -3", DocumentEditDiagnostics.LastError);
    }

    /// <summary>
    /// The acceptance control: ordinary numbers are kept, in ascending order, and nothing is
    /// reported — so the refusals above are about the number and not about a helper that drops
    /// everything.
    /// </summary>
    [Fact]
    public void OrdinaryObjectNumbers_AreKeptInAscendingOrder_AndNothingIsReported()
    {
        DocumentEditDiagnostics.Clear();

        var created = new Dictionary<int, PdfObject> { [9] = new PdfDictionary(), [4] = new PdfDictionary() };
        var modified = new Dictionary<int, PdfObject> { [6] = new PdfDictionary() };

        List<IndirectObject> revision = RevisionObjects.From(created, modified, null, Operation);

        Assert.Equal(new[] { 4, 6, 9 }, revision.Select(o => o.ObjectNumber));
        Assert.Null(DocumentEditDiagnostics.LastError);
    }

    /// <summary>
    /// An entry with no value is refused as well — the other half of the same filter, and the only
    /// other way a caller's map can name something unwritable.
    /// </summary>
    [Fact]
    public void AnEntryWithNoValue_IsLeftOutOfTheRevision_AndReported()
    {
        DocumentEditDiagnostics.Clear();

        var created = new Dictionary<int, PdfObject> { [11] = null, [12] = new PdfDictionary() };

        List<IndirectObject> revision = RevisionObjects.From(created, null, null, Operation);

        Assert.Equal(new[] { 12 }, revision.Select(o => o.ObjectNumber));
        Assert.Contains("object 11", DocumentEditDiagnostics.LastError);
    }
}
