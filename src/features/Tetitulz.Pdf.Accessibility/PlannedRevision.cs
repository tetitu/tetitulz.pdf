// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// What a remediation step wants appended: the objects, and optionally a newly minted
    /// <c>/Info</c> the appended <b>trailer</b> must point at.
    /// </summary>
    /// <remarks>
    /// <para>This mirrors <c>Encryption.EncryptedRevision</c> without referencing it — the two
    /// assemblies are deliberately unconnected, and the caller's lambda maps one to the other. It
    /// exists because <c>Objects</c> alone cannot express every revision: an object that only the
    /// trailer reaches is unreachable no matter how correctly it is written, and <c>/Info</c> is
    /// exactly that object. A title written into an unreferenced <c>/Info</c> is a title no reader
    /// can find — UA04 failing silently.</para>
    /// <para><b>Only <c>/Info</c>, deliberately.</b> <c>/Root</c> would repoint the catalog, which
    /// supersedes a document rather than revising it; <c>/Encrypt</c> is the dictionary the write is
    /// encrypting against; <c>/ID</c> is derived. Widening this would offer three ways to produce an
    /// unopenable file for one legitimate use.</para>
    /// </remarks>
    public sealed class PlannedRevision
    {
        /// <param name="objects">Objects to append. Null is treated as none.</param>
        /// <param name="newInfoObjectNumber">
        /// Object number of a newly minted <c>/Info</c> the appended trailer must reference, or
        /// <c>-1</c> when the revision mints none. Pass it only for an object that is actually in
        /// <paramref name="objects"/>, or the result is a dangling <c>/Info</c>.
        /// </param>
        public PlannedRevision(IReadOnlyList<IndirectObject> objects, int newInfoObjectNumber = -1)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            NewInfoObjectNumber = newInfoObjectNumber;
        }

        /// <summary>An empty revision: nothing to append, nothing to point at.</summary>
        public static PlannedRevision Nothing { get; } =
            new PlannedRevision(Array.Empty<IndirectObject>());

        /// <summary>The objects to append.</summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>The minted <c>/Info</c> object number, or <c>-1</c>.</summary>
        public int NewInfoObjectNumber { get; }
    }
}
