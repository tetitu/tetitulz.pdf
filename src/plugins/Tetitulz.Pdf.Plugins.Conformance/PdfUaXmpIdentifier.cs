// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Xml;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The PDF/UA identification schema's two properties, read out of an XMP packet:
    /// <c>pdfuaid:part</c> (ISO 14289-1 §5, ISO 14289-2 Clause 5) and <c>pdfuaid:rev</c>
    /// (ISO 14289-2 Clause 5). Each may be written as an element or as an attribute of an
    /// <c>rdf:Description</c> (XMP Part 1 §7.9.2.2 allows both), and the element wins.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, three readers.</b> <c>PdfUaChecker</c>'s UA03,
    /// <c>PdfUa2Checker</c>'s UA2-5 and the accessibility audit's target detection all ask which part
    /// a document declares. UA03 and UA2-5 each carried this read verbatim; a third copy for the
    /// detection was the moment to make it one, because the attribute fallback is exactly the kind
    /// of clause a copy drifts on.</para>
    ///
    /// <para><b>Locating and decoding the packet is the caller's.</b> Each caller reports a missing,
    /// non-stream, undecodable or empty packet in its own code's terms — and, on an encrypted
    /// document, as "unverifiable" rather than as a defect — so what is shared is the one step whose
    /// answer is the same for all of them: the XML.</para>
    /// </remarks>
    internal static class PdfUaXmpIdentifier
    {
        /// <summary>
        /// Reads <c>pdfuaid:part</c> and <c>pdfuaid:rev</c> from <paramref name="xmpBytes"/>, each
        /// trimmed, or <c>null</c> where the packet declares none.
        /// </summary>
        /// <exception cref="XmlException">The packet is not well-formed XML. The caller decides what
        /// that means: ciphertext on an encrypted document, a defect otherwise.</exception>
        internal static void Read(byte[] xmpBytes, out string part, out string rev)
            => Read(xmpBytes, out part, out rev, out _);

        /// <param name="xmpBytes">The packet.</param>
        /// <param name="part">pdfuaid:part.</param>
        /// <param name="rev">pdfuaid:rev.</param>
        /// <param name="hasDefaultLanguageText">Whether a language alternative (<c>rdf:Alt</c>) has an
        /// <c>x-default</c> item and no item in a named language — text whose language only the
        /// catalog's <c>/Lang</c> can give (rule 7.2-33: its corpus fails a <c>dc:title</c>
        /// in <c>x-default</c> alone, 7.2-t33-fail-a, and passes one beside <c>en-US</c>,
        /// 7.2-t21-pass-b).</param>
        internal static void Read(byte[] xmpBytes, out string part, out string rev, out bool hasDefaultLanguageText)
        {
            var xmlDoc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(
                new MemoryStream(xmpBytes),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            {
                xmlDoc.Load(reader);
            }

            var nsMgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsMgr.AddNamespace("pdfuaid", Core.Metadata.XmpPacket.PdfUaIdNamespace);

            part = Property(xmlDoc, nsMgr, "part");
            rev = Property(xmlDoc, nsMgr, "rev");
            nsMgr.AddNamespace("rdf", RdfNamespace);
            hasDefaultLanguageText = false;
            foreach (XmlNode alt in xmlDoc.SelectNodes("//rdf:Alt", nsMgr))
            {
                bool defaultItem = false, namedItem = false;
                foreach (XmlNode li in alt.ChildNodes)
                {
                    if (!(li is XmlElement item) || item.LocalName != "li" || item.NamespaceURI != RdfNamespace) continue;
                    string lang = item.GetAttribute("xml:lang");
                    if (lang == "x-default") defaultItem = true;
                    else if (lang.Length > 0) namedItem = true;
                }
                if (defaultItem && !namedItem) { hasDefaultLanguageText = true; break; }
            }
        }

        private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        private static string Property(XmlDocument xmlDoc, XmlNamespaceManager nsMgr, string localName)
        {
            string value = xmlDoc.SelectSingleNode("//pdfuaid:" + localName, nsMgr)?.InnerText?.Trim();
            if (value == null)
                value = (xmlDoc.SelectSingleNode("//*/@pdfuaid:" + localName, nsMgr) as XmlAttribute)?.Value?.Trim();
            return value;
        }
    }
}
