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
using Tetitulz.Pdf.Base.Operations.Structure;
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
        /// <summary>Reads the logical structure tree from a Tagged PDF file.</summary>
        public static PdfStructElement ReadStructureTree(string inputPath)
            => PdfStructureTreeReader.ReadStructureTree(inputPath);

        /// <summary>Reads the logical structure tree from a Tagged PDF stream.</summary>
        public static PdfStructElement ReadStructureTree(Stream input)
            => ReadStructureTree(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static PdfStructElement ReadStructureTree(Stream input, string password)
            => WithDecrypted(input, password, PdfStructureTreeReader.ReadStructureTree);

        /// <summary>True when the PDF declares a logical structure tree (a Tagged PDF).</summary>
        public static bool IsTaggedPdf(Stream input)
            => IsTaggedPdf(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static bool IsTaggedPdf(Stream input, string password)
            => WithDecrypted(input, password, Operations.Extraction.PdfTaggedTextExtractor.IsTagged);

        /// <summary>
        /// Structure-aware text extraction for Tagged PDFs: returns logical-structure blocks (P, H1–H6,
        /// table cells, list items, figures) in authoritative reading order, honoring /ActualText and
        /// exposing figure /Alt text.  Returns an empty list for non-tagged documents — fall back to
        /// <see cref="ExtractStructuredText(Stream)"/> in that case.
        /// </summary>
        public static IReadOnlyList<Operations.Extraction.PdfTextBlock> ExtractTaggedText(
            Stream input, Operations.Extraction.WordAssemblyOptions options = null)
            => Operations.Extraction.PdfTaggedTextExtractor.Extract(input, options);

        /// <summary>Structure-aware tagged-text extraction from an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="options">
        /// Word- and line-assembly tolerances, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<Operations.Extraction.PdfTextBlock> ExtractTaggedText(
            Stream input, string password,
            Operations.Extraction.WordAssemblyOptions options = null)
            => WithDecrypted(input, password,
                s => Operations.Extraction.PdfTaggedTextExtractor.Extract(s, options));

        /// <summary>Structure-aware tagged-text extraction from an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="options">
        /// Word- and line-assembly tolerances, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<Operations.Extraction.PdfTextBlock> ExtractTaggedText(
            string inputPath, string password,
            Operations.Extraction.WordAssemblyOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExtractTaggedText(file, password, options);
        }

        /// <summary>Structure-aware tagged-text extraction from a file. See the stream overload.</summary>
        public static IReadOnlyList<Operations.Extraction.PdfTextBlock> ExtractTaggedText(
            string inputPath, Operations.Extraction.WordAssemblyOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Operations.Extraction.PdfTaggedTextExtractor.Extract(fs, options);
        }
    }
}
