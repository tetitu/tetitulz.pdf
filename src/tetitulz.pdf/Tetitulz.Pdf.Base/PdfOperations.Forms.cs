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
using Tetitulz.Pdf.Base.Operations.Forms;
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
        /// <summary>Fills form field values by fully-qualified field name.</summary>
        public static void FillForm(Stream input, Stream output,
            IDictionary<string, string> values)
            => PdfFormFiller.Fill(input, output, values);

        /// <summary>
        /// Fills form field values and reports what happened: which fields matched, which requested
        /// names did not, and which values were stored but could not be drawn (see
        /// <see cref="FormFillResult"/>).  Check boxes and radio buttons take the state name from
        /// <see cref="PdfFormFieldInfo.OnStates"/>, or a plain "true"/"false".
        /// </summary>
        public static FormFillResult FillForm(Stream input, Stream output,
            IDictionary<string, string> values, FormFillOptions options)
            => PdfFormFiller.Fill(input, output, values, options);

        /// <summary>
        /// Reads the document's AcroForm fields — names, types, current values, choice options,
        /// check-box states and widget positions.  The
        /// <see cref="PdfFormFieldInfo.FullyQualifiedName"/> values are the keys
        /// <see cref="FillForm(Stream, Stream, IDictionary{string, string})"/> expects.
        /// </summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<PdfFormFieldInfo> GetFormFields(Stream input)
            => GetFormFields(input, null);

        /// <summary>
        /// Reads the document's AcroForm fields, decrypting first with
        /// <paramref name="password"/> when the document is encrypted.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the password overload had to reach the facade.</b> An encrypted document's
        /// catalog and AcroForm resolve on name keys alone, so the structure walks fine — but every
        /// <c>/T</c> field name is a PDF <i>string</i>, i.e. ciphertext. The no-password form
        /// therefore returned a plausible list of fields whose
        /// <see cref="PdfFormFieldInfo.FullyQualifiedName"/> values were mojibake, with no
        /// exception; a caller that then passed those names to
        /// <see cref="FillForm(Stream, Stream, IDictionary{string, string})"/> matched nothing and
        /// was told nothing. <c>PdfFormReader.GetFields(Stream, string)</c> existed for exactly
        /// this and was unreachable from the facade.</para>
        /// <para>Read <see cref="LastExtractionError"/> after the call to tell "encrypted, could
        /// not read" from "no fields present".</para>
        /// </remarks>
        public static IReadOnlyList<PdfFormFieldInfo> GetFormFields(Stream input, string password)
            => WithDecrypted(input, password, PdfFormReader.GetFields);

        /// <summary>Reads the AcroForm fields of the PDF at <paramref name="inputPath"/>.</summary>
        public static IReadOnlyList<PdfFormFieldInfo> GetFormFields(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return GetFormFields(fs, null);
        }

        /// <summary>Flattens all form fields, rendering them as static content.</summary>
        public static void FlattenForm(Stream input, Stream output)
            => PdfFormFlattener.Flatten(input, output);

        /// <summary>
        /// Flattens all form fields, regenerating any appearance the document's
        /// <c>/NeedAppearances</c> flag has marked stale (§12.7.3.3).
        /// </summary>
        /// <param name="input">The PDF to flatten. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="options">
        /// Appearance-generation settings — chiefly <see cref="FormFillOptions.FontProvider"/>, which
        /// a value outside a single-byte encoding needs.
        /// </param>
        /// <remarks>
        /// Read <c>Operations.DocumentEditDiagnostics.LastError</c> after the call to learn whether a
        /// regeneration was needed and whether it succeeded: a value whose glyphs need an unsupplied
        /// font leaves the document's own — blank — appearance stamped, which the flattening cannot
        /// undo.
        /// </remarks>
        public static void FlattenForm(Stream input, Stream output, FormFillOptions options)
            => PdfFormFlattener.Flatten(input, output, options);

        // ── FDF / XFDF form-data interchange ─────────────────────────────────
        //
        // How form data leaves a PDF for a system that is not a PDF, and how it comes back.
        // PdfFormDataExchange carries the detail; these are the four calls a caller usually wants.

        /// <summary>
        /// Exports a document's filled field values as <b>XFDF</b> (ISO 19444-1).
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="output">Where the XFDF is written. Not disposed.</param>
        public static void ExportFormDataAsXfdf(Stream input, Stream output)
            => PdfFormDataExchange.WriteXfdf(PdfFormDataExchange.Export(input), output);

        /// <summary>
        /// Exports a document's filled field values as <b>FDF</b> (§12.7.7).
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="output">Where the FDF is written. Not disposed.</param>
        public static void ExportFormDataAsFdf(Stream input, Stream output)
            => PdfFormDataExchange.WriteFdf(PdfFormDataExchange.Export(input), output);

        /// <summary>
        /// Fills <paramref name="input"/> from an <b>XFDF</b> file and writes the result.
        /// </summary>
        /// <param name="input">The PDF to fill. Not disposed.</param>
        /// <param name="xfdf">The XFDF data. Not disposed.</param>
        /// <param name="output">Where the filled PDF is written. Not disposed.</param>
        public static FormFillResult ImportFormDataFromXfdf(Stream input, Stream xfdf, Stream output)
            => PdfFormDataExchange.Import(input, PdfFormDataExchange.ReadXfdf(xfdf), output);

        /// <summary>
        /// Fills <paramref name="input"/> from an <b>FDF</b> file and writes the result.
        /// </summary>
        /// <param name="input">The PDF to fill. Not disposed.</param>
        /// <param name="fdf">The FDF data. Not disposed.</param>
        /// <param name="output">Where the filled PDF is written. Not disposed.</param>
        public static FormFillResult ImportFormDataFromFdf(Stream input, Stream fdf, Stream output)
            => PdfFormDataExchange.Import(input, PdfFormDataExchange.ReadFdf(fdf), output);

        /// <summary>Adds a text input field to the document.</summary>
        public static void AddTextField(Stream input, Stream output, FormTextField field)
            => PdfFormFieldBuilder.AddTextField(input, output, field);

        /// <summary>Adds a checkbox field to the document.</summary>
        public static void AddCheckbox(Stream input, Stream output, FormCheckboxField field)
            => PdfFormFieldBuilder.AddCheckbox(input, output, field);

        /// <summary>Adds a combo box (drop-down list) field to the document.</summary>
        public static void AddComboBox(Stream input, Stream output, FormComboField field)
            => PdfFormFieldBuilder.AddComboBox(input, output, field);

        /// <summary>Adds a list box (scrollable list) field to the document.</summary>
        public static void AddListBox(Stream input, Stream output, FormListField field)
            => PdfFormFieldBuilder.AddListBox(input, output, field);

        /// <summary>Adds a radio button group — one field, one widget per button — to the document.</summary>
        public static void AddRadioGroup(Stream input, Stream output, FormRadioGroupField field)
            => PdfFormFieldBuilder.AddRadioGroup(input, output, field);

        /// <summary>
        /// Adds several fields of any kind in <b>one</b> appended revision. Nothing is written if any
        /// of them is refused.
        /// </summary>
        public static void AddFormFields(Stream input, Stream output, IReadOnlyList<FormFieldBase> fields)
            => PdfFormFieldBuilder.AddFields(input, output, fields);
    }
}
