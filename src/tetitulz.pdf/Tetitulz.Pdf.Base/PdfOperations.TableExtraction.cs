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
        /// Extracts tables from all pages of a PDF file.
        /// Uses <see cref="TableStrategy.Auto"/> by default: tries vector-ruling
        /// detection first and falls back to text-position heuristics.
        /// </summary>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="options">
        /// Detection options, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<PdfTable> ExtractTables(
            string inputPath, TableExtractionOptions options = null)
            => PdfTableExtractor.Extract(inputPath, options);

        /// <summary>Extracts tables from an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="options">
        /// Detection options, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<PdfTable> ExtractTables(
            string inputPath, string password, TableExtractionOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExtractTables(file, password, options);
        }

        /// <summary>Extracts tables from all pages of a PDF stream.</summary>
        public static IReadOnlyList<PdfTable> ExtractTables(
            Stream input, TableExtractionOptions options = null)
            => PdfTableExtractor.Extract(input, options);

        /// <summary>
        /// Extracts tables from text layouts and graphics already extracted
        /// (<see cref="ExtractStructuredText(Stream)"/>, <see cref="PdfGraphicsExtractor.Extract(Stream)"/>), for a
        /// caller that reads the layouts too and would otherwise extract the text twice.
        /// </summary>
        /// <param name="pages">One layout a page, in page order.</param>
        /// <param name="graphics">One a page, or <see langword="null"/> to read every page by the text strategy.</param>
        /// <param name="options">Detection options, or <see langword="null"/> for the defaults.</param>
        public static IReadOnlyList<PdfTable> ExtractTables(
            IReadOnlyList<PdfPageTextLayout> pages, IReadOnlyList<PdfPageGraphics> graphics,
            TableExtractionOptions options = null)
            => PdfTableExtractor.Extract(pages, graphics, options);

        /// <summary>Extracts tables from an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one,
        /// and that placement is also what keeps the pair unambiguous at every arity.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="options">
        /// Detection options, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<PdfTable> ExtractTables(
            Stream input, string password, TableExtractionOptions options = null)
            => WithDecrypted(input, password, s => PdfTableExtractor.Extract(s, options));

        /// <summary>Finds tables within an area of a page of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="area">
        /// Search rectangle in PDF points, measured from the page's bottom-left corner.
        /// </param>
        /// <param name="options">
        /// Detection options, or <see langword="null"/> for the defaults.
        /// </param>
        public static IReadOnlyList<PdfTable> FindTables(
            Stream input,
            int pageIndex,
            (float X, float Y, float Width, float Height) area,
            string password,
            Operations.Extraction.TableExtractionOptions options = null)
            => WithDecrypted(input, password, s => FindTables(s, pageIndex, area, options));

        /// <summary>
        /// Searches for tables within a specific bounding box on the given page.
        /// </summary>
        public static IReadOnlyList<PdfTable> FindTables(
            Stream input,
            int pageIndex,
            (float X, float Y, float Width, float Height) area,
            Operations.Extraction.TableExtractionOptions options = null)
            => Operations.Extraction.PdfTableExtractor.FindTables(input, pageIndex, area, options);
    }
}
