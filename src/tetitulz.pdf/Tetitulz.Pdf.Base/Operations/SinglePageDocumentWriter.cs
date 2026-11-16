// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Assembles the minimal one-page PDF file a generator emits when it has nothing to update:
    /// catalog, page tree, one page with a single Helvetica resource, one content stream, a
    /// four-entry cross-reference table and a trailer.
    ///
    /// <para>This was written out twice, line for line — <c>SvgToPdfConverter.BuildPdf</c> and
    /// <c>PdfBarcodeWriter.BuildPdf</c> — down to the same <c>header1/obj1/obj2/obj3/obj4h/obj4f</c>
    /// decomposition, the same offset arithmetic, and the same verbatim two-line comment about
    /// <c>Append</c> versus <c>AppendLine</c>. Only the 20-byte entry formatter had been shared.</para>
    /// </summary>
    /// <remarks>
    /// <b>The font encoding is a required parameter, and that is the point of the type.</b> Callers
    /// differ on exactly that entry: <c>/Encoding /WinAnsiEncoding</c>, or none, falling back to
    /// Helvetica's built-in StandardEncoding. A parameter with no default makes each caller say
    /// which it means.
    ///
    /// <para><b>No static fields, so no type initializer.</b> Both copies held a
    /// <c>static readonly Encoding</c>; this writes Latin-1 by casting, which is the same 1:1
    /// byte↔code-point mapping and keeps the type a leaf by construction — the property CLAUDE.md
    /// asks for whenever something becomes shared. A code point above U+00FF is written as
    /// <c>?</c>, byte-identically to <c>Encoding.GetEncoding("iso-8859-1")</c>, whose encoder
    /// fallback substitutes exactly that.</para>
    ///
    /// <para><b>It is not a general writer.</b> <see cref="PdfWriter"/> owns the real object/xref
    /// machinery; this is the fixed five-object skeleton those two generators need, and nothing
    /// here should grow object numbering, page counts or incremental update. A generator that needs
    /// those needs <see cref="PdfWriter"/>.</para>
    /// </remarks>
    public static class SinglePageDocumentWriter
    {
        /// <summary>The number of objects the skeleton always contains, plus the free entry.</summary>
        private const int ObjectCount = 4;

        /// <summary>The number the first of the extra objects gets; each after it, one more, in order.</summary>
        internal const int FirstExtraObject = ObjectCount + 1;

        /// <summary>The page object's number, for a destination that names the page.</summary>
        internal const int PageObject = 3;

        /// <summary>
        /// A complete one-page PDF drawing <paramref name="contentStream"/>.
        /// </summary>
        /// <param name="contentStream">Page content stream text; encoded as Latin-1.</param>
        /// <param name="mediaBox">
        /// The <c>/MediaBox</c> array, already formatted — e.g. <c>"[0 0 200.00 100.00]"</c>. The
        /// callers format their numbers differently (F2 against F4) and neither should be made to
        /// change its output, so the choice stays with them.
        /// </param>
        /// <param name="fontEncoding">
        /// The <c>/Encoding</c> value for the Helvetica resource — normally
        /// <c>"/WinAnsiEncoding"</c>. Pass <c>null</c> for the font's built-in encoding, which is
        /// StandardEncoding and disagrees with Latin-1 over the apostrophe and the grave accent as
        /// well as most of the upper half. See the remarks on the type.
        /// </param>
        public static byte[] Build(string contentStream, string mediaBox, string fontEncoding)
            => Build(contentStream, mediaBox, fontEncoding, extGStates: null);

        /// <summary>
        /// <see cref="Build(string, string, string)"/>, with an <c>/ExtGState</c> resource.
        /// </summary>
        /// <param name="contentStream">Page content stream text; encoded as Latin-1.</param>
        /// <param name="mediaBox">The <c>/MediaBox</c> array, already formatted.</param>
        /// <param name="fontEncoding">The Helvetica resource's <c>/Encoding</c>, or <c>null</c>.</param>
        /// <param name="extGStates">
        /// The entries of the page's <c>/ExtGState</c> dictionary, already formatted as direct
        /// dictionaries — <c>/GS0 &lt;&lt; /ca 0.5 /CA 1 &gt;&gt;</c> — or <c>null</c> for none, which
        /// writes the file byte for byte as the three-argument form does. Direct, so the skeleton
        /// keeps its five objects.
        /// </param>
        internal static byte[] Build(string contentStream, string mediaBox, string fontEncoding, string extGStates)
            => Build(contentStream, mediaBox, fontEncoding, extGStates, annotations: null);

        /// <summary>
        /// <see cref="Build(string, string, string, string)"/>, with annotations on the page.
        /// </summary>
        /// <param name="contentStream">Page content stream text; encoded as Latin-1.</param>
        /// <param name="mediaBox">The <c>/MediaBox</c> array, already formatted.</param>
        /// <param name="fontEncoding">The Helvetica resource's <c>/Encoding</c>, or <c>null</c>.</param>
        /// <param name="extGStates">The page's <c>/ExtGState</c> entries, or <c>null</c>.</param>
        /// <param name="annotations">
        /// Annotation dictionaries, already formatted, or <c>null</c> for none. Each is written as an
        /// object of its own, numbered from 5 in order, and the page's <c>/Annots</c> refers to them:
        /// an annotation is an indirect object (ISO 32000-1 Table 30). None writes the file byte for
        /// byte as the four-argument form does.
        /// </param>
        internal static byte[] Build(string contentStream, string mediaBox, string fontEncoding, string extGStates,
            IReadOnlyList<string> annotations)
            => Build(contentStream, mediaBox, fontEncoding, extGStates, xObjects: null,
                annotations?.Select(ExtraObject.Annotation).ToList());

        /// <summary>
        /// An object written after the skeleton's four: a dictionary, a stream, or an annotation the
        /// page's <c>/Annots</c> lists.
        /// </summary>
        internal sealed class ExtraObject
        {
            private ExtraObject(string dictionary, byte[] stream, bool isAnnotation, PdfObject obj = null)
            { Dictionary = dictionary; Stream = stream; IsAnnotation = isAnnotation; Object = obj; }

            /// <summary>For a stream, its dictionary's entries without the delimiters (the writer adds <c>/Length</c>); else the whole object.</summary>
            internal string Dictionary { get; }
            internal byte[] Stream { get; }
            internal bool IsAnnotation { get; }
            /// <summary>A built object, written by <see cref="PdfWriter"/>; the two texts above are null then.</summary>
            internal PdfObject Object { get; }

            internal static ExtraObject Annotation(string dictionary) => new ExtraObject(dictionary, null, true);
            internal static ExtraObject StreamObject(string entries, byte[] data) => new ExtraObject(entries, data, false);
            /// <summary>A dictionary object, written as given: the whole object between <c>obj</c> and <c>endobj</c>.</summary>
            internal static ExtraObject Plain(string dictionary) => new ExtraObject(dictionary, null, false);
            /// <summary>
            /// An object built as a <see cref="PdfObject"/> — a <see cref="PdfStream"/> with its
            /// data, or a dictionary — written as <see cref="PdfWriter.WriteIndirectObject(int, int, PdfObject)"/>
            /// writes it. For an object graph another builder makes (<see cref="Type0FontBuilder"/>), whose
            /// objects would otherwise have to be serialised to text by hand.
            /// </summary>
            internal static ExtraObject Built(PdfObject obj) => new ExtraObject(null, null, false, obj);
        }

        /// <summary>
        /// The page's <c>/Resources</c> dictionary: Helvetica as <c>/F1</c> and <paramref name="fonts"/>
        /// after it, then the <c>/ExtGState</c> and <c>/XObject</c> entries when there are any. The page
        /// writes it, and a caller whose Form XObject draws with the page's resources writes the same
        /// text as an object of its own, so the two cannot differ.
        /// </summary>
        internal static string Resources(string fontEncoding, string extGStates, string xObjects, string fonts, string patterns = null)
        {
            string encodingEntry = fontEncoding == null ? "" : " /Encoding " + fontEncoding;
            string stateEntry = string.IsNullOrEmpty(extGStates) ? "" : " /ExtGState << " + extGStates + " >>";
            string xObjectEntry = string.IsNullOrEmpty(xObjects) ? "" : " /XObject << " + xObjects + " >>";
            string fontEntries = string.IsNullOrEmpty(fonts) ? "" : " " + fonts;
            string patternEntry = string.IsNullOrEmpty(patterns) ? "" : " /Pattern << " + patterns + " >>";
            return $"<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica{encodingEntry} >>{fontEntries} >>{stateEntry}{xObjectEntry}{patternEntry} >>";
        }

        /// <summary>
        /// <see cref="Build(string, string, string, string)"/>, with objects of its own after the
        /// skeleton's four — numbered from 5 in the order given, so a caller knows each one's number —
        /// and an <c>/XObject</c> resource naming the ones the content draws.
        /// </summary>
        /// <param name="contentStream">Page content stream text; encoded as Latin-1.</param>
        /// <param name="mediaBox">The <c>/MediaBox</c> array, already formatted.</param>
        /// <param name="fontEncoding">The Helvetica resource's <c>/Encoding</c>, or <c>null</c>.</param>
        /// <param name="extGStates">The page's <c>/ExtGState</c> entries, or <c>null</c>.</param>
        /// <param name="xObjects">The page's <c>/XObject</c> entries — <c>/Im0 5 0 R</c> — or <c>null</c>.</param>
        /// <param name="objects">
        /// The objects, or <c>null</c> for none. An annotation is listed in the page's <c>/Annots</c>, since
        /// an annotation is an indirect object (ISO 32000-1 Table 30). None writes the file byte for byte
        /// as the four-argument form does.
        /// </param>
        /// <param name="fonts">
        /// The page's <c>/Font</c> entries after Helvetica's <c>/F1</c> — <c>/F2 &lt;&lt; … &gt;&gt;</c> —
        /// or <c>null</c> for none.
        /// </param>
        /// <param name="patterns">The page's <c>/Pattern</c> entries — <c>/P0 5 0 R</c> — or <c>null</c> for none.</param>
        internal static byte[] Build(string contentStream, string mediaBox, string fontEncoding, string extGStates,
            string xObjects, IReadOnlyList<ExtraObject> objects, string fonts = null, string patterns = null)
        {
            byte[] cs = Latin1(contentStream ?? "");
            int extraCount = objects?.Count ?? 0;
            string annotsEntry = "";
            var refs = new StringBuilder();
            for (int i = 0; i < extraCount; i++)
                if (objects[i].IsAnnotation)
                    refs.Append(refs.Length == 0 ? "" : " ").Append(ObjectCount + 1 + i).Append(" 0 R");
            if (refs.Length > 0) annotsEntry = "\n   /Annots [" + refs + "]";

            // The binary comment PdfWriter.WriteHeader writes: ISO 32000 7.5.2 recommends it and
            // ISO 19005 6.1.2 requires it (% and four bytes above 127), so a one-page file can be PDF/A.
            string header1 = "%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n";
            string obj1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
            string obj2 = $"2 0 obj\n<< /Type /Pages /Kids [{PageObject} 0 R] /Count 1 >>\nendobj\n";
            string obj3 = $"{PageObject} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox}\n   /Contents 4 0 R{annotsEntry}\n   /Resources {Resources(fontEncoding, extGStates, xObjects, fonts, patterns)}\n>>\nendobj\n";
            // One `>>` each for /F1, /Font, /Resources and the page. There was a fifth: every file
            // this wrote closed a dictionary it never opened, which this library's parser tolerated.
            string obj4h = $"4 0 obj\n<< /Length {cs.Length} >>\nstream\n";
            string obj4f = "\nendstream\nendobj\n";

            var ms = new MemoryStream();
            var offsets = new List<long>();
            Write(ms, header1);
            offsets.Add(ms.Position); Write(ms, obj1);
            offsets.Add(ms.Position); Write(ms, obj2);
            offsets.Add(ms.Position); Write(ms, obj3);
            offsets.Add(ms.Position); Write(ms, obj4h);
            ms.Write(cs, 0, cs.Length);
            Write(ms, obj4f);
            PdfWriter writer = null;
            for (int i = 0; i < extraCount; i++)
            {
                ExtraObject o = objects[i];
                offsets.Add(ms.Position);
                int number = ObjectCount + 1 + i;
                if (o.Object != null)
                    (writer ??= new PdfWriter(ms)).WriteIndirectObject(number, 0, o.Object);
                else if (o.Stream == null)
                    Write(ms, $"{number} 0 obj\n{o.Dictionary}\nendobj\n");
                else
                {
                    Write(ms, $"{number} 0 obj\n<< {o.Dictionary} /Length {o.Stream.Length} >>\nstream\n");
                    ms.Write(o.Stream, 0, o.Stream.Length);
                    Write(ms, "\nendstream\nendobj\n");
                }
            }
            long xref = ms.Position;
            int size = ObjectCount + 1 + extraCount;

            var xrefTable = new StringBuilder();
            xrefTable.AppendLine("xref");
            xrefTable.AppendLine("0 " + size);
            // Append, not AppendLine: a cross-reference entry must be exactly 20 bytes including
            // its EOL, and AppendLine emits Environment.NewLine — 21 bytes on Windows.
            xrefTable.Append(PdfXrefFormat.FreeListHead);
            foreach (long offset in offsets) xrefTable.Append(PdfXrefFormat.Entry(offset));
            xrefTable.AppendLine("trailer");
            xrefTable.AppendLine("<< /Size " + size + " /Root 1 0 R >>");
            xrefTable.AppendLine("startxref");
            xrefTable.AppendLine(xref.ToString(System.Globalization.CultureInfo.InvariantCulture));
            xrefTable.Append("%%EOF");
            Write(ms, xrefTable.ToString());
            return ms.ToArray();
        }

        private static void Write(MemoryStream ms, string s)
        {
            byte[] b = Latin1(s);
            ms.Write(b, 0, b.Length);
        }

        /// <summary>
        /// Latin-1 bytes through <c>Core.Text.Latin1Text</c>, the one Latin-1 encoder. See the remarks
        /// on the type for why there is no <c>Encoding</c> here and why an unrepresentable code point
        /// becomes <c>?</c>.
        /// </summary>
        private static byte[] Latin1(string s) => Core.Text.Latin1Text.Encode(s);
    }
}
