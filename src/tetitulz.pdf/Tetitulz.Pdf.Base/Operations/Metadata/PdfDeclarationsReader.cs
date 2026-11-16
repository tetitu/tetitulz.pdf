// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Reads the <b>PDF Declarations</b> a document's XMP metadata carries — the read side of
    /// <see cref="PdfDeclarationsWriter"/>: which standards or profiles the file claims to conform to,
    /// by their <c>pdfd:conformsTo</c> URIs.
    /// </summary>
    /// <remarks>
    /// <para><b>A claim, not a verdict.</b> A declaration records what the producer asserts; reading
    /// one says nothing about whether the file meets the standard. A checker that is told which level
    /// a file claims — Well-Tagged PDF's reuse or accessibility level, for instance — uses this to
    /// know what to judge it against.</para>
    ///
    /// <para><b>One hardened parse.</b> The packet is read, loaded and searched through
    /// <c>Core.Metadata.XmpPacketDom</c> — no DTD processing, no resolver, the same size ceiling and
    /// depth cap — the helpers the writer merges with, so the two directions cannot disagree about
    /// what a packet is.</para>
    ///
    /// <para><b>Null is not empty.</b> An empty list means the packet was read and declares nothing.
    /// Null means it could not be read — an encrypted document, a metadata stream that will not
    /// decode or is too large, a packet that is not XML — and <see cref="LastError"/> says which. A
    /// caller must not read an unreadable packet as "no claim".</para>
    /// </remarks>
    public static class PdfDeclarationsReader
    {
        [ThreadStatic] private static string _lastError;

        /// <summary>Why the most recent read on the calling thread returned null, or <c>null</c>.</summary>
        public static string LastError => _lastError;

        /// <summary>
        /// Every <c>pdfd:conformsTo</c> URI in the XMP metadata of the PDF read from
        /// <paramref name="input"/>, trimmed, in document order; an empty list when there are none;
        /// null when the metadata could not be read (see <see cref="LastError"/>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static IReadOnlyList<string> ReadConformsTo(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;

            byte[] bytes = StreamBytes.ReadAll(input);
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                using var doc = PdfDocument.Open(ms);
                return ReadConformsTo(doc);
            }
            catch (Exception ex)
            {
                _lastError = "The document could not be parsed: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// As <see cref="ReadConformsTo(Stream)"/>, for a document the caller already has open.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
        public static IReadOnlyList<string> ReadConformsTo(PdfDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _lastError = null;

            if (document.IsEncrypted)
            {
                _lastError = "The document is encrypted; its XMP metadata cannot be read without decrypting it.";
                return null;
            }

            PdfDictionary catalog = document.Catalog?.Dictionary;
            if (catalog == null)
            {
                _lastError = "The document has no catalog dictionary.";
                return null;
            }

            if (catalog[PdfName.Metadata] == null) return Array.Empty<string>();

            string xmp = XmpPacketDom.ReadCatalogPacket(document, catalog);
            if (xmp == null)
            {
                _lastError = "The /Metadata stream could not be decoded, or exceeds " +
                             PdfDeclarationsWriter.MaxExistingPacketBytes + " bytes.";
                return null;
            }

            XmlDocument packet = XmpPacketDom.TryLoad(xmp);
            if (packet == null)
            {
                _lastError = "The XMP packet is not well-formed XML.";
                return null;
            }

            XmlElement declarations = XmpPacketDom.FindElement(
                packet.DocumentElement, PdfDeclarationsWriter.PdfDeclarationsNamespace, "declarations");
            var uris = new List<string>();
            if (declarations != null) Collect(declarations, 0, uris);
            return uris;
        }

        /// <summary>
        /// Collects <c>pdfd:conformsTo</c> from both XMP spellings of a property — a child element
        /// (what <see cref="PdfDeclarationsWriter"/> writes) and an attribute on an
        /// <c>rdf:Description</c> (the shorthand form) — bounded by
        /// <see cref="PdfDeclarationsWriter.MaxSearchDepth"/>.
        /// </summary>
        private static void Collect(XmlNode node, int depth, List<string> uris)
        {
            if (depth > PdfDeclarationsWriter.MaxSearchDepth || !(node is XmlElement element)) return;

            if (element.LocalName == "conformsTo"
                && element.NamespaceURI == PdfDeclarationsWriter.PdfDeclarationsNamespace)
            {
                AddTrimmed(element.InnerText, uris);
                return;
            }

            foreach (XmlAttribute attribute in element.Attributes)
                if (attribute.LocalName == "conformsTo"
                    && attribute.NamespaceURI == PdfDeclarationsWriter.PdfDeclarationsNamespace)
                    AddTrimmed(attribute.Value, uris);

            for (XmlNode child = element.FirstChild; child != null; child = child.NextSibling)
                Collect(child, depth + 1, uris);
        }

        private static void AddTrimmed(string value, List<string> uris)
        {
            string trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) uris.Add(trimmed);
        }
    }
}
