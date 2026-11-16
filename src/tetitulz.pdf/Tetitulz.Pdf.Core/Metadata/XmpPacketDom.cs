// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Metadata
{
    /// <summary>
    /// What every writer that merges into an existing XMP packet has to do the same way: read the
    /// catalog's packet within a byte ceiling, parse it with no DTD and no resolver, bound what a
    /// small hostile packet can grow into, find properties within a depth cap, and serialise the
    /// result inside the <c>xpacket</c> wrapper.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation</b> — the load, the depth-capped searches, the amplification
    /// bounds, the serialiser, the packet read — for <c>Accessibility.Metadata.PdfUaXmpWriter</c>
    /// and <c>Base.Operations.Metadata.PdfDeclarationsWriter</c>. The report is a parameter, so
    /// each give-up reaches the caller's diagnostics sink and none is swallowed.</para>
    ///
    /// <para><b>Why the bounds.</b> Indented output prefixes an element at level <i>d</i> with
    /// 2·<i>d</i> spaces on both tags, so a <i>D</i>-level packet serialises to Θ(<i>D</i>²)
    /// characters however few bytes it took: a few hundred bytes of <c>/FlateDecode</c>d
    /// <c>&lt;a&gt;</c>×20000 once came back out as an 800&#160;MB PDF. The depth cap costs
    /// nothing reachable — the searches stop there already.</para>
    ///
    /// <para>Internal: Base and Accessibility are both in Core's <c>InternalsVisibleTo</c>, and the
    /// public faces are the two writers' own constants and methods.</para>
    /// </remarks>
    internal static class XmpPacketDom
    {
        /// <summary>Deepest node a search or the mergeability walk will visit.</summary>
        internal const int MaxSearchDepth = 64;

        /// <summary>
        /// Largest decoded <c>/Metadata</c> stream, in bytes, that is merged with. An XMP packet is
        /// metadata, not a payload; <c>GetDecodedData</c> is bounded only by the filter-level cap
        /// sized for images.
        /// </summary>
        internal const int MaxExistingPacketBytes = 2 * 1024 * 1024;

        /// <summary>Ceiling, in characters, on a packet's text and on its projected re-serialisation.</summary>
        internal const int MaxMergedPacketChars = 4 * 1024 * 1024;

        /// <summary>
        /// The catalog's <c>/Metadata</c> packet as text, or null when there is none, it is not a
        /// stream, it cannot be decoded, it is empty, or it decodes past
        /// <see cref="MaxExistingPacketBytes"/>. Each give-up but "none" and "empty" is reported.
        /// </summary>
        internal static string ReadCatalogPacket(
            PdfDocument doc, PdfDictionary catalog, Action<string, Exception> report = null)
        {
            var metaObj = catalog[PdfName.Metadata];
            if (metaObj == null) return null;

            PdfStream metaStream;
            try
            {
                metaStream = doc.ResolveObject(metaObj) as PdfStream;
            }
            catch (Exception ex)
            {
                report?.Invoke("The catalog /Metadata reference could not be resolved; a new packet will be generated.", ex);
                return null;
            }

            if (metaStream == null)
            {
                report?.Invoke("The catalog /Metadata entry does not point to a stream; a new packet will be generated.", null);
                return null;
            }

            if (!TryReadPacketBytes(metaStream, out byte[] bytes, out string reason))
            {
                report?.Invoke(
                    "The existing /Metadata stream could not be decoded within the " +
                    MaxExistingPacketBytes.ToString(CultureInfo.InvariantCulture) +
                    "-byte limit for an XMP packet (" + reason.TrimEnd('.') +
                    "); a new packet will be generated in its place.", null);
                return null;
            }

            if (bytes == null || bytes.Length == 0) return null;

            return XmpPacket.DecodeText(bytes);
        }

        /// <summary>
        /// A <c>/Metadata</c> stream's decoded bytes, decoded no further than
        /// <see cref="MaxExistingPacketBytes"/>: false, with the reason, for a stream that will not
        /// decode or decodes past it.
        /// </summary>
        /// <remarks>
        /// Every reader of a packet's bytes asks this before it parses one. Each decoded with
        /// <c>GetDecodedData</c>, bounded only by the per-stream ceiling sized for images, and this
        /// read compared the length after the decode: a few kilobytes of flate were inflated to half
        /// a gigabyte before the 2 MB answer. The conformance plugin, outside Core's
        /// <c>InternalsVisibleTo</c>, asks the same ceiling through the public
        /// <see cref="PdfStream.TryGetDecodedData(long, out byte[], out string)"/> and Base's
        /// <c>PdfDeclarationsWriter.MaxExistingPacketBytes</c>.
        /// </remarks>
        internal static bool TryReadPacketBytes(PdfStream metadata, out byte[] bytes, out string reason)
        {
            if (metadata.TryGetDecodedData(MaxExistingPacketBytes, out bytes, out reason)) return true;
            reason = reason ?? "it would not decode";
            return false;
        }

        /// <summary>
        /// Whether a packet's text is small enough to parse at all — the cheap pre-filter for a
        /// caller handed any string, before the parser sees it.
        /// </summary>
        internal static bool IsWithinMergeBudget(string existingXmp, Action<string, Exception> report = null)
        {
            if (existingXmp.Length <= MaxMergedPacketChars) return true;

            report?.Invoke(
                "The existing XMP packet is " +
                existingXmp.Length.ToString(CultureInfo.InvariantCulture) +
                " characters, past the " +
                MaxMergedPacketChars.ToString(CultureInfo.InvariantCulture) +
                "-character merge limit; a new packet was generated in its place.", null);
            return false;
        }

        /// <summary>
        /// Parses a packet with no DTD processing and no resolver, whitespace-only nodes dropped so
        /// a writer re-indents from scratch. Null, reported, when it is not well-formed XML.
        /// </summary>
        internal static XmlDocument TryLoad(string xmp, Action<string, Exception> report = null)
        {
            string text = StripLeadingByteOrderMark(xmp);
            try
            {
                var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    ConformanceLevel = ConformanceLevel.Document,
                    IgnoreComments = false,
                };

                using var stringReader = new StringReader(text);
                using var reader = XmlReader.Create(stringReader, settings);
                doc.Load(reader);
                return doc.DocumentElement == null ? null : doc;
            }
            catch (XmlException ex)
            {
                report?.Invoke("The existing XMP packet is not well-formed XML; a new packet was generated in its place.", ex);
                return null;
            }
        }

        /// <summary>
        /// Whether a parsed packet may be merged into and re-serialised: no node deeper than
        /// <see cref="MaxSearchDepth"/>, and the indentation re-serialising it costs, summed exactly
        /// over every node, within <see cref="MaxMergedPacketChars"/>. Iterative, because a
        /// recursive walk would overflow on exactly the input it exists to reject.
        /// </summary>
        internal static bool IsMergeable(XmlDocument doc, Action<string, Exception> report = null)
        {
            XmlNode root = doc.DocumentElement;
            if (root == null) return false;

            var pending = new Stack<(XmlNode Node, int Depth)>();
            pending.Push((root, 0));

            long indentCost = 0;
            while (pending.Count > 0)
            {
                (XmlNode node, int depth) = pending.Pop();

                if (depth > MaxSearchDepth)
                {
                    report?.Invoke(
                        "The existing XMP packet nests deeper than the " +
                        MaxSearchDepth.ToString(CultureInfo.InvariantCulture) +
                        "-level search cap, so its content is unreachable to this writer and " +
                        "re-indenting it would cost O(depth^2) output; a new packet was " +
                        "generated in its place.", null);
                    return false;
                }

                indentCost += 2L * (1 + 2 * depth);
                if (indentCost > MaxMergedPacketChars)
                {
                    report?.Invoke(
                        "Re-serialising the existing XMP packet would exceed the " +
                        MaxMergedPacketChars.ToString(CultureInfo.InvariantCulture) +
                        "-character output limit; a new packet was generated in its place.", null);
                    return false;
                }

                for (XmlNode child = node.FirstChild; child != null; child = child.NextSibling)
                    pending.Push((child, depth + 1));
            }

            return true;
        }

        /// <summary>
        /// A fresh packet's skeleton: <c>x:xmpmeta</c> / <c>rdf:RDF</c> /
        /// <c>rdf:Description rdf:about=""</c>, with an <c>x:xmptk</c> when
        /// <paramref name="toolkit"/> is given.
        /// </summary>
        internal static XmlDocument CreateEmpty(string toolkit = null)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };

            var meta = doc.CreateElement("x", "xmpmeta", XmpPacket.XmpMetaNamespace);
            if (toolkit != null)
            {
                var toolkitAttribute = doc.CreateAttribute("x", "xmptk", XmpPacket.XmpMetaNamespace);
                toolkitAttribute.Value = toolkit;
                meta.Attributes.Append(toolkitAttribute);
            }
            doc.AppendChild(meta);

            var rdf = doc.CreateElement("rdf", "RDF", XmpPacket.RdfNamespace);
            meta.AppendChild(rdf);
            rdf.AppendChild(CreateDescription(doc));

            return doc;
        }

        /// <summary>
        /// The <c>rdf:Description</c> new properties go into, created when the packet has an
        /// <c>rdf:RDF</c> but no description. Null when there is no <c>rdf:RDF</c> at all, which
        /// tells the caller to start a fresh packet.
        /// </summary>
        internal static XmlElement EnsureDescription(XmlDocument doc)
        {
            var root = doc.DocumentElement;
            if (root == null) return null;

            var rdf = FindElement(root, XmpPacket.RdfNamespace, "RDF");
            if (rdf == null) return null;

            var description = FindElement(rdf, XmpPacket.RdfNamespace, "Description");
            if (description == null)
            {
                description = CreateDescription(doc);
                rdf.AppendChild(description);
            }
            return description;
        }

        /// <summary><c>rdf:Description rdf:about=""</c>.</summary>
        internal static XmlElement CreateDescription(XmlDocument doc)
        {
            var description = doc.CreateElement("rdf", "Description", XmpPacket.RdfNamespace);
            var about = doc.CreateAttribute("rdf", "about", XmpPacket.RdfNamespace);
            about.Value = string.Empty;
            description.Attributes.Append(about);
            return description;
        }

        /// <summary>
        /// Depth-first search for the first element with the given namespace and local name,
        /// capped at <see cref="MaxSearchDepth"/>.
        /// </summary>
        internal static XmlElement FindElement(XmlNode root, string namespaceUri, string localName, int depth = 0)
        {
            if (root == null || depth > MaxSearchDepth) return null;

            if (root is XmlElement element
                && string.Equals(element.LocalName, localName, StringComparison.Ordinal)
                && string.Equals(element.NamespaceURI, namespaceUri, StringComparison.Ordinal))
            {
                return element;
            }

            for (var child = root.FirstChild; child != null; child = child.NextSibling)
            {
                var hit = FindElement(child, namespaceUri, localName, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// Depth-first search for the first attribute with the given namespace and local name,
        /// under the same cap.
        /// </summary>
        internal static XmlAttribute FindAttribute(XmlNode root, string namespaceUri, string localName)
        {
            var found = new List<XmlAttribute>(1);
            CollectAttributes(root, namespaceUri, localName, 0, found, firstOnly: true);
            return found.Count > 0 ? found[0] : null;
        }

        /// <summary>
        /// Every attribute with the given namespace and local name, in the order
        /// <see cref="FindAttribute"/> meets them, under the same cap — one walk, so a caller removing
        /// them all does not search from the root again after each (a packet carrying one on 120 000
        /// elements took 134 s that way).
        /// </summary>
        internal static List<XmlAttribute> FindAttributes(XmlNode root, string namespaceUri, string localName)
        {
            var found = new List<XmlAttribute>();
            CollectAttributes(root, namespaceUri, localName, 0, found, firstOnly: false);
            return found;
        }

        /// <summary>
        /// The nodes the attribute searches on this thread have visited — the work a test bounds, since an
        /// answer cannot show whether one walk or one per removal found it.
        /// </summary>
        [ThreadStatic] internal static long AttributeSearchNodesVisited;

        private static bool CollectAttributes(
            XmlNode node, string namespaceUri, string localName, int depth, List<XmlAttribute> found, bool firstOnly)
        {
            if (node == null || depth > MaxSearchDepth) return false;
            AttributeSearchNodesVisited++;

            if (node is XmlElement element && element.HasAttributes)
            {
                foreach (XmlAttribute attribute in element.Attributes)
                {
                    if (string.Equals(attribute.LocalName, localName, StringComparison.Ordinal)
                        && string.Equals(attribute.NamespaceURI, namespaceUri, StringComparison.Ordinal))
                    {
                        found.Add(attribute);
                        if (firstOnly) return true;
                    }
                }
            }

            for (var child = node.FirstChild; child != null; child = child.NextSibling)
                if (CollectAttributes(child, namespaceUri, localName, depth + 1, found, firstOnly)) return true;
            return false;
        }

        /// <summary>
        /// The document element, indented, inside the <c>xpacket</c> wrapper with
        /// <paramref name="padding"/> before the trailer. No XML declaration — the XMP
        /// specification forbids one inside the wrapper.
        /// </summary>
        /// <remarks>
        /// The padding is the caller's: a writer that promises byte-identical output across runs
        /// fixes its own, and empty padding is exactly <see cref="XmpPacket.Wrap"/> with none.
        /// </remarks>
        internal static string Serialize(XmlDocument doc, string padding = "")
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                CheckCharacters = true,
            };

            var body = new StringBuilder();
            using (var writer = XmlWriter.Create(body, settings))
            {
                doc.DocumentElement.WriteTo(writer);
                writer.Flush();
            }

            string header = XmpPacket.Header;
            var packet = new StringBuilder(
                header.Length + body.Length + padding.Length + XmpPacket.Trailer.Length + 8);
            packet.Append(header).Append('\n');
            packet.Append(body);
            packet.Append('\n');
            packet.Append(padding);
            packet.Append(XmpPacket.Trailer);
            return packet.ToString();
        }

        private static string StripLeadingByteOrderMark(string text)
        {
            char bom = XmpPacket.ByteOrderMark[0];
            int start = 0;
            while (start < text.Length && text[start] == bom) start++;
            return start == 0 ? text : text.Substring(start);
        }
    }
}
