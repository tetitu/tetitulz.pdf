// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// The revision that makes an XMP packet the document's <c>/Metadata</c>: the stream, and — when
    /// the catalog has none yet — a revised catalog that points at it.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation</b>, for <c>PdfMetadataWriter.WriteXmpMetadata</c>,
    /// <c>PdfDeclarationsWriter</c> and the Accessibility feature's <c>PdfUaXmpWriter</c>: each
    /// give-up reported, the catalog and the stream kept at their own generations, and the
    /// cross-reference section in the document's own form.</para>
    ///
    /// <para><b>Public because the Accessibility feature needs it</b>, and Base names no feature in
    /// its <c>InternalsVisibleTo</c>. Its writer computes the same revision to encrypt it
    /// (<c>PdfUaXmpWriter.Plan</c>), which is why the objects are available on their own.</para>
    /// </remarks>
    public static class XmpMetadataRevision
    {
        /// <summary>
        /// The objects of the revision, computed against <paramref name="index"/> and written by
        /// nobody: the new <c>/Metadata</c> stream (superseding the existing one at its own number
        /// and generation, or created), and the revised catalog when the stream is new.
        /// </summary>
        /// <param name="doc">The document the revision is for.</param>
        /// <param name="index">Its object index, which allocates a new number and knows generations.</param>
        /// <param name="catalog">Its catalog dictionary.</param>
        /// <param name="xmp">The complete packet, <c>xpacket</c> wrapper included.</param>
        /// <param name="modified">Objects superseded, keyed by object number.</param>
        /// <param name="created">Objects added, keyed by object number.</param>
        /// <param name="report">Receives each malformed entry worked around; may be null.</param>
        /// <exception cref="PdfException">
        /// The stream is new and the trailer's <c>/Root</c> is a direct dictionary, so there is no
        /// catalog object to revise — writing the stream alone would leave it unreferenced, a
        /// no-op dressed up as success.
        /// </exception>
        public static void Build(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, string xmp,
            out Dictionary<int, PdfObject> modified, out Dictionary<int, PdfObject> created,
            Action<string, Exception> report = null)
            => Build(doc, index, catalog, xmp, null, out modified, out created, report);

        /// <summary>
        /// <see cref="Build(PdfDocument, PdfObjectIndex, PdfDictionary, string, out Dictionary{int, PdfObject}, out Dictionary{int, PdfObject}, Action{string, Exception})"/>,
        /// the revised catalog also declaring <paramref name="catalogVersion"/> as its <c>/Version</c>
        /// (§7.7.2: it takes precedence over the header's when later) when not null — a declaration
        /// the packet makes that needs the file's version, PDF/UA-2's 2.0, written in the same revision.
        /// </summary>
        public static void Build(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, string xmp, PdfName catalogVersion,
            out Dictionary<int, PdfObject> modified, out Dictionary<int, PdfObject> created,
            Action<string, Exception> report = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (xmp == null) throw new ArgumentNullException(nameof(xmp));

            modified = new Dictionary<int, PdfObject>();
            created = new Dictionary<int, PdfObject>();

            PdfStream metadataStream = MetadataStream(xmp);

            PdfDictionary revisedCatalog = null;
            int metadataObjectNumber = ExistingMetadataObjectNumber(doc, index, catalog, report);
            if (metadataObjectNumber > 0)
                modified[metadataObjectNumber] = metadataStream;
            else
            {
                metadataObjectNumber = index.AllocateObjectNumber();
                created[metadataObjectNumber] = metadataStream;
                revisedCatalog = PdfDictionary.CloneShallow(catalog);
                revisedCatalog.Set(PdfName.Metadata, new PdfReference(metadataObjectNumber, 0));
            }

            if (catalogVersion != null)
            {
                revisedCatalog ??= PdfDictionary.CloneShallow(catalog);
                revisedCatalog.Set(PdfName.Version, catalogVersion);
            }
            if (revisedCatalog != null) modified[CatalogObjectNumber(doc)] = revisedCatalog;
        }

        /// <summary>
        /// <paramref name="original"/> with the revision appended, through
        /// <see cref="DocumentRevisionWriter"/> — which writes a cross-reference section of the
        /// same form as the one it supersedes.
        /// </summary>
        /// <returns>The whole document, revision included.</returns>
        /// <exception cref="PdfException">
        /// The revision could not be built (see <c>Build</c>), or the writer appended
        /// nothing.
        /// </exception>
        /// <remarks>
        /// <see cref="DocumentRevisionWriter"/> reports a failed write to
        /// <see cref="DocumentEditDiagnostics"/> and hands back the original bytes, because most of
        /// its callers are edits that must never corrupt a file. Here that outcome was answered
        /// three ways: one caller refused it and the other two returned success with nothing
        /// written. It is refused here, once. No fixture reaches it — the Core writer serialises
        /// anything the parser produced — so it is a statement of the contract, not a tested path.
        /// </remarks>
        public static byte[] Append(
            byte[] original, PdfDocument doc, PdfDictionary catalog, string xmp,
            Action<string, Exception> report = null)
            => Append(original, doc, catalog, xmp, null, report);

        /// <summary>
        /// <see cref="Append(byte[], PdfDocument, PdfDictionary, string, Action{string, Exception})"/>,
        /// the catalog's <c>/Version</c> set to <paramref name="catalogVersion"/> in the same revision when not null.
        /// </summary>
        public static byte[] Append(
            byte[] original, PdfDocument doc, PdfDictionary catalog, string xmp, PdfName catalogVersion,
            Action<string, Exception> report = null)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));

            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            Build(doc, index, catalog, xmp, catalogVersion, out var modified, out var created, report);

            using var buffer = new MemoryStream(original.Length + xmp.Length + 4096);
            DocumentRevisionWriter.Write(buffer, original, doc, index, modified, created);
            if (buffer.Length <= original.Length)
                throw new PdfException("The /Metadata revision could not be written; the document is unchanged.");

            return buffer.ToArray();
        }

        /// <summary>
        /// The packet a writer wants written: <see cref="Xmp"/> null when the document already
        /// carries the packet it would write, and <see cref="CatalogVersion"/> the catalog's
        /// <c>/Version</c> to set in the same revision, or null.
        /// </summary>
        public readonly struct PacketPlan
        {
            /// <summary>The plan.</summary>
            /// <param name="xmp">The complete packet, or null when nothing need be written.</param>
            /// <param name="catalogVersion">The catalog <c>/Version</c> to set, or null.</param>
            public PacketPlan(string xmp, PdfName catalogVersion = null)
            {
                Xmp = xmp;
                CatalogVersion = catalogVersion;
            }

            /// <summary>The complete packet, or null when nothing need be written.</summary>
            public string Xmp { get; }

            /// <summary>The catalog <c>/Version</c> to set in the same revision, or null.</summary>
            public PdfName CatalogVersion { get; }
        }

        /// <summary>
        /// The pass-through write of a packet a writer builds from the existing one — the
        /// declarations writer's and the PDF/UA identifier's: the document opened, the packet read,
        /// <paramref name="plan"/> asked for the new one, and the revision appended. Every refusal
        /// writes the input to <paramref name="output"/> unchanged and is said through
        /// <paramref name="fail"/>: an empty input, an encrypted document (its packet would go in as
        /// clear text), a document that does not parse (one with no catalog among them), a plan that
        /// throws, a revision that cannot be written.
        /// </summary>
        /// <param name="input">The source PDF.</param>
        /// <param name="output">Receives the updated PDF, or on a refusal the input unchanged.</param>
        /// <param name="plan">The packet to write, given the document and its packet (null when it has none).</param>
        /// <param name="fail">Receives each refusal's message and cause.</param>
        /// <param name="report">Receives each malformed entry read past; may be null.</param>
        /// <param name="appendedRevision">Whether a revision was appended — false when the plan found the packet already current.</param>
        /// <returns>True when the output declares the plan's packet; false on a refusal.</returns>
        public static bool Write(
            Stream input, Stream output, Func<PdfDocument, string, PacketPlan> plan,
            Action<string, Exception> fail, Action<string, Exception> report, out bool appendedRevision)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (fail == null) throw new ArgumentNullException(nameof(fail));
            appendedRevision = false;

            byte[] original = Core.IO.StreamBytes.ReadAll(input);
            if (original.Length == 0)
            {
                fail("The input stream is empty; there is no PDF to update.", null);
                return false;
            }

            // The document stays open past the packet: the revision needs its trailer, its
            // cross-reference table and the form of its last cross-reference section.
            MemoryStream probe = null;
            PdfDocument doc = null;
            try
            {
                PdfDictionary catalog;
                string existingXmp;
                try
                {
                    probe = new MemoryStream(original, writable: false);
                    doc = PdfDocument.Open(probe);

                    if (doc.IsEncrypted)
                    {
                        output.Write(original, 0, original.Length);
                        fail("The document is encrypted; XMP metadata cannot be written as a clear-text " +
                             "incremental update. Decrypt the document first.", null);
                        return false;
                    }

                    // An unencrypted document opens with a catalog dictionary or not at all
                    // (PdfDocument refuses "Cannot find document catalog" into the catch below).
                    catalog = doc.Catalog.Dictionary;
                    existingXmp = Core.Metadata.XmpPacketDom.ReadCatalogPacket(doc, catalog, report);
                }
                catch (Exception ex)
                {
                    output.Write(original, 0, original.Length);
                    fail("The document could not be parsed.", ex);
                    return false;
                }

                PacketPlan packet;
                try
                {
                    packet = plan(doc, existingXmp);
                }
                catch (Exception ex)
                {
                    output.Write(original, 0, original.Length);
                    fail("The XMP packet could not be built.", ex);
                    return false;
                }

                if (packet.Xmp == null)
                {
                    // Already declared: the output says what was asked with the bytes it had.
                    output.Write(original, 0, original.Length);
                    return true;
                }

                // Buffered, so a writer that throws halfway leaves no truncated document in output;
                // Append refuses a revision that appends nothing.
                byte[] updated;
                try
                {
                    updated = Append(original, doc, catalog, packet.Xmp, packet.CatalogVersion, report);
                }
                catch (Exception ex)
                {
                    output.Write(original, 0, original.Length);
                    fail("The /Metadata stream could not be written.", ex);
                    return false;
                }

                output.Write(updated, 0, updated.Length);
                appendedRevision = true;
                return true;
            }
            finally
            {
                doc?.Dispose();
                probe?.Dispose();
            }
        }

        /// <summary>
        /// Built from scratch — never by cloning the old stream, so a <c>/Filter</c> the previous
        /// packet carried cannot be inherited and left describing plain text. PDF/A and PDF/UA both
        /// require the packet to be readable without decoding it, so none is applied.
        /// </summary>
        /// <remarks>
        /// It carries no identity stamp. <see cref="DocumentRevisionWriter"/> takes every number and
        /// generation from the map key and the index, so a stamp here would have no effect.
        /// </remarks>
        private static PdfStream MetadataStream(string xmp)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(xmp);

            var stream = new PdfStream();
            stream.Set(PdfName.Type, PdfName.Metadata);
            stream.Set(PdfName.Subtype, PdfName.XML);
            stream.Set(PdfName.Length, new PdfNumber(bytes.Length));
            stream.RawData = bytes;
            return stream;
        }

        /// <summary>
        /// The object number of the <c>/Metadata</c> stream to supersede in place, or <c>-1</c>
        /// when a fresh object must be allocated and the catalog repointed.
        /// </summary>
        /// <remarks>
        /// <para><b>Superseded only when the entry resolves to a stream the document defines.</b>
        /// A reference's number is not taken on trust: <c>/Metadata 2 0 R</c> naming the page tree
        /// would have the page tree overwritten by the packet, and a reference to an undefined object
        /// — which §7.3.10 reads as <c>null</c> — the packet written at whatever number it named,
        /// however large. Both get a new object
        /// and a revised catalog, and the malformed entry is reported.</para>
        ///
        /// <para>A direct stream is impossible (§7.3.8.1: streams are indirect), so the one path
        /// covers both forms of the entry: the index names the resolved instance.</para>
        /// </remarks>
        private static int ExistingMetadataObjectNumber(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, Action<string, Exception> report)
        {
            PdfObject raw = catalog[PdfName.Metadata];
            if (raw == null) return -1;

            PdfObject target;
            try
            {
                target = doc.ResolveObject(raw);
            }
            catch (Exception ex)
            {
                report?.Invoke(
                    "The catalog's /Metadata entry could not be resolved; a new /Metadata object was allocated.", ex);
                return -1;
            }

            int number = target is PdfStream ? index.GetObjectNumber(target) : -1;
            if (number <= 0)
                report?.Invoke(
                    "The catalog's /Metadata entry does not name a stream in the document; " +
                    "a new /Metadata object was allocated and the catalog repointed.", null);

            return number;
        }

        /// <summary>
        /// The catalog's object number, from the trailer's <c>/Root</c> — which §7.5.5 requires to
        /// be an indirect reference.
        /// </summary>
        /// <remarks>
        /// <para><b>A direct <c>/Root</c> is refused.</b> The appended trailer copies
        /// <c>/Root</c> only as a reference (<c>IncrementalUpdateWriter.CopyTrailerKeys</c>), so a
        /// revision built over a direct one would name no catalog at all.</para>
        ///
        /// <para>No fallback to the index or the dictionary's stamped number: neither can answer: a direct <c>/Root</c> is in no cross-reference
        /// entry and carries no stamp, and <see cref="PdfDocument"/> takes its catalog from
        /// <c>/Root</c> alone. Both copies also wrapped this read in a <c>try</c> — around a
        /// dictionary lookup, which cannot throw — and the Accessibility one reported from the
        /// <c>catch</c>, a report that could never fire.</para>
        /// </remarks>
        private static int CatalogObjectNumber(PdfDocument doc)
            => Core.Writing.CatalogObject.NumberOrRefuse(doc.Trailer, "the /Metadata stream");
    }
}
