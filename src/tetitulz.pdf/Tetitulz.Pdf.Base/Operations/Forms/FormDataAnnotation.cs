// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// One annotation in an FDF / XFDF exchange file — the markup half of the format, beside
    /// <see cref="FormDataField"/>'s form half.
    /// </summary>
    /// <remarks>
    /// <para><b>Page numbers are zero-based, and that is the format's choice rather than this
    /// library's.</b> Both FDF's <c>/Page</c> and XFDF's <c>page</c> attribute count from zero,
    /// which is the opposite of the <c>/PageLabels</c> convention a reader sees and a reliable
    /// source of off-by-one imports. It is stated here because this is where a caller reads it.</para>
    ///
    /// <para><b>Geometry is carried verbatim, in PDF user space.</b> No transform is applied on the
    /// way in or out: an exchange format's coordinates are the ones the producing viewer computed
    /// against the same page box, and "helpfully" flipping or scaling them is how an annotation
    /// lands somewhere its author never put it.</para>
    ///
    /// <para><b>Only the subtypes that carry their geometry in the annotation dictionary are
    /// modelled.</b> Text markup (<c>QuadPoints</c>), <c>Line</c>, <c>Ink</c>,
    /// <c>Polygon</c>/<c>PolyLine</c> (<c>Vertices</c>), <c>Square</c>/<c>Circle</c> and
    /// <c>Text</c>/<c>FreeText</c> round-trip completely. A <c>Stamp</c>'s appearance, a
    /// <c>FileAttachment</c>'s payload and a <c>Sound</c>'s samples are streams that FDF can carry
    /// and this model deliberately does not: importing a stream is a different security question
    /// from importing coordinates, and silently dropping one is worse than not offering it —
    /// <c>PdfFormDataExchange.LastIgnoredContent</c> is where a caller learns it happened.</para>
    ///
    /// <para><b>Unrecognised keys are read, and the plain ones written back.</b> <see cref="Extras"/>
    /// holds every dictionary entry this model does not name, as token text, for the caller to read.
    /// The entries made only of plain values — numbers, names, strings, booleans, and arrays and
    /// dictionaries of them — are also kept as values, so the annotation writer and the FDF writer
    /// carry a producer's <c>/RC</c> rich text, <c>/BE</c> border effect, <c>/LE</c> line endings or
    /// <c>/IT</c> intent through a read-modify-write. An FDF is untrusted input, so nothing that points
    /// elsewhere or runs is carried: no reference left unresolved, no stream, no action
    /// (<c>/A</c>, <c>/AA</c>, <c>/PA</c>), no appearance (<c>/AP</c>, <c>/AS</c>), no link into the
    /// document the annotation came from (<c>/P</c>, <c>/Popup</c>, <c>/Parent</c>, <c>/IRT</c>,
    /// <c>/OC</c>, <c>/StructParent</c>, <c>/Dest</c>) and no payload (<c>/FS</c>, <c>/Sound</c>,
    /// <c>/Movie</c>). Such an entry stays in <see cref="Extras"/> to be read, and is not written.</para>
    /// </remarks>
    public sealed class FormDataAnnotation
    {
        private readonly List<double[]> _inkList = new List<double[]>();
        private readonly Dictionary<string, string> _extras =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Tetitulz.Pdf.Core.Objects.PdfObject> _carried =
            new Dictionary<string, Tetitulz.Pdf.Core.Objects.PdfObject>(StringComparer.Ordinal);

        /// <param name="subtype">
        /// The §12.5.6 annotation subtype without its slash — <c>Highlight</c>, <c>Text</c>,
        /// <c>Square</c>, <c>Ink</c>, <c>Line</c>, <c>Polygon</c> and so on.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="subtype"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="subtype"/> is empty.</exception>
        public FormDataAnnotation(string subtype)
        {
            if (subtype == null) throw new ArgumentNullException(nameof(subtype));
            if (subtype.Length == 0)
                throw new ArgumentException("An annotation subtype cannot be empty.", nameof(subtype));

            Subtype = subtype;
        }

        /// <summary>The §12.5.6 subtype, without its slash.</summary>
        public string Subtype { get; }

        /// <summary><b>Zero-based</b> page index — FDF's <c>/Page</c>, XFDF's <c>page</c>.</summary>
        public int PageIndex { get; set; }

        /// <summary>
        /// <c>/Rect</c> as <c>[x1 y1 x2 y2]</c> in PDF user space, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <b>Not normalised on the way in.</b> §12.5.2 says a reader shall normalise a rectangle
        /// whose corners are the wrong way round, and the writer does so — but the value a caller
        /// reads back is the one the file carried, because a round trip that "corrects" its input
        /// cannot be used to tell whether the input needed correcting.
        /// </remarks>
        public double[] Rect { get; set; }

        /// <summary><c>/C</c> — the annotation's colour, 0 to 1 per component; <see langword="null"/> for none.</summary>
        /// <remarks>
        /// One, three or four components select DeviceGray, DeviceRGB or DeviceCMYK (§12.5.2), and
        /// an <b>empty</b> array is not the same as absent: it means "no colour", which for a
        /// <c>Square</c> is a border that is not drawn.
        /// </remarks>
        public double[] Color { get; set; }

        /// <summary><c>/IC</c> — interior colour for <c>Square</c>, <c>Circle</c>, <c>Line</c>, <c>Polygon</c>.</summary>
        public double[] InteriorColor { get; set; }

        /// <summary><c>/Contents</c> — the note text, or the accessible description of a markup annotation.</summary>
        public string Contents { get; set; }

        /// <summary><c>/T</c> — the author. XFDF calls this <c>title</c>.</summary>
        public string Title { get; set; }

        /// <summary><c>/Subj</c> — the annotation's subject.</summary>
        public string Subject { get; set; }

        /// <summary><c>/NM</c> — a name unique among the page's annotations.</summary>
        /// <remarks>
        /// The one field that makes an import idempotent: a second import of the same file should
        /// replace what it added before, and nothing else in the model identifies an annotation.
        /// </remarks>
        public string Name { get; set; }

        /// <summary><c>/M</c> — the modification date, in §7.9.4 form (<c>D:YYYYMMDDHHmmSSOHH'mm</c>).</summary>
        public string ModifiedDate { get; set; }

        /// <summary><c>/CreationDate</c>, in the same form as <see cref="ModifiedDate"/>.</summary>
        public string CreationDate { get; set; }

        /// <summary><c>/CA</c> — constant opacity from 0 to 1, or <see langword="null"/> for the default 1.</summary>
        public double? Opacity { get; set; }

        /// <summary><c>/F</c> — the §12.5.3 flag bits, or <see langword="null"/> for none.</summary>
        public int? Flags { get; set; }

        /// <summary><c>/QuadPoints</c> — four corners per quad, for the text-markup subtypes.</summary>
        /// <remarks>
        /// <b>The corner order is the one §12.5.6.10 gives</b> (upper-left, upper-right, lower-left,
        /// lower-right), which is not the order a rectangle is usually written in and is a documented
        /// source of viewer disagreement. Carried through untouched for that reason.
        /// </remarks>
        public double[] QuadPoints { get; set; }

        /// <summary><c>/Vertices</c> — <c>x y</c> pairs for <c>Polygon</c> and <c>PolyLine</c>.</summary>
        public double[] Vertices { get; set; }

        /// <summary><c>/L</c> — <c>[x1 y1 x2 y2]</c> for a <c>Line</c>.</summary>
        public double[] Line { get; set; }

        /// <summary><c>/InkList</c> — one array of <c>x y</c> pairs per stroke.</summary>
        public IList<double[]> InkList => _inkList;

        /// <summary><c>/BS /W</c> — border width in points, or <see langword="null"/> for the default.</summary>
        public double? BorderWidth { get; set; }

        /// <summary>
        /// Dictionary entries this model does not name, by key (without the slash), as the raw PDF
        /// token text.
        /// </summary>
        /// <remarks>
        /// Read so a caller can see what the model did not understand. Token text rather than a parsed
        /// object, so a producer's <c>/RC</c> rich text is shown as the file has it. Editing it changes
        /// nothing written: what is written back is the plain entries the file carried, as values
        /// (see the class remarks).
        /// </remarks>
        public IDictionary<string, string> Extras => _extras;

        /// <summary>
        /// The unrecognised entries that are written back: each one's value made only of plain direct
        /// values, read from the file. Filled by the reader; the writers add each entry the model does
        /// not set itself.
        /// </summary>
        internal IDictionary<string, Tetitulz.Pdf.Core.Objects.PdfObject> Carried => _carried;

        /// <inheritdoc/>
        public override string ToString()
            => $"{Subtype} on page {PageIndex}" + (Title == null ? string.Empty : $" by {Title}");
    }
}
