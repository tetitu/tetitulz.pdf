// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Values shared by the signature, counter-signature and document-timestamp writers.
    ///
    /// <para>The 32&#160;KB /Contents reserve is read by <see cref="Timestamping.PdfDocumentTimestamper"/>,
    /// <see cref="Models.SignatureParameters"/> and <see cref="PdfSigningOperations"/>, and the
    /// /ByteRange placeholder by <see cref="PdfSigner"/> and
    /// <see cref="Timestamping.PdfDocumentTimestamper"/>. A placeholder whose length drifts between
    /// the writer and the patcher silently corrupts the signed file, so there is exactly one of each
    /// here.</para>
    /// </summary>
    internal static class SigningDefaults
    {
        /// <summary>
        /// Bytes reserved in a signature or timestamp dictionary's <c>/Contents</c> (written as
        /// twice this many hex digits). A typical SHA-256 TSA response is 4–10&#160;KB and a
        /// PAdES B-LT CMS with an embedded chain plus OCSP is larger still, so 32&#160;KB is the
        /// figure used whenever timestamping or LTV is in play.
        /// </summary>
        internal const int SignatureReserveBytes = 32768;

        /// <summary>
        /// <c>/ByteRange</c> placeholder written before the real offsets are known. Twelve digits
        /// per number covers files up to ~999&#160;GB; the actual value is right-padded with
        /// spaces to this exact length when it overwrites the placeholder in place.
        /// </summary>
        internal const string ByteRangePlaceholder =
            "[000000000000 000000000000 000000000000 000000000000]";

        /// <summary>
        /// The same twelve-digit width as a <b>number</b>: the largest value one column of
        /// <see cref="ByteRangePlaceholder"/> holds. A signature planned as objects rather than raw
        /// bytes (<see cref="PdfSigner.Plan"/>) cannot write leading zeros, so its
        /// <c>/ByteRange</c> is <c>[0 V V V]</c> with this value, and the array
        /// <c>Core.Writing.PdfWriter</c> emits is exactly as wide as the real one can need. Core's
        /// PDF MAC seal reserves its <c>/ByteRange</c> the same way.
        /// </summary>
        internal const long ByteRangePlaceholderValue = 999_999_999_999L;

        /// <summary>
        /// Returns the leading ASN.1 object of a <c>/Contents</c> string, dropping the reserve
        /// padding that follows it. Returns <paramref name="contents"/> itself when there is
        /// nothing to drop or the bytes cannot be read as ASN.1.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> <see cref="PdfSigner"/> writes the CMS into a fixed
        /// <see cref="SignatureReserveBytes"/> hole and right-pads the hex with <c>'0'</c>, so every
        /// signature this library produces is followed by thousands of zero bytes. BouncyCastle
        /// 2.6.2 ignores them; <b>2.7.0 refuses the array outright</b> with
        /// <c>CmsException: Exception reading content</c> wrapping
        /// <c>IOException: extra data found after object</c>. Measured under both versions, and it
        /// is the whole of that upgrade's mass signature failures: the four call sites that hand
        /// <c>PdfString.RawBytes</c> to a CMS or timestamp parser are exactly the four areas that
        /// failed — verification, document timestamps, counter-signatures and LTV refresh.</para>
        ///
        /// <para><b>The obvious implementation is wrong, and destructively so.</b> Walking back from
        /// the end while the bytes are zero cuts <i>into</i> the object: the encoding is BER with an
        /// <b>indefinite length</b>, which terminates in end-of-contents octets that are
        /// <c>00 00</c>, and the signature itself is an OCTET STRING of essentially random bytes
        /// whose tail is zero about one time in 256. So the length is read by an ASN.1 reader, which
        /// is the only thing that knows where the object ends.
        /// <c>SignatureContentsPaddingTests</c> asserts both halves — that the zero-scanning trim
        /// throws and that this one does not — precisely so the cheaper version cannot come back.
        /// </para>
        ///
        /// <para><b>It does not judge the tail.</b> The padding lies in the <c>/ByteRange</c> gap
        /// and is therefore unsigned, so a non-zero tail is a tamper signal — but refusing one is a
        /// policy decision that can reject other producers' documents, and it belongs behind
        /// <see cref="Models.SignatureVerificationOptions"/> rather than inside a byte helper.
        /// This method only makes the parser see the object the producer wrote.</para>
        ///
        /// <para><b>Callers must not trim before the <c>/ByteRange</c> gap arithmetic.</b>
        /// <c>PdfSignatureVerifier</c> sizes the expected hole as <c>2 × contents.Length + 2</c>
        /// from the <i>padded</i> array; handing that check a trimmed length would make every
        /// legitimate signature look like it had unsigned bytes hidden in the hole.</para>
        /// </remarks>
        /// <param name="contents">
        /// The raw <c>/Contents</c> bytes. Non-nullable deliberately: two of the four call sites
        /// hold a possibly-null <c>RawBytes</c>, and making them state what an absent
        /// <c>/Contents</c> means is better than having this method invent an answer for them.
        /// </param>
        internal static byte[] TrimToAsn1Object(byte[] contents)
            => TrimToAsn1Object(contents, out _);

        /// <summary>
        /// As <see cref="TrimToAsn1Object(byte[])"/>, also reporting whether the bytes it discarded
        /// were anything other than the zero padding a writer reserves.
        /// </summary>
        /// <param name="contents">The raw <c>/Contents</c> bytes, padding included.</param>
        /// <param name="hasNonZeroTail">
        /// True when at least one discarded byte was non-zero. False when the tail was all zeros,
        /// when there was no tail, and when the content could not be read as ASN.1 at all — in
        /// that last case nothing was discarded, so there is nothing to judge.
        /// </param>
        /// <remarks>
        /// <para><b>The tail is unsigned, and that is the whole reason to look at it.</b> A
        /// <c>/Contents</c> string is written at a fixed reserve and the CMS is always shorter, so
        /// the remainder sits inside the <c>/ByteRange</c> gap — excluded from the digest by
        /// construction, and therefore writable after signing without disturbing the signature.
        /// Zero padding is what every writer here and elsewhere puts there; anything else is either
        /// a producer this library has not seen or a payload someone parked in a hole no verifier
        /// was looking at.</para>
        ///
        /// <para><b>Reporting is separated from judging deliberately.</b> This helper serves the
        /// counter-signer and the LTV refresher as well as the verifier, and neither of those holds
        /// a verification policy; what to <i>do</i> about a non-zero tail is
        /// <c>SignatureVerificationOptions.RejectNonZeroContentsPadding</c>'s question.</para>
        /// </remarks>
        internal static byte[] TrimToAsn1Object(byte[] contents, out bool hasNonZeroTail)
        {
            hasNonZeroTail = false;
            if (contents.Length < 2) return contents;

            try
            {
                using var ms = new MemoryStream(contents, writable: false);
                using var input = new Org.BouncyCastle.Asn1.Asn1InputStream(ms);
                if (input.ReadObject() == null) return contents;

                long consumed = ms.Position;
                if (consumed <= 0 || consumed >= contents.Length) return contents;

                for (int i = (int)consumed; i < contents.Length; i++)
                {
                    if (contents[i] == 0) continue;
                    hasNonZeroTail = true;
                    break;
                }

                return Trim(contents, (int)consumed);
            }
            catch (Exception)
            {
                // Unreadable as ASN.1 is not this helper's problem to report: the parser the
                // caller is about to invoke will fail with its own, better-attributed message.
                return contents;
            }
        }

        private static byte[] Trim(byte[] source, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, 0, result, 0, length);
            return result;
        }
    }

    /// <summary>
    /// Why an encrypted document yields no AcroForm, in one wording for every operation that
    /// reaches for one.
    /// </summary>
    /// <remarks>
    /// <c>PdfDocument.Load</c> deliberately stops at the trailer for an encrypted file, so
    /// <c>Catalog</c> and <c>AcroForm</c> are both null - which is why "Document has no AcroForm."
    /// is a true statement about an unencrypted file and a false one about this file. An operator
    /// reading it concludes the document is not signed and stops, rather than decrypting first.
    /// <see cref="PdfSignatureVerifier"/> carried this text privately; it is here so the
    /// counter-signer and the document timestamper cannot tell a caller something different about
    /// the same input.
    /// </remarks>
    internal static class EncryptedDocumentMessages
    {
        internal const string AcroFormUnreadable =
            "The document is encrypted, so its AcroForm could not be read. This is not evidence " +
            "that the document is unsigned or has no form. Decrypt it first " +
            "(Tetitulz.Pdf.Encryption) and operate on the decrypted bytes, noting that a " +
            "signature's /ByteRange indexes the file as stored — verify the CMS " +
            "against the ORIGINAL bytes.";
    }

    /// <summary>
    /// A signing exception raised after the signed document was written in full: the signature is in
    /// the output, and what failed came after it (LTV data that could not be embedded or is
    /// incomplete). A caller that buffers the output delivers it before rethrowing. Marked in the
    /// exception's <see cref="Exception.Data"/>, so its type stays <see cref="Core.PdfSigningException"/>.
    /// </summary>
    internal static class SignedButIncomplete
    {
        private const string Key = "Tetitulz.Pdf.Signing.SignedButIncomplete";

        internal static Core.PdfSigningException Raise(string message, Exception? inner = null)
        {
            var ex = inner == null ? new Core.PdfSigningException(message) : new Core.PdfSigningException(message, inner);
            ex.Data[Key] = true;
            return ex;
        }

        internal static bool Is(Exception ex) => ex.Data.Contains(Key);
    }

    /// <summary>
    /// Stream, date and string helpers shared across the signing pipeline.
    ///
    /// <para><c>Tetitulz.Pdf.Signing</c> deliberately does not reference
    /// <c>Tetitulz.Pdf.Base</c>, so it cannot use <c>Base.PdfHelpers</c>; one definition of each
    /// is here.</para>
    /// </summary>
    internal static class SigningHelpers
    {
        /// <summary>
        /// Returns a seekable view of <paramref name="s"/>, buffering into memory when it is not
        /// seekable.
        /// </summary>
        /// <param name="s">Stream to make seekable.</param>
        /// <param name="rewind">
        /// When true an already-seekable stream is also rewound to position 0. The verification
        /// path needs that; the signing/timestamping paths deliberately read from the caller's
        /// current position, so it defaults to false.
        /// </param>
        // One implementation, in Core.IO.SeekableStream — which is in Core rather than in Base
        // because this assembly may not reference Base. The rewind flag stays a parameter: it is
        // the one thing the four copies differed on deliberately.
        internal static Stream EnsureSeekable(Stream s, bool rewind = false)
            => Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(s, rewind);

        /// <summary>
        /// Refuses to append a signature or timestamp to a document whose previous cross-reference
        /// section cannot be <b>verified</b> — before a byte reaches the caller's stream.
        /// </summary>
        /// <remarks>
        /// <para><b>What this prevents is the worst of the three possible outcomes.</b> An appended
        /// signature revision declares three objects — the signature dictionary, the signature
        /// field and a cloned catalog — and with no nameable previous section there is no
        /// <c>/Prev</c>, so nothing re-declares the rest. On reopening, <c>/Root</c> resolves (it is
        /// one of the three), <c>/Pages</c> resolves to nothing, and <c>PdfPageTree.GetPages</c>
        /// returns an empty list <i>without throwing</i>. Every page, content stream and font is
        /// still physically in the file; nothing names them.</para>
        ///
        /// <para><b>And the signature verified.</b> <c>PdfSignatureVerifier</c> reaches signatures
        /// through <c>AcroForm.GetSignatureFields()</c> and never consults the page tree, so every
        /// object it needs is among the three. The digest covered bytes nobody had touched — a
        /// cryptographically valid attestation over a document the signer's own write had emptied,
        /// with nothing to tell a relying party.</para>
        ///
        /// <para><b>Why this refuses where the edit path recovers.</b> A broken edit is loudly
        /// broken and retryable; a broken signature is quietly authoritative. Core's
        /// <c>AddRecoveryEntries</c> re-declares the document from a scan-recovered table, and that
        /// scan has no concept of a <i>free</i> entry — so an object deleted in a later revision
        /// comes back. Recovering and then signing would attest to content the document had
        /// deliberately removed. A caller who wants that should repair the file explicitly and sign
        /// the repaired bytes, which is a separate, inspectable step; there is deliberately no
        /// opt-out flag.</para>
        ///
        /// <para><b>Verified, not range-checked.</b> <c>PdfParser.ParseCrossReferences</c> assigns
        /// <c>StartXrefOffset</c> <i>before</i> parsing the section there, so a bogus but in-range
        /// offset survives scan recovery and passes
        /// <c>PdfTrailerBuilder.IsUsablePreviousXref</c>. This reads the bytes.</para>
        ///
        /// <para><b>Shared by all three writers that build their own trailer</b> —
        /// <c>PdfSigner</c>, <c>PdfDocumentTimestamper</c> and <c>VisualSignatureBuilder</c> —
        /// because fixing one is the "landed one level short" pattern this repository keeps
        /// recording, and here it would leave two paths still able to produce a verifying signature
        /// over an empty document.</para>
        /// </remarks>
        /// <exception cref="PdfSigningException">The chain cannot be named.</exception>
        internal static void RefuseUnnameableCrossReferenceChain(Stream original, long previousXrefOffset)
        {
            if (original == null || !original.CanSeek) return;

            // One bounded window at the candidate offset — the classification never looks further,
            // so reading the whole document here would only cost a second copy of it.
            if (Tetitulz.Pdf.Core.Writing.IncrementalUpdateWriter
                    .PointsAtCrossReferenceSection(original, previousXrefOffset))
                return;

            throw new PdfSigningException(
                "The document's previous cross-reference section could not be located or verified, " +
                "so an appended revision would have no /Prev to chain to. The result would open with " +
                "a catalog and no pages — and the signature over it would still verify, which is " +
                "worse than a visible failure. Repair the document first " +
                "(PdfOperations.RepairPdf), then sign the repaired bytes.");
        }

        // The generation an existing object carries is Core.Writing.ExistingObjectGeneration.Of.
        // It lived here while the three writers in this assembly — which build their own xref
        // sections and so never reach IncrementalUpdateWriter — were its only callers; the
        // incremental writer asks the same question now.

        /// <summary>
        /// The number of the object <paramref name="obj"/> names — <c>0</c> for a direct object,
        /// which has no number a revision could supersede.
        /// </summary>
        /// <remarks>
        /// <b>One reading</b>, for <c>PdfSigner</c>, <c>VisualSignatureBuilder</c> and
        /// <c>PdfLtvRefresher</c>. Every caller treats <c>0</c> as "refuse": §7.5.4 reserves
        /// object 0 as the head of the free list, so a catalog written there is unreachable.
        /// </remarks>
        internal static int ObjectNumberOf(PdfObject? obj)
            => obj is PdfReference r ? r.ReferenceObjectNumber : obj?.ObjectNumber ?? 0;

        // One implementation, in Core.Util.PdfDate (ISO 32000-1 7.9.4).
        internal static string FormatPdfDate(DateTime dt) => PdfDate.FormatUtc(dt);

        /// <summary>
        /// Why <c>PdfAcroForm.FindSignatureField</c> answered <c>null</c> for <paramref name="name"/>:
        /// no such field, or a partial name more than one field carries.
        /// </summary>
        internal static string SignatureFieldNotFound(string name, int partialNameMatches)
            => partialNameMatches > 1
                ? $"Signature field '{name}' is ambiguous: {partialNameMatches} signature fields have that " +
                  "partial name. Name the one you mean by its fully qualified name, e.g. 'parent.child'."
                : $"Signature field '{name}' not found.";

        /// <summary>
        /// Refuses to verify an encrypted document through a door that takes no password: it cannot
        /// read the form, so "no signature" would be an answer about a document it never read.
        /// </summary>
        internal static void RefuseEncryptedVerification(Core.Document.PdfDocument doc)
        {
            if (doc.IsEncrypted)
                throw new PdfSigningException(
                    "The document is encrypted, so its signatures cannot be read without the password. " +
                    "Verify it with Tetitulz.Pdf.Encryption.PdfEncryptionOperations.VerifySignatures, which takes one.");
        }

        // One implementation, in Core.Objects.PdfStringEscape (ISO 32000-1 7.3.4.2).
        internal static string EscapePdfString(string s) => PdfStringEscape.EscapeLiteral(s);

        /// <summary>
        /// A text string (§7.9.2.2) as the token written into the file: a literal string when every
        /// character is ASCII, else UTF-16BE behind its byte-order mark, in hex. The raw writer
        /// encodes ASCII only, so a literal of other characters came out as question marks.
        /// </summary>
        internal static string TextStringToken(string? text)
        {
            text ??= string.Empty;
            bool ascii = true;
            foreach (char c in text) if (c > 0x7E) { ascii = false; break; }
            if (ascii) return "(" + PdfStringEscape.EscapeLiteral(text) + ")";
            var sb = new System.Text.StringBuilder("<FEFF", 6 + text.Length * 4);
            foreach (byte b in System.Text.Encoding.BigEndianUnicode.GetBytes(text)) sb.Append(b.ToString("X2"));
            return sb.Append('>').ToString();
        }

        /// <summary>Upper-case hex encoding, as written into a <c>/Contents</c> placeholder.</summary>
        internal static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        /// <summary>
        /// Writes <paramref name="value"/> as hex over a <c>/Contents</c> placeholder's digits,
        /// zero-padded to the <paramref name="reservedHexChars"/> the placeholder holds.
        /// </summary>
        /// <remarks>
        /// <para><b>One writer</b>, for the signer, the deferred signer's completion, the
        /// counter-signer and the document timestamper: encoded, padded and written at the offset in
        /// one case. Both cases are valid (§7.3.4.3), which is exactly what lets copies drift without
        /// anything noticing.</para>
        ///
        /// <para><b>The fit check stays with each caller</b>, because what to tell someone whose value
        /// does not fit depends on how the placeholder was made: a larger
        /// <c>EstimatedSignatureSize</c>, a fresh <c>Prepare</c>, or <c>EstimateCmsSize</c> before the
        /// original was signed.</para>
        /// </remarks>
        /// <param name="file">The document holding the placeholder.</param>
        /// <param name="contentsStart">Offset of the placeholder's first hex digit, just past its <c>&lt;</c>.</param>
        /// <param name="reservedHexChars">How many hex digits the placeholder holds.</param>
        /// <param name="value">The CMS or timestamp token; it must fit.</param>
        internal static void WriteContents(Stream file, long contentsStart, int reservedHexChars, byte[] value)
        {
            byte[] digits = Encoding.ASCII.GetBytes(BytesToHex(value).PadRight(reservedHexChars, '0'));
            file.Position = contentsStart;
            file.Write(digits, 0, digits.Length);
        }

        /// <summary>
        /// Finds the hex region inside the <c>/Contents &lt;…&gt;</c> string that <c>/ByteRange</c>
        /// excludes: <paramref name="contentsStart"/> is the first hex digit and
        /// <paramref name="contentsEnd"/> the closing <c>&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The delimiters are LOCATED, never assumed.</b> The excluded region is not the
        /// hex run — it also covers the <c>&lt;</c> and <c>&gt;</c> themselves and whatever
        /// whitespace a writer put around them, so treating <c>HoleStart</c> as the first hex digit
        /// writes the CMS one byte early and corrupts every signature it touches.</para>
        ///
        /// <para><b>One implementation, because this is the third caller.</b> It was private to
        /// <c>PdfCounterSigner</c>, whose own comment records the defect that taught it; the
        /// deferred-signing completion needs exactly the same arithmetic. <c>SignatureByteRange</c>
        /// and <c>Core.Document.SignedByteRanges</c> both exist because earlier copies of the
        /// surrounding offset maths drifted — one of them by a single byte, which still verified.
        /// A third copy of this would be the same bet.</para>
        /// </remarks>
        internal static void FindContentsDelimiters(
            Stream file, long holeStart, long holeEnd, out long contentsStart, out long contentsEnd)
        {
            long span = holeEnd - holeStart;
            if (span <= 0 || span > int.MaxValue)
                throw new PdfSigningException(
                    "The region excluded by /ByteRange is empty or absurdly large, so the " +
                    "/Contents placeholder cannot be located.");

            int length = (int)span;
            byte[] window = new byte[length];
            long saved = file.Position;
            try
            {
                file.Position = holeStart;
                if (Core.IO.StreamBytes.Fill(file, window, 0, length) < length)
                    throw new PdfSigningException(
                        "Could not read the /Contents region named by /ByteRange.");
            }
            finally { file.Position = saved; }

            int open = Array.IndexOf(window, (byte)'<');
            int close = Array.LastIndexOf(window, (byte)'>');
            if (open < 0 || close < 0 || close <= open)
                throw new PdfSigningException(
                    "The region excluded by /ByteRange does not contain a '<...>' /Contents string, " +
                    "so a signature cannot be written into it without corrupting the file.");

            contentsStart = holeStart + open + 1;   // first hex digit
            contentsEnd = holeStart + close;        // the '>' itself
        }

        /// <summary>
        /// How far past a planned signature dictionary's <c>N G obj</c> header its two placeholders
        /// are looked for, beyond the <c>/Contents</c> reserve itself. The keys that precede them
        /// are a handful of names; the bound stops a damaged offset from making the scan read the
        /// whole file.
        /// </summary>
        private const int PlannedPlaceholderProbeBytes = 4096;

        /// <summary>
        /// Locates, in a written file, the <c>/ByteRange</c> and <c>/Contents</c> placeholders of a
        /// signature dictionary <see cref="PdfSigner.Plan"/> built and a writer serialised at
        /// <paramref name="objectOffset"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Found in the bytes, never assumed.</b> The plan's dictionary went through
        /// <c>Core.Writing.PdfWriter</c>, and for an encrypted document its <c>/Reason</c>,
        /// <c>/Location</c> and <c>/M</c> are ciphertext by the time they reach the file — so the
        /// only things this may rely on are the two placeholders themselves: a <c>/ByteRange</c>
        /// whose array is exactly as wide as the plan wrote it, and a <c>/Contents</c> whose hex run
        /// is exactly <paramref name="reservedHexChars"/> long and closed by <c>&gt;</c>. Anything
        /// else is refused, because a value written one byte off corrupts every signature it
        /// touches (see <see cref="FindContentsDelimiters"/>).</para>
        /// <para>The token scan is Core's — <c>StandalonePdfMacSeal</c> locates its own placeholder
        /// the same way — so there is one reading of "this name, not a longer one it prefixes".</para>
        /// </remarks>
        /// <param name="file">The written document, readable and seekable; its position is restored.</param>
        /// <param name="objectOffset">Stream position of the signature dictionary's <c>N G obj</c> header.</param>
        /// <param name="objectNumber">The number that header must carry.</param>
        /// <param name="reservedHexChars">How many hex digits the <c>/Contents</c> placeholder holds.</param>
        /// <param name="byteRangeOpen">Position of the <c>[</c> opening the <c>/ByteRange</c> array.</param>
        /// <param name="byteRangeWidth">Length of that array from <c>[</c> to <c>]</c> inclusive — the width to overwrite.</param>
        /// <param name="contentsStart">Position of the first hex digit inside <c>/Contents &lt;…&gt;</c>.</param>
        /// <param name="contentsEnd">Position of the closing <c>&gt;</c>.</param>
        /// <exception cref="PdfSigningException">The object at <paramref name="objectOffset"/> is not the planned dictionary.</exception>
        internal static void LocatePlannedPlaceholders(
            Stream file, long objectOffset, int objectNumber, int reservedHexChars,
            out long byteRangeOpen, out int byteRangeWidth, out long contentsStart, out long contentsEnd)
        {
            if (objectOffset < 0 || objectOffset >= file.Length)
                throw new PdfSigningException(
                    $"The cross-reference table places object {objectNumber} at offset {objectOffset}, " +
                    "which is outside the file; the planned signature dictionary cannot be located.");

            int want = (int)Math.Min(file.Length - objectOffset, (long)PlannedPlaceholderProbeBytes + reservedHexChars);
            var window = new byte[want];
            long saved = file.Position;
            try
            {
                file.Position = objectOffset;
                if (Core.IO.StreamBytes.Fill(file, window, 0, want) < want)
                    throw new PdfSigningException("The file ended before the planned signature dictionary could be read.");
            }
            finally { file.Position = saved; }

            byte[] header = Encoding.ASCII.GetBytes(objectNumber.ToString(CultureInfo.InvariantCulture) + " ");
            bool headed = window.Length > header.Length;
            for (int i = 0; headed && i < header.Length; i++) headed = window[i] == header[i];
            if (!headed)
                throw new PdfSigningException(
                    $"Offset {objectOffset} does not hold object {objectNumber}; the planned signature " +
                    "dictionary cannot be located.");

            int byteRangeKey = Core.Writing.StandalonePdfMacSeal.IndexOfName(window, "/ByteRange", 0);
            int open = byteRangeKey < 0 ? -1
                : Core.Writing.StandalonePdfMacSeal.NextNonWhitespace(window, byteRangeKey + "/ByteRange".Length, '[');
            int close = open < 0 ? -1 : Array.IndexOf(window, (byte)']', open);

            int contentsKey = Core.Writing.StandalonePdfMacSeal.IndexOfName(window, "/Contents", 0);
            int lt = contentsKey < 0 ? -1
                : Core.Writing.StandalonePdfMacSeal.NextNonWhitespace(window, contentsKey + "/Contents".Length, '<');
            long gt = lt < 0 ? -1 : (long)lt + 1 + reservedHexChars;

            if (close < 0 || lt < 0 || gt >= window.Length || window[gt] != (byte)'>')
                throw new PdfSigningException(
                    $"Object {objectNumber} does not carry the planned /ByteRange and /Contents placeholders " +
                    $"(a {reservedHexChars}-digit hex reserve); the signature cannot be completed into it.");

            byteRangeOpen = objectOffset + open;
            byteRangeWidth = close - open + 1;
            contentsStart = objectOffset + lt + 1;
            contentsEnd = objectOffset + gt;
        }
    }

    /// <summary>
    /// Adapts a BouncyCastle <see cref="Org.BouncyCastle.Crypto.IDigest"/> to
    /// <see cref="HashAlgorithm"/> so SHA-3 is usable on .NET&#160;Standard&#160;2.0 hosts where
    /// the BCL lacks <c>SHA3_*</c>.
    ///
    /// <para>This existed twice — once inside <c>TsaTokenVerifier</c> and once inside
    /// <c>PdfDocumentTimestamper</c>, the former's doc-comment admitting it mirrored the
    /// latter.</para>
    /// </summary>
    internal sealed class BouncyCastleHashAlgorithm : HashAlgorithm
    {
        private readonly Org.BouncyCastle.Crypto.IDigest _digest;

        internal BouncyCastleHashAlgorithm(Org.BouncyCastle.Crypto.IDigest digest)
        {
            _digest = digest;
            HashSizeValue = digest.GetDigestSize() * 8;
        }

        public override void Initialize() => _digest.Reset();

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
            => _digest.BlockUpdate(array, ibStart, cbSize);

        protected override byte[] HashFinal()
        {
            var output = new byte[_digest.GetDigestSize()];
            _digest.DoFinal(output, 0);
            return output;
        }
    }

    /// <summary>
    /// Builds a <see cref="HashAlgorithm"/> for a digest identified either by OID (verification
    /// paths, where the OID comes out of the token) or by algorithm name (request paths, where
    /// the caller chose it). SHA-3 routes through BouncyCastle so it also works on
    /// .NET&#160;Standard&#160;2.0.
    ///
    /// <para><c>TsaTokenVerifier</c> and <c>PdfDocumentTimestamper</c> both use it.</para>
    /// </summary>
    internal static class DigestAlgorithms
    {
        /// <summary>
        /// Whether <paramref name="oid"/> is a digest this library signs, verifies and time-stamps
        /// with: SHA-1, SHA-256/384/512, SHA3-256/384/512 (RFC 8702) and SHAKE256 (ISO/TS 32001) —
        /// the set <see cref="CreateFromOid"/> builds. MD5 and MD2, which <see cref="PdfDigestOids"/>
        /// also names, never; Streebog only where a caller opts in.
        /// </summary>
        /// <remarks>
        /// One set, for the generator and the verifier's ESS check alike: a verifier set lacking the
        /// SHA-3 family the generator can emit would reject this library's own signatures.
        /// </remarks>
        internal static bool IsStandard(string oid) => oid switch
        {
            PdfDigestOids.Sha1 or PdfDigestOids.Sha256 or PdfDigestOids.Sha384 or PdfDigestOids.Sha512
                or PdfDigestOids.Sha3_256 or PdfDigestOids.Sha3_384 or PdfDigestOids.Sha3_512
                or PdfDigestOids.Shake256 => true,
            _ => false,
        };

        /// <summary>
        /// Creates the hash algorithm named by <paramref name="oid"/>.
        /// Throws <see cref="CryptographicException"/> for an OID outside the SHA-1/2/3 families —
        /// surfacing the actual OID beats a silent SHA-256 fallback that would report a bogus
        /// "hash mismatch".
        /// </summary>
        internal static HashAlgorithm CreateFromOid(string oid)
        {
            switch (oid)
            {
                case PdfDigestOids.Sha256: return SHA256.Create();
                case PdfDigestOids.Sha384: return SHA384.Create();
                case PdfDigestOids.Sha512: return SHA512.Create();
                case PdfDigestOids.Sha1:   return SHA1.Create();
                // SHA-3 family per RFC 8702.  Without these cases, a token
                // signed with SHA-3 would silently fall through to SHA-256
                // and the message-imprint comparison would fail with a
                // misleading "hash mismatch" instead of correctly verifying.
                case PdfDigestOids.Sha3_256:
                    return new BouncyCastleHashAlgorithm(new Org.BouncyCastle.Crypto.Digests.Sha3Digest(256));
                case PdfDigestOids.Sha3_384:
                    return new BouncyCastleHashAlgorithm(new Org.BouncyCastle.Crypto.Digests.Sha3Digest(384));
                case PdfDigestOids.Sha3_512:
                    return new BouncyCastleHashAlgorithm(new Org.BouncyCastle.Crypto.Digests.Sha3Digest(512));
                // ISO/TS 32001 §5.1.4: id-shake256 fixes the output at 512 bits, so
                // ShakeDigest(256)'s default digest size (already 64 bytes) is exactly right —
                // verified against the FIPS 202 SHAKE256("") test vector.
                case PdfDigestOids.Shake256:
                    return new BouncyCastleHashAlgorithm(new Org.BouncyCastle.Crypto.Digests.ShakeDigest(256));
                default:
                    throw new CryptographicException(
                        "Unsupported digest OID '" + oid + "' on TimeStampToken.");
            }
        }

        /// <summary>
        /// Resolves a digest-algorithm <b>name</b> to its OID for a <b>new</b> signature, a
        /// counter-signature or an RFC&#160;3161 request — the generation side, where the caller
        /// chose the name. Anything outside the accepted set throws
        /// <see cref="ArgumentException"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>One implementation</b>, for <c>CreateFromName</c> below, <c>TsaClient
        /// .GetHashOid</c>, <c>PdfCounterSigner.GetDigestOid</c> and
        /// <c>CmsSignatureGenerator.GetDigestOid</c>, so they agree on what an unrecognised name
        /// means.</para>
        ///
        /// <para><b>The defect this closes.</b> <c>CmsSignatureGenerator</c>'s <c>default:</c> arm
        /// returned <see cref="PdfDigestOids.Sha256"/> instead of throwing, and
        /// <c>PdfSigner.Sign</c> passes <c>SignatureParameters.DigestAlgorithm</c> into it with no
        /// prior acceptance check (the <c>TsaClient.GetHashOid</c> call on the signing path gates
        /// only the separate <c>TimestampDigestAlgorithm</c>). So
        /// <c>DigestAlgorithm = "SHA-224"</c> — or "MD5", or any typo — produced a
        /// <b>SHA-256</b> signature with no warning of any kind, while
        /// <c>PdfCounterSigner.AddCounterSignature</c> refused the identical string. A caller who
        /// asked for a stronger digest, or a weaker one they were entitled to be told about, got
        /// neither the algorithm nor the error.</para>
        ///
        /// <para><b>Why it survived.</b> The downgrade is invisible in the artefact: the CMS is
        /// well-formed, it verifies, and its <c>digestAlgorithm</c> field says SHA-256 — which is
        /// exactly what a correct SHA-256 signature says. Nothing in the output distinguishes
        /// "the caller asked for SHA-256" from "the caller asked for something else and we
        /// substituted". Only a test that names an unsupported algorithm and reads the OID back
        /// out of the generated CMS can tell them apart.</para>
        ///
        /// <para>The two genuine policy differences are parameters rather than drift:
        /// <paramref name="allowStreebog"/> (GOST&#160;R&#160;34.11-2012 is a live signing path —
        /// <c>SignatureParametersExtensions.UseAlgorithm(SignatureAlgorithm.GostR3410_2012_256)</c>
        /// sets <c>DigestAlgorithm = "GOST3411-2012-256"</c> — but no TSA request or
        /// <see cref="HashAlgorithm"/> factory here supports it), and
        /// <paramref name="defaultWhenUnnamed"/>, which keeps <c>null</c>/empty meaning "use the
        /// default" on the public <c>CmsSignatureGenerator</c> entry points. <b>Unnamed is not the
        /// same as unrecognised</b>: only the first is a caller declining to choose.</para>
        /// </remarks>
        /// <param name="name">Caller-supplied algorithm name, e.g. "SHA-256" or "SHA3_512".</param>
        /// <param name="parameterName">
        /// The name of the caller's own parameter, so the thrown
        /// <see cref="ArgumentException"/> points at the argument the caller actually passed.
        /// </param>
        /// <param name="allowStreebog">
        /// Accept GOST R 34.11-2012 (Streebog), including the "STREEBOG256" / "STREEBOG-512"
        /// aliases <see cref="PdfDigestOids.FromName"/> does not carry.
        /// </param>
        /// <param name="defaultWhenUnnamed">
        /// Return SHA-256 for a <see langword="null"/> or empty name instead of throwing. An
        /// unrecognised name still throws — that is the whole point of this method.
        /// </param>
        internal static string ResolveOidForNewSignature(
            string name,
            string parameterName,
            bool allowStreebog = false,
            bool defaultWhenUnnamed = false)
        {
            if (defaultWhenUnnamed && string.IsNullOrEmpty(name))
                return PdfDigestOids.Sha256;

            // PdfDigestOids.FromName is the single name -> OID table; the underscore spellings
            // ("SHA3_256") every one of these call sites has always accepted are folded onto the
            // hyphenated form it understands.
            string? spelling = name?.Replace('_', '-');

            if (allowStreebog)
            {
                switch (spelling?.ToUpperInvariant())
                {
                    case "STREEBOG256":
                    case "STREEBOG-256":
                        return PdfDigestOids.Streebog256;
                    case "STREEBOG512":
                    case "STREEBOG-512":
                        return PdfDigestOids.Streebog512;
                }
            }

            // FromName also knows MD5 and MD2. They are deliberately NOT accepted here: this is the
            // generation side, and a broken digest must not be selectable by a caller's typo.
            string oid = PdfDigestOids.FromName(spelling);
            if (IsStandard(oid)) return oid;
            if (allowStreebog && (oid == PdfDigestOids.Streebog256 || oid == PdfDigestOids.Streebog512)) return oid;

            throw new ArgumentException(
                "Unsupported digest algorithm '" + name + "'. " +
                "Supported: SHA-1, SHA-256, SHA-384, SHA-512, " +
                "SHA3-256, SHA3-384, SHA3-512, SHAKE256" +
                (allowStreebog ? ", GOST3411-2012-256, GOST3411-2012-512." : "."),
                parameterName);
        }

        /// <summary>
        /// Creates the hash algorithm named by <paramref name="name"/> ("SHA-256", "SHA3_512", …).
        /// Throws <see cref="ArgumentException"/> for anything outside the supported set rather
        /// than downgrading to SHA-256 behind the caller's back.
        /// </summary>
        internal static HashAlgorithm CreateFromName(string name)
            // Resolving to an OID first keeps the supported set identical to CreateFromOid's.
            => CreateFromOid(ResolveOidForNewSignature(name, nameof(name)));
    }
}


