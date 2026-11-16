// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Tagging
{
    /// <summary>
    /// A tagging revision that has been computed but <b>not written</b> — the output of
    /// <see cref="PdfAutoTagger.Plan"/>.
    ///
    /// <para>
    /// <b>Why this shape exists.</b> Every writer in this assembly appends a <i>clear-text</i>
    /// incremental update, which is why they all refuse an encrypted document: a clear-text
    /// revision appended to an encrypted file does not match its encryption dictionary and is
    /// unreadable. This assembly cannot decrypt either — it does not reference
    /// <c>Tetitulz.Pdf.Encryption</c>, and it must not (that edge would drag BouncyCastle into
    /// the package graph of every accessibility consumer; see CLAUDE.md → Architecture
    /// Principles). So the operation travels to the decryptor instead of the decryptor
    /// travelling here: this type is a plain description of what to write, which
    /// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c> encrypts with the document's own file
    /// key and appends to the original bytes. It is the same inversion
    /// <c>PdfEncryptedRoundTrip</c> uses, and the same plan/write split
    /// <c>PdfFormFiller.Plan</c> and <c>PdfImageReplacer.Plan</c> already ship.
    /// </para>
    ///
    /// <para>
    /// <b>Why this is not just "use the round trip".</b>
    /// <c>PdfEncryptedRoundTrip</c> rewrites every byte, so it can preserve neither an existing
    /// signature nor the owner password (<c>/O</c> and <c>/U</c> derive one-way from both
    /// passwords, and a caller holds only the one that opened the file). An appended revision
    /// leaves the original bytes untouched, so both survive. Tagging only adds and replaces
    /// objects, so it is exactly the case the incremental path exists for.
    /// </para>
    /// </summary>
    public sealed class AutoTagPlan
    {
        private readonly AutoTagResult _applied;
        private readonly AutoTagResult _discarded;

        internal AutoTagPlan(
            IReadOnlyList<IndirectObject> objects,
            int newInfoObjectNumber,
            AutoTagResult applied,
            AutoTagResult discarded)
        {
            Objects             = objects ?? Array.Empty<IndirectObject>();
            NewInfoObjectNumber = newInfoObjectNumber;
            _applied            = applied;
            _discarded          = discarded;
        }

        /// <summary>
        /// The objects to append, ordered by object number. Existing objects carry the generation
        /// the document already records — writing a different one makes a strict reader treat the
        /// whole appended section as stale — and newly allocated ones carry generation 0.
        ///
        /// <para>Empty when the document could not be tagged at all, in which case the caller
        /// should append nothing; <see cref="Describe"/> explains why.</para>
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>
        /// Object number of a freshly built <c>/Info</c> dictionary, or <c>-1</c> when none was
        /// needed. Only non-negative when <c>AutoTagOptions.Title</c> was set on a document whose
        /// trailer has no <c>/Info</c>.
        ///
        /// <para><b>The appended trailer must be pointed at it</b> (Core's
        /// <c>IncrementalUpdateOptions.NewInfoObjectNumber</c>); a writer that cannot do that
        /// leaves the object unreferenced and the title unset. That is reported as a warning on
        /// <see cref="Describe"/> rather than left to be discovered in the output, because an
        /// unreferenced <c>/Info</c> is indistinguishable from a title that was never
        /// asked for.</para>
        /// </summary>
        public int NewInfoObjectNumber { get; }

        /// <summary>
        /// What this plan amounts to, given whether the caller's writer actually appended it.
        ///
        /// <para>
        /// <b>The parameter is mandatory on purpose.</b> Every writer in this assembly consults
        /// <c>Base.Operations.VerifiedWrite</c> before reporting, because a result assembled
        /// ahead of the write describes a document that may never have been produced — "17
        /// elements, ContentMarked = true" about a file that is byte-for-byte the input. Handing
        /// the plan to a caller moves that check outside this assembly, so the honest reporting
        /// invariant is preserved by making the caller state the answer: pass <c>false</c> and
        /// the counts are zero and the reason is in
        /// <see cref="AutoTagResult.Warnings"/>.
        /// </para>
        /// </summary>
        /// <param name="revisionWritten">
        /// <c>true</c> only when the objects in <see cref="Objects"/> really reached the output
        /// stream.
        /// </param>
        public AutoTagResult Describe(bool revisionWritten) => revisionWritten ? _applied : _discarded;
    }
}
