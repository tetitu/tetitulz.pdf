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
        /// Locates labelled fields (e.g. invoice number, date, total, VAT) in a PDF stream and reads
        /// their adjacent values.  Structured extraction runs with bidi enabled so Hebrew/RTL labels
        /// and their values resolve in logical order.  The caller supplies the labels to search for —
        /// there is no built-in invoice schema (see <see cref="Operations.Forms.PdfFieldExtractor"/>).
        /// </summary>
        public static IReadOnlyList<Operations.Forms.FieldResult> ExtractFields(
            Stream input,
            IReadOnlyList<Operations.Forms.FieldQuery> queries,
            Operations.Extraction.WordAssemblyOptions options = null)
            => Operations.Forms.PdfFieldExtractor.Extract(input, queries, options);

        /// <summary>Runs field queries against an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. The password sits <i>before</i> the optional tail —
        /// a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="queries">The fields to look for; one result is returned per query.</param>
        /// <param name="options">
        /// Word-assembly options for the text underlying each field, or <see langword="null"/> for
        /// the defaults.
        /// </param>
        public static IReadOnlyList<Operations.Forms.FieldResult> ExtractFields(
            Stream input,
            IReadOnlyList<Operations.Forms.FieldQuery> queries,
            string password,
            Operations.Extraction.WordAssemblyOptions options = null)
            => WithDecrypted(input, password,
                s => Operations.Forms.PdfFieldExtractor.Extract(s, queries, options));

        /// <summary>Runs field queries against an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="queries">The fields to look for; one result is returned per query.</param>
        /// <param name="options">
        /// Word-assembly options for the text underlying each field, or <see langword="null"/> for
        /// the defaults.
        /// </param>
        public static IReadOnlyList<Operations.Forms.FieldResult> ExtractFields(
            string inputPath,
            IReadOnlyList<Operations.Forms.FieldQuery> queries,
            string password,
            Operations.Extraction.WordAssemblyOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExtractFields(file, queries, password, options);
        }

        /// <summary>Locates labelled fields in a PDF file. See the stream overload.</summary>
        public static IReadOnlyList<Operations.Forms.FieldResult> ExtractFields(
            string inputPath,
            IReadOnlyList<Operations.Forms.FieldQuery> queries,
            Operations.Extraction.WordAssemblyOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Operations.Forms.PdfFieldExtractor.Extract(fs, queries, options);
        }

        /// <summary>
        /// Reads an Israeli invoice — document type, number, date, supplier and customer numbers,
        /// allocation number, subtotal, VAT, total and line items — and validates it. See
        /// <see cref="Operations.Forms.IsraeliInvoiceExtractor"/>; for analysis, turn results into
        /// tables with <see cref="Operations.Forms.IsraeliInvoiceTables"/>.
        /// </summary>
        public static Operations.Forms.IsraeliInvoice ExtractIsraeliInvoice(
            Stream input, Operations.Forms.IsraeliInvoiceOptions options = null)
            => Operations.Forms.IsraeliInvoiceExtractor.Extract(input, options);

        /// <summary>Reads and validates an encrypted Israeli invoice, opened with <paramref name="password"/>.</summary>
        public static Operations.Forms.IsraeliInvoice ExtractIsraeliInvoice(
            Stream input, string password, Operations.Forms.IsraeliInvoiceOptions options)
            => Operations.Forms.IsraeliInvoiceExtractor.Extract(input, password, options);

        /// <summary>Reads and validates the Israeli invoice in a file; its name becomes the invoice's source.</summary>
        public static Operations.Forms.IsraeliInvoice ExtractIsraeliInvoice(
            string inputPath, Operations.Forms.IsraeliInvoiceOptions options = null)
            => Operations.Forms.IsraeliInvoiceExtractor.Extract(inputPath, options);

        /// <summary>
        /// Reads every document a PDF holds — a month's summary, its tax invoices and the report
        /// approved as another document, printed into one file — one invoice each, in page order.
        /// See <see cref="Operations.Forms.IsraeliInvoiceExtractor.ExtractAll(string, Operations.Forms.IsraeliInvoiceOptions)"/>.
        /// </summary>
        public static IReadOnlyList<Operations.Forms.IsraeliInvoice> ExtractIsraeliInvoices(
            Stream input, Operations.Forms.IsraeliInvoiceOptions options = null)
            => Operations.Forms.IsraeliInvoiceExtractor.ExtractAll(input, options);

        /// <summary>Reads every document in an encrypted PDF, opened with <paramref name="password"/>.</summary>
        public static IReadOnlyList<Operations.Forms.IsraeliInvoice> ExtractIsraeliInvoices(
            Stream input, string password, Operations.Forms.IsraeliInvoiceOptions options)
            => Operations.Forms.IsraeliInvoiceExtractor.ExtractAll(input, password, options);

        /// <summary>Reads every document in a PDF file; its name becomes each invoice's source.</summary>
        public static IReadOnlyList<Operations.Forms.IsraeliInvoice> ExtractIsraeliInvoices(
            string inputPath, Operations.Forms.IsraeliInvoiceOptions options = null)
            => Operations.Forms.IsraeliInvoiceExtractor.ExtractAll(inputPath, options);
    }
}
