// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Two-phase signing: write the document and its placeholder now, obtain the CMS from somewhere
    /// else, inject it later.
    /// </summary>
    /// <remarks>
    /// <para><b>The case <see cref="IExternalSigner"/> cannot express.</b> That interface is called
    /// from inside <c>PdfSigner.Sign</c>, with the half-written output stream open, so it reaches a
    /// signing device in the same process and no further. A remote signing service, a CSC/eIDAS
    /// flow, or a browser holding a smart card all live behind a request boundary: the answer comes
    /// back in a different call, often in a different process. What has to survive that boundary is
    /// <b>data</b>, which is what <see cref="PreparedSignature"/> is.</para>
    ///
    /// <para><b>A prepared document is not a signed one and must not be distributed.</b> Its
    /// <c>/Contents</c> holds a zero-padded placeholder, so every verifier will call the signature
    /// broken — correctly. <see cref="Complete"/> overwrites those hex digits and <b>nothing
    /// else</b>, which is what keeps the <c>/ByteRange</c> written in phase one true.</para>
    ///
    /// <para><b>Two parameters are refused rather than ignored</b>, because both would otherwise
    /// look like they had been honoured. <see cref="SignatureParameters.EmbedLtv"/> appends a
    /// <i>second</i> revision after the signature, so the DSS would wrap a placeholder and then be
    /// covered by nothing. <see cref="SignatureParameters.TimestampAuthorityUrl"/> would make phase
    /// one fetch a real timestamp over the network and <b>discard it</b> — <c>PdfSigner</c> embeds a
    /// TSA token only on the local-key path, and says so: on the external-signer path the timestamp
    /// belongs to whoever builds the CMS. Here that is the far side of the boundary.</para>
    ///
    /// <para><b>A service that returns a raw signature value rather than a CMS is served by
    /// <see cref="Cms.CmsDeferredSignatureBuilder"/>.</b> Such a service cannot be handed the
    /// document digest: a CAdES/PAdES signature covers the DER of the <i>signed attributes</i>, one
    /// of which is that digest (RFC 5652 §5.4). So the flow gains one step on each side —
    /// <c>GetSignedAttributes</c> over <see cref="PreparedSignature.SignedBytes"/> produces the few
    /// hundred bytes that cross the wire, and <c>AssembleDetachedSignature</c> turns what comes back
    /// into the CMS <see cref="Complete"/> takes.</para>
    /// </remarks>
    public static class PdfDeferredSigner
    {
        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with a signature field and a
        /// <c>/Contents</c> placeholder, and reports everything needed to finish it later.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">
        /// Receives the prepared document. Must be readable and seekable: the placeholder's
        /// position is read back out of the document this just wrote.
        /// </param>
        /// <param name="certificateChainDer">
        /// The signer's certificate chain, DER-encoded, signer first. Required: the signature
        /// dictionary names the certificate, so a signature cannot be prepared without knowing
        /// whose it is — even though the key stays wherever it lives.
        /// </param>
        /// <param name="parameters">
        /// Signing parameters. <see cref="SignatureParameters.ExternalSigner"/> and the three
        /// certificate members are ignored — there is no key on this side — and
        /// <see cref="SignatureParameters.EstimatedSignatureSize"/> sizes the placeholder.
        /// </param>
        /// <exception cref="PdfSigningException">
        /// The chain is empty, <see cref="SignatureParameters.EmbedLtv"/> or
        /// <see cref="SignatureParameters.TimestampAuthorityUrl"/> is set, or the document could
        /// not be prepared.
        /// </exception>
        public static PreparedSignature Prepare(
            Stream input, Stream output,
            IList<byte[]> certificateChainDer,
            SignatureParameters parameters)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            if (certificateChainDer == null || certificateChainDer.Count == 0)
                throw new PdfSigningException(
                    "Prepare needs the signer's certificate chain: the signature dictionary names " +
                    "the certificate, so a signature cannot be prepared without knowing whose it is.");

            if (parameters.EmbedLtv)
                throw new PdfSigningException(
                    "EmbedLtv cannot be used with Prepare: it appends a second revision AFTER the " +
                    "signature, which here would wrap a placeholder. Complete the signature first, " +
                    "then add validation information with PdfLtvRefresher.AddVerificationInfo.");

            if (!string.IsNullOrEmpty(parameters.TimestampAuthorityUrl))
                throw new PdfSigningException(
                    "TimestampAuthorityUrl cannot be used with Prepare: the timestamp is an " +
                    "unsigned attribute inside the CMS, so it belongs to whoever builds the CMS — " +
                    "and preparing with it set would fetch a token from the TSA and discard it.");

            if (!output.CanSeek || !output.CanRead)
                throw new PdfSigningException(
                    "Prepare needs a readable, seekable output stream: the /Contents placeholder's " +
                    "position is read back out of the document it just wrote.");

            // The signer that never signs. Phase one needs three things from IExternalSigner — the
            // chain, the reserve size and the bytes to be signed — and exactly one thing it cannot
            // have, which is the signature. Capturing here rather than refactoring PdfSigner.Sign
            // keeps every tested step (field adoption, /ByteRange placement, the two-pass patch)
            // on one code path, so a prepared document and a directly-signed one cannot drift.
            var capture = new CapturingSigner(certificateChainDer, parameters.EstimatedSignatureSize);

            new PdfSigner().Sign(input, output, ForPreparation(parameters, capture));

            if (capture.SignedBytes == null)
                throw new PdfSigningException(
                    "The document was written but the byte range was never presented for signing, " +
                    "so there is nothing to prepare.");

            // The field name is read back rather than assumed: PdfSigner renames a colliding
            // SignatureFieldName, and the caller's parameters still say the one asked for — so trusting
            // them would name a DIFFERENT signature in the descriptor, and Complete would write
            // this CMS over an already-valid signature's /Contents.
            LocateNewestPlaceholder(output, out string fieldName,
                                    out long start, out long end, out long[] byteRange,
                                    out string subFilter, out DateTimeOffset? signingTime);

            return new PreparedSignature(
                fieldName: fieldName,
                // The digest PdfSigner chose, which a field's seed value can change from the
                // caller's (/SV /DigestMethod): the far side builds its CMS with what this names.
                digestAlgorithm: capture.DigestAlgorithm ?? parameters.DigestAlgorithm,
                contentsStart: start,
                contentsEnd: end,
                reservedBytes: (int)((end - start) / 2),
                byteRange: byteRange,
                signedBytes: capture.SignedBytes,
                subFilter: subFilter,
                signingTime: signingTime);
        }

        /// <summary>
        /// Writes a finished CMS into a document produced by <see cref="Prepare"/>.
        /// </summary>
        /// <param name="prepared">The prepared document. Not disposed.</param>
        /// <param name="descriptor">What <see cref="Prepare"/> returned for it.</param>
        /// <param name="cms">The complete CMS <c>SignedData</c> blob, detached.</param>
        /// <param name="output">Receives the completed document. Must be seekable.</param>
        /// <exception cref="PdfSigningException">
        /// The CMS does not fit the reserve, or the prepared document does not match the descriptor.
        /// </exception>
        /// <remarks>
        /// <b>Only the hex digits change.</b> Everything else is copied byte for byte, because the
        /// <c>/ByteRange</c> written in phase one describes this file and would stop describing it
        /// the moment anything moved.
        /// </remarks>
        public static void Complete(
            Stream prepared, PreparedSignature descriptor, byte[] cms, Stream output)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (cms == null) throw new ArgumentNullException(nameof(cms));
            if (output == null) throw new ArgumentNullException(nameof(output));

            if (cms.Length > descriptor.ReservedBytes)
                throw new PdfSigningException(
                    $"The CMS is {cms.Length} bytes and the placeholder reserves " +
                    $"{descriptor.ReservedBytes}. Re-prepare with a larger " +
                    "SignatureParameters.EstimatedSignatureSize; the reserve cannot grow " +
                    "afterwards without moving every byte after it and invalidating /ByteRange.");

            if (!output.CanSeek)
                throw new PdfSigningException(
                    "Complete needs a seekable output stream: the CMS is written back over the " +
                    "placeholder once the document has been copied through.");

            // Re-locate the delimiters in the file actually handed back, and check they agree with
            // the descriptor. A caller who prepared twice, or edited in between, would otherwise
            // write a valid-looking CMS at an offset that means nothing — and the result verifies
            // as "signature broken", which reads as a signing failure rather than a mix-up.
            var source = SigningHelpers.EnsureSeekable(prepared, rewind: true);
            try
            {
                LocatePlaceholder(source, descriptor.FieldName, out long start, out long end);

                if (start != descriptor.ContentsStart || end != descriptor.ContentsEnd)
                    throw new PdfSigningException(
                        $"The document's /Contents placeholder is at {start}..{end} but the " +
                        $"descriptor names {descriptor.ContentsStart}..{descriptor.ContentsEnd}. " +
                        "They come from different Prepare calls, or the document was edited after " +
                        "being prepared.");

                // The offsets are document-relative, so the seek back is too. PdfSigner writes its
                // own output from the stream's position and patches it the same way; a caller who
                // hands this one a stream positioned elsewhere gets the same treatment rather than
                // a CMS written over whatever preceded it.
                long outputBase = output.Position;

                source.Position = 0;
                source.CopyTo(output);

                SigningHelpers.WriteContents(output, outputBase + start, (int)(end - start), cms);
                output.Flush();
            }
            finally
            {
                if (!ReferenceEquals(source, prepared)) source.Dispose();
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Locates the placeholder of the signature covering the most of the file — the one the
        /// outermost revision added, which is the one <see cref="Prepare"/> just wrote.
        /// </summary>
        /// <remarks>
        /// <b>"The last <c>&lt;…&gt;</c> in the file" is a different question</b> once the document
        /// already carries a signature, and preparing over a signed document is a normal thing to
        /// do. <c>/ByteRange</c> element 2 plus element 3 is where each signature's coverage ends,
        /// and an earlier revision's ends earlier by construction, so the maximum names the newest.
        /// </remarks>
        private static void LocateNewestPlaceholder(
            Stream file, out string fieldName,
            out long contentsStart, out long contentsEnd, out long[] byteRange,
            out string subFilter, out DateTimeOffset? signingTime)
        {
            ReadPlaceholder(file, null, out fieldName, out contentsStart, out contentsEnd,
                            out byteRange, out subFilter, out signingTime);
        }

        /// <summary>
        /// Locates the <c>/Contents</c> hex run of the signature in field
        /// <paramref name="fieldName"/>.
        /// </summary>
        private static void LocatePlaceholder(
            Stream file, string fieldName, out long contentsStart, out long contentsEnd)
        {
            ReadPlaceholder(file, fieldName, out string _, out contentsStart, out contentsEnd,
                            out long[] _, out string _, out DateTimeOffset? _);
        }

        /// <remarks>
        /// The document is closed before the raw read: <c>PdfDocument.Open</c> may wrap a non-memory
        /// stream in a <c>BufferedStream</c> that shares this cursor, and reading around a live
        /// wrapper is how two readers end up disagreeing about where they are. The wrapper is
        /// read-only and never owns the caller's stream, so closing it costs nothing.
        /// </remarks>
        private static void ReadPlaceholder(
            Stream file, string? wantedField, out string fieldName,
            out long contentsStart, out long contentsEnd, out long[] byteRange,
            out string subFilter, out DateTimeOffset? signingTime)
        {
            long o1 = 0, l1 = 0, o2 = 0, l2 = 0;
            string? found = null;
            string foundSubFilter = "";
            DateTimeOffset? foundSigningTime = null;

            file.Position = 0;
            using (var doc = PdfDocument.Open(file))
            {
                // Encryption first: for an encrypted file PdfDocument.Load never reads the catalog,
                // so AcroForm is ALWAYS null and "no AcroForm" asserts something false about the
                // document. Same wording as PdfSignatureVerifier and PdfCounterSigner.
                if (doc.IsEncrypted)
                    throw new PdfSigningException(EncryptedDocumentMessages.AcroFormUnreadable);

                if (doc.AcroForm == null)
                    throw new PdfSigningException(
                        "The document has no AcroForm, so it carries no prepared signature.");

                long bestCoverage = -1;
                foreach (PdfSignatureField candidate in doc.AcroForm.GetSignatureFields())
                {
                    if (!candidate.HasSignature) continue;
                    if (wantedField != null && candidate.FieldName != wantedField) continue;

                    if (!SignedByteRanges.TryRead(
                            candidate.SignatureValue, doc, out var ranges, out string why))
                    {
                        // Worth reporting only when this is the field the caller named. An
                        // unreadable /ByteRange on some OTHER signature is that signature's
                        // problem, and refusing over it would let an unrelated malformed
                        // signature block a correctly prepared one.
                        if (wantedField != null)
                            throw new PdfSigningException(
                                $"Signature field '{wantedField}' has no readable /ByteRange, so " +
                                $"its /Contents placeholder cannot be located: {why}");
                        continue;
                    }

                    if (ranges.CoverageEnd <= bestCoverage) continue;

                    bestCoverage = ranges.CoverageEnd;
                    found = candidate.FieldName;
                    o1 = ranges.Offset1; l1 = ranges.Length1;
                    o2 = ranges.Offset2; l2 = ranges.Length2;
                    foundSubFilter = candidate.SubFilter ?? "";
                    foundSigningTime = Core.Util.PdfDate.TryParse(candidate.SigningTime);
                }

                if (bestCoverage < 0 || found == null)
                    throw new PdfSigningException(wantedField != null
                        ? $"The document has no signature value in a field named '{wantedField}'."
                        : "The document carries no signature with a readable /ByteRange.");

                fieldName = found;
                subFilter = foundSubFilter;
                signingTime = foundSigningTime;
            }

            SigningHelpers.FindContentsDelimiters(
                file, o1 + l1, o2, out contentsStart, out contentsEnd);
            byteRange = new[] { o1, l1, o2, l2 };
        }

        /// <summary>
        /// Members of <see cref="SignatureParameters"/> that deliberately do <b>not</b> travel into
        /// phase one. Everything else does — see <see cref="ForPreparation"/>.
        /// </summary>
        /// <remarks>
        /// The four are the key material. <c>PdfSigner.Sign</c> prefers <c>ExternalSigner</c> over
        /// both certificate routes, so carrying them would change nothing — but a
        /// <c>CertificateStream</c> is a live stream, and copying one into an object this class owns
        /// invites a second reader of somebody else's cursor.
        /// </remarks>
        internal static readonly string[] ParametersNotCarried =
        {
            nameof(SignatureParameters.ExternalSigner),
            nameof(SignatureParameters.CertificatePath),
            nameof(SignatureParameters.CertificatePassword),
            nameof(SignatureParameters.CertificateStream),
        };

        /// <summary>
        /// The parameters phase one runs under: the caller's, with the capturing signer in place of
        /// whatever key material they supplied.
        /// </summary>
        /// <remarks>
        /// <b>Copy everything, drop a named few</b> — the opposite polarity from listing what
        /// travels. A property added to <see cref="SignatureParameters"/> later shapes the signature
        /// dictionary or the placeholder, and under a carry-list it would silently stop applying
        /// here while <c>PdfSigner.Sign</c> kept honouring it: a prepared document and a directly
        /// signed one would differ, which is the one thing this design exists to prevent. Under a
        /// drop-list the default is "it travels", and a member that must not is a deliberate entry
        /// with a reason.
        /// </remarks>
        internal static SignatureParameters ForPreparation(
            SignatureParameters src, IExternalSigner signer)
        {
            var clone = new SignatureParameters();
            foreach (PropertyInfo p in typeof(SignatureParameters)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (Array.IndexOf(ParametersNotCarried, p.Name) >= 0) continue;
                p.SetValue(clone, p.GetValue(src, null), null);
            }

            clone.ExternalSigner = signer;
            return clone;
        }

        /// <summary>
        /// An <see cref="IExternalSigner"/> that records the bytes it is asked to sign and returns a
        /// stub, so phase one can run the real signing path without a key.
        /// </summary>
        /// <remarks>
        /// The stub is one byte. Its only job is to be small enough to fit whatever reserve the
        /// caller asked for; <see cref="Complete"/> overwrites the whole hex run, padding included,
        /// so nothing about the stub survives into the finished document.
        /// </remarks>
        private sealed class CapturingSigner : IExternalSigner
        {
            private readonly IList<byte[]> _chain;
            private readonly int _reserve;

            internal CapturingSigner(IList<byte[]> chain, int reserve)
            {
                _chain = chain;
                _reserve = reserve;
            }

            internal byte[]? SignedBytes { get; private set; }

            /// <summary>The digest PdfSigner asked for — the field's seed value's, when it requires one.</summary>
            internal string? DigestAlgorithm { get; private set; }

            public byte[] SignData(byte[] data, string digestAlgorithm)
            {
                SignedBytes = data;
                DigestAlgorithm = digestAlgorithm;
                return new byte[] { 0 };
            }

            public byte[] SignDigest(byte[] digest, string digestAlgorithm)
                => throw new NotSupportedException(
                    "Prepare presents the byte range through SignData; SignDigest is never called.");

            public IList<byte[]> GetCertificateChain() => _chain;

            public int EstimatedSignatureSize => _reserve;
        }
    }
}
