// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <c>ContentScanGraph.Deref</c> — the bounded reference-follow every scanner in this plugin reads
/// its object map through, and the form <c>PdfDocument.ResolveObject</c> cannot serve because these
/// scanners hold no document.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over CLAUDE.md's shared decision points cut
/// the hop budget from 32 to 1 — so a reference pointing at another reference stopped resolving —
/// and the whole Maintenance suite stayed green. Every fixture in it reaches its objects in a single
/// hop.</para>
///
/// <para><b>A chain of references is ordinary, not exotic.</b> A producer that revises an object by
/// pointing the old number at a new one, a linearizer that indirects a shared resource dictionary,
/// and any file assembled by appending revisions can all produce <c>5 0 R → 6 0 R → the object</c>.
/// Under a one-hop read every one of those resolves to a <see cref="PdfReference"/>, and every
/// <c>as PdfDictionary</c> downstream silently yields <c>null</c>: the optimizer then reports a
/// clean run over resources it never saw. That is the same failure shape as the page walk that
/// pushed object numbers instead of objects, which GAPS.md records for this very assembly.</para>
/// </remarks>
public sealed class ContentScanGraphDerefTests
{
    [Fact]
    public void AChainOfReferences_ResolvesToTheObjectAtItsEnd()
    {
        var target = new PdfDictionary();
        target.Set(PdfName.Type, PdfName.Page);

        var objects = new Dictionary<int, PdfObject>
        {
            [5] = new PdfReference(6, 0),
            [6] = new PdfReference(7, 0),
            [7] = target,
        };

        PdfObject resolved = ContentScanGraph.Deref(new PdfReference(5, 0), objects);

        Assert.Same(target, resolved);
    }

    /// <summary>
    /// The acceptance control: one hop still works, so the test above is about the <i>chain</i>
    /// rather than about a resolver that stopped working altogether.
    /// </summary>
    [Fact]
    public void ASingleReference_StillResolves()
    {
        var target = new PdfDictionary();
        var objects = new Dictionary<int, PdfObject> { [7] = target };

        Assert.Same(target, ContentScanGraph.Deref(new PdfReference(7, 0), objects));
    }

    /// <summary>A direct object is returned as it stands, with no lookup at all.</summary>
    [Fact]
    public void ADirectObject_IsReturnedUnchanged()
    {
        var direct = new PdfDictionary();

        Assert.Same(direct, ContentScanGraph.Deref(direct, new Dictionary<int, PdfObject>()));
    }

    /// <summary>
    /// A reference <b>cycle</b> terminates rather than spinning, which is what the hop budget is
    /// for — a malformed or hostile object map must not hang a scan.
    /// </summary>
    /// <remarks>
    /// The assertion is the guard's own evidence — the call returns — and deliberately not a clock:
    /// under a full-solution run elapsed time measures the machine's backlog, not this loop. What it
    /// returns is left unasserted on purpose: "not the object" is the contract, and pinning the
    /// exact shape would pin the budget's arithmetic instead of the guard.
    /// </remarks>
    [Fact]
    public void AReferenceCycle_TerminatesWithoutResolvingToAnObject()
    {
        var objects = new Dictionary<int, PdfObject>
        {
            [1] = new PdfReference(2, 0),
            [2] = new PdfReference(1, 0),
        };

        PdfObject resolved = ContentScanGraph.Deref(new PdfReference(1, 0), objects);

        Assert.False(resolved is PdfDictionary, "a cycle must not resolve to an object");
    }

    /// <summary>A reference naming nothing in the map answers null rather than throwing.</summary>
    [Fact]
    public void AReferenceToAMissingObject_AnswersNull()
        => Assert.Null(ContentScanGraph.Deref(new PdfReference(99, 0), new Dictionary<int, PdfObject>()));
}
