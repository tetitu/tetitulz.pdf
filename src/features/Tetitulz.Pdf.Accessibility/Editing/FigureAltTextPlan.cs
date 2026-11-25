// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Editing
{
    /// <summary>
    /// What <see cref="PdfStructureTreeEditor.PlanFigureAltText"/> or
    /// <see cref="PdfStructureTreeEditor.PlanLinkDescriptions"/> would append, plus the three counts
    /// the clear-text <c>SetFigureAltText</c> or <c>SetLinkDescriptions</c> returns — a link's
    /// <c>/Contents</c> being its alternate description, the outcomes are the same three.
    /// </summary>
    /// <remarks>
    /// <para>This carries counts where <see cref="StructureTreePlan"/> does not, because alt text is
    /// the one structure-tree edit whose outcome is not a single number: a figure can be
    /// <b>updated</b>, deliberately <b>skipped</b> (the provider returned <c>null</c> — normally
    /// because the figure already has an <c>/Alt</c>), or <b>failed</b> (the provider threw, or the
    /// element is inline and has no object number to revise). Collapsing those into one count is
    /// what would let a caller report a document as remediated when a third of its figures were
    /// left without alternate text — which is a PDF/UA-1 failure, not a nuance.</para>
    /// <para><b>These are <i>planned</i> counts, and the naming says so deliberately.</b> A plan is
    /// never evidence that the document changed; only the caller, after the revision has actually
    /// reached its output, may report anything as done. The clear-text path enforces the same rule
    /// through <c>VerifiedWrite</c> and returns zeros when its revision is lost.</para>
    /// </remarks>
    public sealed class FigureAltTextPlan
    {
        internal FigureAltTextPlan(
            IReadOnlyList<IndirectObject> objects, int updated, int skipped, int failed,
            int leftWithoutDescription = 0)
        {
            Objects                = objects ?? Array.Empty<IndirectObject>();
            PlannedUpdates         = updated;
            Skipped                = skipped;
            Failed                 = failed;
            LeftWithoutDescription = leftWithoutDescription;
        }

        /// <summary>
        /// Links the provider declined that have no <c>/Contents</c> of their own — carried into
        /// <see cref="Describe"/>'s result unchanged, a lost revision having added no description.
        /// </summary>
        internal int LeftWithoutDescription { get; }

        internal static FigureAltTextPlan Nothing { get; } =
            new FigureAltTextPlan(Array.Empty<IndirectObject>(), 0, 0, 0);

        /// <summary>
        /// The objects to append, ordered by object number. Empty when nothing could be staged, in
        /// which case the caller should append nothing and treat the operation as not performed.
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>
        /// Figures this plan would give an <c>/Alt</c> to. <b>Not</b> a count of figures that have
        /// one — see the remarks on the type.
        /// </summary>
        public int PlannedUpdates { get; }

        /// <summary>Figures the provider declined, by returning <c>null</c>.</summary>
        public int Skipped { get; }

        /// <summary>
        /// Figures that could not be given alt text: the provider threw, or the element is a direct
        /// (inline) structure element with no object number, which an incremental update cannot
        /// revise.
        /// </summary>
        public int Failed { get; }

        /// <summary><c>true</c> when this plan would change the document.</summary>
        public bool WouldChangeDocument => Objects.Count > 0;

        /// <summary>
        /// The result the clear-text <c>SetFigureAltText</c> would have returned, given whether the
        /// revision carrying this plan actually reached the caller's output.
        /// </summary>
        /// <remarks>
        /// This is what lets the remediation pipeline report an encrypted run in exactly the same
        /// terms as a clear-text one — the two routes share a single <c>describe</c>, so they cannot
        /// drift on what a step claims. <b>A lost revision reports zero updates</b>, and the figures
        /// that would have been described are counted as failures rather than vanishing: they have
        /// no alternate text in the produced document, which is a failure, not a skip.
        /// </remarks>
        public SetAltTextResult Describe(bool revisionWasAppended)
            => revisionWasAppended
                ? new SetAltTextResult(PlannedUpdates, Skipped, Failed, LeftWithoutDescription)
                : new SetAltTextResult(0, Skipped, Failed + PlannedUpdates, LeftWithoutDescription);
    }
}
