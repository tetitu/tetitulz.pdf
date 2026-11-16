// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// The things page <paramref name="pageIndex"/> draws — its runs of text, pictures, painted
        /// paths, shadings and forms — each with the box it paints in. See
        /// <see cref="PdfPageElements"/> for what an element is.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="password">
        /// The password of an encrypted document, which is then listed from its decrypted copy. The
        /// ids name operations of the page's content, which decryption does not change, so they
        /// are the ids <see cref="RemovePageElements"/> takes for the document once decrypted.
        /// </param>
        public static IReadOnlyList<PdfPageElement> ListPageElements(Stream input, int pageIndex, string password = null)
            => WithDecrypted(input, password, s => PdfPageElements.List(s, pageIndex));

        /// <summary>
        /// Writes <paramref name="input"/> with the elements <paramref name="elementIds"/> name taken
        /// out of page <paramref name="pageIndex"/>, the rest of the page drawn where it was; how many
        /// were taken out. The file is written anew, so a signature does not survive it. See
        /// <see cref="PdfPageElements"/> for how each kind is taken out and what stays in the file.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The ids of the elements to take out, from <see cref="ListPageElements"/> over the same bytes.</param>
        public static int RemovePageElements(Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds)
            => PdfPageElements.Remove(input, output, pageIndex, elementIds);

        /// <summary>
        /// Writes <paramref name="input"/> with the elements <paramref name="elementIds"/> name moved
        /// or scaled on page <paramref name="pageIndex"/> by <paramref name="matrix"/> — six numbers,
        /// <c>a b c d e f</c>, in the page's own coordinates — the rest of the page drawn where it
        /// was; how many were moved. The file is written anew, so a signature does not survive it.
        /// See <see cref="PdfPageElements.Transform"/> for how each kind is moved and what is refused.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The ids of the elements to move, from <see cref="ListPageElements"/> over the same bytes.</param>
        /// <param name="matrix">The matrix, as a <c>cm</c> takes it.</param>
        public static int TransformPageElements(Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds, double[] matrix)
            => PdfPageElements.Transform(input, output, pageIndex, elementIds, matrix);

        /// <summary>
        /// Writes <paramref name="input"/> with the text of the runs <paramref name="elementIds"/>
        /// name on page <paramref name="pageIndex"/> replaced by <paramref name="text"/>, drawn in the
        /// slot the runs occupied — shrunk to fit it when wider — and nothing else on the page moved.
        /// The file is written anew, so a signature does not survive it. See
        /// <see cref="PdfTemplateEngine.ReplaceRun"/> for what is refused and how the result says so.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="elementIds">The ids of the text elements, from <see cref="ListPageElements"/> over the same bytes.</param>
        /// <param name="text">What the runs show instead.</param>
        /// <param name="options">The font the replacement may be drawn in, among the rest; null for the defaults.</param>
        public static TemplateReplaceResult ReplacePageElementText(Stream input, Stream output, int pageIndex, IReadOnlyCollection<int> elementIds, string text,
            TemplateReplaceOptions options = null)
            => PdfTemplateEngine.ReplaceRun(input, output, pageIndex, elementIds, text, options);

        /// <summary>
        /// Writes <paramref name="input"/> with <paramref name="options"/>' text drawn on page
        /// <paramref name="pageIndex"/> — any script the font covers, shaped and laid out in its own
        /// direction, upright as the page is shown, in a font embedded with the glyphs drawn — as one
        /// appended revision. The text is the page's own content afterwards, so
        /// <see cref="ListPageElements"/> lists each line. See <see cref="PdfPageTextWriter"/> for
        /// what is refused.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="options">The text, where it starts, its size, font and colour.</param>
        /// <returns>The box the text was drawn in and the font it was drawn with.</returns>
        public static PageTextResult AddPageText(Stream input, Stream output, int pageIndex, PageTextOptions options)
            => PdfPageTextWriter.AddText(input, output, pageIndex, options);
    }
}
