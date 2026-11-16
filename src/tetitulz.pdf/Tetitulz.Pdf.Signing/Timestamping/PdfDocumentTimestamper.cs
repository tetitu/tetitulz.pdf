// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Timestamping
{
    /// <summary>
    /// Adds a document-level RFC 3161 timestamp to a PDF, enabling PAdES B-LTA archival
    /// validation.
    ///
    /// <para>
    /// A document timestamp (<c>/DocTimeStamp</c>) is a signature field whose
    /// <c>/SubFilter</c> is <c>/ETSI.RFC3161</c> and whose <c>/Contents</c> contains
    /// a raw DER-encoded TimeStampToken (CMS ContentInfo wrapping a TSTInfo structure).
    /// The ByteRange covers the entire document, including any previously applied
    /// digital signatures, making the timestamp a notarisation of the complete file.
    /// </para>
    ///
    /// <para>
    /// This does NOT require a signing certificate — only a live RFC 3161 TSA endpoint
    /// (e.g. <c>http://timestamp.digicert.com</c>).
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   PdfDocumentTimestamper.AddTimestamp(
    ///       input, output,
    ///       tsaUrl: "http://timestamp.digicert.com",
    ///       hashAlgorithm: "SHA-256");
    /// </code>
    /// </summary>
    public static class PdfDocumentTimestamper
    {
        // Reserved space for the timestamp token (DER bytes × 2 for hex).
        // A typical SHA-256 TSA response is ~4-10 KB; 32 KB is safe. Shared with the signer and
        // the PAdES B-LT/B-LTA parameter defaults, which reserve the same amount.
        private const int TokenReserveBytes = SigningDefaults.SignatureReserveBytes;

        /// <summary>
        /// Adds a document-level RFC 3161 timestamp as a PAdES B-LTA archival signature.
        /// </summary>
        /// <param name="input">Source PDF stream (may be signed or unsigned).</param>
        /// <param name="output">Output PDF stream with the appended document timestamp.</param>
        /// <param name="tsaUrl">URL of the RFC 3161 timestamp authority.</param>
        /// <param name="hashAlgorithm">Hash algorithm for the TSA request (default "SHA-256").</param>
        /// <param name="fieldName">Name of the new signature field (default "DocTimeStamp1").</param>
        public static void AddTimestamp(
            Stream input, Stream output,
            string tsaUrl,
            string hashAlgorithm = "SHA-256",
            string fieldName = "DocTimeStamp1")
            => AddTimestamp(input, output, tsaUrl, null, hashAlgorithm, fieldName);

        /// <summary>
        /// <see cref="AddTimestamp(Stream, Stream, string, string, string)"/> with credentials, a
        /// proxy and a timeout for the TSA request.
        /// </summary>
        /// <remarks>
        /// A separate overload rather than a widened signature: C# bakes an optional argument's
        /// default in at the call site, so widening the shipped one is a binary break for an
        /// already-compiled consumer. <paramref name="network"/> sits before the two optional
        /// parameters so it cannot be confused with them positionally.
        /// </remarks>
        /// <param name="input">The document to timestamp. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="tsaUrl">URL of the RFC 3161 TSA endpoint.</param>
        /// <param name="network">The TSA network policy; null keeps the anonymous default.</param>
        /// <param name="hashAlgorithm">Digest for the document hash.</param>
        /// <param name="fieldName">Name for the appended DocTimeStamp field.</param>
        public static void AddTimestamp(
            Stream input, Stream output,
            string tsaUrl,
            SigningNetworkOptions? network,
            string hashAlgorithm = "SHA-256",
            string fieldName = "DocTimeStamp1")
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (tsaUrl == null) throw new ArgumentNullException(nameof(tsaUrl));

            var ms = EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Refuse an encrypted document BEFORE a byte reaches the caller's stream. PdfDocument
            // returns early for one, leaving Catalog null, so doc.Pages and doc.Catalog.Dictionary
            // below threw NullReferenceException out of a public API - after ms.CopyTo(output) had
            // already written a partial, corrupt file. And had they not thrown, this method would
            // have written clear-text catalog and AcroForm objects into an encrypted document.
            if (doc.IsEncrypted)
                throw new PdfException(
                    EncryptedDocumentMessages.AcroFormUnreadable +
                    " A document timestamp cannot be appended to it.");

            // And refuse an unnameable cross-reference chain, in the same place and for a sharper
            // reason. This writer emits "/Prev {prevXref}" UNCONDITIONALLY at the trailer below —
            // so on such a document it writes /Prev 0, which points the chain at the %PDF header:
            // a claim no reader can follow, on a revision whose own section names three objects.
            // The reopened file then has a catalog, no pages, and a timestamp that still verifies.
            // See PdfSigner.RefuseUnnameableCrossReferenceChain for why this refuses rather than
            // recovering.
            SigningHelpers.RefuseUnnameableCrossReferenceChain(ms, doc.Parser.StartXrefOffset);

            // And a direct /Root, before the copy below — the same refusal PdfSigner and
            // VisualSignatureBuilder make. A trailer whose /Root is `<< /Type /Catalog … >>` has no
            // object number to supersede: writing the updated catalog as object 0 — the free-list
            // head — and the trailer's /Root as `0 n R` would be a file no reader can open, reported
            // as timestamped.
            int catalogObjNum = PdfSigner.CatalogObjectNumberOrRefuse(doc.Trailer);

            string hashOid = TsaClient.GetHashOid(hashAlgorithm);
            int contentsHexLen = TokenReserveBytes * 2;

            // Allocate new object numbers.
            int maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            int sigDictObjNum  = maxObj + 1;
            int sigFieldObjNum = maxObj + 2;
            int newSize        = maxObj + 3;

            // The form this revision extends, read before anything is written: the new field's name
            // has to avoid every name already in it. Taking the caller's name as given, timestamping
            // a document a second time would make a second field called "DocTimeStamp1" — and
            // §12.7.4.2 requires fields sharing a fully qualified name to share their value, which
            // two different timestamps cannot.
            int acroFormObjNum = 0;
            var acroFormObj = doc.Catalog.Dictionary[PdfName.AcroForm];
            PdfDictionary? existingAcroForm = null;
            if (acroFormObj is PdfReference af)
            {
                acroFormObjNum = af.ReferenceObjectNumber;
                existingAcroForm = SignatureFieldRegistration.FormOf(acroFormObj, doc.ResolveObject);
            }
            else if (acroFormObj is PdfDictionary afd)
            {
                existingAcroForm = afd;
            }

            fieldName = SignatureFieldRegistration.UniqueName(
                fieldName, SignatureFieldRegistration.FieldNames(existingAcroForm, doc.ResolveObject));

            // ---- Copy the source PDF to output (incremental update base). ----
            ms.Position = 0;
            ms.CopyTo(output);

            var writer = new PdfWriter(output);

            // ---- 1. Signature dictionary object (with ByteRange + Contents placeholders). ----
            long sigDictOffset = output.Position;
            writer.WriteRaw($"{sigDictObjNum} 0 obj\n");
            // ISO 32000-2 Table 255: a document timestamp's /Type is DocTimeStamp, never Sig.
            writer.WriteRaw("<< /Type /DocTimeStamp");
            writer.WriteRaw(" /Filter /Adobe.PPKLite");
            writer.WriteRaw(" /SubFilter /ETSI.RFC3161");

            // ByteRange placeholder.
            writer.WriteRaw(" /ByteRange ");
            long byteRangePos = output.Position;
            const string brPlaceholder = SigningDefaults.ByteRangePlaceholder;
            writer.WriteRaw(brPlaceholder);

            // Contents placeholder.
            writer.WriteRaw(" /Contents <");
            long contentsStart = output.Position;
            writer.WriteRaw(new string('0', contentsHexLen));
            long contentsEnd = output.Position;
            writer.WriteRaw(">");

            // Core.Util.PdfDate.FormatUtc (through SigningHelpers) is the single implementation and
            // passes CultureInfo.InvariantCulture. A bare ToString("yyyyMMddHHmmss") uses the current
            // thread culture's CALENDAR: under th-TH (ThaiBuddhistCalendar) or ar-SA
            // (UmAlQuraCalendar) - routine in a Middle-East or Thai deployment, and reachable in any
            // server that sets CurrentCulture per request - it writes a Buddhist- or Hijri-era year,
            // i.e. a /M date no reader can make sense of, silently.
            writer.WriteRaw($" /M ({SigningHelpers.FormatPdfDate(DateTime.UtcNow)})");
            writer.WriteRaw(" >>\nendobj\n");

            // ---- 2. Signature field widget annotation. ----
            long sigFieldOffset = output.Position;
            writer.WriteRaw($"{sigFieldObjNum} 0 obj\n");

            // Determine page object number (first page).
            int pageObjNum = doc.Pages.Count > 0 ? doc.Pages[0].Dictionary.ObjectNumber : 0;

            writer.WriteRaw("<< /Type /Annot /Subtype /Widget");
            writer.WriteRaw($" /FT /Sig /T ({EscapePdf(fieldName)})");
            writer.WriteRaw($" /V {sigDictObjNum} 0 R");
            writer.WriteRaw($" /F {PdfAnnotationFlags.Print}");
            writer.WriteRaw(" /Rect [0 0 0 0]");  // Invisible (zero-size rect)
            if (pageObjNum > 0)
            {
                // The page at its own generation (ExistingObjectGeneration), never 0.
                writer.WriteRaw(" /P ");
                writer.WriteObject(Core.Writing.ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, pageObjNum));
            }
            writer.WriteRaw(" >>\nendobj\n");

            // ---- 3. xref + trailer (incremental update). ----
            // The form gains the new field and /SigFlags 3 — also when the form already exists, so a
            // form that declared /SigFlags 0, or none, stops telling readers the document holds no
            // signatures (Table 225).
            long acroFormOffset = 0;
            PdfDictionary updatedAcroForm = SignatureFieldRegistration.FormWithSignature(
                existingAcroForm,
                SignatureFieldRegistration.FieldsOf(existingAcroForm, doc.ResolveObject),
                new PdfReference(sigFieldObjNum, 0));

            // Write AcroForm / catalog update.
            long catalogOffset = 0;
            var  catalogRef    = doc.Trailer[PdfName.Root] as PdfReference;   // indirect: refused above otherwise

            // §7.5.6 — an appended revision supersedes an object only when it re-declares it at the
            // SAME generation. Both of these already exist in the document; writing them as
            // generation 0 against a document whose object is generation n supersedes nothing, and
            // a strict reader keeps the old catalog and drops the timestamp. Read from the
            // cross-reference table, never from the clone below: CloneShallow carries no generation.
            int acroFormGen = Core.Writing.ExistingObjectGeneration.Of(
                doc.CrossReferenceTable, acroFormObjNum, doc.Catalog.Dictionary[PdfName.AcroForm]);
            int catalogGen = Core.Writing.ExistingObjectGeneration.Of(
                doc.CrossReferenceTable, catalogObjNum, catalogRef);

            // Write updated AcroForm inline into updated catalog if no existing indirect.
            if (acroFormObjNum > 0)
            {
                acroFormOffset = output.Position;
                writer.WriteRaw($"{acroFormObjNum} {acroFormGen} obj\n");
                WriteDict(writer, updatedAcroForm);
                writer.WriteRaw("\nendobj\n");
            }

            // Updated catalog pointing to new AcroForm.
            var updatedCatalog = CloneDictionary(doc.Catalog.Dictionary);
            if (acroFormObjNum > 0)
                updatedCatalog.Set(PdfName.AcroForm, new PdfReference(acroFormObjNum, acroFormGen));
            else
                updatedCatalog.Set(PdfName.AcroForm, updatedAcroForm);

            catalogOffset = output.Position;
            writer.WriteRaw($"{catalogObjNum} {catalogGen} obj\n");
            WriteDict(writer, updatedCatalog);
            writer.WriteRaw("\nendobj\n");

            // xref table — through Core.Writing.PdfWriter, the same call PdfSigner and
            // VisualSignatureBuilder make, so the section has that method's §7.5.4 10-digit offset
            // guard (a hand-rolled one would wrap the fixed-width slot silently past ~9.31 GiB) and
            // its grouping of contiguous object numbers into a single subsection.
            var xrefEntries = new List<(int objNum, int gen, long offset)>
            {
                (sigDictObjNum, 0, sigDictOffset),
                (sigFieldObjNum, 0, sigFieldOffset),
                // The entry's generation must match the object header written above — a file
                // where they disagree is self-inconsistent, which is worse than either alone.
                (catalogObjNum, catalogGen, catalogOffset),
            };
            if (acroFormObjNum > 0)
                xrefEntries.Add((acroFormObjNum, acroFormGen, acroFormOffset));

            long xrefOffset = writer.WriteXrefTable(xrefEntries);

            // Trailer — ONE implementation, in Core.Writing.PdfTrailerBuilder.
            //
            // A hand-spelled `<< /Size … /Root … /Prev … [ /ID … ] >>` would carry exactly those four
            // keys and NOTHING ELSE, dropping every other key the document's own trailer held from
            // the newest revision: /Info above all — the document information dictionary §14.3.3
            // defines — but equally any producer-specific key. ForAppendedRevision copies the original
            // trailer forward wholesale, as for PdfSigner and VisualSignatureBuilder, so
            // document-timestamping a file produces a last revision structurally like the one signing
            // it produces.
            //
            // A test reading the finished file through PdfDocument cannot see the loss: the parser
            // MERGES trailers along the /Prev chain (first section wins per key), so /Info is still
            // found in the older revision. Only the raw bytes of the last `trailer` dictionary show
            // it — which is what a reader that stops at the newest trailer, or a revision extracted
            // on its own, actually sees.
            //
            // /Prev is set by the builder under IsUsablePreviousXref rather than written
            // unconditionally; RefuseUnnameableCrossReferenceChain above has already refused the
            // documents where that guard would bite, so this is belt-and-braces.
            long prevXref = doc.Parser.StartXrefOffset;
            var newTrailer = PdfTrailerBuilder.ForAppendedRevision(
                doc.Trailer, newSize, prevXref, xrefOffset);

            // /Root must name the generation the catalog was just written at. This writer mints the
            // reference itself rather than copying the original trailer's, which is how it stayed
            // gen 0 while PdfSigner and VisualSignatureBuilder — both going through
            // PdfTrailerBuilder.ForAppendedRevision, which copies /Root verbatim — did not.
            newTrailer.Set(PdfName.Root, new PdfReference(catalogObjNum, catalogGen));

            writer.WriteTrailer(newTrailer, xrefOffset);
            output.Flush();

            // ---- 4. Compute ByteRange. ----
            // ONE implementation, in SignatureByteRange. The excluded hole is the /Contents string
            // object, and a PDF hex string is `<…>` — delimiters included — so ByteRange[1] steps back
            // over the opening '<'. Taking ByteRange[1] = contentsStart would still verify (the
            // missing byte is a constant at a fixed offset, so the digest is self-consistent), which
            // is why only a shared implementation keeps the writers on one convention.
            var range = SignatureByteRange.Compute(contentsStart, contentsEnd, output.Length);
            long br0 = 0;
            long br1 = range.BeforeLength;
            long br2 = range.AfterStart;
            long br3 = range.AfterLength;

            // Overwrite ByteRange placeholder.
            output.Position = byteRangePos;
            byte[] brBytes = Encoding.ASCII.GetBytes(range.Format(brPlaceholder.Length));
            output.Write(brBytes, 0, brBytes.Length);
            output.Flush();

            // ---- 5. Hash the two byte ranges. ----
            byte[]? docHash = HashByteRanges(output, br0, br1, br2, br3, hashAlgorithm);

            // ---- 6. Get TSA token. ----
            byte[] token = TsaClient.GetTimestampToken(docHash, hashOid, tsaUrl, network);

            if (token.Length > TokenReserveBytes)
                throw new PdfSigningException(
                    $"TSA token ({token.Length} bytes) exceeds reserved space ({TokenReserveBytes} bytes). " +
                    "Increase TokenReserveBytes and retry.");

            // ---- 7. Hex-encode and write token into Contents placeholder. ----
            SigningHelpers.WriteContents(output, contentsStart, contentsHexLen, token);
            output.Flush();
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// The digest of the two signed ranges, streamed through <c>SignedByteRanges</c>' one reader.
        /// </summary>
        /// <remarks>
        /// The hash comes from <see cref="DigestAlgorithms.CreateFromName"/>, shared with
        /// <c>TsaTokenVerifier</c>'s OID-keyed factory: SHA-3 routes through BouncyCastle because
        /// .NET Standard 2.0 lacks <c>System.Security.Cryptography.SHA3_*</c>, and without parity a
        /// caller asking for SHA-3 would silently get SHA-256.
        /// </remarks>
        private static byte[]? HashByteRanges(Stream s,
            long br0, long br1, long br2, long br3, string algorithm)
        {
            using var ha = DigestAlgorithms.CreateFromName(algorithm);

            // Reads to the end of each range: stopping at a short read would hash fewer bytes than
            // the /ByteRange names, which the TSA would then certify.
            if (!SignedByteRanges.DigestSignedBytes(s, br0, br1, br2, br3,
                    (buffer, offset, count) => ha.TransformBlock(buffer, offset, count, null, 0)))
                throw new PdfSigningException(
                    "The output ended before the document timestamp's byte range did, so the range " +
                    "cannot be hashed.");

            ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ha.Hash;
        }

        /// <summary>
        /// §7.3.4.2 literal-string escaping, through the single implementation in Core.
        /// <c>Signing</c> may not reference <c>Base</c>, but <see cref="PdfStringEscape"/> lives in
        /// <c>Core.Objects</c>, which is exactly why it was put there.
        /// </summary>
        private static string EscapePdf(string s) =>
            Tetitulz.Pdf.Core.Objects.PdfStringEscape.EscapeLiteral(s);

        // Catalog / AcroForm / trailer values go through the Core serialiser — the same call
        // PdfSigner and VisualSignatureBuilder make. An escaped literal built from GetText() does
        // not round-trip binary: the trailer /ID is 16 random bytes normally written as a hex
        // string, so copying it that way would change the document's identifier (and a leading
        // FE FF would make GetText decode the rest as UTF-16). Names written as /{Value} with no
        // '#' escaping would break the file outright on a key containing a space or '('.
        //
        // The trailer has no call of its own here: PdfWriter.WriteTrailer serialises the whole
        // dictionary PdfTrailerBuilder built, so /ID reaches the same serialiser by a shorter
        // route.
        private static void WriteDict(PdfWriter w, PdfDictionary d) => w.WriteObject(d);

        private static Stream EnsureSeekable(Stream s) => SigningHelpers.EnsureSeekable(s);

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        private static PdfDictionary CloneDictionary(PdfDictionary source) => PdfDictionary.CloneShallow(source);
    }
}

