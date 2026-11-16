// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Moves form data between a PDF and the two interchange formats a non-PDF system speaks:
    /// <b>FDF</b> (§12.7.7) and <b>XFDF</b> (ISO 19444-1).
    ///
    /// <para>Export reads the field values a document holds; import writes them back into one. The
    /// two ends are independent — data exported here can be edited, generated or consumed by
    /// something that has never opened a PDF, which is the point of the formats.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>The formats are interchangeable through <see cref="FormData"/></b>, so converting
    /// between them is a read followed by a write, and neither one's quirks reach a caller that
    /// only wants the other.</para>
    /// <para><b>Fields and annotations travel independently, and both are carried.</b> Either format
    /// may hold either (XFDF's <c>&lt;annots&gt;</c>, FDF's <c>/Annots</c>) — a filled form returns
    /// fields, a review round returns annotations, and the common case is exactly one of them. They
    /// are written together and read together, but imported by <b>different calls</b>:
    /// <see cref="Import"/> fills fields and <see cref="ImportAnnotations"/> writes annotations.
    /// Folding them into one would mean a caller importing comments also runs a form fill, which
    /// rewrites <c>/V</c> and regenerates appearances on fields nobody touched.</para>
    /// </remarks>
    public static class PdfFormDataExchange
    {
        // ----------------------------------------------------------------
        //  PDF → FormData
        // ----------------------------------------------------------------

        /// <summary>Reads a document's form field values into an exchange-ready model.</summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="options">Export policy; null uses the defaults.</param>
        public static FormData Export(Stream input, FormDataExportOptions options = null)
            => Export(input, null, options);

        /// <summary>
        /// Reads an encrypted document's form field values, given its password.
        /// </summary>
        /// <remarks>
        /// Reading a form out of an encrypted document needs a registered decryptor — see
        /// <see cref="PdfFormReader.GetFields(Stream, string)"/>, which this delegates to.
        /// </remarks>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="password">User or owner password; null for an unencrypted document.</param>
        /// <param name="options">Export policy; null uses the defaults.</param>
        public static FormData Export(Stream input, string password, FormDataExportOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new FormDataExportOptions();

            // Buffered once, because this reads the source twice — the field tree, then the
            // trailer /ID. Reading a forward-only stream a second time yields nothing, so an
            // export from a network stream would silently lose the identifier.
            //
            // `rewind: true` is belt-and-braces and no fixture can tell: PdfDocument.Open locates
            // the header and the cross-reference table by seeking, and ReadDocumentId sets the
            // position itself. A mutation sweep reports it as a survivor; it is kept because it
            // states this caller's requirement rather than relying on the callee's, and the
            // buffering half — which a one-pass stream genuinely needs — is what the call is for.
            Stream source = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                var fields = password == null
                    ? PdfFormReader.GetFields(source)
                    : PdfFormReader.GetFields(source, password);

                var data = new FormData();
                foreach (var field in fields)
                {
                    if (field?.FullyQualifiedName == null) continue;

                    // A push button has no value by definition (§12.7.4.2), and a signature
                    // field's value is a signature dictionary, not text — exporting either would
                    // put a name in the file that importing could never act on.
                    if (field.FieldType == PdfFieldType.Signature || field.IsPushButton) continue;

                    IReadOnlyList<string> values = field.SelectedValues.Count > 1
                        ? field.SelectedValues
                        : (string.IsNullOrEmpty(field.Value) ? Array.Empty<string>() : new[] { field.Value });

                    if (values.Count == 0 && !options.IncludeEmptyFields) continue;

                    data.Fields.Add(new FormDataField(field.FullyQualifiedName, values));
                }

                if (options.IncludeDocumentId) ReadDocumentId(source, data);
                return data;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        /// <summary>
        /// Records the trailer <c>/ID</c> so the receiving end can tell the data is coming back to
        /// the document it left. Absent or unreadable is not an error — the identifier is optional
        /// in both formats, and an export without one is still complete.
        /// </summary>
        /// <remarks>
        /// Works on an <b>encrypted</b> document too, and deliberately: §7.6.2 exempts the trailer
        /// <c>/ID</c> from encryption, so it reads correctly with no decryptor registered and no
        /// password — which is the one thing about an encrypted file this assembly can always
        /// answer.
        /// </remarks>
        private static void ReadDocumentId(Stream source, FormData data)
        {
            try
            {
                source.Position = 0;
                using var doc = PdfDocument.Open(source);
                if (!(doc.Trailer?[PdfName.ID] is PdfArray ids)) return;

                if (ids.Count > 0) data.OriginalId = HexOf(doc.ResolveObject(ids[0]) as PdfString);
                if (ids.Count > 1) data.ModifiedId = HexOf(doc.ResolveObject(ids[1]) as PdfString);
            }
            catch (Core.PdfException) { }
            catch (IOException) { }
        }

        private static string HexOf(PdfString s)
        {
            if (s?.RawBytes == null || s.RawBytes.Length == 0) return null;
            var sb = new System.Text.StringBuilder(s.RawBytes.Length * 2);
            foreach (byte b in s.RawBytes)
                sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ----------------------------------------------------------------
        //  FormData ↔ files
        // ----------------------------------------------------------------

        /// <summary>Writes <paramref name="data"/> as XFDF (ISO 19444-1). The stream is not disposed.</summary>
        public static void WriteXfdf(FormData data, Stream output) => XfdfSerializer.Write(data, output);

        /// <summary>Writes <paramref name="data"/> as FDF (§12.7.7). The stream is not disposed.</summary>
        public static void WriteFdf(FormData data, Stream output) => FdfSerializer.Write(data, output);

        /// <summary>
        /// What the last read on this thread found in the file and did <b>not</b> import, or
        /// <c>null</c> when it imported everything the file carried.
        /// </summary>
        /// <remarks>
        /// <para><b>Both formats carry more than fields and annotations.</b> An FDF may also hold
        /// <c>/JavaScript</c>, <c>/Pages</c>, <c>/Differences</c> and <c>/EmbeddedFDFs</c>, none of
        /// which is imported. This slot is what makes "the file carried three things this reader
        /// does not import" different from "the file had nothing in it" — two answers a caller acts
        /// on differently.</para>
        ///
        /// <para><b>The annotation sections are read, not reported.</b> A comment review exported
        /// from a viewer is mostly <c>&lt;annots&gt;</c>; the reporting
        /// stayed for everything else.</para>
        ///
        /// <para><c>[ThreadStatic]</c>, cleared as the first statement of every read on every path.
        /// </para>
        /// </remarks>
        public static string LastIgnoredContent => _lastIgnoredContent;

        [ThreadStatic]
        private static string _lastIgnoredContent;

        /// <summary>Records what a reader skipped. Internal: the serializers report through it.</summary>
        internal static void ReportIgnored(IReadOnlyList<string> sections)
        {
            _lastIgnoredContent = sections == null || sections.Count == 0
                ? null
                : "the file carried " + string.Join(", ", sections) +
                  ", which this reader does not import";
        }

        /// <summary>
        /// Reads an XFDF file. The reader prohibits DTDs, which closes both external-entity
        /// resolution and entity expansion.
        /// </summary>
        /// <remarks>
        /// Anything the file carries beyond its fields and annotations is reported on
        /// <see cref="LastIgnoredContent"/>.
        /// </remarks>
        /// <exception cref="Core.PdfException">
        /// The file is not well-formed XML, or exceeds a structural limit.
        /// </exception>
        public static FormData ReadXfdf(Stream input)
        {
            _lastIgnoredContent = null;
            return XfdfSerializer.Read(input);
        }

        /// <summary>Reads an FDF file.</summary>
        /// <remarks>
        /// Anything the file carries beyond its fields and annotations is reported on
        /// <see cref="LastIgnoredContent"/>.
        /// </remarks>
        /// <exception cref="Core.PdfException">
        /// The file carries no <c>/FDF</c> dictionary, or exceeds a structural limit.
        /// </exception>
        public static FormData ReadFdf(Stream input)
        {
            _lastIgnoredContent = null;
            return FdfSerializer.Read(input);
        }

        // ----------------------------------------------------------------
        //  FormData → PDF
        // ----------------------------------------------------------------

        /// <summary>
        /// Fills <paramref name="input"/>'s fields from <paramref name="data"/> and writes the
        /// result to <paramref name="output"/>.
        /// </summary>
        /// <remarks>
        /// A thin join of <see cref="FormData.ToDictionary"/> and <see cref="PdfFormFiller.Fill(Stream, Stream, IDictionary{string, string}, FormFillOptions)"/>,
        /// so every rule the filler has applies unchanged — encrypted input is refused there, not
        /// here, and appearances are generated on the same terms. A multi-select list box's several
        /// selections reach the filler joined (<see cref="PdfFormFiller.MultiSelectSeparator"/>) and
        /// are written as the array <c>/V</c> with its <c>/I</c>.
        /// </remarks>
        /// <param name="input">The PDF to fill. Not disposed.</param>
        /// <param name="data">The values to write.</param>
        /// <param name="output">Where the filled PDF is written. Not disposed.</param>
        /// <param name="options">Fill policy; null uses the defaults.</param>
        public static FormFillResult Import(
            Stream input, FormData data, Stream output, FormFillOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (output == null) throw new ArgumentNullException(nameof(output));

            return PdfFormFiller.Fill(input, output, data.ToDictionary(), options ?? new FormFillOptions());
        }
        /// <summary>
        /// Writes an exchange file's <b>annotations</b> into a PDF, in one appended revision, and
        /// returns how many landed.
        /// </summary>
        /// <param name="input">The PDF to annotate. Not disposed.</param>
        /// <param name="data">The exchange file's contents; only its annotations are read.</param>
        /// <param name="output">Where the annotated PDF is written. Not disposed.</param>
        /// <remarks>
        /// <para><b>The other half of <see cref="Import"/>, and deliberately not folded into it.</b>
        /// A file's fields and its annotations are independent — a filled form returns fields, a
        /// review round returns annotations, and the common case is exactly one of them. Folding
        /// both into one call would mean a caller importing comments also runs a form fill, which
        /// rewrites <c>/V</c> and regenerates appearances on fields nobody touched.</para>
        ///
        /// <para><b>Page indices come from the file and are zero-based</b>, which is the format's
        /// convention in both FDF and XFDF. An annotation naming a page the document does not have
        /// is skipped rather than throwing, and named in
        /// <see cref="PdfAnnotationWriter.LastAnnotationsSkipped"/> — a review file routinely
        /// outlives the pages it was written against.</para>
        ///
        /// <para><b>Existing annotations are kept</b>, so importing a second review adds to the
        /// first rather than replacing it.</para>
        /// </remarks>
        public static int ImportAnnotations(Stream input, FormData data, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var annotations = new List<FormDataAnnotation>(data.Annotations);
            return PdfAnnotationWriter.WriteAnnotations(input, output, annotations);
        }
    }
}
