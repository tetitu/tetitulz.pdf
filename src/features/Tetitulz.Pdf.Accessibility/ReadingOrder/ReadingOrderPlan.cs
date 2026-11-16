// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.ReadingOrder
{
    /// <summary>
    /// A reading-order repair that has been computed but <b>not written</b> — the output of
    /// <see cref="PdfReadingOrderAnalyzer.Plan(Tetitulz.Pdf.Core.Document.PdfDocument, int)"/>, and the sibling of <c>Tagging.AutoTagPlan</c>
    /// and <c>Artifacts.ArtifactMarkPlan</c>.
    /// </summary>
    /// <remarks>
    /// <para>Every writer in this assembly appends a <i>clear-text</i> incremental update, which is
    /// why they refuse an encrypted document. This assembly cannot decrypt either — it does not
    /// reference <c>Tetitulz.Pdf.Encryption</c>, and must not — so the operation travels to the
    /// decryptor instead of the decryptor travelling here.</para>
    /// <para><b><see cref="Describe"/> takes the answer the caller has and this type does not.</b>
    /// Whether the revision reached the file is known only to whoever performed the write, and a
    /// permutation that was planned and then lost must be reported as <i>zero</i> moved — or the
    /// result describes a document the caller does not have. That is the <c>VerifiedWrite</c> rule
    /// the clear-text path applies to itself, moved to the caller because here the caller writes.</para>
    /// </remarks>
    public sealed class ReadingOrderPlan
    {
        private readonly int _moved;

        internal ReadingOrderPlan(IReadOnlyList<IndirectObject> objects, int moved)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            _moved = moved;
        }

        /// <summary>A plan with nothing to append — no structure tree, or nothing out of order.</summary>
        internal static ReadingOrderPlan Nothing { get; } =
            new ReadingOrderPlan(Array.Empty<IndirectObject>(), 0);

        /// <summary>
        /// The objects to append, ordered by object number, each a revision of one the document
        /// already has. Empty when there was nothing to repair.
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>What to report, given whether the revision actually reached the output.</summary>
        /// <param name="revisionWasAppended">
        /// <c>true</c> when <see cref="Objects"/> were written; <c>false</c> when the write was lost
        /// or skipped, in which case the reported count is zero.
        /// </param>
        public ReadingOrderFixResult Describe(bool revisionWasAppended)
            => new ReadingOrderFixResult(revisionWasAppended ? _moved : 0);
    }
}
