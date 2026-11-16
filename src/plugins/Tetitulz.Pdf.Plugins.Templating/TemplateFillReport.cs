// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Templating
{
    /// <summary>
    /// What <see cref="PdfTemplateFiller.Fill"/> actually did — as opposed to what it was asked to do.
    ///
    /// <para><b>Invariant:</b> every key the caller supplied appears in exactly one of <b>four</b>
    /// terminal buckets — <see cref="FilledFields"/>, <see cref="FilledTokens"/>,
    /// <see cref="NotEditableKeys"/> or <see cref="UnmatchedKeys"/>. A key cannot go missing from
    /// the report.</para>
    ///
    /// <para><b>All four count.</b> A key whose token sits in a shared Form XObject is filed under
    /// <see cref="NotEditableKeys"/> alone, so the reconciliation is
    /// <c>values.Count == FilledFields.Count + FilledTokens.Count + NotEditableKeys.Count + UnmatchedKeys.Count</c>,
    /// and a caller detecting "a placeholder is still printed on the page" reads
    /// <see cref="NotEditableKeys"/> as well as <see cref="UnmatchedKeys"/>.</para>
    ///
    /// <para><see cref="NotDrawn"/> and <see cref="UnverifiedKeys"/> are <b>overlays</b> on the four
    /// buckets, not buckets of their own: a key on either is also on one of the four, so counting
    /// them into a reconciliation double-counts. <see cref="Warnings"/> is prose, keyed to nothing.</para>
    /// </summary>
    public sealed class TemplateFillReport
    {
        internal readonly List<string> FilledFieldList = new List<string>();
        internal readonly List<string> FilledTokenList = new List<string>();
        internal readonly List<string> UnmatchedList = new List<string>();
        internal readonly List<string> NotDrawnList = new List<string>();
        internal readonly List<string> NotEditableList = new List<string>();
        internal readonly List<string> UnverifiedList = new List<string>();
        internal readonly List<string> WarningList = new List<string>();

        /// <summary>Names of AcroForm fields whose value was written.</summary>
        public IReadOnlyList<string> FilledFields => FilledFieldList;

        /// <summary>Page tokens that were located and replaced.</summary>
        public IReadOnlyList<string> FilledTokens => FilledTokenList;

        /// <summary>
        /// Keys the caller supplied that matched <b>nothing</b> — no form field and no page token.
        ///
        /// <para><b>Check this.</b> Silence here is how a contract ships with
        /// <c>{{signature_date}}</c> still printed on it: the fill "succeeded", the document looks
        /// finished, and the placeholder is sitting in the output. There is no configuration under
        /// which an unmatched key is not worth knowing about, which is why it is a first-class part
        /// of the report rather than a warning string.</para>
        /// </summary>
        public IReadOnlyList<string> UnmatchedKeys => UnmatchedList;

        /// <summary>
        /// Placeholders whose value was written into the document but which could not be
        /// <i>drawn</i> — typically no font covering the text. Each has a matching entry in
        /// <see cref="Warnings"/>. For a form field this means the value is present and a viewer
        /// will render it (see <see cref="NeedAppearances"/>); for a page token it means the token
        /// was left alone.
        /// </summary>
        public IReadOnlyList<string> NotDrawn => NotDrawnList;

        /// <summary>
        /// Placeholders that <b>were located</b> but sit somewhere that cannot be edited safely —
        /// a Form XObject drawn more than once, where changing it would change every other place it
        /// appears. The document is left untouched for these.
        ///
        /// <para>Kept out of <see cref="UnmatchedKeys"/> on purpose: "unmatched" tells a caller to
        /// check the key for a typo, while this tells them the key is right and the <i>template</i>
        /// needs restructuring. They lead to opposite investigations.</para>
        /// </summary>
        public IReadOnlyList<string> NotEditableKeys => NotEditableList;

        /// <summary>
        /// Placeholders reported as replaced whose token is nonetheless <b>still extractable</b>
        /// from the produced document, so the erasure missed an occurrence.
        /// </summary>
        /// <remarks>
        /// <b>Empty is not a guarantee.</b> The check can only falsify: text extraction is lossy, so
        /// a token surviving in a font that reads back as something else stays invisible to it. A
        /// non-empty list is proof of a miss; an empty one is the absence of that proof rather than
        /// its converse. The common cause is a token that also appears in a <i>shared</i> Form
        /// XObject — see <see cref="NotEditableKeys"/>, which the same key will usually be on.
        /// </remarks>
        public IReadOnlyList<string> UnverifiedKeys => UnverifiedList;

        /// <summary>One entry per tolerated failure, naming the placeholder and the reason.</summary>
        public IReadOnlyList<string> Warnings => WarningList;

        /// <summary>
        /// The value written to the AcroForm's <c>/NeedAppearances</c>. True when at least one
        /// field's appearance is missing, so viewers are asked to draw the values themselves.
        /// </summary>
        public bool NeedAppearances { get; internal set; }

        /// <summary>
        /// True when the output differs from the input — i.e. something was actually filled.
        /// </summary>
        public bool ChangedAnything => FilledFieldList.Count > 0 || FilledTokenList.Count > 0;

        internal void Warn(string placeholder, string message)
            => WarningList.Add(placeholder == null ? message : placeholder + ": " + message);
    }
}
