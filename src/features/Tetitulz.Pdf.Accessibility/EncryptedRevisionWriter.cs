// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// Appends one <b>encrypted</b> revision to an encrypted document: the caller's bridge to
    /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedIncrementalUpdate.Run</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a delegate rather than a reference.</b> Every accessibility writer
    /// produces a <b>clear-text</b> incremental update, which an encrypted document cannot accept.
    /// This assembly cannot decrypt one either: it does not reference
    /// <c>Tetitulz.Pdf.Encryption</c>, and it must not — that would drag the crypto stack into the
    /// package graph of every consumer, which is the design
    /// GAPS.md §5a records as deliberately rejected. So the dependency
    /// is inverted: the <i>operation</i> travels to the Encryption assembly, exactly as
    /// <c>PdfEncryptedRoundTrip.Run</c> already takes an <c>Action&lt;Stream, Stream&gt;</c>.</para>
    /// <para>Wiring it is one lambda, and the password stays entirely in the caller's hands — this
    /// assembly never sees one and contains no occurrence of the word:</para>
    /// <code>
    /// options.EncryptedRevisionWriter = (input, output, plan) =&gt;
    ///     PdfEncryptedIncrementalUpdate.Run(input, output, password, (doc, firstFree) =&gt;
    ///     {
    ///         PlannedRevision r = plan(doc, firstFree);
    ///         return new EncryptedRevision(r.Objects, r.NewInfoObjectNumber);
    ///     });
    /// </code>
    /// <para>The two revision types are deliberately separate rather than shared: neither assembly
    /// references the other, and the four-line map is what keeps it that way. <c>PlannedRevision</c>
    /// carries a minted <c>/Info</c> number because <c>Objects</c> alone cannot express an object
    /// that only the trailer reaches — a title written into an unreferenced <c>/Info</c> is a title
    /// no reader can find.</para>
    /// <para><b>The plan callback receives two things and both matter.</b> The
    /// <see cref="PdfDocument"/> is a <i>decrypted copy</i> — an encrypted document loads no
    /// catalog, so the page tree and structure tree are simply unreachable without it. The
    /// <c>int</c> is the first object number free in the <b>target</b>, which can be
    /// <i>higher</i> than anything in the decrypted copy: decryption unpacks object streams and
    /// drops the cross-reference stream, so allocating from the copy's own ceiling would overwrite
    /// a live object. <b>Reserve that ceiling before minting anything</b> —
    /// <c>PdfArtifactMarker</c> reserved after minting once, superseded the object the
    /// <c>/Encrypt</c> dictionary needed, and produced a file nothing could open.</para>
    /// </remarks>
    /// <param name="input">The encrypted PDF. Read only.</param>
    /// <param name="output">Receives the original bytes followed by one appended revision.</param>
    /// <param name="plan">
    /// Given the decrypted document and the target's first free object number, returns the
    /// <see cref="PlannedRevision"/> to append. An empty one must leave the original bytes
    /// unchanged.
    /// </param>
    public delegate void EncryptedRevisionWriter(
        Stream input,
        Stream output,
        Func<PdfDocument, int, PlannedRevision> plan);
}
