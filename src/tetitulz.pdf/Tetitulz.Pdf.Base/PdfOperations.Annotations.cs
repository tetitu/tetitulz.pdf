// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Adds a sticky-note annotation to the specified page.</summary>
        public static void AddTextNote(Stream input, Stream output,
            int pageIndex, double x, double y, string text, string author = null)
            => PdfAnnotationWriter.AddTextNote(input, output, pageIndex, x, y, text, author);

        /// <summary>Adds a highlight annotation.</summary>
        public static void AddHighlight(Stream input, Stream output,
            int pageIndex, double x1, double y1, double x2, double y2,
            string contents = null)
            => PdfAnnotationWriter.AddHighlight(input, output, pageIndex, x1, y1, x2, y2, contents);

        /// <summary>
        /// Removes every annotation of the named subtypes — e.g. <c>"Highlight"</c>, <c>"Text"</c>.
        /// </summary>
        /// <param name="input">The PDF to edit. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="subtypes">Subtype names, without the leading slash.</param>
        /// <remarks>
        /// <b>This is not redaction.</b> The result is an incremental update, so the annotation is
        /// still in the previous revision's bytes and <see cref="GetRevisions(Stream)"/> hands it
        /// back. Use <c>Redact</c> when the content has to be destroyed.
        /// </remarks>
        public static PdfAnnotationEditor.AnnotationEditResult RemoveAnnotations(
            Stream input, Stream output, params string[] subtypes)
            => PdfAnnotationEditor.RemoveBySubtype(input, output, subtypes);

        /// <summary>
        /// Draws every non-widget annotation's appearance into its page and drops the annotation,
        /// so the marks survive in a viewer that ignores annotations and can no longer be edited.
        /// </summary>
        /// <param name="input">The PDF to flatten. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <remarks>
        /// Form widgets are left to <see cref="FlattenForm(Stream, Stream)"/>, which also has to
        /// remove the <c>/AcroForm</c>; doing half of that here would leave a form whose fields
        /// have no widgets.
        /// </remarks>
        public static PdfAnnotationEditor.AnnotationEditResult FlattenAnnotations(
            Stream input, Stream output)
            => PdfAnnotationEditor.Flatten(input, output);

        /// <summary>Adds a URI link annotation.</summary>
        public static void AddLink(Stream input, Stream output,
            int pageIndex, double x1, double y1, double x2, double y2, string uri)
            => PdfAnnotationWriter.AddUriLink(input, output, pageIndex, x1, y1, x2, y2, uri);

        /// <summary>
        /// Extracts every clickable /Link annotation with its hit rectangle (PDF
        /// user space, points, origin bottom-left) and resolved navigation target
        /// (external URI or in-document page). The read-side counterpart to
        /// <see cref="AddLink(Stream, Stream, int, double, double, double, double, string)"/>.
        /// </summary>
        public static IReadOnlyList<PdfLink> ExtractLinks(Stream input, string password = null)
            => WithDecrypted(input, password, PdfLinkExtractor.ExtractLinks);

        /// <summary>
        /// Extracts every clickable /Link annotation from the PDF at <paramref name="inputPath"/>.
        /// </summary>
        public static IReadOnlyList<PdfLink> ExtractLinks(string inputPath)
            => PdfLinkExtractor.ExtractLinks(inputPath);

        /// <summary>
        /// Lists <b>every</b> annotation in the PDF — subtype, page, normalised rectangle, note
        /// text, author, date, <c>/F</c> flags and whether the file supplies an appearance.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="password">User or owner password, or null.</param>
        /// <remarks>
        /// <para>The read-side counterpart to <see cref="RemoveAnnotations"/>: it answers "what is in
        /// this file, and what would I be removing?" without the caller re-implementing the page
        /// walk. <see cref="PdfAnnotationExtractor.LastError"/> says when the listing was truncated —
        /// an inventory that silently omits entries is worse than one that refuses, because a caller
        /// then decides what to delete from a list missing the things it does not know about.</para>
        /// </remarks>
        public static IReadOnlyList<PdfAnnotationInfo> ListAnnotations(Stream input, string password = null)
            => WithDecrypted(input, password, PdfAnnotationExtractor.ListAnnotations);

        /// <summary>Lists every annotation in the PDF at <paramref name="inputPath"/>.</summary>
        /// <param name="inputPath">Path to the PDF.</param>
        public static IReadOnlyList<PdfAnnotationInfo> ListAnnotations(string inputPath)
            => PdfAnnotationExtractor.ListAnnotations(inputPath);

        /// <summary>
        /// Finds URLs written as plain text, <b>without changing the document</b>.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="options">Detection options, or null for the defaults.</param>
        /// <param name="password">User or owner password, or null.</param>
        /// <remarks>
        /// <para>The review step for <see cref="AutoLinkUrls"/>: adding annotations is not
        /// selectively reversible, and a URL detector's false positives are the whole risk.</para>
        /// <para>The password overload exists because <c>FacadePasswordCoverageTests</c> requires one
        /// on every read-only façade entry point: without it an encrypted document extracts no text
        /// and this answers "no URLs", which is indistinguishable from a document that has none.</para>
        /// </remarks>
        public static IReadOnlyList<AutoLinkCandidate> FindLinkableUrls(
            Stream input, AutoLinkOptions options = null, string password = null)
            => WithDecrypted(input, password, s => PdfAutoLinker.FindLinks(s, options));

        /// <summary>
        /// Writes a <c>/Link</c> annotation over every URL the page text spells out, as one
        /// incremental revision.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="options">Detection options, or null for the defaults.</param>
        /// <returns>The links that were written.</returns>
        /// <remarks>
        /// A URL broken across a line wrap is <b>skipped</b>, not linked with a rectangle spanning
        /// both lines — see <see cref="PdfAutoLinker"/> for that and the other extraction limit.
        /// </remarks>
        public static IReadOnlyList<AutoLinkCandidate> AutoLinkUrls(
            Stream input, Stream output, AutoLinkOptions options = null)
            => PdfAutoLinker.AddLinks(input, output, options);
    }
}
