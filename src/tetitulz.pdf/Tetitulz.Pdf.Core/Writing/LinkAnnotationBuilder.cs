// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The §12.5.6.5 link-annotation dictionary that every producer in this tree wraps around an
    /// action: the rectangle, the invisible border, and the annotation itself.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Three copies — two in <see cref="PdfDocumentBuilder"/> (external
    /// links and internal ones are built in separate passes) and one in <c>DocxToPdf</c>'s
    /// <c>PdfPageBuilder</c>. All three assembled the same six entries in the same order around an
    /// action that legitimately differs.</para>
    ///
    /// <para><b>The action stays the caller's, and that is not laziness — the two producers differ
    /// on purpose and must keep differing.</b> Core writes internal destinations as the
    /// <c>/Names /Dests</c> <i>name tree</i> (§7.9.6), whose keys are strings, so its
    /// <c>/GoTo</c> actions set <c>/D</c> to a <see cref="PdfString"/>. <c>DocxToPdf</c> writes the
    /// legacy catalog <c>/Dests</c> <i>dictionary</i> (§12.3.2.3), whose keys are names, so its
    /// <c>/D</c> is a <see cref="PdfName"/>. Both are valid under Table 173 and each matches the
    /// container its own writer emits. <b>Making the two <c>/D</c> types agree would break whichever
    /// one was changed</b>, because the destination lookup would no longer find its key.</para>
    ///
    /// <para><b><c>/Border [0 0 0]</c> is deliberate.</b> Table 164 reads it as
    /// <c>[hRadius vRadius width]</c>, so a zero width is an invisible border — the near-universal
    /// choice for a text hyperlink. It is superseded by <c>/BS</c> but not removed, and every reader
    /// still honours it; if that ever changes, this is the one place to change.</para>
    ///
    /// <para>A static class with no fields, so it adds no type initializer.</para>
    /// </remarks>
    internal static class LinkAnnotationBuilder
    {
        /// <summary>
        /// A link annotation over the rectangle <c>(x0, y0)-(x1, y1)</c> that performs
        /// <paramref name="action"/>, stamped with <paramref name="objectNumber"/> so the caller can
        /// add it to its own object list and reference it from the page's <c>/Annots</c>.
        /// </summary>
        internal static PdfDictionary Build(
            double x0, double y0, double x1, double y1, PdfDictionary action, int objectNumber)
        {
            var rect = new PdfArray();
            rect.Add(new PdfNumber(x0)); rect.Add(new PdfNumber(y0));
            rect.Add(new PdfNumber(x1)); rect.Add(new PdfNumber(y1));

            var border = new PdfArray();
            border.Add(new PdfNumber(0)); border.Add(new PdfNumber(0)); border.Add(new PdfNumber(0));

            var ann = new PdfDictionary();
            ann.Set(PdfName.Type, PdfName.Annot);
            ann.Set(PdfName.Subtype, PdfName.Link);
            ann.Set(PdfName.Rect, rect);
            ann.Set(PdfName.Border, border);
            ann.Set(PdfName.A, action);
            ann.ObjectNumber = objectNumber;
            ann.GenerationNumber = 0;
            return ann;
        }
    }
}
