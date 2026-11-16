// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The XMP packet the two conformance converters stamp: one envelope, one schema.
    ///
    /// <para><c>PdfAConverter.BuildPdfA1bXmp</c> and <c>PdfXConverter.BuildPdfXXmp</c> write the
    /// same <c>x:xmpmeta</c> / <c>rdf:RDF</c> / <c>rdf:Description</c> skeleton, the same <c>dc</c>
    /// and <c>xmp</c> namespace declarations, the same <c>dc:title</c> alt-array block, the same
    /// <c>xmp:ModifyDate</c> line and the same W3CDTF format string, differing only in the schema
    /// namespace and the one or two properties inside it.</para>
    /// </summary>
    /// <remarks>
    /// <b>The date format is the part that must not drift.</b> A custom format string is resolved
    /// against <c>CurrentCulture</c>'s <i>calendar</i>, so a th-TH host would write a Buddhist year
    /// and an ar-SA host an Umm al-Qura date into a field that must be W3CDTF — in the very packet
    /// that stamps <c>pdfaid:part</c>. The date is formatted under the invariant culture, here
    /// once.
    ///
    /// <para><b>Escaping is applied here, once, so no caller can forget it</b>, through
    /// <see cref="XmlEscape"/> in Core, which is the only ring a plugin can reach.</para>
    ///
    /// <para><b>Newlines are LF, chosen rather than inherited.</b> A verbatim multi-line literal
    /// takes its line endings from however the .cs file is saved, and composing such a template
    /// with a built-up body is how a file ends up with both. Emitting <c>\n</c> explicitly makes
    /// the output identical on every checkout; readers of an XMP packet see the element structure,
    /// and the tests read values out by parsing rather than by matching bytes.</para>
    /// </remarks>
    internal static class XmpConformancePacket
    {
        /// <summary>W3CDTF, invariant calendar. See the remarks on the type.</summary>
        private const string W3CDateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        /// <summary>
        /// A complete XMP packet declaring <paramref name="schemaPrefix"/> bound to
        /// <paramref name="schemaNamespace"/> and carrying <paramref name="schemaProperties"/>.
        /// </summary>
        /// <param name="schemaProperties">
        /// Property local names and values, in document order. Values are escaped here.
        /// </param>
        /// <param name="title">The <c>dc:title</c> x-default value; null becomes empty.</param>
        /// <param name="creatorTool">
        /// Written as <c>xmp:CreatorTool</c> when non-null. PDF/X does not set it, which is why the
        /// parameter is nullable rather than defaulted.
        /// </param>
        /// <param name="modifiedUtc">The <c>xmp:ModifyDate</c> instant, in UTC.</param>
        /// <param name="schemaPrefix">
        /// Namespace prefix for the conformance schema — <c>pdfaid</c>, <c>pdfxid</c> or
        /// <c>pdfuaid</c>.
        /// </param>
        /// <param name="schemaNamespace">The URI that prefix is bound to.</param>
        internal static string Build(
            string schemaPrefix,
            string schemaNamespace,
            IReadOnlyList<KeyValuePair<string, string>> schemaProperties,
            string title,
            string creatorTool,
            DateTime modifiedUtc)
        {
            var sb = new StringBuilder();
            sb.Append(XmpPacket.Header).Append('\n');
            // The four fixed namespace URIs come from Core.Metadata.XmpPacket, shared with Base's
            // tagged-document builder and the Accessibility writer: a wrong one fails only
            // silently — the packet still parses and simply declares nothing a reader recognises.
            sb.Append("<x:xmpmeta xmlns:x=\"").Append(XmpPacket.XmpMetaNamespace).Append("\">\n");
            sb.Append("  <rdf:RDF xmlns:rdf=\"").Append(XmpPacket.RdfNamespace).Append("\">\n");
            sb.Append("    <rdf:Description rdf:about=\"\"\n");
            sb.Append("        xmlns:dc=\"").Append(XmpPacket.DublinCoreNamespace).Append("\"\n");
            sb.Append("        xmlns:xmp=\"").Append(XmpPacket.XmpBasicNamespace).Append("\"\n");
            sb.Append("        xmlns:").Append(schemaPrefix).Append("=\"")
              .Append(schemaNamespace).Append("\">\n");

            if (schemaProperties != null)
            {
                foreach (var p in schemaProperties)
                    sb.Append("      <").Append(schemaPrefix).Append(':').Append(p.Key).Append('>')
                      .Append(XmlEscape.EscapeText(p.Value))
                      .Append("</").Append(schemaPrefix).Append(':').Append(p.Key).Append(">\n");
            }

            sb.Append("      <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">")
              .Append(XmlEscape.EscapeText(title))
              .Append("</rdf:li></rdf:Alt></dc:title>\n");

            if (creatorTool != null)
                sb.Append("      <xmp:CreatorTool>").Append(XmlEscape.EscapeText(creatorTool))
                  .Append("</xmp:CreatorTool>\n");

            sb.Append("      <xmp:ModifyDate>")
              .Append(modifiedUtc.ToString(W3CDateFormat, CultureInfo.InvariantCulture))
              .Append("</xmp:ModifyDate>\n");

            sb.Append("    </rdf:Description>\n");
            sb.Append("  </rdf:RDF>\n");
            sb.Append("</x:xmpmeta>");
            sb.Append(XmpPacket.Trailer);
            return sb.ToString();
        }
    }
}
