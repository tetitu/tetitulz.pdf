// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;
using System.Xml;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Reads and writes XFDF (XML Forms Data Format, ISO 19444-1) — the interchange format a
    /// non-PDF system speaks when it sends form data to or takes it from a PDF.
    /// </summary>
    /// <remarks>
    /// <para><b>Nesting is the name.</b> A <c>&lt;field&gt;</c>'s <c>name</c> attribute is a
    /// <i>partial</i> name (§12.7.3.2 forbids a period in one), and the fully-qualified name is the
    /// ancestors' names joined by <c>.</c>. Writing therefore splits an FQN back into a tree, which
    /// is what a viewer produces and expects. Reading accepts either shape, because a flat file
    /// whose <c>name</c> already contains periods is common in the wild and unambiguous — an
    /// element with a period in its name cannot be a legal partial name, so nothing is lost by
    /// taking it as an FQN.</para>
    /// <para><b>The reader is hardened.</b> <see cref="DtdProcessing.Prohibit"/> plus a null
    /// resolver closes XXE and entity expansion (a DTD is the only way to declare an entity, so
    /// refusing the DTD refuses the billion-laughs shape with it). <b>Prohibit rather than
    /// Ignore</b>, deliberately: this is caller-supplied data and both close XXE, but they differ
    /// in whether the caller <i>learns</i> — and silently dropping part of the data a caller asked
    /// to import is exactly the failure this format exists to avoid.</para>
    /// </remarks>
    internal static class XfdfSerializer
    {
        /// <summary>The XFDF namespace. Written; not required when reading.</summary>
        internal const string Namespace = "http://ns.adobe.com/xfdf/";

        private const string RootElement = "xfdf";
        private const string FieldsElement = "fields";
        private const string FieldElement = "field";
        private const string ValueElement = "value";
        private const string FileElement = "f";
        private const string IdsElement = "ids";

        /// <summary>The XFDF section that carries annotations.</summary>
        private const string AnnotsElement = "annots";
        private const string ContentsElement = "contents";

        /// <summary>
        /// An annotation's rich text, <c>/RC</c>: the element's children are the XHTML. The name and
        /// the mapping are two independent implementations' alike;
        /// the reading — the inner markup, not its text — is the first's, as
        /// the second's writer says its own text-only form "might be wrong".
        /// </summary>
        private const string RichContentsElement = "contents-richtext";

        /// <summary>The rich text's key in the annotation dictionary.</summary>
        private const string RichContentsKey = "RC";

        private const string InkListElement = "inklist";
        private const string GestureElement = "gesture";
        private const string VerticesElement = "vertices";

        // ----------------------------------------------------------------
        //  Write
        // ----------------------------------------------------------------

        internal static void Write(FormData data, Stream output)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                // No BOM: an XFDF file is consumed by everything from a desktop viewer to a servlet, and a
                // BOM before the declaration trips some of them. The declaration names the encoding.
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                CloseOutput = false,
            };

            using var writer = XmlWriter.Create(output, settings);
            writer.WriteStartDocument();
            writer.WriteStartElement(RootElement, Namespace);

            // Field values are exchanged verbatim — leading, trailing and repeated spaces in a text
            // field are the user's data, and the default XML collapse would quietly eat them.
            writer.WriteAttributeString("space", "http://www.w3.org/XML/1998/namespace", "preserve");

            if (!string.IsNullOrEmpty(data.SourceFile))
            {
                writer.WriteStartElement(FileElement, Namespace);
                writer.WriteAttributeString("href", Legal(data.SourceFile));
                writer.WriteEndElement();
            }

            if (!string.IsNullOrEmpty(data.OriginalId) || !string.IsNullOrEmpty(data.ModifiedId))
            {
                writer.WriteStartElement(IdsElement, Namespace);
                if (!string.IsNullOrEmpty(data.OriginalId))
                    writer.WriteAttributeString("original", Legal(data.OriginalId));
                if (!string.IsNullOrEmpty(data.ModifiedId))
                    writer.WriteAttributeString("modified", Legal(data.ModifiedId));
                writer.WriteEndElement();
            }

            writer.WriteStartElement(FieldsElement, Namespace);
            WriteFieldTree(writer, BuildTree(data.Fields));
            writer.WriteEndElement();

            // Only when there is something to say: an empty <annots/> is a claim that a review
            // found nothing, which is not what a fields-only file means.
            if (data.Annotations.Count > 0) WriteAnnotations(writer, data.Annotations);

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        /// <summary>One node of the partial-name tree an FQN list expands into.</summary>
        private sealed class Node
        {
            internal string PartialName;
            internal IReadOnlyList<string> Values;
            internal readonly List<Node> Children = new List<Node>();
            internal readonly Dictionary<string, Node> ByName = new Dictionary<string, Node>(StringComparer.Ordinal);
        }

        private static Node BuildTree(IEnumerable<FormDataField> fields)
        {
            var root = new Node();
            foreach (var field in fields)
            {
                if (field?.Name == null) continue;

                string[] parts = field.Name.Split('.');
                Node node = root;
                foreach (string part in parts)
                {
                    if (!node.ByName.TryGetValue(part, out Node child))
                    {
                        child = new Node { PartialName = part };
                        node.ByName[part] = child;
                        node.Children.Add(child);
                    }
                    node = child;
                }
                // A repeated name overwrites, matching FormData.ToDictionary's last-one-wins rule.
                node.Values = field.Values;
            }
            return root;
        }

        private static void WriteFieldTree(XmlWriter writer, Node node)
        {
            foreach (Node child in node.Children)
            {
                writer.WriteStartElement(FieldElement, Namespace);
                writer.WriteAttributeString("name", Legal(child.PartialName));

                if (child.Values != null)
                    foreach (string value in child.Values)
                        writer.WriteElementString(ValueElement, Namespace, Legal(value ?? string.Empty));

                WriteFieldTree(writer, child);
                writer.WriteEndElement();
            }
        }

        // ----------------------------------------------------------------
        //  Read
        // ----------------------------------------------------------------

        internal static FormData Read(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = false,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            var data = new FormData();
            try
            {
                using var reader = XmlReader.Create(input, settings);
                ReadDocument(reader, data);
            }
            catch (XmlException ex)
            {
                throw new PdfException("The XFDF file could not be read: " + ex.Message, ex);
            }
            return data;
        }

        private static void ReadDocument(XmlReader reader, FormData data)
        {
            // The name stack is the fully-qualified name being built; its depth is the element
            // nesting, so capping it caps recursion without a recursive reader.
            var names = new List<string>();
            var values = new List<string>();
            bool collectingValue = false;
            var valueText = new StringBuilder();
            int fieldCount = 0;

            bool insideAnnots = false;

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (reader.LocalName == FieldElement)
                        {
                            bool empty = reader.IsEmptyElement;
                            string partial = reader.GetAttribute("name") ?? string.Empty;

                            if (names.Count >= PdfLimits.MaxTreeDepth)
                                throw new PdfException(
                                    $"The XFDF file nests <field> elements more than {PdfLimits.MaxTreeDepth} " +
                                    "deep, which no field tree does; refusing to read it.");

                            // A field element ends the values of the field enclosing it: the values
                            // belong to whichever <field> was open when they were read.
                            Flush(data, names, values, ref fieldCount);

                            names.Add(partial);
                            if (empty)
                            {
                                Flush(data, names, values, ref fieldCount);
                                names.RemoveAt(names.Count - 1);
                            }
                        }
                        else if (reader.LocalName == ValueElement && names.Count > 0)
                        {
                            if (reader.IsEmptyElement) values.Add(string.Empty);
                            else { collectingValue = true; valueText.Clear(); }
                        }
                        else if (reader.LocalName == FileElement)
                        {
                            data.SourceFile = reader.GetAttribute("href") ?? data.SourceFile;
                        }
                        else if (reader.LocalName == IdsElement)
                        {
                            data.OriginalId = reader.GetAttribute("original") ?? data.OriginalId;
                            data.ModifiedId = reader.GetAttribute("modified") ?? data.ModifiedId;
                        }
                        else if (reader.LocalName == AnnotsElement)
                        {
                            insideAnnots = !reader.IsEmptyElement;
                        }
                        else if (insideAnnots)
                        {
                            if (data.Annotations.Count >= PdfLimits.MaxFormDataFields)
                                throw new PdfException(
                                    $"The XFDF file carries more than {PdfLimits.MaxFormDataFields} " +
                                    "annotations; refusing to read it.");

                            data.Annotations.Add(ReadAnnotation(reader));
                        }
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        if (collectingValue) valueText.Append(reader.Value);
                        break;

                    case XmlNodeType.EndElement:
                        if (reader.LocalName == ValueElement && collectingValue)
                        {
                            values.Add(valueText.ToString());
                            collectingValue = false;
                        }
                        else if (reader.LocalName == FieldElement && names.Count > 0)
                        {
                            Flush(data, names, values, ref fieldCount);
                            names.RemoveAt(names.Count - 1);
                        }
                        else if (reader.LocalName == AnnotsElement)
                        {
                            insideAnnots = false;
                        }
                        break;
                }
            }

        }

        /// <summary>
        /// Emits the field currently on the name stack, if it collected any values, and clears the
        /// value buffer. Called both when a nested field opens and when the field closes, so a
        /// field with children and a value of its own — legal, and what a text field inside a group
        /// looks like — keeps both.
        /// </summary>
        private static void Flush(FormData data, List<string> names, List<string> values, ref int fieldCount)
        {
            if (values.Count == 0 || names.Count == 0) { values.Clear(); return; }

            if (fieldCount >= PdfLimits.MaxFormDataFields)
                throw new PdfException(
                    $"The XFDF file declares more than {PdfLimits.MaxFormDataFields} fields; refusing to read it.");

            // Through the one join, so an empty name="" adds no segment: the form reader lists the
            // field as a.b, and a string.Join here called it a..b.
            string name = null;
            foreach (string partial in names) name = Core.Forms.FieldNames.Qualify(name, partial);

            data.Fields.Add(new FormDataField(name ?? string.Empty, values));
            fieldCount++;
            values.Clear();
        }
        // ----------------------------------------------------------------
        //  Annotations
        // ----------------------------------------------------------------

        /// <summary>
        /// Text made legal in XML (<see cref="Core.Text.XmlLegalText"/>): a value or name holding a
        /// character XML 1.0 forbids made XmlWriter throw an ArgumentException, which no door catches.
        /// </summary>
        private static string Legal(string s) => s == null ? null : Core.Text.XmlLegalText.Replace(s, out _);

        /// <summary>
        /// An annotation's element name, its subtype in lower case — refused when that is not an XML
        /// name (<c>3D</c> starts with a digit), which XmlWriter would refuse with an ArgumentException.
        /// </summary>
        private static string ElementNameOf(string subtype)
        {
            string name = subtype.ToLowerInvariant();
            try { XmlConvert.VerifyName(name); }
            catch (XmlException)
            {
                throw new PdfException(
                    "An annotation of subtype /" + subtype + " cannot be written to XFDF: its element name would be '" +
                    name + "', which is not an XML name.");
            }
            return name;
        }

        /// <summary>
        /// Writes the <c>&lt;annots&gt;</c> section: one element per annotation, named for its
        /// subtype in lower case.
        /// </summary>
        /// <remarks>
        /// <para><b>The element name IS the subtype, lower-cased</b> — <c>highlight</c>,
        /// <c>freetext</c>, <c>strikeout</c>. There is no attribute carrying it, which is why an
        /// unknown subtype still round-trips: it becomes an element nobody else recognises rather
        /// than an attribute value nobody else reads.</para>
        ///
        /// <para><b>Colour is <c>#RRGGBB</c> and that is the format's limit, not this writer's.</b>
        /// A grey <c>/C</c> converts exactly; a CMYK one does not, and there is nowhere in XFDF to
        /// put the fourth component. <b>FDF carries the array losslessly</b>, so a caller who needs
        /// CMYK fidelity has a format that keeps it — which is a better answer than inventing an
        /// attribute a viewer would ignore.</para>
        ///
        /// <para><b>Geometry is comma-separated and never re-ordered.</b> <c>coords</c> is
        /// <c>/QuadPoints</c> in §12.5.6.10's corner order, which is not the order a rectangle is
        /// usually written in; passing it through untouched is what keeps a highlight on the words
        /// it was drawn over.</para>
        /// </remarks>
        private static void WriteAnnotations(XmlWriter writer, IEnumerable<FormDataAnnotation> annotations)
        {
            writer.WriteStartElement(AnnotsElement, Namespace);

            foreach (var a in annotations)
            {
                // Only the null test: FormDataAnnotation's constructor refuses a null or empty
                // subtype and the property is get-only, so an entry that exists always has one.
                // A mutation sweep found the second clause unreachable; the FDF writer beside this
                // one tests null alone for the same reason.
                if (a == null) continue;

                writer.WriteStartElement(ElementNameOf(a.Subtype), Namespace);

                writer.WriteAttributeString("page", Legal(a.PageIndex.ToString(CultureInfo.InvariantCulture)));
                WriteNumbers(writer, "rect", a.Rect);
                WriteColor(writer, "color", a.Color);
                WriteColor(writer, "interior-color", a.InteriorColor);

                if (a.Title != null) writer.WriteAttributeString("title", Legal(a.Title));
                if (a.Subject != null) writer.WriteAttributeString("subject", Legal(a.Subject));
                if (a.Name != null) writer.WriteAttributeString("name", Legal(a.Name));
                if (a.ModifiedDate != null) writer.WriteAttributeString("date", Legal(a.ModifiedDate));
                if (a.CreationDate != null) writer.WriteAttributeString("creationdate", Legal(a.CreationDate));

                if (a.Opacity.HasValue)
                    writer.WriteAttributeString("opacity", Legal(Num(a.Opacity.Value)));
                if (a.Flags.HasValue)
                    writer.WriteAttributeString("flags", Legal(a.Flags.Value.ToString(CultureInfo.InvariantCulture)));
                if (a.BorderWidth.HasValue)
                    writer.WriteAttributeString("width", Legal(Num(a.BorderWidth.Value)));

                WriteNumbers(writer, "coords", a.QuadPoints);

                if (a.Line != null && a.Line.Length == 4)
                {
                    writer.WriteAttributeString("start", Legal(Num(a.Line[0]) + "," + Num(a.Line[1])));
                    writer.WriteAttributeString("end", Legal(Num(a.Line[2]) + "," + Num(a.Line[3])));
                }
                WriteEndingsAndIntent(writer, a);

                // VERTICES IS A CHILD ELEMENT, NOT AN ATTRIBUTE, AND THAT IS NOT SYMMETRIC WITH
                // coords. Text-markup quad points ride on the `coords` attribute; a polygon's
                // vertices do not, and an independent reader refuses the whole annotation with "missing
                // element 'vertices'" when they are written as one. The asymmetry is the format's,
                // and it was found by asking an independent reader to read what this writes rather than by
                // reasoning about what looked consistent.
                if (a.Vertices != null && a.Vertices.Length > 0)
                {
                    writer.WriteStartElement(VerticesElement, Namespace);
                    writer.WriteString(Legal(Join(a.Vertices)));
                    writer.WriteEndElement();
                }

                if (a.Contents != null)
                {
                    writer.WriteStartElement(ContentsElement, Namespace);
                    writer.WriteString(Legal(a.Contents));
                    writer.WriteEndElement();
                }

                // The rich text goes in as markup, so it must be markup: a value that is not
                // well-formed XML would make the whole file unreadable, and is left out instead
                // (its plain text is in <contents> above).
                if (a.Carried.TryGetValue(RichContentsKey, out var rc)
                    && rc is Tetitulz.Pdf.Core.Objects.PdfString richString
                    && IsWellFormedFragment(richString.GetText()))
                {
                    writer.WriteStartElement(RichContentsElement, Namespace);
                    writer.WriteRaw(richString.GetText());
                    writer.WriteEndElement();
                }

                if (a.InkList.Count > 0)
                {
                    writer.WriteStartElement(InkListElement, Namespace);
                    foreach (double[] stroke in a.InkList)
                    {
                        if (stroke == null) continue;
                        writer.WriteStartElement(GestureElement, Namespace);
                        writer.WriteString(Legal(Join(stroke)));
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        private const string LineEndingsKey = "LE";
        private const string IntentKey = "IT";

        /// <summary>
        /// The two XFDF intents whose names are not the PDF names they stand for (XFDF 2.0, the
        /// polygon and polyline attributes); every other intent is its PDF name.
        /// </summary>
        private static readonly (string Xfdf, string Pdf)[] RenamedIntents =
        {
            ("polygon-dimension", "PolygonDimension"),
            ("polyline-dimension", "PolyLineDimension"),
        };

        /// <summary>
        /// A line's or a polyline's <c>/LE</c> as its <c>head</c> and <c>tail</c> attributes, and a line's,
        /// polygon's, polyline's or free text's <c>/IT</c> as its <c>intent</c> — XFDF 2.0's attributes
        /// (Adobe's specification, the line, polygon/polyline and freetext attribute tables; Apache
        /// independent readers read the same names). They rode an FDF
        /// export as carried entries, and an XFDF one carries them too.
        /// </summary>
        private static void WriteEndingsAndIntent(XmlWriter writer, FormDataAnnotation a)
        {
            string element = a.Subtype.ToLowerInvariant();
            if ((element == "line" || element == "polyline")
                && a.Carried.TryGetValue(LineEndingsKey, out var le)
                && le is Tetitulz.Pdf.Core.Objects.PdfArray ends && ends.Count == 2)
            {
                if (ends[0] is Tetitulz.Pdf.Core.Objects.PdfName head) writer.WriteAttributeString("head", Legal(head.Value));
                if (ends[1] is Tetitulz.Pdf.Core.Objects.PdfName tail) writer.WriteAttributeString("tail", Legal(tail.Value));
            }
            if ((element == "line" || element == "polygon" || element == "polyline" || element == "freetext")
                && a.Carried.TryGetValue(IntentKey, out var it) && it is Tetitulz.Pdf.Core.Objects.PdfName intent)
            {
                string value = intent.Value;
                foreach (var (xfdf, pdf) in RenamedIntents)
                    if (value == pdf) value = xfdf;
                writer.WriteAttributeString("intent", Legal(value));
            }
        }

        /// <summary>
        /// <see cref="WriteEndingsAndIntent"/>'s attributes read back into <c>/LE</c> (an ending left out
        /// is <c>None</c>, the specification's default) and <c>/IT</c>, as carried entries.
        /// </summary>
        private static void ReadEndingsAndIntent(XmlReader reader, FormDataAnnotation a)
        {
            string element = a.Subtype.ToLowerInvariant();
            if (element == "line" || element == "polyline")
            {
                string head = reader.GetAttribute("head"), tail = reader.GetAttribute("tail");
                if (!string.IsNullOrEmpty(head) || !string.IsNullOrEmpty(tail))
                {
                    var ends = new Tetitulz.Pdf.Core.Objects.PdfArray();
                    ends.Add(new Tetitulz.Pdf.Core.Objects.PdfName(string.IsNullOrEmpty(head) ? "None" : head));
                    ends.Add(new Tetitulz.Pdf.Core.Objects.PdfName(string.IsNullOrEmpty(tail) ? "None" : tail));
                    a.Carried[LineEndingsKey] = ends;
                    a.Extras[LineEndingsKey] = AnnotationDictionaryMap.PdfWriterText.Serialize(ends);
                }
            }
            string intentValue = reader.GetAttribute("intent");
            if (!string.IsNullOrEmpty(intentValue)
                && (element == "line" || element == "polygon" || element == "polyline" || element == "freetext"))
            {
                foreach (var (xfdf, pdf) in RenamedIntents)
                    if (intentValue == xfdf) intentValue = pdf;
                var intent = new Tetitulz.Pdf.Core.Objects.PdfName(intentValue);
                a.Carried[IntentKey] = intent;
                a.Extras[IntentKey] = AnnotationDictionaryMap.PdfWriterText.Serialize(intent);
            }
        }

        /// <summary>
        /// Reads one annotation element, positioned on its start tag.
        /// </summary>
        /// <remarks>
        /// <b>Through a subtree reader, so the caller's loop does not have to know the shape.</b>
        /// An annotation's children (<c>contents</c>, <c>inklist</c>) are shallow but not uniform,
        /// and threading them through the outer streaming switch would put every annotation's state
        /// into the field walk's variables — where a malformed file would leave them set.
        /// </remarks>
        private static FormDataAnnotation ReadAnnotation(XmlReader reader)
        {
            var a = new FormDataAnnotation(CanonicalSubtype(reader.LocalName));

            if (int.TryParse(reader.GetAttribute("page"), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int page))
                a.PageIndex = page;

            a.Rect = ParseNumbers(reader.GetAttribute("rect"));
            a.Color = ParseColor(reader.GetAttribute("color"));
            a.InteriorColor = ParseColor(reader.GetAttribute("interior-color"));
            a.QuadPoints = ParseNumbers(reader.GetAttribute("coords"));

            // Read from the attribute too, though nothing conformant writes one: a lenient read
            // costs a line and an unread attribute costs the whole annotation's geometry.
            a.Vertices = ParseNumbers(reader.GetAttribute("vertices"));

            a.Title = reader.GetAttribute("title");
            a.Subject = reader.GetAttribute("subject");
            a.Name = reader.GetAttribute("name");
            a.ModifiedDate = reader.GetAttribute("date");
            a.CreationDate = reader.GetAttribute("creationdate");

            if (TryParseFinite(reader.GetAttribute("opacity"), out double opacity))
                a.Opacity = opacity;
            if (int.TryParse(reader.GetAttribute("flags"), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int flags))
                a.Flags = flags;
            if (TryParseFinite(reader.GetAttribute("width"), out double width))
                a.BorderWidth = width;

            double[] start = ParseNumbers(reader.GetAttribute("start"));
            double[] end = ParseNumbers(reader.GetAttribute("end"));
            if (start != null && start.Length == 2 && end != null && end.Length == 2)
                a.Line = new[] { start[0], start[1], end[0], end[1] };
            ReadEndingsAndIntent(reader, a);

            if (reader.IsEmptyElement) return a;

            string richText = null;
            using (XmlReader sub = reader.ReadSubtree())
            {
                sub.Read();   // the element itself
                sub.Read();

                // A branch that reads an element's content leaves the reader on the node after it, so
                // only a node nothing read is stepped past. Stepping past every node — `while
                // (sub.Read())` — skipped whatever followed a read element at once: in a compact file
                // with no white space between them, the <vertices> after a <contents> and every second
                // <gesture> of an <inklist> (this library's own writer indents, so a round trip
                // cannot show it).
                while (!sub.EOF)
                {
                    if (sub.NodeType != XmlNodeType.Element) { sub.Read(); continue; }

                    if (sub.LocalName == ContentsElement)
                    {
                        a.Contents = sub.ReadElementContentAsString();
                    }
                    else if (sub.LocalName == RichContentsElement)
                    {
                        string markup = sub.ReadInnerXml();
                        if (markup.Trim().Length > 0)
                        {
                            a.Carried[RichContentsKey] = new Tetitulz.Pdf.Core.Objects.PdfString(markup);
                            a.Extras[RichContentsKey] = "(" + markup + ")";
                            richText = TextOf(markup);
                        }
                    }
                    else if (sub.LocalName == GestureElement)
                    {
                        double[] stroke = ParseNumbers(sub.ReadElementContentAsString());
                        if (stroke != null) a.InkList.Add(stroke);
                    }
                    else if (sub.LocalName == VerticesElement)
                    {
                        a.Vertices = ParseNumbers(sub.ReadElementContentAsString()) ?? a.Vertices;
                    }
                    else
                    {
                        sub.Read();   // into an element this reads nothing of — <inklist>, <popup>
                    }
                }
            }

            // A file with rich text and no plain <contents> still gives a viewer that does not draw
            // /RC something to show: the rich text's own text, as independent readers give it.
            if (a.Contents == null && richText != null) a.Contents = richText;

            return a;
        }

        /// <summary>The text of an XHTML fragment, its whitespace runs collapsed; null when it does not parse.</summary>
        private static string TextOf(string markup)
        {
            if (!TryReadFragment(markup, out string text)) return null;
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Whether <paramref name="markup"/> is well-formed as element content, read under the same hardening.</summary>
        private static bool IsWellFormedFragment(string markup) => TryReadFragment(markup, out _);

        private static bool TryReadFragment(string markup, out string text)
        {
            text = null;
            if (markup == null) return false;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ConformanceLevel = ConformanceLevel.Fragment,
            };
            var sb = new StringBuilder();
            try
            {
                using (var reader = XmlReader.Create(new StringReader(markup), settings))
                    while (reader.Read())
                        if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA
                            || reader.NodeType == XmlNodeType.SignificantWhitespace || reader.NodeType == XmlNodeType.Whitespace)
                            sb.Append(reader.Value);
            }
            catch (XmlException)
            {
                return false;
            }
            text = sb.ToString();
            return true;
        }

        /// <summary>
        /// The §12.5.6 subtype an XFDF element name stands for — <c>highlight</c> is
        /// <c>Highlight</c>, <c>freetext</c> is <c>FreeText</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The element name is lower case and a PDF subtype is not, and the difference is
        /// not a capital letter.</b> <c>FreeText</c>, <c>PolyLine</c>, <c>StrikeOut</c>,
        /// <c>FileAttachment</c>, <c>PrinterMark</c>, <c>TrapNet</c> and <c>RichMedia</c> all carry
        /// an interior capital that no case rule recovers, so this is a table rather than a
        /// <c>ToTitleCase</c>.</para>
        ///
        /// <para><b>Reading the element name verbatim was a data-destroying bug and it round-tripped
        /// silently.</b> A <c>&lt;highlight&gt;</c> came back as subtype <c>highlight</c>, which
        /// wrote <c>/Subtype /highlight</c> into the PDF — a name no reader matches, so the
        /// annotation is present, well-formed and invisible. It was found by converting an independent implementation's
        /// FDF to XFDF and back and diffing against the original, not by reading this code.</para>
        ///
        /// <para><b>An unrecognised name is returned unchanged</b>, so a subtype from a later
        /// specification survives the round trip in whatever case it arrived in. That is better
        /// than dropping it and better than guessing a capitalisation.</para>
        /// </remarks>
        private static string CanonicalSubtype(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return elementName;

            foreach (string known in KnownSubtypes)
                if (string.Equals(known, elementName, StringComparison.OrdinalIgnoreCase))
                    return known;

            return elementName;
        }

        /// <summary>Every §12.5.6 subtype, which is the set an XFDF element name can name.</summary>
        private static readonly string[] KnownSubtypes =
        {
            PdfAnnotationSubtypes.Text, PdfAnnotationSubtypes.Link, PdfAnnotationSubtypes.FreeText,
            PdfAnnotationSubtypes.Line, PdfAnnotationSubtypes.Square, PdfAnnotationSubtypes.Circle,
            PdfAnnotationSubtypes.Polygon, PdfAnnotationSubtypes.PolyLine,
            PdfAnnotationSubtypes.Highlight, PdfAnnotationSubtypes.Underline,
            PdfAnnotationSubtypes.Squiggly, PdfAnnotationSubtypes.StrikeOut,
            PdfAnnotationSubtypes.Stamp, PdfAnnotationSubtypes.Caret, PdfAnnotationSubtypes.Ink,
            PdfAnnotationSubtypes.Popup, PdfAnnotationSubtypes.FileAttachment,
            PdfAnnotationSubtypes.Sound, PdfAnnotationSubtypes.Movie, PdfAnnotationSubtypes.Widget,
            PdfAnnotationSubtypes.Screen, PdfAnnotationSubtypes.PrinterMark,
            PdfAnnotationSubtypes.TrapNet, PdfAnnotationSubtypes.Watermark,
            PdfAnnotationSubtypes.ThreeD, PdfAnnotationSubtypes.Redact,
            PdfAnnotationSubtypes.Projection, PdfAnnotationSubtypes.RichMedia,
        };

        private static void WriteNumbers(XmlWriter writer, string attribute, double[] values)
        {
            if (values == null || values.Length == 0) return;
            writer.WriteAttributeString(attribute, Join(values));
        }

        private static string Join(double[] values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Num(values[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// A number in the invariant culture, round-trippable.
        /// </summary>
        /// <remarks>
        /// <c>R</c> rather than a fixed number of decimals: a coordinate that arrived as
        /// <c>101.99999</c> is what the producing viewer computed, and rounding it here moves an
        /// annotation by a hair every time the file is opened and saved.
        /// </remarks>
        private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Splits a comma- or semicolon-separated coordinate list.</summary>
        /// <remarks>
        /// <b>Semicolons are accepted because producers emit them</b> in <c>gesture</c> content,
        /// where the pairs are sometimes separated by <c>;</c> and the components by <c>,</c>.
        /// Treating both as the same separator reads either shape into the flat <c>x y x y</c>
        /// array the PDF dictionary wants, which is what both spellings mean.
        /// </remarks>
        private static double[] ParseNumbers(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!TryParseFinite(parts[i], out values[i]))
                    return null;

            return values;
        }

        /// <summary>
        /// A number the file wrote, refused when it is NaN or infinite. <see cref="NumberStyles.Float"/>
        /// reads both symbols, and a non-finite value reached the annotation's dictionary, where the
        /// writer puts it as 0: a degenerate <c>/Rect</c>, a <c>/CA</c> of 0, reported as an import.
        /// </summary>
        private static bool TryParseFinite(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Writes a colour as <c>#RRGGBB</c>; see the section remarks for CMYK.</summary>
        private static void WriteColor(XmlWriter writer, string attribute, double[] color)
        {
            if (color == null || color.Length == 0) return;

            double r, g, b;
            switch (color.Length)
            {
                case 1: r = g = b = color[0]; break;
                case 3: r = color[0]; g = color[1]; b = color[2]; break;
                case 4:
                    // §10.4.2.3's conversion. Lossy, and the only thing XFDF can express.
                    r = (1 - color[0]) * (1 - color[3]);
                    g = (1 - color[1]) * (1 - color[3]);
                    b = (1 - color[2]) * (1 - color[3]);
                    break;
                default: return;
            }

            writer.WriteAttributeString(
                attribute, "#" + Channel(r) + Channel(g) + Channel(b));
        }

        private static string Channel(double v)
        {
            int i = SaturatingCast.ToInt(Math.Round(Math.Max(0, Math.Min(1, v)) * 255));
            return i.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>Parses <c>#RRGGBB</c> into three components; null for anything else.</summary>
        private static double[] ParseColor(string text)
        {
            if (text == null || text.Length != 7 || text[0] != '#') return null;

            var rgb = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!TryHex(text[1 + i * 2], out int hi) || !TryHex(text[2 + i * 2], out int lo))
                    return null;
                rgb[i] = (hi * 16 + lo) / 255.0;
            }
            return rgb;
        }

        private static bool TryHex(char c, out int value) => FdfSerializer.TryHex(c, out value);

    }
}
