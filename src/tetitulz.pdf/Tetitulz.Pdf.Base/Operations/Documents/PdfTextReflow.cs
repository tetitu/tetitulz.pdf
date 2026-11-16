// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Find-and-replace that <b>re-breaks the containing paragraph</b> — GAPS.md §19d's reflowing
    /// half.
    /// </summary>
    /// <remarks>
    /// <para><b>This gives up the guarantee <see cref="PdfTemplateEngine.ReplaceText"/> makes, and
    /// that is the whole difference between them.</b> There, a replacement is drawn into the slot
    /// the original occupied and everything else on the page is exactly where it was. Here the
    /// paragraph's lines are re-broken, so words the caller never named move — which is what makes
    /// a long replacement possible at all, and what makes this a separate class rather than an
    /// option. A caller has to choose it knowingly.</para>
    ///
    /// <para><b>What it still guarantees: nothing outside the paragraph moves.</b> A PDF records
    /// where text was drawn, not what it belongs to, so there is no way to push the rest of the page
    /// down — the content below a paragraph is not related to it by anything in the file. A
    /// paragraph whose replacement needs more lines than it had is therefore <b>refused</b> rather
    /// than drawn over what follows, unless
    /// <see cref="TemplateReplaceOptions.AllowReflowPastTheParagraph"/> says the space is free.</para>
    ///
    /// <para><b>What counts as a paragraph is narrow, and every part of it is a refusal you can
    /// read.</b> One content stream, one text object, one font size, consecutive baselines a uniform
    /// distance apart, a shared left margin, no rotation or skew. That is left-aligned prose set as
    /// a block — and a justified block, whose lines but the last share a right edge too, is
    /// re-broken justified, its new lines but the last spread by word spacing. A table cell, a
    /// centred heading, a two-column layout or a rotated stamp is left
    /// alone and named in <see cref="TemplateReplaceResult.NotReflowedKeys"/>, because re-breaking
    /// it would move text in a direction nobody asked about.</para>
    ///
    /// <para><b>Everything else is the template engine's</b> — locating the text, deciding whether
    /// the stream can be edited, erasing it, embedding a font that covers the replacement, and
    /// verifying afterwards that the old text is gone. Only the drawing back differs.</para>
    /// </remarks>
    public static class PdfTextReflow
    {
        /// <summary>
        /// Replaces literal text and re-breaks each affected paragraph, writing a full
        /// single-revision PDF to <paramref name="output"/>.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Receives a full single-revision PDF.</param>
        /// <param name="replacements">Literal text to find, mapped to what replaces it.</param>
        /// <param name="options">
        /// The template engine's options. <c>TokenOpen</c> and <c>TokenClose</c> are unused here and
        /// are not validated; <c>KeepSlot</c>'s shrink-to-fit is deliberately not applied, because
        /// reflow's answer to "too wide" is another line rather than smaller type.
        /// </param>
        /// <returns>
        /// The same result the template engine produces, with
        /// <see cref="TemplateReplaceResult.NotReflowedKeys"/> carrying the keys whose paragraph
        /// could not be re-broken — each with a warning saying which rule it failed.
        /// </returns>
        /// <exception cref="PdfTemplateException">
        /// The document cannot be edited safely — encrypted input, or no resolvable catalog.
        /// </exception>
        public static TemplateReplaceResult Replace(
            Stream input, Stream output,
            IReadOnlyDictionary<string, string> replacements,
            TemplateReplaceOptions options)
            => PdfTemplateEngine.ReplaceReflowing(input, output, replacements, options);
    }
}
