// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Xml;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// What a document's scripts read of the document — its page count, its fields, its
    /// <c>/Info</c> and authors, its annotations, its optional content groups, its page words and
    /// its attachments — handed to an execution context the one way, for
    /// <c>ExecuteDocumentScripts</c> and the field-commit operations alike.
    /// </summary>
    internal static class PdfJsDocumentFacts
    {
        /// <summary>
        /// Describes <paramref name="doc"/> to <paramref name="ctx"/>; a field's value is set only
        /// where the caller set none. The page words are the caller's to supply
        /// (<see cref="PageWordsReader"/>), since they are read when a script asks, after this returns.
        /// </summary>
        internal static void Describe(PdfDocument doc, PdfJsExecutionContext ctx)
        {
            ctx.NumPages = doc.Pages.Count;
            foreach (PdfFormFieldInfo field in PdfFormReader.Read(doc))
            {
                if (string.IsNullOrEmpty(field.FullyQualifiedName)) continue;
                ctx.FieldDescriptions[field.FullyQualifiedName] = field;
                // A multi-select list box's selections travel joined, as the filler takes them.
                if (!ctx.FieldValues.ContainsKey(field.FullyQualifiedName))
                    ctx.FieldValues[field.FullyQualifiedName] = field.IsMultiSelect
                        ? string.Join(PdfFormFiller.MultiSelectSeparator, field.SelectedValues)
                        : field.Value ?? string.Empty;
            }

            if (doc.ResolveObject(doc.Trailer?[PdfName.Info]) is PdfDictionary info)
                foreach (var entry in info)
                    if (doc.ResolveObject(entry.Value) is PdfString text)
                        ctx.DocumentInfo[entry.Key.Value] = text.GetText();

            // Acrobat's info.Authors is the XMP packet's dc:creator list, and its custom keys the
            // packet's pdfx properties; a document with no packet gets its /Info /Author as the one
            // author (the key is absent when neither names one).
            ctx.Authors.Clear();
            ctx.Metadata = null;
            ReadPacket(doc, ctx);
            if (ctx.Authors.Count == 0 && ctx.DocumentInfo.TryGetValue("Author", out string author)
                && !string.IsNullOrEmpty(author))
                ctx.Authors.Add(author);

            ctx.Pages.Clear();
            for (int i = 0; i < doc.Pages.Count; i++) ctx.Pages.Add(PageInfoOf(doc, doc.Pages[i]));

            ctx.Annotations.Clear();
            ctx.Annotations.AddRange(PdfAnnotationExtractor.Collect(doc));
            ctx.OptionalContentGroups.Clear();
            ctx.OptionalContentGroups.AddRange(PdfOcgManager.CollectOcgs(doc));

            ctx.Attachments.Clear();
            ctx.AttachmentContents.Clear();
            // Attachments reach a script only when the caller asked: off, none is listed or decoded.
            if (!ctx.ExposeAttachments) return;
            ctx.Attachments.AddRange(PdfAttachmentManager.CollectAttachments(doc));

            // The contents a script may read, decoded under the bounds: the document chooses what
            // its attachments inflate to, and every script of it may ask. One walk finds every
            // attachment's stream; a decode that does not fit is charged the bound it ran to, since
            // that was work all the same, and a document of a thousand bombs would otherwise cost a
            // thousand bounds.
            Dictionary<string, PdfStream> streams = PdfAttachmentManager.EmbeddedStreamsByName(doc);
            long left = MaxAttachmentBytesPerDocument;
            foreach (PdfAttachmentInfo a in ctx.Attachments)
            {
                if (a.Filename == null || ctx.AttachmentContents.ContainsKey(a.Filename)) continue;
                long bound = Math.Min(MaxAttachmentBytes, left);
                if (bound <= 0) break;
                if (!streams.TryGetValue(a.Filename, out PdfStream stream) || stream == null) continue;
                if (stream.TryGetDecodedDataUncached(bound, out byte[] data, out _) && data != null)
                {
                    ctx.AttachmentContents[a.Filename] = data;
                    left -= data.Length;
                }
                else left -= bound;
            }
        }

        /// <summary>The largest attachment whose contents a script is handed.</summary>
        internal const long MaxAttachmentBytes = 1024 * 1024;

        /// <summary>
        /// The contents of one document's attachments a script is handed, together — and the
        /// decoding spent on them, a decode that does not fit charged the bound it ran to.
        /// </summary>
        internal const long MaxAttachmentBytesPerDocument = 4 * 1024 * 1024;

        /// <summary>The most authors read from a packet's <c>dc:creator</c> list.</summary>
        internal const int MaxAuthors = 256;

        /// <summary>The most <c>pdfx:</c> properties read from a packet into <c>info</c>.</summary>
        internal const int MaxCustomProperties = 64;

        /// <summary>
        /// Reads the catalog's XMP packet into <paramref name="ctx"/>: its text (<c>metadata</c>), the
        /// authors <c>dc:creator</c> lists — its items in order (a sequence, a bag or an alternative
        /// list), its one text, or the attribute form — at most <see cref="MaxAuthors"/>, and the
        /// <c>pdfx:</c> custom properties, each filling an <c>info</c> key the <c>/Info</c> lacks, at
        /// most <see cref="MaxCustomProperties"/>. Nothing when the document has no packet this
        /// reader accepts (<see cref="XmpPacketDom"/>'s ceilings and its hardened load).
        /// </summary>
        internal static void ReadPacket(PdfDocument doc, PdfJsExecutionContext ctx)
        {
            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;
            string packet = XmpPacketDom.ReadCatalogPacket(doc, catalog);
            if (packet == null || !XmpPacketDom.IsWithinMergeBudget(packet)) return;
            ctx.Metadata = packet;
            XmlDocument xml = XmpPacketDom.TryLoad(packet);
            if (xml?.DocumentElement == null) return;

            ctx.Authors.AddRange(XmpAuthors(xml.DocumentElement));

            // The custom properties sit on an rdf:Description, as elements or as attributes.
            XmlElement rdf = XmpPacketDom.FindElement(xml.DocumentElement, XmpPacket.RdfNamespace, "RDF");
            if (rdf == null) return;
            int custom = 0;
            for (XmlNode d = rdf.FirstChild; d != null && custom < MaxCustomProperties; d = d.NextSibling)
            {
                if (!(d is XmlElement description) || description.NamespaceURI != XmpPacket.RdfNamespace || description.LocalName != "Description") continue;
                foreach (XmlAttribute attribute in description.Attributes)
                    if (attribute.NamespaceURI == XmpPacket.PdfxNamespace && custom < MaxCustomProperties
                        && !ctx.DocumentInfo.ContainsKey(attribute.LocalName))
                    {
                        ctx.DocumentInfo[attribute.LocalName] = attribute.Value;
                        custom++;
                    }
                for (XmlNode p = description.FirstChild; p != null && custom < MaxCustomProperties; p = p.NextSibling)
                    if (p is XmlElement property && property.NamespaceURI == XmpPacket.PdfxNamespace
                        && !ctx.DocumentInfo.ContainsKey(property.LocalName))
                    {
                        ctx.DocumentInfo[property.LocalName] = property.InnerText.Trim();
                        custom++;
                    }
            }
        }

        /// <summary>The authors a packet's <c>dc:creator</c> names, at most <see cref="MaxAuthors"/>.</summary>
        private static List<string> XmpAuthors(XmlElement root)
        {
            var authors = new List<string>();
            XmlElement creator = XmpPacketDom.FindElement(root, XmpPacket.DublinCoreNamespace, "creator");
            if (creator == null)
            {
                XmlAttribute attribute = XmpPacketDom.FindAttribute(root, XmpPacket.DublinCoreNamespace, "creator");
                string one = attribute?.Value.Trim();
                if (!string.IsNullOrEmpty(one)) authors.Add(one);
                return authors;
            }

            XmlElement list = null;
            for (XmlNode child = creator.FirstChild; child != null && list == null; child = child.NextSibling)
                if (child is XmlElement e && e.NamespaceURI == XmpPacket.RdfNamespace
                    && (e.LocalName == "Seq" || e.LocalName == "Bag" || e.LocalName == "Alt"))
                    list = e;
            if (list == null)
            {
                string text = creator.InnerText.Trim();
                if (text.Length > 0) authors.Add(text);
                return authors;
            }
            for (XmlNode child = list.FirstChild; child != null && authors.Count < MaxAuthors; child = child.NextSibling)
            {
                if (!(child is XmlElement li) || li.NamespaceURI != XmpPacket.RdfNamespace || li.LocalName != "li") continue;
                string text = li.InnerText.Trim();
                if (text.Length > 0) authors.Add(text);
            }
            return authors;
        }

        /// <summary>A page's rotation and boxes as the document gives them (the page's own defaults).</summary>
        internal static PdfJsPageInfo PageInfoOf(PdfDocument doc, PdfPage page)
        {
            // §7.7.3.3: a multiple of 90; anything else is no rotation.
            int rotate = ((page.Rotate ?? 0) % 360 + 360) % 360;
            if (rotate % 90 != 0) rotate = 0;
            return new PdfJsPageInfo
            {
                Rotate = rotate,
                MediaBox = BoxOf(doc, page.MediaBox),
                CropBox = BoxOf(doc, page.CropBox),
                BleedBox = BoxOf(doc, page.BleedBox),
                TrimBox = BoxOf(doc, page.TrimBox),
                ArtBox = BoxOf(doc, page.ArtBox),
            };
        }

        /// <summary>A box's four numbers as left, bottom, right, top; null for a box that is not four finite numbers.</summary>
        private static IReadOnlyList<double> BoxOf(PdfDocument doc, PdfArray box)
        {
            if (box == null || box.Count < 4) return null;
            var n = new double[4];
            for (int i = 0; i < 4; i++)
            {
                n[i] = Rendering.PdfResolve.ArrayDouble(doc, box, i);
                if (double.IsNaN(n[i]) || double.IsInfinity(n[i])) return null;
            }
            return new[] { Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]) };
        }

        /// <summary>
        /// A reader of a page's words for <see cref="PdfJsExecutionContext.PageWords"/>: the page's
        /// structured text in reading order, read when a script first asks for the page and kept for
        /// the reader's life, through the document <paramref name="open"/> answers — disposed after
        /// each read when <paramref name="ownsEachOpen"/>, the caller's own otherwise.
        /// </summary>
        internal static Func<int, IReadOnlyList<string>> PageWordsReader(Func<PdfDocument> open, bool ownsEachOpen)
        {
            if (open == null) throw new ArgumentNullException(nameof(open));
            var cache = new Dictionary<int, IReadOnlyList<string>>();
            return page =>
            {
                if (cache.TryGetValue(page, out IReadOnlyList<string> words)) return words;
                PdfDocument doc = open();
                try { words = WordsOf(doc, page); }
                finally { if (ownsEachOpen) doc.Dispose(); }
                cache[page] = words;
                return words;
            };
        }

        /// <summary>The words of page <paramref name="page"/> in reading order; none for a page the document lacks.</summary>
        internal static IReadOnlyList<string> WordsOf(PdfDocument doc, int page)
        {
            if (page < 0 || page >= doc.Pages.Count) return Array.Empty<string>();
            var words = new List<string>();
            PdfPageTextLayout layout = PdfStructuredTextExtractor.Extract(doc, page);
            foreach (PdfTextLine line in layout.Lines)
                foreach (PdfTextWord word in line.Words)
                    if (!string.IsNullOrEmpty(word.Text)) words.Add(word.Text);
            return words;
        }
    }
}
