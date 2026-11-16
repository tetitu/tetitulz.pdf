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
        /// <summary>
        /// Extracts text from all pages with per-character, word, and line bounding boxes
        /// in PDF page coordinates (origin bottom-left, units = PDF points).
        /// </summary>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(string inputPath)
            => Operations.Extraction.PdfStructuredTextExtractor.Extract(inputPath);

        /// <summary>
        /// Extracts text from all pages of an <b>encrypted</b> file, with per-character, word and
        /// line bounding boxes.
        /// </summary>
        /// <param name="password">User or owner password. Null reads an unencrypted document.</param>
        /// <param name="inputPath">Path to the PDF file.</param>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(
            string inputPath, string password)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExtractStructuredText(file, password);
        }

        /// <summary>
        /// Extracts text from a PDF stream with per-character, word, and line bounding boxes.
        /// </summary>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(Stream input)
            => Operations.Extraction.PdfStructuredTextExtractor.Extract(input);

        /// <summary>
        /// Extracts text from an <b>encrypted</b> PDF stream with per-character, word, and line
        /// bounding boxes.
        /// </summary>
        /// <param name="input">The PDF. Read only.</param>
        /// <param name="password">User or owner password. Null reads an unencrypted document.</param>
        /// <remarks>
        /// <para>A separate overload rather than a <c>password = null</c> appended to the one above:
        /// C# bakes optional-argument defaults in at the <i>call site</i>, so widening the shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer even though it still
        /// compiles from source. See the remarks on <c>PdfOperations.WithDecrypted</c>.</para>
        /// <para><b>The no-password overloads deliberately do NOT delegate here.</b> They call the
        /// extractor directly, exactly as they shipped. Routing them through <c>WithDecrypted</c>
        /// with a null password looks tidier and is a behaviour change: on an encrypted document
        /// with a decryptor registered, <c>EnsureDecryptedStream</c> <b>throws</b>
        /// <c>PdfException("Invalid password")</c>, where the password-less form refuses the
        /// encrypted document through <c>PdfEncryptedDocumentException</c>, naming this overload.</para>
        /// </remarks>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(
            Stream input, string password)
            => WithDecrypted(input, password,
                Operations.Extraction.PdfStructuredTextExtractor.Extract);

        /// <summary>
        /// Extracts text from all pages using custom word assembly tolerances.
        /// </summary>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(
            Stream input, Operations.Extraction.WordAssemblyOptions options)
            => Operations.Extraction.PdfStructuredTextExtractor.Extract(input, options);

        /// <summary>
        /// Extracts text from all pages of an <b>encrypted</b> document using custom word assembly
        /// tolerances.
        /// </summary>
        /// <param name="password">User or owner password. Null reads an unencrypted document.</param>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="options">
        /// Word- and line-assembly tolerances, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<PdfPageTextLayout> ExtractStructuredText(
            Stream input, Operations.Extraction.WordAssemblyOptions options, string password)
            => WithDecrypted(input, password,
                s => Operations.Extraction.PdfStructuredTextExtractor.Extract(s, options));

        /// <summary>
        /// Extracts text from a single page, optionally with custom word assembly tolerances.
        /// </summary>
        public static PdfPageTextLayout ExtractStructuredText(
            Stream input, int pageIndex, Operations.Extraction.WordAssemblyOptions options = null)
            => Operations.Extraction.PdfStructuredTextExtractor.Extract(input, pageIndex, options);

        /// <summary>
        /// Extracts text from a single page of an <b>encrypted</b> document.
        /// </summary>
        /// <param name="password">User or owner password. Null reads an unencrypted document.</param>
        /// <remarks>
        /// <paramref name="password"/> sits <i>before</i> the optional <paramref name="options"/>
        /// because a required parameter cannot follow an optional one — and that placement is also
        /// what keeps the pair unambiguous: this overload is inapplicable unless a password is
        /// actually supplied, so no existing call site can start resolving to it.
        /// </remarks>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="options">
        /// Word- and line-assembly tolerances, or <see langword="null"/> for the defaults.
        /// </param>
        public static PdfPageTextLayout ExtractStructuredText(
            Stream input, int pageIndex, string password,
            Operations.Extraction.WordAssemblyOptions options = null)
            => WithDecrypted(input, password,
                s => Operations.Extraction.PdfStructuredTextExtractor.Extract(s, pageIndex, options));

        /// <summary>
        /// Puts an arbitrary set of glyphs from <b>one line</b> into reading order.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists for callers holding glyphs rather than a whole document</b> — a
        /// viewer with a drag selection, a tool assembling a fragment. Structured extraction applies
        /// the same rule through <c>WordAssemblyOptions.ApplyBidi</c>, but that rebuilds
        /// <c>PdfTextLine.Text</c> and leaves the words and chars in their geometric order, so a
        /// caller that builds its own string from the chars cannot reach it. Same implementation,
        /// second door — not a second implementation.</para>
        ///
        /// <para><b>Give it one line's glyphs.</b> The visual order it sorts by is a single X axis,
        /// so glyphs from several lines interleave into nonsense. Split on the line breaks first.</para>
        ///
        /// <para><b>It is not "reorder the string you already have".</b> The glyphs are sorted into
        /// <i>visual</i> (left-to-right) order first, because that is the input the Unicode bidi
        /// algorithm is defined on. Running the algorithm over text that is already in reading order
        /// reverses what was right — the trap recorded against the viewer in GAPS.md §10.</para>
        /// </remarks>
        /// <param name="glyphs">
        /// The glyphs, in any order; their <c>X</c> and <c>Width</c> decide the result.
        /// </param>
        /// <param name="applyBidi">
        /// <see langword="true"/> runs the full Unicode bidi algorithm, which keeps an embedded
        /// Latin or numeric run inside a right-to-left line the right way round.
        /// <see langword="false"/> uses the majority-direction heuristic, reproducing the ordering
        /// structured extraction produces by default.
        /// </param>
        /// <returns>The glyphs as text, with word gaps filled in from their spacing.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="glyphs"/> is null.</exception>
        public static string ToReadingOrder(
            IReadOnlyList<Operations.Extraction.PdfTextChar> glyphs, bool applyBidi = true)
        {
            if (glyphs == null) throw new ArgumentNullException(nameof(glyphs));
            return Operations.TextDirection.ToReadingOrder(glyphs, applyBidi);
        }
    }
}
