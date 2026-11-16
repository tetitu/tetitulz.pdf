// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// One field's data in an FDF / XFDF exchange file: a fully-qualified name and its value.
    /// </summary>
    /// <remarks>
    /// <b>Values is a list because a multi-select list box has several selections</b> (§12.7.4.4 —
    /// <c>/V</c> is then an array of strings), not because ordinary fields ever have more than one.
    /// Both formats can carry the whole set; see <see cref="FormData.ToDictionary"/> for what
    /// happens on the way back into a PDF.
    /// </remarks>
    public sealed class FormDataField
    {
        private readonly List<string> _values = new List<string>();

        /// <param name="name">Fully-qualified field name — partial names joined by <c>.</c>.</param>
        /// <param name="values">The field's value, or every selection of a multi-select list box.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public FormDataField(string name, params string[] values)
            : this(name, (IEnumerable<string>)values)
        {
        }

        /// <param name="name">Fully-qualified field name — partial names joined by <c>.</c>.</param>
        /// <param name="values">The field's value, or every selection of a multi-select list box.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public FormDataField(string name, IEnumerable<string> values)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (values != null)
                foreach (string v in values)
                    _values.Add(v ?? string.Empty);
        }

        /// <summary>Fully-qualified field name — the name <see cref="PdfFormFiller"/> matches on.</summary>
        public string Name { get; }

        /// <summary>Every value the file carries for this field; empty when the field has none.</summary>
        public IReadOnlyList<string> Values => _values;

        /// <summary>The first value, or the empty string when there is none.</summary>
        public string Value => _values.Count > 0 ? _values[0] : string.Empty;

        /// <summary>True when this is a multi-select list box carrying more than one selection.</summary>
        public bool IsMultiValued => _values.Count > 1;

        /// <inheritdoc/>
        public override string ToString() => Name + " = " + string.Join(", ", _values);
    }

    /// <summary>
    /// The contents of an FDF or XFDF form-data file: which fields, and — optionally — which
    /// document they came from.
    ///
    /// <para>This is the shape both serialisers read and write, so a file can be converted between
    /// the two formats by reading one and writing the other, and neither format's quirks reach the
    /// caller.</para>
    /// </summary>
    public sealed class FormData
    {
        /// <summary>The fields, in the order the file lists them.</summary>
        public IList<FormDataField> Fields { get; } = new List<FormDataField>();

        /// <summary>The annotations, in the order the file lists them.</summary>
        /// <remarks>
        /// <para><b>Separate from <see cref="Fields"/> because the two halves travel
        /// independently.</b> Both formats allow a file to carry either, and the common cases are
        /// exactly one of them: a filled form returns fields, a review round returns annotations.
        /// A caller importing one must not be made to think about the other.</para>
        /// <para>An annotation whose widget is a form field is a field, not an annotation, and
        /// appears in <see cref="Fields"/>. Nothing here writes a <c>/Widget</c>.</para>
        /// </remarks>
        public IList<FormDataAnnotation> Annotations { get; } = new List<FormDataAnnotation>();

        /// <summary>
        /// The document the data belongs to — XFDF's <c>&lt;f href&gt;</c>, FDF's <c>/F</c>. Null
        /// when the file names none, which is legal in both formats and common when the data is
        /// being handed to a system that already knows which form it asked for.
        /// </summary>
        public string SourceFile { get; set; }

        /// <summary>
        /// The source document's original file identifier as an upper-case hex string without
        /// delimiters — XFDF's <c>&lt;ids original&gt;</c>, the first half of FDF's <c>/ID</c>.
        /// Null when absent.
        /// </summary>
        public string OriginalId { get; set; }

        /// <summary>The identifier of the revision the data was taken from; see <see cref="OriginalId"/>.</summary>
        public string ModifiedId { get; set; }

        /// <summary>
        /// The fields as <see cref="PdfFormFiller"/> wants them: fully-qualified name → value, a
        /// multi-select list box's selections joined with <see cref="PdfFormFiller.MultiSelectSeparator"/>,
        /// which the filler writes as the array <c>/V</c> §12.7.4.4 asks for.
        /// </summary>
        /// <remarks>
        /// A later field wins over an earlier one of the same name, which is how a reader resolves
        /// a file that lists a name twice.
        /// </remarks>
        public IDictionary<string, string> ToDictionary()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Fields)
                if (f != null && f.Name != null)
                    map[f.Name] = f.IsMultiValued ? string.Join(PdfFormFiller.MultiSelectSeparator, f.Values) : f.Value;
            return map;
        }
    }

    /// <summary>Policy for <see cref="PdfFormDataExchange.Export(System.IO.Stream, FormDataExportOptions)"/>.</summary>
    public sealed class FormDataExportOptions
    {
        /// <summary>
        /// Whether to export fields that hold no value. Off by default, matching what a viewer's
        /// "export form data" produces: the file then carries what was filled in, not the form's
        /// whole field list. Turn it on to produce a template of every name.
        /// </summary>
        public bool IncludeEmptyFields { get; set; }

        /// <summary>
        /// Whether to record the source document's <c>/ID</c> in the exported file. On by default:
        /// it is what lets the receiving end tell that data is being imported back into the
        /// document it came from.
        /// </summary>
        public bool IncludeDocumentId { get; set; } = true;
    }
}
