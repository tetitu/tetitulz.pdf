// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Metadata
{
    /// <summary>
    /// Writes the PDF/UA identification schema (<c>pdfuaid:part</c>) into a document's
    /// XMP metadata packet, and optionally the Dublin Core title and language.
    ///
    /// <para>
    /// PDF/UA-1 clause 5 (and the PDF/UA-1 checkpoint reported by
    /// <c>Tetitulz.Pdf.Plugins.Conformance.PdfUaChecker</c> as <b>UA03</b>) requires the
    /// document catalog to carry a <c>/Metadata</c> XMP stream that declares
    /// <c>pdfuaid:part = 1</c>. Without it a document can satisfy every structural rule and
    /// still fail validation, so this writer is the final step of a remediation pipeline.
    /// </para>
    ///
    /// <para><b>Merge, don't clobber.</b> If the document already carries an XMP packet it is
    /// parsed and every existing property is preserved; only <c>pdfuaid:part</c> (plus
    /// <c>dc:title</c> / <c>dc:language</c> when supplied) is added or replaced. A document with
    /// no packet — or with one that is not well-formed XML — gets a complete, freshly generated
    /// packet instead.</para>
    ///
    /// <para><b>Security.</b> The existing packet is parsed with <c>DtdProcessing.Prohibit</c> and
    /// a null <see cref="XmlResolver"/>, so a hostile <c>/Metadata</c> stream cannot mount an XXE
    /// or billion-laughs attack. Node searches are depth-capped
    /// (<see cref="MaxSearchDepth"/>) so a deeply nested packet cannot overflow the stack.</para>
    ///
    /// <para><b>Amplification bounds.</b> Three further bounds stop a <i>small</i> hostile packet
    /// from being amplified into a huge one. A packet is only merged when (a) the decoded
    /// <c>/Metadata</c> stream is at most
    /// <see cref="MaxExistingPacketBytes"/>, (b) it nests no deeper than
    /// <see cref="MaxSearchDepth"/>, and (c) its indented re-serialisation is projected to stay
    /// under <see cref="MaxMergedPacketChars"/>. Any packet that fails one of them is discarded
    /// and replaced by a freshly generated one, exactly as a not-well-formed packet is. The depth
    /// bound is the load-bearing one: indented output prefixes an element at level <i>d</i> with
    /// 2·<i>d</i> spaces, so serialising a <i>D</i>-level packet costs Θ(<i>D</i>²) characters no
    /// matter how few bytes it occupied on input — a 861-byte <c>/FlateDecode</c>d
    /// <c>&lt;a&gt;</c>×20000 packet would come back out as an 800&#160;MB PDF, and a slightly
    /// deeper one kill the process with <c>OutOfMemoryException</c>. Discarding costs nothing
    /// real: content below <see cref="MaxSearchDepth"/> is already invisible to
    /// <c>FindElement</c>, so it could never have been merged with in the first place.</para>
    ///
    /// <para><b>Determinism.</b> The generated packet contains no timestamps and the writer
    /// re-normalises indentation and <c>xpacket</c> padding, so running the operation twice over
    /// the same input produces byte-identical XMP. Pass a <c>modifyDate</c> through the extended
    /// overloads to opt into <c>xmp:ModifyDate</c> / <c>xmp:MetadataDate</c>.</para>
    ///
    /// <para><b>Output contract.</b> The result is written as a single appended revision through
    /// <c>DocumentRevisionWriter</c> — the same path every other accessibility writer
    /// takes — which revises the existing <c>/Metadata</c> object, or allocates one and patches
    /// the catalog when the entry is missing. Going through that writer is what makes the appended
    /// cross-reference section match the form of the section it supersedes: a classic <c>xref</c>
    /// table over a classic table, a <c>/Type /XRef</c> stream over an xref stream. Appending a
    /// classic table (with a classic <c>trailer</c> carrying the parser's merged xref-stream keys)
    /// to an xref-stream document produces a hybrid that independent checkers reject and that an
    /// xref-stream-only reader cannot follow at all — and because this writer runs <i>last</i> in
    /// the remediation pipeline, that malformed section would be the one every reader lands on.
    /// On failure the original bytes are copied to <c>output</c> unchanged and <c>false</c> is
    /// returned, so a remediation pipeline that chains streams never ends up with an empty or
    /// truncated document.</para>
    ///
    /// <para><b>Idempotent — a second run costs nothing.</b> Because the packet is deterministic,
    /// a document that already carries exactly the packet this call would write needs no revision.
    /// That case is detected before anything is appended: the input is copied through byte for
    /// byte, <c>true</c> is returned (the output does declare <c>pdfuaid:part</c>) and
    /// <see cref="LastCallAppendedRevision"/> is <c>false</c>. Without that check, calling the
    /// operation twice grew the file by a no-op ~3&#160;KB revision each time — <c>RemediateWithReport</c>
    /// gates its own step this way, so only the standalone public API was affected.</para>
    ///
    /// <para><b>Encrypted documents go through <see cref="Plan"/>.</b> This entry point emits the
    /// new metadata stream in clear text, which would not match the document's encryption
    /// dictionary, so it passes an encrypted document through untouched and reports it via
    /// <c>AccessibilityDiagnostics</c>. That is not the end of the road: <see cref="Plan"/>
    /// computes the same revision without writing it, and
    /// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c> encrypts it with the document's own file
    /// key and appends it — in place, so an existing signature and the owner password both survive.
    /// This assembly still cannot decrypt and must not; the operation travels to the decryptor.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// using var input  = File.OpenRead("tagged.pdf");
    /// using var output = File.Create("tagged-ua.pdf");
    /// bool ok = PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part: 1,
    ///                                             title: "Quarterly Report", language: "en-US");
    /// if (!ok) Console.Error.WriteLine(PdfUaXmpWriter.LastError);
    /// </code>
    /// </example>
    public static class PdfUaXmpWriter
    {
        // ----------------------------------------------------------------
        //  Namespace constants
        // ----------------------------------------------------------------

        // Defined in Core.Metadata.XmpPacket, which is the only ring the Conformance plugin and
        // Base can also reach — they had the same five URIs as literals. These stay on this type
        // because they are part of its published surface; `const` forwarding is compile-time, so a
        // consumer sees exactly the same value it always did.

        /// <summary>The PDF/UA identification schema namespace.</summary>
        public const string PdfUaIdNamespace = XmpPacket.PdfUaIdNamespace;

        /// <summary>The Dublin Core element namespace (<c>dc:title</c>, <c>dc:language</c>).</summary>
        public const string DublinCoreNamespace = XmpPacket.DublinCoreNamespace;

        /// <summary>The RDF syntax namespace.</summary>
        public const string RdfNamespace = XmpPacket.RdfNamespace;

        /// <summary>The XMP basic schema namespace (<c>xmp:ModifyDate</c>, <c>xmp:MetadataDate</c>).</summary>
        public const string XmpBasicNamespace = XmpPacket.XmpBasicNamespace;

        /// <summary>The Adobe XMP container namespace (<c>x:xmpmeta</c>).</summary>
        public const string XmpMetaNamespace = XmpPacket.XmpMetaNamespace;

        /// <summary>Maximum node depth walked when searching an existing packet (DoS guard).</summary>
        public const int MaxSearchDepth = XmpPacketDom.MaxSearchDepth;

        /// <summary>
        /// Largest decoded <c>/Metadata</c> stream, in bytes, that will be merged with (DoS guard).
        /// An XMP packet is metadata, not a payload — the specification even recommends keeping it
        /// small enough to rewrite in place — so anything past this ceiling is either corrupt or a
        /// decompression bomb, and is discarded in favour of a freshly generated packet.
        ///
        /// <para>Necessary because <c>PdfStream.GetDecodedData()</c> is bounded only by
        /// <c>PdfLimits.MaxDecompressedBytes</c> (512&#160;MB), which is a filter-level cap sized
        /// for image data, not for a metadata packet.</para>
        /// </summary>
        public const int MaxExistingPacketBytes = XmpPacketDom.MaxExistingPacketBytes;

        /// <summary>
        /// Ceiling on the projected size, in characters, of the re-serialised merged packet
        /// (DoS guard). Checked <i>before</i> serialising — see <see cref="MaxSearchDepth"/> for
        /// why indented output is superlinear in nesting depth — so the oversized string is never
        /// built at all.
        /// </summary>
        public const int MaxMergedPacketChars = XmpPacketDom.MaxMergedPacketChars;

        private const string XmlSpecNamespace = "http://www.w3.org/XML/1998/namespace";

        /// <summary>Toolkit marker written only into freshly generated packets (no version — determinism).</summary>
        private const string XmpToolkit = "Tetitulz.Pdf Accessibility";

        /// <summary>Operation name used for all diagnostics raised by this class.</summary>
        private const string DiagnosticOperation = "PdfUaXmpWriter";

        /// <summary>Bytes of trailing whitespace padding written before <c>&lt;?xpacket end?&gt;</c>.</summary>
        private const int PaddingLength = 2048;

        private const int PaddingLineLength = 64;


        private static readonly string Padding = BuildPadding();

        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        [ThreadStatic] private static string _lastXmpWriteError;

        [ThreadStatic] private static bool _lastCallAppendedRevision;

        /// <summary>
        /// Description of the most recent failure on the calling thread, or <c>null</c> if the
        /// last call succeeded. Set whenever one of the <c>SetPdfUaIdentifier</c> overloads
        /// returns <c>false</c>. Thread-local, so concurrent callers never observe each other's
        /// errors.
        /// </summary>
        public static string LastError => _lastXmpWriteError;

        /// <summary>
        /// Whether the most recent <c>SetPdfUaIdentifier</c> call on the calling thread actually
        /// appended a revision.
        ///
        /// <para>
        /// <c>true</c> means a new <c>/Metadata</c> stream was written. <c>false</c> after a call
        /// that returned <c>true</c> means the document <b>already carried exactly the packet that
        /// would have been written</b>, so the input was copied through byte for byte and the file
        /// did not grow — the operation succeeded without costing a revision. <c>false</c> after a
        /// call that returned <c>false</c> simply means the write failed; read
        /// <see cref="LastError"/> for that case.
        /// </para>
        ///
        /// <para>
        /// The <c>bool</c> returns of the <c>SetPdfUaIdentifier</c> overloads answer "does the
        /// output declare <c>pdfuaid:part</c>?", which is the question a remediation caller has.
        /// This answers the separate question "did the file change?", which is the one a caller
        /// driving the steps itself needs in order to decide whether to keep the result. It is
        /// thread-local for the same reason <see cref="LastError"/> is.
        /// </para>
        /// </summary>
        public static bool LastCallAppendedRevision => _lastCallAppendedRevision;

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Adds or replaces the PDF/UA identifier in the XMP metadata of the PDF read from
        /// <paramref name="input"/> and writes the updated document to <paramref name="output"/>.
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF. Never modified.</param>
        /// <param name="output">
        /// Writable stream that receives the updated PDF. On failure it receives an exact copy of
        /// the source bytes so that chained remediation steps keep working.
        /// </param>
        /// <param name="part">
        /// The PDF/UA part to declare: <c>1</c> for PDF/UA-1 (ISO 14289-1) or <c>2</c> for
        /// PDF/UA-2 (ISO 14289-2).
        /// </param>
        /// <param name="title">
        /// Optional document title written as <c>dc:title</c> in <c>rdf:Alt</c> /
        /// <c>rdf:li xml:lang="x-default"</c> form. Null or whitespace leaves any existing title
        /// untouched.
        /// </param>
        /// <param name="language">
        /// Optional BCP-47 language tag (e.g. <c>"en-US"</c>) written as <c>dc:language</c> in
        /// <c>rdf:Bag</c> form. Null or whitespace leaves any existing value untouched.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="output"/> holds a PDF declaring the requested
        /// identifier — whether this call appended it or the document already carried exactly that
        /// packet (see <see cref="LastCallAppendedRevision"/> to tell the two apart); otherwise
        /// <c>false</c> (inspect <see cref="LastError"/>).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static bool SetPdfUaIdentifier(
            Stream input,
            Stream output,
            int part = 1,
            string title = null,
            string language = null)
        {
            return SetPdfUaIdentifier(input, output, part, title, language, null);
        }

        /// <summary>
        /// File-path overload of
        /// <see cref="SetPdfUaIdentifier(Stream, Stream, int, string, string)"/>. The input file is
        /// streamed rather than buffered whole, and the output file is created (or truncated).
        /// </summary>
        /// <param name="inputPath">Path of the source PDF.</param>
        /// <param name="outputPath">Path of the PDF to create.</param>
        /// <param name="part">PDF/UA part to declare (1 or 2).</param>
        /// <param name="title">Optional <c>dc:title</c> value.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> value.</param>
        /// <returns><c>true</c> on success — including the already-correct case, where nothing is
        /// appended and <see cref="LastCallAppendedRevision"/> stays <c>false</c>; otherwise
        /// <c>false</c> (see <see cref="LastError"/>).</returns>
        /// <exception cref="ArgumentNullException">A path argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static bool SetPdfUaIdentifier(
            string inputPath,
            string outputPath,
            int part = 1,
            string title = null,
            string language = null)
        {
            return SetPdfUaIdentifier(inputPath, outputPath, part, title, language, null);
        }

        /// <summary>
        /// Extended overload that additionally stamps <c>xmp:ModifyDate</c> and
        /// <c>xmp:MetadataDate</c>. Supplying <paramref name="modifyDate"/> is the only way to get
        /// a timestamp into the packet — the default overloads stay deterministic on purpose so
        /// that regression tests can compare output byte for byte.
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream that receives the updated (or, on failure, the
        /// unmodified) PDF.</param>
        /// <param name="part">PDF/UA part to declare (1 or 2).</param>
        /// <param name="title">Optional <c>dc:title</c> value.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> value.</param>
        /// <param name="modifyDate">Timestamp to record, or <c>null</c> for a timestamp-free packet.</param>
        /// <returns><c>true</c> on success — including the already-correct case, where nothing is
        /// appended and <see cref="LastCallAppendedRevision"/> stays <c>false</c>; otherwise
        /// <c>false</c> (see <see cref="LastError"/>).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static bool SetPdfUaIdentifier(
            Stream input,
            Stream output,
            int part,
            string title,
            string language,
            DateTimeOffset? modifyDate)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            ValidatePart(part);

            _lastXmpWriteError = null;
            _lastCallAppendedRevision = false;

            bool written = XmpMetadataRevision.Write(input, output, (doc, existingXmp) =>
            {
                string xmp = BuildPdfUaXmpPacket(existingXmp, part, title, language, modifyDate);
                PdfName version = VersionToDeclare(doc, part);

                // The packet builder is deterministic — no timestamp unless the caller asks for one,
                // indentation and xpacket padding regenerated — so a document whose packet already
                // equals this one, at the version already declared, needs no revision: appending it
                // anyway grows the file by some 3 KB a call for nothing. RemediateWithReport gates the
                // step on the same comparison.
                bool current = version == null && existingXmp != null && string.Equals(xmp, existingXmp, StringComparison.Ordinal);
                return new XmpMetadataRevision.PacketPlan(current ? null : xmp, version);
            }, Fail, Report, out bool appended);
            _lastCallAppendedRevision = appended;
            return written;
        }

        /// <summary>
        /// Extended file-path overload of
        /// <see cref="SetPdfUaIdentifier(Stream, Stream, int, string, string, DateTimeOffset?)"/>.
        /// </summary>
        /// <param name="inputPath">Path of the source PDF.</param>
        /// <param name="outputPath">Path of the PDF to create.</param>
        /// <param name="part">PDF/UA part to declare (1 or 2).</param>
        /// <param name="title">Optional <c>dc:title</c> value.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> value.</param>
        /// <param name="modifyDate">Timestamp to record, or <c>null</c> for a timestamp-free packet.</param>
        /// <returns><c>true</c> on success — including the already-correct case, where nothing is
        /// appended and <see cref="LastCallAppendedRevision"/> stays <c>false</c>; otherwise
        /// <c>false</c> (see <see cref="LastError"/>).</returns>
        /// <exception cref="ArgumentNullException">A path argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static bool SetPdfUaIdentifier(
            string inputPath,
            string outputPath,
            int part,
            string title,
            string language,
            DateTimeOffset? modifyDate)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            ValidatePart(part);

            using var inFs = File.OpenRead(inputPath);
            using var outFs = File.Create(outputPath);
            return SetPdfUaIdentifier(inFs, outFs, part, title, language, modifyDate);
        }

        /// <summary>
        /// Builds the XMP packet text that <see cref="SetPdfUaIdentifier(Stream, Stream, int, string, string)"/>
        /// embeds, without touching a PDF. Exposed because it is independently useful (and
        /// directly testable): callers that already manage their own <c>/Metadata</c> stream can
        /// merge the PDF/UA identifier into it themselves.
        /// </summary>
        /// <param name="existingXmp">
        /// The current XMP packet, or <c>null</c>/whitespace for none. If it is not well-formed
        /// XML — or contains no <c>rdf:RDF</c> element, or trips one of the amplification bounds
        /// (<see cref="MaxMergedPacketChars"/>, <see cref="MaxSearchDepth"/>) — a diagnostic is
        /// reported and a fresh packet is generated instead of throwing, because refusing to write
        /// would leave the PDF/UA violation unfixable.
        /// </param>
        /// <param name="part">PDF/UA part to declare (1 or 2).</param>
        /// <param name="title">Optional <c>dc:title</c> value.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> value.</param>
        /// <param name="modifyDate">Optional timestamp for <c>xmp:ModifyDate</c> / <c>xmp:MetadataDate</c>.</param>
        /// <returns>A complete XMP packet including the <c>xpacket</c> processing instructions.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static string BuildPdfUaXmpPacket(
            string existingXmp,
            int part = 1,
            string title = null,
            string language = null,
            DateTimeOffset? modifyDate = null)
        {
            ValidatePart(part);
            title = XmlLegal(title, "title");
            language = XmlLegal(language, "language");

            XmlDocument doc = null;
            XmlElement description = null;

            if (!string.IsNullOrWhiteSpace(existingXmp) && XmpPacketDom.IsWithinMergeBudget(existingXmp, Report))
            {
                doc = XmpPacketDom.TryLoad(existingXmp, Report);

                // Reject the packet before anything is merged into it: a packet that nests past
                // the search cap has nothing this writer could reach anyway, and re-serialising
                // it with indentation costs O(depth^2) characters. IsMergeable reports its own
                // diagnostic naming which bound was hit.
                if (doc != null && !XmpPacketDom.IsMergeable(doc, Report)) doc = null;

                if (doc != null)
                {
                    description = XmpPacketDom.EnsureDescription(doc);
                    if (description == null)
                    {
                        AccessibilityDiagnostics.Report(
                            DiagnosticOperation,
                            "The existing XMP packet contains no rdf:RDF element; " +
                            "a new packet was generated in its place.");
                        doc = null;
                    }
                }
            }

            if (doc == null)
            {
                doc = XmpPacketDom.CreateEmpty(XmpToolkit);
                description = XmpPacketDom.EnsureDescription(doc);
            }

            SetSimpleProperty(doc, description, PdfUaIdNamespace, "pdfuaid", "part",
                part.ToString(CultureInfo.InvariantCulture));

            // ISO 14289-2 Clause 5 (Table 1): a PDF/UA-2 file also names the revision of the part it
            // conforms to, as the four digits of the year — ISO 14289-2:2024.
            bool withRevision = part == 2;
            if (withRevision)
                SetSimpleProperty(doc, description, PdfUaIdNamespace, "pdfuaid", "rev", PdfUa2Revision);

            // A PDF/A-1, -2 or -3 packet may hold only properties of a predefined schema or of one it
            // describes (ISO 19005-1 6.7.9, 19005-2/3 6.6.2.3.1), and neither edition of the XMP
            // Specification those parts cite predefines pdfuaid; so in such a packet the schema is
            // described too, or declaring PDF/UA would break the PDF/A declaration beside it.
            if (DeclaresPdfAPartOneToThree(doc))
                EnsurePdfUaExtensionSchema(doc, description, withRevision);

            if (!string.IsNullOrWhiteSpace(title))
                SetLanguageAlternative(doc, description, DublinCoreNamespace, "dc", "title", title);

            if (!string.IsNullOrWhiteSpace(language))
                SetUnorderedArray(doc, description, DublinCoreNamespace, "dc", "language", language);

            if (modifyDate.HasValue)
            {
                string stamp = modifyDate.Value.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
                SetSimpleProperty(doc, description, XmpBasicNamespace, "xmp", "ModifyDate", stamp);
                SetSimpleProperty(doc, description, XmpBasicNamespace, "xmp", "MetadataDate", stamp);
            }

            return XmpPacketDom.Serialize(doc, Padding);
        }

        // ================================================================
        //  Packet construction
        // ================================================================

        /// <summary><paramref name="value"/> with what XML 1.0 forbids replaced, and said: the serializer would throw on it, and a host's title may hold a control character.</summary>
        private static string XmlLegal(string value, string what)
        {
            if (value == null) return null;
            string legal = Core.Text.XmlLegalText.Replace(value, out bool changed);
            if (changed)
                AccessibilityDiagnostics.Report(DiagnosticOperation,
                    "The " + what + " held a character XML forbids; it was replaced with U+FFFD in the packet.");
            return legal;
        }

        private const string PdfAExtensionNamespace = "http://www.aiim.org/pdfa/ns/extension/";
        private const string PdfASchemaNamespace = "http://www.aiim.org/pdfa/ns/schema#";
        private const string PdfAPropertyNamespace = "http://www.aiim.org/pdfa/ns/property#";

        /// <summary>Whether the packet's <c>pdfaid:part</c> is 1, 2 or 3 — the parts with the predefined-schema rule.</summary>
        private static bool DeclaresPdfAPartOneToThree(XmlDocument doc)
        {
            string part = XmpPacketDom.FindElement(doc.DocumentElement, XmpPacket.PdfAIdNamespace, "part")?.InnerText
                ?? XmpPacketDom.FindAttribute(doc.DocumentElement, XmpPacket.PdfAIdNamespace, "part")?.Value;
            part = part?.Trim();
            return part == "1" || part == "2" || part == "3";
        }

        /// <summary>
        /// Describes the PDF/UA identification schema in the packet's <c>pdfaExtension:schemas</c>
        /// (ISO 19005-1 6.7.8, 19005-2/3 6.6.2.3.3), unless a description of its namespace is
        /// already there. An existing container that is not a Bag is left alone and reported.
        /// </summary>
        private static void EnsurePdfUaExtensionSchema(XmlDocument doc, XmlElement description, bool withRevision)
        {
            var schemas = XmpPacketDom.FindElement(doc.DocumentElement, PdfAExtensionNamespace, "schemas");
            XmlElement bag = null;
            if (schemas == null)
            {
                schemas = doc.CreateElement("pdfaExtension", "schemas", PdfAExtensionNamespace);
                bag = doc.CreateElement("rdf", "Bag", RdfNamespace);
                schemas.AppendChild(bag);
                description.AppendChild(schemas);
            }
            else
            {
                foreach (XmlNode child in schemas.ChildNodes)
                    if (child is XmlElement e && e.NamespaceURI == RdfNamespace && e.LocalName == "Bag")
                        bag = e;
                if (bag == null)
                {
                    AccessibilityDiagnostics.Report(
                        DiagnosticOperation,
                        "The packet's pdfaExtension:schemas is not a Bag, so the pdfuaid schema could not be " +
                        "described in it; the PDF/A declaration beside pdfuaid:part is then not met.");
                    return;
                }
                foreach (XmlNode uri in bag.GetElementsByTagName("namespaceURI", PdfASchemaNamespace))
                    if (uri.InnerText.Trim() == PdfUaIdNamespace)
                    {
                        // Described already — by this writer for a part 1 declaration, or by the
                        // producer — and a part 2 declaration adds pdfuaid:rev, which must be described too.
                        if (withRevision) EnsureRevisionDescribed(doc, uri.ParentNode as XmlElement);
                        return;
                    }
            }

            var schema = Resource(doc);
            AppendText(doc, schema, "pdfaSchema", "schema", PdfASchemaNamespace, "PDF/UA Universal Accessibility Schema");
            AppendText(doc, schema, "pdfaSchema", "namespaceURI", PdfASchemaNamespace, PdfUaIdNamespace);
            AppendText(doc, schema, "pdfaSchema", "prefix", PdfASchemaNamespace, "pdfuaid");
            var property = doc.CreateElement("pdfaSchema", "property", PdfASchemaNamespace);
            var seq = doc.CreateElement("rdf", "Seq", RdfNamespace);
            seq.AppendChild(PartProperty(doc));
            if (withRevision) seq.AppendChild(RevisionProperty(doc));
            property.AppendChild(seq);
            schema.AppendChild(property);
            bag.AppendChild(schema);
        }

        /// <summary>The year of ISO 14289-2's current edition, which a PDF/UA-2 file declares as <c>pdfuaid:rev</c>.</summary>
        internal const string PdfUa2Revision = "2024";

        private static XmlElement PartProperty(XmlDocument doc)
        {
            var part = Resource(doc);
            AppendText(doc, part, "pdfaProperty", "name", PdfAPropertyNamespace, "part");
            AppendText(doc, part, "pdfaProperty", "valueType", PdfAPropertyNamespace, "Integer");
            AppendText(doc, part, "pdfaProperty", "category", PdfAPropertyNamespace, "internal");
            AppendText(doc, part, "pdfaProperty", "description", PdfAPropertyNamespace,
                "Indicates which part of ISO 14289 the file conforms to");
            return part;
        }

        private static XmlElement RevisionProperty(XmlDocument doc)
        {
            var rev = Resource(doc);
            AppendText(doc, rev, "pdfaProperty", "name", PdfAPropertyNamespace, "rev");
            AppendText(doc, rev, "pdfaProperty", "valueType", PdfAPropertyNamespace, "Integer");
            AppendText(doc, rev, "pdfaProperty", "category", PdfAPropertyNamespace, "internal");
            AppendText(doc, rev, "pdfaProperty", "description", PdfAPropertyNamespace,
                "Indicates the year of the revision of the part of ISO 14289 the file conforms to");
            return rev;
        }

        /// <summary>
        /// Adds <c>rev</c> to an existing pdfuaid schema description's properties, unless it is there;
        /// a description whose properties are not a Seq is left alone and reported.
        /// </summary>
        private static void EnsureRevisionDescribed(XmlDocument doc, XmlElement schema)
        {
            if (schema == null) return;
            foreach (XmlNode name in schema.GetElementsByTagName("name", PdfAPropertyNamespace))
                if (name.InnerText.Trim() == "rev") return;

            XmlElement property = null, seq = null;
            foreach (XmlNode child in schema.ChildNodes)
                if (child is XmlElement e && e.NamespaceURI == PdfASchemaNamespace && e.LocalName == "property") property = e;
            if (property == null)
            {
                property = doc.CreateElement("pdfaSchema", "property", PdfASchemaNamespace);
                schema.AppendChild(property);
            }
            foreach (XmlNode child in property.ChildNodes)
                if (child is XmlElement e && e.NamespaceURI == RdfNamespace && e.LocalName == "Seq") seq = e;
            if (seq == null && property.HasChildNodes)
            {
                AccessibilityDiagnostics.Report(DiagnosticOperation,
                    "The packet's pdfuaid schema description lists its properties other than as a Seq, so " +
                    "pdfuaid:rev could not be described; the PDF/A declaration beside it is then not met.");
                return;
            }
            if (seq == null)
            {
                seq = doc.CreateElement("rdf", "Seq", RdfNamespace);
                property.AppendChild(seq);
            }
            seq.AppendChild(RevisionProperty(doc));
        }

        /// <summary>An <c>rdf:li rdf:parseType="Resource"</c>: an array item that is a structure.</summary>
        private static XmlElement Resource(XmlDocument doc)
        {
            var li = doc.CreateElement("rdf", "li", RdfNamespace);
            var parseType = doc.CreateAttribute("rdf", "parseType", RdfNamespace);
            parseType.Value = "Resource";
            li.Attributes.Append(parseType);
            return li;
        }

        private static void AppendText(XmlDocument doc, XmlElement parent, string prefix, string localName, string ns, string text)
        {
            var e = doc.CreateElement(prefix, localName, ns);
            e.AppendChild(doc.CreateTextNode(text));
            parent.AppendChild(e);
        }

        // ----------------------------------------------------------------
        //  Property setters (merge semantics)
        // ----------------------------------------------------------------

        /// <summary>
        /// Sets a simple (single-valued) XMP property. An existing element form is updated in
        /// place wherever it lives in the packet; failing that, an existing RDF attribute-shorthand
        /// form is updated; failing that, a new element is appended to
        /// <paramref name="description"/>. Only one of the two forms is ever produced, so the
        /// result never carries a duplicate property.
        /// </summary>
        private static void SetSimpleProperty(
            XmlDocument doc,
            XmlElement description,
            string namespaceUri,
            string prefix,
            string localName,
            string value)
        {
            var existing = XmpPacketDom.FindElement(doc.DocumentElement, namespaceUri, localName);
            if (existing != null)
            {
                existing.RemoveAll();
                existing.AppendChild(doc.CreateTextNode(value));
                return;
            }

            var attribute = XmpPacketDom.FindAttribute(doc.DocumentElement, namespaceUri, localName);
            if (attribute != null)
            {
                attribute.Value = value;
                return;
            }

            var created = doc.CreateElement(prefix, localName, namespaceUri);
            created.AppendChild(doc.CreateTextNode(value));
            description.AppendChild(created);
        }

        /// <summary>
        /// Sets a language-alternative property (<c>rdf:Alt</c> containing a single
        /// <c>rdf:li xml:lang="x-default"</c>). This is exactly the shape
        /// <c>PdfUaChecker.FindXmpDcTitle</c> accepts — it scans the <c>dc:title</c> subtree for a
        /// non-empty text node — so the attribute shorthand is removed first to guarantee the
        /// element form wins.
        /// </summary>
        private static void SetLanguageAlternative(
            XmlDocument doc,
            XmlElement description,
            string namespaceUri,
            string prefix,
            string localName,
            string value)
        {
            RemoveAttributeForm(doc.DocumentElement, namespaceUri, localName);

            var target = XmpPacketDom.FindElement(doc.DocumentElement, namespaceUri, localName);
            if (target == null)
            {
                target = doc.CreateElement(prefix, localName, namespaceUri);
                description.AppendChild(target);
            }
            target.RemoveAll();

            var alt = doc.CreateElement("rdf", "Alt", RdfNamespace);
            var item = doc.CreateElement("rdf", "li", RdfNamespace);

            var lang = doc.CreateAttribute("xml", "lang", XmlSpecNamespace);
            lang.Value = "x-default";
            item.Attributes.Append(lang);
            item.AppendChild(doc.CreateTextNode(value));

            alt.AppendChild(item);
            target.AppendChild(alt);
        }

        /// <summary>
        /// Sets an unordered-array property (<c>rdf:Bag</c> containing a single <c>rdf:li</c>),
        /// which is the XMP form required for <c>dc:language</c>.
        /// </summary>
        private static void SetUnorderedArray(
            XmlDocument doc,
            XmlElement description,
            string namespaceUri,
            string prefix,
            string localName,
            string value)
        {
            RemoveAttributeForm(doc.DocumentElement, namespaceUri, localName);

            var target = XmpPacketDom.FindElement(doc.DocumentElement, namespaceUri, localName);
            if (target == null)
            {
                target = doc.CreateElement(prefix, localName, namespaceUri);
                description.AppendChild(target);
            }
            target.RemoveAll();

            var bag = doc.CreateElement("rdf", "Bag", RdfNamespace);
            var item = doc.CreateElement("rdf", "li", RdfNamespace);
            item.AppendChild(doc.CreateTextNode(value));
            bag.AppendChild(item);
            target.AppendChild(bag);
        }

        /// <summary>
        /// Removes every RDF attribute-shorthand occurrence of a property so that the element form
        /// written afterwards is not a duplicate. One walk collects them, under the search's depth cap,
        /// then they are removed: a search from the root per removal made a packet carrying the attribute
        /// on 120 000 elements 134 s (07's DoS hunt, F10).
        /// </summary>
        private static void RemoveAttributeForm(XmlNode root, string namespaceUri, string localName)
        {
            foreach (var attribute in XmpPacketDom.FindAttributes(root, namespaceUri, localName))
                attribute.OwnerElement?.RemoveAttributeNode(attribute);
        }

        // ----------------------------------------------------------------
        //  Depth-capped node searches
        // ----------------------------------------------------------------

        // ----------------------------------------------------------------
        //  Amplification guards
        // ----------------------------------------------------------------

        // ----------------------------------------------------------------
        //  Serialisation
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds the fixed whitespace padding recommended by the XMP specification so that
        /// downstream tools can rewrite the packet in place. Deterministic by construction.
        /// </summary>
        private static string BuildPadding()
        {
            var sb = new StringBuilder(PaddingLength);
            int lines = PaddingLength / PaddingLineLength;
            for (int i = 0; i < lines; i++)
            {
                sb.Append(' ', PaddingLineLength - 1);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ================================================================
        //  PDF plumbing helpers
        // ================================================================

        /// <summary>
        /// Computes the PDF/UA identifier revision <b>without writing it</b>, so a caller that owns
        /// the write can append it to bytes this method never sees.
        /// </summary>
        /// <remarks>
        /// <para><b>What this is for.</b>
        /// <see cref="SetPdfUaIdentifier(Stream, Stream, int, string, string, DateTimeOffset?)"/>
        /// appends a <i>clear-text</i> revision and therefore refuses an encrypted document — a
        /// clear-text revision does not match the encryption dictionary and is unreadable. This
        /// assembly cannot decrypt (it does not reference <c>Tetitulz.Pdf.Encryption</c>, and must
        /// not), so the operation travels to the decryptor instead, exactly as
        /// <see cref="Tagging.PdfAutoTagger.Plan"/> already does:</para>
        /// <code>
        /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
        ///     (decrypted, firstFree) =&gt; PdfUaXmpWriter.Plan(decrypted, firstFree, part: 1));
        /// </code>
        /// <para><b><paramref name="firstFreeObjectNumber"/> is not a convenience.</b> The document
        /// handed in is a <i>decrypted copy</i> of the file the revision is appended to, and
        /// decryption unpacks object streams and drops the cross-reference stream — so the copy's
        /// highest object number can be <i>lower</i> than the original's. Allocating from the
        /// copy's own ceiling would mint a number that is live in the file being written, and the
        /// appended object would silently supersede it.</para>
        /// <para><b>An empty list is a real answer, not a failure.</b> The packet builder is
        /// deterministic, so a document whose existing XMP already equals what would be written
        /// needs no revision at all — appending one would grow the file by ~3&#160;KB for nothing.
        /// The caller should append nothing and treat the document as already conformant, which is
        /// the same judgement the clear-text path makes through
        /// <see cref="LastCallAppendedRevision"/>.</para>
        /// <para>The unencrypted case needs none of this and should keep using
        /// <c>SetPdfUaIdentifier</c>, which owns the write and so can verify it landed.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate — the object-number ceiling of the file
        /// the revision will be appended to, plus one.
        /// </param>
        /// <param name="part">PDF/UA part to declare (1 or 2).</param>
        /// <param name="title">Optional <c>dc:title</c> value.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> value.</param>
        /// <param name="modifyDate">Timestamp to record, or <c>null</c> for a timestamp-free packet.</param>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="part"/> is not 1 or 2, or <paramref name="firstFreeObjectNumber"/> is
        /// below 1 — object number 0 is the free-list head and can never be written.
        /// </exception>
        public static IReadOnlyList<IndirectObject> Plan(
            PdfDocument doc,
            int firstFreeObjectNumber,
            int part = 1,
            string title = null,
            string language = null,
            DateTimeOffset? modifyDate = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");
            ValidatePart(part);

            _lastXmpWriteError = null;

            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                Fail("The document has no catalog dictionary; it is malformed.", null);
                return Array.Empty<IndirectObject>();
            }

            string existingXmp = XmpPacketDom.ReadCatalogPacket(doc, catalog, Report);
            string xmp = BuildPdfUaXmpPacket(existingXmp, part, title, language, modifyDate);

            PdfName version = VersionToDeclare(doc, part);

            // Already correct: appending a byte-identical packet is a no-op that costs ~3 KB.
            if (version == null && existingXmp != null && string.Equals(xmp, existingXmp, StringComparison.Ordinal))
                return Array.Empty<IndirectObject>();

            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            // Lift the allocator to the TARGET file's ceiling before anything is minted.
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            XmpMetadataRevision.Build(doc, index, catalog, xmp, version,
                out Dictionary<int, PdfObject> modified, out Dictionary<int, PdfObject> created, Report);

            var list = RevisionObjects.From(
                created, modified, index, "PdfUaXmpWriter.Plan");

            return list;
        }

        /// <summary>
        /// The catalog <c>/Version</c> a declaration of <paramref name="part"/> needs written beside it:
        /// <c>/2.0</c> for PDF/UA-2 in a file whose version is below it — ISO 14289-2 builds on
        /// ISO 32000-2, and its first rule after the identification is "The file version shall be 2.0,
        /// specified either in the header or as the value of the Version entry" (veraPDF's PDF/UA-2
        /// profile, 6.1-1) — and null otherwise. An incremental revision cannot rewrite the header, so
        /// the catalog says it.
        /// </summary>
        internal static PdfName VersionToDeclare(PdfDocument doc, int part)
            => part == 2 && doc.Version.CompareTo(new PdfVersion(2, 0)) < 0 ? Pdf20Version : null;

        private static readonly PdfName Pdf20Version = new PdfName("2.0");

        private static void ValidatePart(int part)
        {
            if (part != 1 && part != 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(part), part,
                    "PDF/UA defines part 1 (ISO 14289-1) and part 2 (ISO 14289-2) only.");
            }
        }

        /// <summary>
        /// The give-ups <c>XmpPacketDom</c> and <c>XmpMetadataRevision</c> work around, reported
        /// to this feature's sink under this writer's operation name — the diagnostics these
        /// helpers raised inline when they were this file's own copies.
        /// </summary>
        private static void Report(string detail, Exception ex)
            => AccessibilityDiagnostics.Report(DiagnosticOperation, detail, ex);

        /// <summary>
        /// Records a failure on the calling thread and forwards it to the accessibility
        /// diagnostics sink so hosts can log it. Never swallows the cause.
        /// </summary>
        private static void Fail(string detail, Exception ex)
        {
            _lastXmpWriteError = ex == null ? detail : detail + " " + ex.Message;
            AccessibilityDiagnostics.Report(DiagnosticOperation, detail, ex);
        }
    }
}

