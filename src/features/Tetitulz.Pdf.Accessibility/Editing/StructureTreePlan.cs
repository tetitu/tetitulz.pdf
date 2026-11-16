// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Editing
{
    /// <summary>
    /// A structure-tree edit that has been computed but <b>not written</b> — the output of the
    /// <c>Plan*</c> methods on <see cref="PdfStructureTreeEditor"/>, and the sibling of
    /// <c>Tagging.AutoTagPlan</c>, <c>Artifacts.ArtifactMarkPlan</c>,
    /// <c>ReadingOrder.ReadingOrderPlan</c> and <c>Fonts.FontEmbeddingPlan</c>.
    /// </summary>
    /// <remarks>
    /// <para>Every writer in this assembly appends a <i>clear-text</i> incremental update, which is
    /// why they refuse an encrypted document. This assembly cannot decrypt either — it does not
    /// reference <c>Tetitulz.Pdf.Encryption</c>, and must not — so the operation travels to the
    /// decryptor instead of the decryptor travelling here.</para>
    /// <para><b><see cref="NewInfoObjectNumber"/> is not decoration.</b> When a document has no
    /// <c>/Info</c> dictionary the title plan mints one, and the appended trailer has to carry an
    /// <c>/Info</c> reference to it or the object is written and unreachable — a title nobody can
    /// read. A caller appending this plan must pass the number through to whatever writes the
    /// trailer; it is <c>-1</c> when no <c>/Info</c> was created.</para>
    /// </remarks>
    public sealed class StructureTreePlan
    {
        internal StructureTreePlan(IReadOnlyList<IndirectObject> objects, int newInfoObjectNumber)
            : this(objects, newInfoObjectNumber, (objects ?? Array.Empty<IndirectObject>()).Count)
        {
        }

        internal StructureTreePlan(
            IReadOnlyList<IndirectObject> objects, int newInfoObjectNumber, int itemsChanged)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            NewInfoObjectNumber = newInfoObjectNumber;
            ItemsChanged = itemsChanged;
        }

        internal static StructureTreePlan Nothing { get; } =
            new StructureTreePlan(Array.Empty<IndirectObject>(), -1);

        /// <summary>
        /// The objects to append, ordered by object number. Empty when the edit could not be
        /// staged — in which case the caller should append nothing and treat the operation as not
        /// performed, exactly as the clear-text path returns <c>false</c>.
        /// </summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>
        /// Object number of a newly created <c>/Info</c> dictionary, or <c>-1</c> when none was
        /// created. See the remarks: an appended trailer must reference it.
        /// </summary>
        public int NewInfoObjectNumber { get; }

        /// <summary>
        /// <c>true</c> when this plan would change the document. The clear-text entry points return
        /// exactly this, once their write has landed — <b>a plan alone is never evidence the
        /// document changed</b>, which is why this is not called "Succeeded".
        /// </summary>
        public bool WouldChangeDocument => Objects.Count > 0;

        /// <summary>
        /// What the clear-text entry point would count — headings renumbered, pages given
        /// <c>/Tabs /S</c>, and so on.
        /// </summary>
        /// <remarks>
        /// It defaults to <see cref="Objects"/>.Count because these edits revise exactly one object
        /// per item changed, but it is <b>stated rather than derived</b> at each planning site: the
        /// day an edit revises two objects for one item, a derived count would silently double every
        /// number the pipeline reports, with no test able to see it.
        /// </remarks>
        public int ItemsChanged { get; }

        /// <summary>
        /// The count the clear-text entry point would return, given whether the revision carrying
        /// this plan actually reached the caller's output. Zero when it did not — the pipeline's
        /// standing invariant is that a reported count above zero implies the output differs from
        /// the input.
        /// </summary>
        public int Describe(bool revisionWasAppended) => revisionWasAppended ? ItemsChanged : 0;
    }
}
