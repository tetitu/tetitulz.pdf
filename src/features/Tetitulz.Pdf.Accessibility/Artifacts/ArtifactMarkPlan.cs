// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Artifacts
{
    /// <summary>
    /// An artifact-marking revision that has been computed but <b>not written</b> — the output of
    /// <see cref="PdfArtifactMarker.Plan"/>, and the sibling of
    /// <c>Tagging.AutoTagPlan</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this shape exists.</b> Every writer in this assembly appends a <i>clear-text</i>
    /// incremental update, which is why they refuse an encrypted document: a clear-text revision
    /// appended to an encrypted file does not match its encryption dictionary and is unreadable.
    /// This assembly cannot decrypt either — it does not reference <c>Tetitulz.Pdf.Encryption</c>,
    /// and it must not — so the operation travels to the decryptor instead of the decryptor
    /// travelling here:</para>
    /// <code>
    /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
    ///     (decrypted, firstFree) =&gt; PdfArtifactMarker.Plan(decrypted, firstFree).Objects);
    /// </code>
    /// <para><b><see cref="Describe"/> takes the answer the caller has and this type does not.</b>
    /// Whether the revision reached the file is known only to whoever performed the write, and the
    /// counts mean opposite things either way: a marking that was planned and then lost must be
    /// reported as <i>zero</i> artifacts on zero changed pages, with those pages counted as skipped,
    /// or the result describes a document the caller does not have. That is the same
    /// <c>VerifiedWrite</c> rule the clear-text path applies to itself — moved to the caller because
    /// here the caller owns the write.</para>
    /// </remarks>
    public sealed class ArtifactMarkPlan
    {
        private readonly ArtifactMarkDetail _applied;
        private readonly ArtifactMarkDetail _discarded;
        private readonly string _markInfoWarning;

        internal ArtifactMarkPlan(
            IReadOnlyList<IndirectObject> objects,
            ArtifactMarkDetail applied,
            ArtifactMarkDetail discarded,
            string markInfoWarning)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            _applied = applied;
            _discarded = discarded;
            _markInfoWarning = markInfoWarning;
        }

        /// <summary>
        /// The objects to append, ordered by object number. Existing objects carry the generation
        /// the document already records — writing a different one makes a strict reader treat the
        /// whole appended section as stale — and newly allocated ones carry generation 0.
        ///
        /// <para>Empty when nothing could be marked, in which case the caller should append
        /// nothing.</para>
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>
        /// What to report, given whether the revision actually reached the output.
        /// </summary>
        /// <param name="revisionWasAppended">
        /// <c>true</c> when <see cref="Objects"/> were written and the result is the caller's;
        /// <c>false</c> when the write was lost or skipped, in which case the reported counts are
        /// zeroed and the affected pages are counted as skipped.
        /// </param>
        public ArtifactMarkDetail Describe(bool revisionWasAppended)
        {
            if (!revisionWasAppended) return _discarded;

            // The /MarkInfo caveat describes the document that was just written, so it is added
            // only when the write landed — exactly as the clear-text path holds it back.
            if (_markInfoWarning != null && _applied?.Warnings != null)
            {
                var warnings = new List<string>(_applied.Warnings) { _markInfoWarning };
                return new ArtifactMarkDetail(
                    _applied.ArtifactsMarked, _applied.PagesProcessed, _applied.PagesChanged,
                    _applied.PagesSkipped, _applied.PagesWithNothingToMark, warnings);
            }

            return _applied;
        }
    }
}
