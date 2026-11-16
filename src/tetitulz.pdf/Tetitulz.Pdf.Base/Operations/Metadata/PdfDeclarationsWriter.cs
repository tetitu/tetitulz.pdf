// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Writes a <b>PDF Declaration</b> (PDF Association, "PDF Declarations — A use of ISO 32000",
    /// 2019-09) into a document's XMP metadata: a caller-supplied, machine-readable claim that the
    /// document conforms to some external standard or profile — accessibility, privacy, engineering
    /// accuracy, or anything else a claimant names by URI.
    ///
    /// <para><b>This writer does not validate anything.</b> That is the specification's own scope
    /// boundary: a PDF Declaration records a claim, and the claimant is responsible for its truth.
    /// A caller who has determined by some other means (their own checker, a third-party audit) that
    /// a document meets a standard uses this to record that determination in the file itself.</para>
    ///
    /// <para><b>Merge, don't clobber.</b> An existing <c>pdfd:declarations</c> array is preserved and
    /// appended to, never replaced — a document can carry claims from more than one source. Every
    /// other existing XMP property survives untouched. A document with no packet, or one that is not
    /// well-formed XML, gets a complete fresh packet carrying just this declaration.</para>
    ///
    /// <para><b>Security.</b> The packet is read, parsed, bounded and serialised by
    /// <c>Core.Metadata.XmpPacketDom</c> — no DTD, no resolver, and merging refused in favour of a
    /// fresh packet past <see cref="MaxExistingPacketBytes"/> decoded, <see cref="MaxSearchDepth"/>
    /// levels deep, or a re-serialisation past <see cref="MaxMergedPacketChars"/>. The revision is
    /// <see cref="XmpMetadataRevision"/>'s. Both are shared with <c>PdfMetadataWriter</c> and the
    /// Accessibility feature's PDF/UA writer, which this was a line-for-line copy of.</para>
    ///
    /// <para><b>Encrypted documents are refused, not silently mishandled.</b> This writer appends a
    /// clear-text revision, which would not match an encrypted document's encryption dictionary. A
    /// caller with an encrypted document must decrypt first — the same rule every write-side
    /// operation in this ring follows for the same reason.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// using var input  = File.OpenRead("report.pdf");
    /// using var output = File.Create("report-declared.pdf");
    /// bool ok = PdfDeclarationsWriter.AddDeclaration(input, output,
    ///     conformsTo: "https://example.org/standards/accessibility-v2",
    ///     claimBy: "Acme Conformance Labs",
    ///     claimDate: DateTimeOffset.UtcNow,
    ///     claimReport: "https://example.org/reports/report-12345.html");
    /// if (!ok) Console.Error.WriteLine(PdfDeclarationsWriter.LastError);
    /// </code>
    /// </example>
    public static class PdfDeclarationsWriter
    {
        /// <summary>The PDF Declarations schema namespace, prefix <c>pdfd</c>.</summary>
        public const string PdfDeclarationsNamespace = "http://pdfa.org/declarations/";

        /// <summary>Maximum node depth walked when searching or validating an existing packet (DoS guard).</summary>
        public const int MaxSearchDepth = XmpPacketDom.MaxSearchDepth;

        /// <summary>
        /// Largest decoded <c>/Metadata</c> stream, in bytes, that will be merged with (DoS guard).
        /// Past this a fresh packet is generated instead — see the type-level remarks.
        /// </summary>
        public const int MaxExistingPacketBytes = XmpPacketDom.MaxExistingPacketBytes;

        /// <summary>
        /// Ceiling, in characters, on the projected size of the re-serialised merged packet (DoS
        /// guard) — checked before serialising, since indented output cost is Θ(depth) per node.
        /// </summary>
        public const int MaxMergedPacketChars = XmpPacketDom.MaxMergedPacketChars;

        private const string RdfNamespace = XmpPacket.RdfNamespace;

        [ThreadStatic] private static string _lastError;
        [ThreadStatic] private static bool _lastCallAppendedRevision;

        /// <summary>Description of the most recent failure on the calling thread, or <c>null</c>.</summary>
        public static string LastError => _lastError;

        /// <summary>
        /// Whether the most recent <c>AddDeclaration</c> call on the calling thread appended a
        /// revision. <c>false</c> after a call that returned <c>false</c> means the write failed —
        /// read <see cref="LastError"/>.
        /// </summary>
        public static bool LastCallAppendedRevision => _lastCallAppendedRevision;

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Adds a PDF Declaration to the XMP metadata of the PDF read from <paramref name="input"/>
        /// and writes the updated document to <paramref name="output"/>.
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF. Never modified.</param>
        /// <param name="output">Writable stream that receives the updated PDF. On failure it
        /// receives an exact copy of the source bytes.</param>
        /// <param name="conformsTo">
        /// Required. The URI of the standard or profile being claimed (<c>pdfd:conformsTo</c>).
        /// </param>
        /// <param name="claimBy">Optional claimant name (<c>pdfd:claimBy</c>).</param>
        /// <param name="claimDate">Optional date the claim was made (<c>pdfd:claimDate</c>).</param>
        /// <param name="claimCredentials">Optional claimant credentials (<c>pdfd:claimCredentials</c>).</param>
        /// <param name="claimReport">Optional URL of a supporting report (<c>pdfd:claimReport</c>).</param>
        /// <returns><c>true</c> on success; otherwise <c>false</c> (see <see cref="LastError"/>).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="conformsTo"/> is null or whitespace.</exception>
        public static bool AddDeclaration(
            Stream input,
            Stream output,
            string conformsTo,
            string claimBy = null,
            DateTimeOffset? claimDate = null,
            string claimCredentials = null,
            string claimReport = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            ValidateConformsTo(conformsTo);

            _lastError = null;
            _lastCallAppendedRevision = false;

            bool written = XmpMetadataRevision.Write(input, output,
                (doc, existingXmp) => new XmpMetadataRevision.PacketPlan(BuildDeclarationsXmpPacket(
                    existingXmp, conformsTo, claimBy, claimDate, claimCredentials, claimReport)),
                Fail, report: null, out bool appended);
            _lastCallAppendedRevision = appended;
            return written;
        }

        /// <summary>File-path overload of <see cref="AddDeclaration(Stream, Stream, string, string, DateTimeOffset?, string, string)"/>.</summary>
        public static bool AddDeclaration(
            string inputPath,
            string outputPath,
            string conformsTo,
            string claimBy = null,
            DateTimeOffset? claimDate = null,
            string claimCredentials = null,
            string claimReport = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            ValidateConformsTo(conformsTo);

            using var inFs = File.OpenRead(inputPath);
            using var outFs = File.Create(outputPath);
            return AddDeclaration(inFs, outFs, conformsTo, claimBy, claimDate, claimCredentials, claimReport);
        }

        /// <summary>
        /// Builds the XMP packet text that <see cref="AddDeclaration(Stream, Stream, string, string, DateTimeOffset?, string, string)"/>
        /// embeds, without touching a PDF. Exposed because it is independently useful: a caller that
        /// already manages its own <c>/Metadata</c> stream can merge the declaration in itself.
        /// </summary>
        /// <param name="existingXmp">The current XMP packet, or <c>null</c>/whitespace for none. A
        /// packet that is not well-formed XML, has no <c>rdf:RDF</c> element, or trips one of the
        /// amplification bounds is discarded in favour of a fresh packet rather than throwing.</param>
        /// <param name="conformsTo">Required. The URI of the standard or profile being claimed.</param>
        /// <param name="claimBy">Optional claimant name.</param>
        /// <param name="claimDate">Optional date the claim was made.</param>
        /// <param name="claimCredentials">Optional claimant credentials.</param>
        /// <param name="claimReport">Optional URL of a supporting report.</param>
        /// <returns>A complete XMP packet including the <c>xpacket</c> processing instructions.</returns>
        /// <exception cref="ArgumentException"><paramref name="conformsTo"/> is null or whitespace.</exception>
        public static string BuildDeclarationsXmpPacket(
            string existingXmp,
            string conformsTo,
            string claimBy = null,
            DateTimeOffset? claimDate = null,
            string claimCredentials = null,
            string claimReport = null)
        {
            ValidateConformsTo(conformsTo);

            XmlDocument doc = null;
            XmlElement description = null;

            if (!string.IsNullOrWhiteSpace(existingXmp) && XmpPacketDom.IsWithinMergeBudget(existingXmp))
            {
                doc = XmpPacketDom.TryLoad(existingXmp);
                if (doc != null && !XmpPacketDom.IsMergeable(doc)) doc = null;
                if (doc != null)
                {
                    description = XmpPacketDom.EnsureDescription(doc);
                    if (description == null) doc = null;
                }
            }

            if (doc == null)
            {
                doc = XmpPacketDom.CreateEmpty();
                description = XmpPacketDom.EnsureDescription(doc);
            }

            XmlElement declarationsBag =
                EnsureBagProperty(doc, description, PdfDeclarationsNamespace, "pdfd", "declarations");

            var li = doc.CreateElement("rdf", "li", RdfNamespace);
            SetParseTypeResource(li);

            AppendTextProperty(doc, li, "conformsTo", conformsTo);

            if (claimBy != null || claimDate.HasValue || claimCredentials != null || claimReport != null)
            {
                var claimData = doc.CreateElement("pdfd", "claimData", PdfDeclarationsNamespace);
                var claimBag = doc.CreateElement("rdf", "Bag", RdfNamespace);
                var claimLi = doc.CreateElement("rdf", "li", RdfNamespace);
                SetParseTypeResource(claimLi);

                if (claimBy != null) AppendTextProperty(doc, claimLi, "claimBy", claimBy);
                if (claimDate.HasValue)
                    AppendTextProperty(doc, claimLi, "claimDate",
                        claimDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (claimCredentials != null) AppendTextProperty(doc, claimLi, "claimCredentials", claimCredentials);
                if (claimReport != null) AppendTextProperty(doc, claimLi, "claimReport", claimReport);

                claimBag.AppendChild(claimLi);
                claimData.AppendChild(claimBag);
                li.AppendChild(claimData);
            }

            declarationsBag.AppendChild(li);

            return XmpPacketDom.Serialize(doc);
        }

        // ================================================================
        //  Packet construction
        // ================================================================

        private static void AppendTextProperty(XmlDocument doc, XmlElement parent, string localName, string value)
        {
            var el = doc.CreateElement("pdfd", localName, PdfDeclarationsNamespace);
            el.AppendChild(doc.CreateTextNode(value));
            parent.AppendChild(el);
        }

        /// <summary><c>rdf:parseType="Resource"</c> — the element's children are the resource's
        /// properties directly, with no intervening <c>rdf:Description</c>. The form the
        /// specification's own worked example uses for a Declaration and a ClaimData entry.</summary>
        private static void SetParseTypeResource(XmlElement li)
        {
            var attr = li.OwnerDocument.CreateAttribute("rdf", "parseType", RdfNamespace);
            attr.Value = "Resource";
            li.Attributes.Append(attr);
        }

        /// <summary>
        /// Finds (or creates, under <paramref name="description"/>) an <c>rdf:Bag</c>-valued
        /// property and returns the <c>rdf:Bag</c> element itself — the node new <c>rdf:li</c>
        /// entries are appended to. Unlike a simple property, a Bag is never merged in attribute-
        /// shorthand form: RDF's shorthand exists only for single-valued string properties.
        /// </summary>
        private static XmlElement EnsureBagProperty(
            XmlDocument doc, XmlElement description, string namespaceUri, string prefix, string localName)
        {
            var property = XmpPacketDom.FindElement(doc.DocumentElement, namespaceUri, localName);
            if (property == null)
            {
                property = doc.CreateElement(prefix, localName, namespaceUri);
                description.AppendChild(property);
            }

            var bag = XmpPacketDom.FindElement(property, RdfNamespace, "Bag");
            if (bag == null)
            {
                bag = doc.CreateElement("rdf", "Bag", RdfNamespace);
                property.AppendChild(bag);
            }
            return bag;
        }

        private static void ValidateConformsTo(string conformsTo)
        {
            if (string.IsNullOrWhiteSpace(conformsTo))
                throw new ArgumentException(
                    "conformsTo is required — a PDF Declaration with no target standard declares nothing.",
                    nameof(conformsTo));
        }

        private static void Fail(string detail, Exception ex = null)
            => _lastError = ex == null ? detail : detail + " " + ex.Message;
    }
}
