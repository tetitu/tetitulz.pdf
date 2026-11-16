// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Enumerates and extracts the revisions of an incrementally-updated PDF.
    ///
    /// <para>Every signature-preserving edit in this library —
    /// <see cref="Tetitulz.Pdf.Base.Operations.Images.PdfImageReplacer"/>,
    /// form filling, annotations, the accessibility writers — works by <i>appending</i> a
    /// revision, because the earlier bytes are exactly what an existing signature's
    /// <c>/ByteRange</c> digest covers. The superseded objects are unreachable from the new
    /// trailer but still present in the file. This is what reads them back: truncating the file
    /// at an earlier revision boundary yields a complete, openable PDF as it stood then.</para>
    ///
    /// <para><b>That is also the honest counterpart to the replacement API's central caveat.</b>
    /// "Replaced" means hidden, not deleted — and the fact that this class exists is the proof.
    /// Anyone holding the file can recover the pre-replacement image; only
    /// <c>PdfRedactor.RedactRegions</c>, which rewrites the whole document into a single
    /// revision and therefore invalidates every signature, actually removes data.</para>
    ///
    /// <para><b>How boundaries are found.</b> Each revision ends with
    /// <c>startxref &lt;offset&gt; %%EOF</c>. This scans for <c>%%EOF</c> and keeps only the
    /// occurrences preceded, within <see cref="StartXrefWindowBytes"/>, by a <c>startxref</c>
    /// whose offset parses and points backwards into the file. That validation is what stops a
    /// <c>%%EOF</c> sequence occurring inside a content or image stream from inventing a
    /// revision. It does not walk the <c>/Prev</c> chain, so a file whose chain is broken but
    /// whose revisions are physically intact still enumerates.</para>
    ///
    /// <para><b>One <c>%%EOF</c> is deliberately not a boundary.</b> A linearized file's first-page
    /// trailer may end in <c>startxref</c>, an integer and <c>%%EOF</c> that Annex F.3.4 says
    /// "shall be ignored" — see <see cref="LinearizedFirstPageTerminator"/>, which recognises it by
    /// the file's structure rather than by the integer's value.</para>
    /// </summary>
    public static class PdfRevisions
    {
        /// <summary>
        /// How far back from a <c>%%EOF</c> the <c>startxref</c> keyword is looked for. The
        /// keyword, an end-of-line, a decimal offset and another end-of-line always fit well
        /// inside this; bounding it stops the validation from scanning the whole file per match.
        /// </summary>
        private const int StartXrefWindowBytes = 64;

        /// <summary>
        /// How far short of a revision boundary a signature's <c>/ByteRange</c> may end and still
        /// be counted as covering it: the width of the end-of-line after <c>%%EOF</c>, which is
        /// CR LF at most. See <see cref="RevisionCovering"/>.
        /// </summary>
        private const int ByteRangeEndSlackBytes = 2;

        /// <summary>
        /// Stands in for a signature field with no <c>/T</c> anywhere in its <c>/Parent</c> chain.
        /// Such a field is legal and unnameable; dropping it from the attribution instead would
        /// under-report how many signatures cover a revision, which is the one direction this API
        /// must not err in.
        /// </summary>
        private const string UnnamedSignature = "(unnamed)";


        private static readonly byte[] EofToken = { (byte)'%', (byte)'%', (byte)'E', (byte)'O', (byte)'F' };
        private static readonly byte[] StartXrefToken =
        {
            (byte)'s', (byte)'t', (byte)'a', (byte)'r', (byte)'t',
            (byte)'x', (byte)'r', (byte)'e', (byte)'f',
        };
        private static readonly byte[] ObjToken = System.Text.Encoding.ASCII.GetBytes("obj");
        private static readonly byte[] EndObjToken = System.Text.Encoding.ASCII.GetBytes("endobj");
        private static readonly byte[] LinearizedName = System.Text.Encoding.ASCII.GetBytes("/Linearized");
        private static readonly byte[] XRefName = System.Text.Encoding.ASCII.GetBytes("/XRef");

        /// <summary>
        /// Whether the document declares itself linearized (ISO 32000-1 Annex F) — the question a
        /// host has to answer before deciding whether a progressive, byte-range fetch is worth
        /// attempting at all.
        /// </summary>
        /// <remarks>
        /// <para><b>It reads the first 1024 bytes and nothing else.</b> Annex F.3.3 puts the
        /// linearization dictionary in the first object of the file and within that window, so the
        /// answer needs no cross-reference table, no trailer and no seek to the tail — which means
        /// it can be asked of the head of a download that has not finished arriving.</para>
        ///
        /// <para><b>It reports the claim, not conformance.</b> A file whose first object carries
        /// <c>/Linearized</c> says it was written for progressive access; whether its hint tables
        /// are correct, or still match after an incremental update (Table F.1's <c>/L</c> goes stale
        /// the moment a revision is appended), is a different and much larger question that
        /// <c>PdfLinearizer</c>'s own conformance suite asks. Reporting the claim is what a caller
        /// needs to choose a fetch strategy, and over-claiming here would send it down a path the
        /// file cannot support.</para>
        ///
        /// <para><b>A stream cannot say "not yet", so this answers <c>false</c> when the head has
        /// not arrived</b> — the same answer it gives an ordinary file.
        /// <see cref="ReadLinearizationState"/> takes a <c>Core.IO.IByteRangeSource</c> and has the
        /// third answer; use it wherever the input may be incomplete.</para>
        ///
        /// <para><b>This library cannot yet consume what it produces</b> — the parser finds
        /// <c>startxref</c> at the tail and walks <c>/Prev</c> like any other file, deliberately
        /// (GAPS.md §23). This predicate is the first piece of that gap, not a claim that the rest
        /// exists.</para>
        /// </remarks>
        /// <param name="input">The document, or as much of its head as has arrived. Not disposed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static bool IsLinearized(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            return LinearizationDictionaryAt(
                ReadHead(input, Core.Constants.PdfLimits.LinearizationDictionaryWindowBytes)) >= 0;
        }

        /// <summary>
        /// Reads the Annex F linearization parameter dictionary (Table F.1), or answers
        /// <c>false</c> when the document does not begin with one.
        /// </summary>
        /// <remarks>
        /// <para><b>Also from the head alone</b>, for the same reason
        /// <see cref="IsLinearized"/> is: the dictionary is the file's first object. What it buys
        /// over the predicate is the numbers a progressive reader needs to ask for anything —
        /// above all <c>/H</c>, the hint stream's offset and length, which is the one range a
        /// client must fetch before it can locate a page.</para>
        ///
        /// <para><b>Every value is the producer's claim about a file this has not read.</b> They
        /// are reported as written, not validated: Table F.1's <c>/L</c> is the length the file had
        /// when it was linearized, and an appended revision makes it — and the hint tables with it —
        /// stale without changing anything here. A caller that cares must compare
        /// <see cref="PdfLinearizationInfo.FileLength"/> against the bytes it actually has, which is
        /// exactly the check a progressive fetch wants to make anyway.</para>
        ///
        /// <para><b>It lives beside the revision walk rather than in a type of its own</b> because
        /// the byte-window recognition is shared with <see cref="LinearizedFirstPageTerminator"/>,
        /// and moving it out would mean a third private copy of the two byte-search primitives this
        /// file already has.</para>
        /// </remarks>
        /// <param name="input">The document, or as much of its head as has arrived. Not disposed.</param>
        /// <param name="info">The parameters, or <c>null</c> when this answers <c>false</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static bool TryReadLinearization(Stream input, out PdfLinearizationInfo info)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            info = null;

            byte[] head = ReadHead(input, ParameterWindows * Core.Constants.PdfLimits.LinearizationDictionaryWindowBytes);

            int obj = LinearizationDictionaryAt(head);
            if (obj < 0) return false;

            return TryReadParameters(head, obj, out info);
        }

        /// <summary>
        /// Windows of head the parameter read asks for: the dictionary itself is inside the first
        /// one by Annex F.3.3, and the rest is room for the object header and <c>endobj</c> the
        /// parser needs to see around it.
        /// </summary>
        private const int ParameterWindows = 4;

        /// <summary>
        /// Parses the linearization dictionary the predicate has already located, into Table F.1.
        /// </summary>
        /// <remarks>
        /// Shared by the <see cref="Stream"/> overload and the byte-range one, which differ only in
        /// how they obtain the head — the reading of it must not differ at all.
        /// </remarks>
        private static bool TryReadParameters(byte[] head, int obj, out PdfLinearizationInfo info)
        {
            info = null;

            int start = ObjectHeaderStart(head, obj);
            PdfDictionary dict;
            try
            {
                using var slice = new MemoryStream(head, start, head.Length - start, writable: false);
                dict = new Core.Parsing.PdfParser(slice).ParseIndirectObject() as PdfDictionary;
            }
            catch (Core.PdfException)
            {
                // A head too short to hold the whole object, or a malformed one. Either way the
                // parameters are not readable, which is the answer rather than an exception: a
                // caller asking this of a partial download is asking whether it has enough yet.
                return false;
            }

            if (dict == null) return false;

            PdfArray hint = dict.GetArray(PdfName.H);
            info = new PdfLinearizationInfo(
                fileLength: dict.GetLong(PdfName.L) ?? 0,
                firstPageObjectNumber: dict.GetInteger(PdfName.O) ?? 0,
                firstPageEndOffset: dict.GetLong(PdfName.E) ?? 0,
                pageCount: dict.GetInteger(PdfName.N) ?? 0,
                mainCrossReferenceOffset: dict.GetLong(PdfName.T) ?? 0,
                hintStreamOffset: HintNumber(hint, 0),
                hintStreamLength: HintNumber(hint, 1),
                overflowHintOffset: HintNumber(hint, 2),
                overflowHintLength: HintNumber(hint, 3));

            return true;
        }

        /// <summary>
        /// Annex F's opening question asked of a file that may not have finished arriving — with
        /// the third answer a <see cref="Stream"/> cannot give.
        /// </summary>
        /// <remarks>
        /// <para><b>The Stream overload folds "not yet" into "no", because it has nowhere else to
        /// put it.</b> A stream reports bytes or the end, so a download that has delivered nothing
        /// is indistinguishable from an ordinary file — and a host deciding whether a byte-range
        /// fetch is worth attempting then decides against it on no evidence.
        /// <see cref="PdfLinearizationState.NotYetAvailable"/> is that missing answer, and it means
        /// exactly one thing: <b>more bytes could change the verdict</b>.</para>
        ///
        /// <para><b>A negative answer is final as soon as the first window is here.</b> Annex F.3.3
        /// puts the dictionary in the file's first object and within
        /// <c>PdfLimits.LinearizationDictionaryWindowBytes</c>, so once those bytes have arrived
        /// without one, no later byte can introduce one. That is why this does not simply defer
        /// whenever the file is incomplete — deferring a settled answer would make the state useless
        /// for the decision it exists to inform.</para>
        /// </remarks>
        /// <param name="source">The file, or as much of it as has arrived.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static PdfLinearizationState ReadLinearizationState(Core.IO.IByteRangeSource source)
            => ReadLinearization(source, out _, parameters: false);

        /// <summary>
        /// The same question, with Table F.1's parameters when they can be read — above all
        /// <c>/H</c>, the one range a progressive client must fetch before it can locate a page.
        /// </summary>
        /// <remarks>
        /// <para><b>It reads forward one Annex F window at a time</b> and stops at the first window
        /// that has not arrived, so a caller is never asked for more of the file than the answer
        /// needs. The claim is settled by the first window; the parameters need the whole first
        /// object, which may run past it.</para>
        ///
        /// <para><b><paramref name="info"/> can be null for a
        /// <see cref="PdfLinearizationState.Linearized"/> file</b>, and that is not a contradiction:
        /// the state reports the claim the first window carries, while the parameters need an object
        /// that parses. A malformed or enormous dictionary whose bytes are all here gives the same
        /// pair the <see cref="Stream"/> overloads give — <c>IsLinearized</c> true,
        /// <c>TryReadLinearization</c> false.</para>
        /// </remarks>
        /// <param name="source">The file, or as much of it as has arrived.</param>
        /// <param name="info">Table F.1, or <c>null</c> when it could not be read.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static PdfLinearizationState ReadLinearization(
            Core.IO.IByteRangeSource source, out PdfLinearizationInfo info)
            => ReadLinearization(source, out info, parameters: true);

        private static PdfLinearizationState ReadLinearization(
            Core.IO.IByteRangeSource source, out PdfLinearizationInfo info, bool parameters)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            info = null;

            int window = Core.Constants.PdfLimits.LinearizationDictionaryWindowBytes;

            // Nothing at all: the one state where nothing can be said.
            if (!TryReadPrefix(source, window, out byte[] head))
                return PdfLinearizationState.NotYetAvailable;

            int obj = LinearizationDictionaryAt(head);
            if (obj < 0) return PdfLinearizationState.NotLinearized;   // final, by Annex F.3.3
            if (!parameters) return PdfLinearizationState.Linearized;

            for (int windows = 1; ; windows++)
            {
                if (TryReadParameters(head, obj, out info)) return PdfLinearizationState.Linearized;

                // Every byte of the head we are willing to read is here and the object still does
                // not parse: the bytes are not the problem, so this is the Stream overloads' answer
                // — the claim stands and the parameters do not.
                if (windows >= ParameterWindows) return PdfLinearizationState.Linearized;

                // More of the object might make it parse, so a missing window is "not yet" here
                // where it was not above.
                if (!TryReadPrefix(source, (windows + 1) * window, out head))
                    return PdfLinearizationState.NotYetAvailable;

                obj = LinearizationDictionaryAt(head);
                if (obj < 0) return PdfLinearizationState.NotLinearized;
            }
        }

        /// <summary>
        /// The file's first <paramref name="count"/> bytes, or <c>false</c> when they have not all
        /// arrived — trimmed to the file's real length, which a short read means and nothing else.
        /// </summary>
        private static bool TryReadPrefix(Core.IO.IByteRangeSource source, int count, out byte[] head)
        {
            head = null;

            var buffer = new byte[count];
            if (!source.TryRead(0, buffer, 0, count, out int read)) return false;

            head = Trimmed(buffer, read);
            return true;
        }

        /// <summary>
        /// The first <paramref name="count"/> bytes of <paramref name="input"/>, or as many as it
        /// has — the read both Annex F questions are answered from, so neither needs the file.
        /// </summary>
        private static byte[] ReadHead(Stream input, int count)
        {
            var head = new byte[count];
            int read = Core.IO.StreamBytes.Fill(input, head, 0, head.Length);
            return Trimmed(head, read);
        }

        /// <summary>
        /// A head buffer cut down to the bytes that actually arrived.
        /// </summary>
        /// <remarks>
        /// <para>Trimming is honesty rather than correctness, and a mutation sweep said so twice:
        /// the untrimmed tail is zeros, which match neither <c>obj</c> nor <c>/Linearized</c>, so
        /// removing it changes no answer either path gives today. It stays because scanning bytes
        /// the file does not have is the kind of thing that is only harmless until the scan
        /// changes.</para>
        ///
        /// <para><b>It is one method because it was one decision in two places.</b> The
        /// <see cref="Stream"/> head read and the byte-range one differ in where they read from —
        /// the caller's position against absolute zero — and in what a failure means, and in
        /// nothing else; the accidentally-equivalent classification above would otherwise have had
        /// to be recorded, and re-verified, at both.</para>
        /// </remarks>
        private static byte[] Trimmed(byte[] buffer, int read)
        {
            if (read < buffer.Length) Array.Resize(ref buffer, read);
            return buffer;
        }

        /// <summary>One entry of Table F.1's <c>/H</c> array, or 0 when it is not there.</summary>
        /// <remarks>
        /// <c>/H</c> carries two numbers, or four when the hint stream is split across the file's
        /// two halves. The second pair is optional and its absence is not a defect, so a missing
        /// entry answers 0 rather than refusing the whole dictionary.
        /// </remarks>
        private static long HintNumber(PdfArray hint, int index) =>
            hint != null && index < hint.Count && hint[index] is PdfNumber n ? n.LongValue : 0;

        /// <summary>
        /// Walks back from an <c>obj</c> keyword over the generation and object numbers, so a
        /// parser can be handed the whole <c>N G obj … endobj</c> rather than its tail.
        /// </summary>
        private static int ObjectHeaderStart(byte[] bytes, int obj)
        {
            int i = obj;
            for (int fields = 0; fields < 2; fields++)
            {
                while (i > 0 && IsWhite(bytes[i - 1])) i--;
                while (i > 0 && bytes[i - 1] >= (byte)'0' && bytes[i - 1] <= (byte)'9') i--;
            }
            return i;
        }

        private static bool IsWhite(byte b) =>
            b == (byte)' ' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\t' || b == 0;

        /// <summary>
        /// The document's revisions, oldest first. A conventionally-written PDF that has never
        /// been incrementally updated has exactly one; each appended revision adds another. An
        /// empty list means no validated <c>%%EOF</c> was found at all, which is a damaged file.
        /// </summary>
        public static IReadOnlyList<PdfRevisionInfo> List(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] bytes = ReadAll(input);
            return Boundaries(bytes);
        }

        /// <summary>
        /// Writes the document as it stood at <paramref name="revisionIndex"/> (0 = the original)
        /// to <paramref name="output"/>. The result is the input's first <i>n</i> bytes, so it is
        /// a valid PDF that any reader will open, and every signature covering that revision still
        /// verifies against it.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">No such revision.</exception>
        public static void Extract(Stream input, int revisionIndex, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (revisionIndex < 0) throw new ArgumentOutOfRangeException(nameof(revisionIndex));

            byte[] bytes = ReadAll(input);
            var revisions = Boundaries(bytes);
            if (revisionIndex >= revisions.Count)
                throw new ArgumentOutOfRangeException(nameof(revisionIndex),
                    $"Document has {revisions.Count} revision(s); requested index {revisionIndex}.");

            output.Write(bytes, 0, (int)revisions[revisionIndex].Length);
        }

        /// <summary>
        /// Writes the revision immediately before the current one — the state an edit superseded.
        /// Returns <c>false</c>, writing nothing, when the document has only one revision and
        /// there is therefore no earlier state to recover.
        /// </summary>
        public static bool TryExtractPrevious(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            byte[] bytes = ReadAll(input);
            var revisions = Boundaries(bytes);
            if (revisions.Count < 2) return false;

            output.Write(bytes, 0, (int)revisions[revisions.Count - 2].Length);
            return true;
        }

        /// <summary>
        /// The document's revisions, oldest first, with each one attributed to the signatures that
        /// cover it — i.e. those whose declared <c>/ByteRange</c> ends at that revision's boundary.
        /// Every returned <see cref="PdfRevisionInfo"/> carries a non-null
        /// <see cref="PdfRevisionInfo.SignedBy"/> when
        /// <see cref="PdfRevisionAttribution.SignaturesExamined"/> is true.
        ///
        /// <para><b>This reads the declaration, not the cryptography.</b> A <c>/ByteRange</c> says
        /// which bytes a signature <i>claims</i> to cover; whether the digest over them actually
        /// verifies, and whether the certificate is trusted, is
        /// <c>Tetitulz.Pdf.Signing.PdfSignatureVerifier</c>'s question. Answering the coverage
        /// question here is what lets an operation in this assembly say "revision 2 is signed" —
        /// <c>Base</c> does not reference <c>Signing</c> and must not start.</para>
        /// </summary>
        public static PdfRevisionAttribution ListWithSignatures(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            byte[] bytes = ReadAll(input);
            var revisions = Boundaries(bytes);
            var unattributed = new List<string>();

            string notExamined = TryAttribute(bytes, revisions, unattributed);
            return new PdfRevisionAttribution(revisions, notExamined, unattributed);
        }

        /// <summary>
        /// Fills in <see cref="PdfRevisionInfo.SignedBy"/> for every revision, or returns the
        /// reason it could not. On any failure path the property is left <c>null</c> on every
        /// revision, so "not examined" can never be mistaken for "examined, found nothing" —
        /// which is the whole difference the caller is entitled to see.
        /// </summary>
        private static string TryAttribute(
            byte[] bytes, List<PdfRevisionInfo> revisions, List<string> unattributed)
        {
            var names = new List<string>[revisions.Count];
            try
            {
                // A fresh stream over the bytes already read: the caller's may be a forward-only
                // one that ReadAll has consumed, and the parser seeks.
                using var ms = new MemoryStream(bytes, writable: false);
                using var doc = PdfDocument.Open(ms);

                // §7.6.2 encrypts strings, so a field's /T would come back as ciphertext, and
                // PdfDocument.Load stops before the catalog for an encrypted file in any case.
                // Reporting the absence is the honest answer; returning an empty attribution
                // would read as "this document has no signatures".
                if (doc.IsEncrypted)
                    return "the document is encrypted, so its form fields were not read; " +
                           "decrypt it first (Tetitulz.Pdf.Encryption) to attribute its signatures";

                foreach (var (field, sig) in SignatureFieldWalk.DistinctSignatures(
                             SignatureFieldWalk.ResolveAcroForm(doc)))
                {
                    // ONE reader, in Core's SignedByteRanges. The comment this replaces said
                    // "§12.8.1 requires /ByteRange to be a direct object — a signer has to write
                    // it before it can digest the file — so a non-resolving read is correct here",
                    // and that is not what §12.8.1 says: the in-place requirement is on /Contents,
                    // because the gap the range skips IS that string's bytes. /ByteRange is an
                    // ordinary array inside the digested dictionary, §7.3.10 permits a reference
                    // for it and for each element, and PdfSignatureVerifier resolves it. Written
                    // indirectly it read as absent here, so the signature was dropped from the
                    // attribution without even reaching `unattributed` — the caller was told the
                    // revision had no signature rather than that one could not be read.
                    if (!SignedByteRanges.TryRead(sig, doc, out var br, out _)) continue;

                    string name = field.FullyQualifiedName;
                    if (string.IsNullOrEmpty(name)) name = UnnamedSignature;

                    int revision = RevisionCovering(revisions, br.Offset2, br.Length2);
                    if (revision < 0) { unattributed.Add(name); continue; }
                    (names[revision] ??= new List<string>()).Add(name);
                }
            }
            catch (Exception ex)
            {
                // The revision list itself is a byte-level read and stands regardless; only the
                // object-graph half is lost, so say which half and keep the rest.
                //
                // Nothing clears `unattributed` here any more: PdfRevisionAttribution derives the
                // empty list from the reason, so a partial list collected before the throw cannot
                // reach a caller however this method returns.
                return "the object graph could not be read: " + ex.Message;
            }

            for (int i = 0; i < revisions.Count; i++)
                revisions[i].SetSignedBy(names[i] == null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : names[i].AsReadOnly());
            return null;
        }

        /// <summary>
        /// The revision a <c>/ByteRange</c>'s second segment ends at, or <c>-1</c> for none.
        ///
        /// <para><b>The match allows slack, and must.</b> <see cref="Boundaries"/> deliberately
        /// swallows the end-of-line after <c>%%EOF</c> so an extracted revision ends where the
        /// writer left it, while a signer is free to end its range at the <c>F</c>. Everything
        /// this repo writes matches exactly — <c>PdfSigner</c> sets the trailing length to
        /// <c>fileLength - rangeAfterStart</c> and both writers emit <c>%%EOF\n</c> — but a
        /// third-party signer need not, so a shortfall of up to a CR LF is accepted and nothing
        /// more. Widening it further would start claiming a <i>different</i> revision.</para>
        ///
        /// <para>The smallest qualifying shortfall wins: revisions are strictly increasing in
        /// length, so at most one can be within two bytes, but ordering by shortfall keeps that
        /// true by construction rather than by assumption. (Consecutive boundaries are at least a
        /// <c>%%EOF</c> apart, so taking the last qualifying revision instead is an equivalent
        /// mutation today — the ordering is the guarantee, not a behaviour any file can show.)</para>
        /// </summary>
        private static int RevisionCovering(List<PdfRevisionInfo> revisions, long start2, long length2)
        {
            // Both halves are needed and neither is theoretical. A NEGATIVE segment length is the
            // dangerous one: /ByteRange [0 n <fileLength> -1] ends one byte before the file does,
            // which is inside the tolerance below, so a malformed range would be reported as
            // covering the current revision. Overflow is the other direction — two enormous legal
            // integers whose sum wraps — and is rejected before it is computed rather than
            // recognised afterwards from a negative result.
            //
            // Their order is load-bearing, and a mutation run proved it: the overflow test
            // subtracts length2, so a negative one wraps `long.MaxValue - length2` to
            // long.MinValue and that test alone rejects every input. It happens to reject the
            // right thing for the malformed case, which is precisely why the negative check has
            // to be stated rather than left as a side effect of the other.
            if (start2 < 0 || length2 < 0) return -1;
            // Defence in depth rather than load-bearing: with both halves non-negative, a wrapped
            // sum can only land in [long.MinValue, -2], and no revision length is within the slack
            // of that — so removing this line is an equivalent mutation. It stays so the arithmetic
            // below never runs on a wrapped value.
            if (start2 > long.MaxValue - length2) return -1;
            long end = start2 + length2;

            int best = -1;
            long bestShortfall = long.MaxValue;
            for (int i = 0; i < revisions.Count; i++)
            {
                long shortfall = revisions[i].Length - end;
                if (shortfall < 0 || shortfall > ByteRangeEndSlackBytes) continue;
                if (shortfall < bestShortfall) { bestShortfall = shortfall; best = i; }
            }
            return best;
        }

        // ----------------------------------------------------------------

        private static byte[] ReadAll(Stream input)
        {
            var seekable = PdfHelpers.EnsureSeekable(input);
            if (seekable is MemoryStream ms) return ms.ToArray();

            using var copy = new MemoryStream();
            seekable.Position = 0;
            seekable.CopyTo(copy);
            return copy.ToArray();
        }

        private static List<PdfRevisionInfo> Boundaries(byte[] bytes)
        {
            var result = new List<PdfRevisionInfo>();
            if (bytes == null || bytes.Length < EofToken.Length) return result;

            // The one %%EOF Annex F says to ignore — a linearized file's first-page terminator —
            // found once, by structure, before the scan.
            int ignoredEof = LinearizedFirstPageTerminator(bytes);

            for (int i = 0; i + EofToken.Length <= bytes.Length; i++)
            {
                if (!Matches(bytes, i, EofToken)) continue;
                if (i == ignoredEof || !HasValidStartXrefBefore(bytes, i)) continue;

                // Include the end-of-line that follows, so an extracted revision ends exactly
                // where the writer left it rather than mid-terminator.
                int end = i + EofToken.Length;
                if (end < bytes.Length && bytes[end] == (byte)'\r') end++;
                if (end < bytes.Length && bytes[end] == (byte)'\n') end++;

                result.Add(new PdfRevisionInfo(result.Count, end));
                // Skips the swallowed end-of-line. Only a saving: no %%EOF can begin inside the
                // one or two EOL bytes, so dropping this finds the same boundaries (equivalent).
                i = end - 1;
            }
            return result;
        }

        /// <summary>
        /// A <c>%%EOF</c> only ends a revision when a <c>startxref</c> with a strictly positive,
        /// backward-pointing offset precedes it. Without this check, the five bytes appearing
        /// inside a compressed stream would split one revision into two, and
        /// <see cref="Extract"/> would hand back a truncated file.
        ///
        /// <para><b>Zero is rejected</b> because offset 0 addresses the <c>%PDF</c> header, which
        /// is never a cross-reference section; <c>IncrementalUpdateWriter</c> refuses to
        /// <i>write</i> a <c>/Prev 0</c> for the same reason. It is also the conventional filler in
        /// a linearized file's first-page trailer — but only the convention, not the rule: Annex F
        /// lets that integer be anything, so the linearized case is handled by structure, in
        /// <see cref="LinearizedFirstPageTerminator"/>, and not by this test alone.</para>
        /// </summary>
        private static bool HasValidStartXrefBefore(byte[] bytes, int eofPos)
        {
            long offset = StartXrefOffsetBefore(bytes, eofPos);
            return offset > 0 && offset < eofPos;
        }

        /// <summary>
        /// The integer of the <c>startxref &lt;integer&gt;</c> that precedes the <c>%%EOF</c> at
        /// <paramref name="eofPos"/> within <see cref="StartXrefWindowBytes"/>, or <c>-1</c> when no
        /// such well-formed pair does. A value past the end of the file saturates to one more than
        /// the file length — still a well-formed trailer terminator, never a valid boundary.
        /// </summary>
        private static long StartXrefOffsetBefore(byte[] bytes, int eofPos)
        {
            int windowStart = Math.Max(0, eofPos - StartXrefWindowBytes);
            for (int j = eofPos - StartXrefToken.Length; j >= windowStart; j--)
            {
                if (!Matches(bytes, j, StartXrefToken)) continue;

                long offset = 0;
                bool anyDigit = false;
                for (int k = j + StartXrefToken.Length; k < eofPos; k++)
                {
                    byte c = bytes[k];
                    if (c >= (byte)'0' && c <= (byte)'9')
                    {
                        anyDigit = true;
                        // Stops accumulating once past the file length, so a long run of digits
                        // cannot overflow; the value is then reported as saturated below.
                        if (offset <= bytes.Length) offset = offset * 10 + (c - (byte)'0');
                    }
                    // §7.2.2 Table 1's full set, through the one classifier: CR, LF, space and tab
                    // alone would send a trailer written `startxref\f123\r\n%%EOF` to the
                    // `return -1` below and leave the revision boundary uncounted, making the public
                    // GetRevisions under-report the file's revisions. NUL was missing too.
                    else if (PdfConstants.IsWhitespace(c))
                    {
                        if (anyDigit) break;
                    }
                    else
                    {
                        return -1; // anything else between the keyword and %%EOF is not a trailer
                    }
                }
                if (!anyDigit) return -1;
                // The saturation keeps the documented return value honest; no caller can see it.
                // HasValidStartXrefBefore wants offset < eofPos and LinearizedFirstPageTerminator
                // only offset >= 0, and an unsaturated value (already past bytes.Length) answers
                // both the same — returning `offset` as-is is an equivalent mutation.
                return offset > bytes.Length ? bytes.Length + 1L : offset;
            }
            return -1;
        }

        /// <summary>
        /// The position of the <c>%%EOF</c> that closes a linearized file's first-page trailer, or
        /// <c>-1</c> when the file is not linearized or that trailer wrote no terminator.
        /// </summary>
        /// <remarks>
        /// <para>Annex F.3.4 (ISO 32000-2, lines 81948–81949 of <c>docs/iso/iso32000-2.txt</c>):
        /// the first-page trailer "may optionally end with startxref, an integer, and %%EOF, just
        /// as in an ordinary trailer. This information shall be ignored." The integer is not
        /// constrained to zero — zero is only the conventional filler, in the annex's Example 3
        /// (81700–81703) and in this library's own linearizer — so
        /// <see cref="HasValidStartXrefBefore"/>'s rejection of offset 0 covered the convention and
        /// not the rule. A first-page trailer naming a real earlier position (the first-page table's
        /// own offset, say) made a phantom revision 0 ending after part 3, whose
        /// <see cref="Extract"/> is a truncated file, and moved every signature attribution one
        /// revision along.</para>
        ///
        /// <para>The recognition follows the structure the annex defines rather than the integer's
        /// value. Part 2 — the linearization parameter dictionary — is the first object and lies
        /// within the first 1024 bytes (F.3.3, 81846–81855); part 3 — the first-page cross-reference
        /// table and trailer — follows it directly (F.3.4, 81936–81946). So the terminator to
        /// ignore is the first <c>%%EOF</c> after that object that a <c>startxref</c> precedes,
        /// provided nothing but the cross-reference section lies between them: no object at all for
        /// a table, or exactly one object carrying <c>/XRef</c> for a cross-reference stream (F.3.4
        /// allows either). A first-page trailer that omitted the optional terminator fails that
        /// test — parts 4 to 10 hold at least the catalog, the first page and the hint stream — and
        /// the main trailer's <c>%%EOF</c> then counts, as it must.</para>
        ///
        /// <para>An update appended to a linearized file leaves all of this in place: its own
        /// terminator follows the main trailer's and is a second revision. Nothing here reads
        /// <c>/L</c>, whose mismatch after an update makes the linearization information stale
        /// (Table F.1) — the first-page terminator is ignorable whether or not the hint tables still
        /// hold.</para>
        /// </remarks>
        private static int LinearizedFirstPageTerminator(byte[] bytes)
        {
            int obj = LinearizationDictionaryAt(bytes);
            if (obj < 0) return -1;
            int endObj = IndexOf(bytes, EndObjToken, obj, bytes.Length);
            if (endObj < 0) return -1;

            int part3 = endObj + EndObjToken.Length;
            int eof = -1;
            for (int i = part3; i + EofToken.Length <= bytes.Length; i++)
            {
                if (!Matches(bytes, i, EofToken)) continue;
                if (StartXrefOffsetBefore(bytes, i) < 0) continue;
                eof = i;
                break;
            }
            if (eof < 0) return -1;

            int objectsBetween = CountOccurrences(bytes, EndObjToken, part3, eof);
            if (objectsBetween == 0) return eof;
            if (objectsBetween == 1 && IndexOfName(bytes, XRefName, part3, eof) >= 0) return eof;
            return -1;
        }

        /// <summary>
        /// Where the Annex F linearization dictionary's <c>obj</c> keyword is, or <c>-1</c> when the
        /// bytes do not begin with one.
        /// </summary>
        /// <remarks>
        /// <para><b>Annex F.3.3 puts the dictionary in the first 1024 bytes</b>, and it must be the
        /// <i>first</i> object in the file — so this looks for one `obj` inside that window and for
        /// <c>/Linearized</c> before the window ends. It reads no further, which is what lets
        /// <see cref="IsLinearized"/> answer from a head rather than from a whole file.</para>
        /// <para><b>One recognition, two callers.</b> <see cref="LinearizedFirstPageTerminator"/>
        /// continues from here to find the first-page <c>%%EOF</c>; a second copy of the window rule
        /// is how the two would come to disagree about what a linearized file is.</para>
        /// </remarks>
        private static int LinearizationDictionaryAt(byte[] bytes)
        {
            // The `Min` is bounded twice over for one of the two callers and defensive for the
            // other, so a mutation sweep reports it as a survivor: IsLinearized never hands over
            // more than the window in the first place, and the revision walk — which does hand over
            // the whole file — can only reach past it with a file that is well-formed enough for
            // the walk AND has its first object beyond 1024 bytes, which no producer writes. It
            // stays because the rule is Annex F.3.3's and belongs where the recognition is, not at
            // whichever caller happens to bound its input today.
            int window = Math.Min(bytes.Length, Core.Constants.PdfLimits.LinearizationDictionaryWindowBytes);
            int obj = IndexOf(bytes, ObjToken, 0, window);
            if (obj < 0) return -1;
            return IndexOfName(bytes, LinearizedName, obj, window) < 0 ? -1 : obj;
        }

        /// <summary>The first position in [<paramref name="from"/>, <paramref name="to"/>) where <paramref name="token"/> fits entirely, or <c>-1</c>.</summary>
        private static int IndexOf(byte[] bytes, byte[] token, int from, int to)
        {
            for (int i = Math.Max(0, from); i + token.Length <= to; i++)
                if (Matches(bytes, i, token)) return i;
            return -1;
        }

        /// <summary>
        /// As <see cref="IndexOf"/>, for a name token: the bytes must be followed by whitespace, a
        /// delimiter or the end of the file, so <c>/Linearized</c> is not found inside
        /// <c>/LinearizedX</c>.
        /// </summary>
        private static int IndexOfName(byte[] bytes, byte[] name, int from, int to)
        {
            for (int i = Math.Max(0, from); i + name.Length <= to; i++)
            {
                if (!Matches(bytes, i, name)) continue;
                int next = i + name.Length;
                if (next >= bytes.Length
                    || PdfConstants.IsWhitespace(bytes[next])
                    || PdfConstants.IsDelimiter(bytes[next]))
                    return i;
            }
            return -1;
        }

        /// <summary>How many non-overlapping times <paramref name="token"/> occurs in [<paramref name="from"/>, <paramref name="to"/>).</summary>
        private static int CountOccurrences(byte[] bytes, byte[] token, int from, int to)
        {
            int count = 0;
            for (int i = Math.Max(0, from); i + token.Length <= to; i++)
            {
                if (!Matches(bytes, i, token)) continue;
                count++;
                i += token.Length - 1;
            }
            return count;
        }

        /// <summary>
        /// The offset just past the first <b>validated</b> <c>%%EOF</c> at or after
        /// <paramref name="from"/>, or <c>-1</c>. Validated means the same thing it means in
        /// <see cref="Boundaries"/>: a <c>startxref</c> with a strictly positive, backward-pointing
        /// offset precedes it.
        /// </summary>
        /// <remarks>
        /// <para>Exposed for <c>PdfIncrementalSaveValidator</c>, which asks where the first revision
        /// ends. <b>Its own scan took the first <c>%%EOF</c> with no validation at all</b>, and
        /// unlike the equivalent scan in <c>Plugins.EthereumSign.PdfBlockchainVerifier</c> — which
        /// documents why it is safe to skip the check, its input being a revision this library wrote
        /// that contains no stream — that one starts at a cross-reference section's offset. For a
        /// PDF 1.5+ file that section is an <b>xref stream</b>, whose Flate-compressed payload can
        /// contain the five bytes <c>%%EOF</c>. The scan then stopped early, the first revision was
        /// measured too short, and <c>SignaturesPreserved</c> could report <c>true</c> for a file
        /// that had overwritten it — a wrong answer to a security question.</para>
        ///
        /// <para>Returning the position <i>past</i> the marker matches what that caller compares
        /// against, and the end-of-line is deliberately not swallowed here: this answers "where did
        /// the terminator finish", not "where does the extracted revision end".</para>
        /// </remarks>
        internal static long FindValidatedEofAfter(byte[] bytes, long from)
        {
            if (bytes == null) return -1;

            int ignoredEof = LinearizedFirstPageTerminator(bytes);
            for (int i = (int)Math.Max(0, from); i + EofToken.Length <= bytes.Length; i++)
            {
                if (!Matches(bytes, i, EofToken)) continue;
                if (i == ignoredEof || !HasValidStartXrefBefore(bytes, i)) continue;
                return i + EofToken.Length;
            }
            return -1;
        }

        private static bool Matches(byte[] bytes, int at, byte[] token)
        {
            if (at < 0 || at + token.Length > bytes.Length) return false;
            for (int k = 0; k < token.Length; k++)
                if (bytes[at + k] != token[k]) return false;
            return true;
        }
    }

    /// <summary>One revision of an incrementally-updated PDF.</summary>
    public sealed class PdfRevisionInfo
    {
        internal PdfRevisionInfo(int index, long length)
        {
            Index = index;
            Length = length;
        }

        /// <summary>Zero-based, oldest first. 0 is the document as originally written.</summary>
        public int Index { get; }

        /// <summary>
        /// Bytes from the start of the file through this revision's <c>%%EOF</c> — i.e. exactly
        /// what <see cref="PdfRevisions.Extract"/> writes for it.
        /// </summary>
        public long Length { get; }

        /// <summary>
        /// The fully-qualified names of the signature fields whose declared <c>/ByteRange</c>
        /// covers exactly this revision — the signatures that would still verify against the
        /// bytes <see cref="PdfRevisions.Extract"/> writes for it.
        ///
        /// <para><b>Three states, and the difference matters.</b> <c>null</c> means the signatures
        /// were not examined at all — which is what <see cref="PdfRevisions.List"/> always
        /// returns, since it is a pure byte-level read that works on files whose object graph does
        /// not parse. An <i>empty</i> list means they were examined and none covers this revision.
        /// A non-empty list names them. Collapsing the first two would make an unreadable document
        /// report as an unsigned one.</para>
        ///
        /// <para>Populated only by <see cref="PdfRevisions.ListWithSignatures"/>.</para>
        /// </summary>
        public IReadOnlyList<string> SignedBy { get; private set; }

        internal void SetSignedBy(IReadOnlyList<string> names) => SignedBy = names;
    }

    /// <summary>
    /// A document's revisions together with the signatures covering each — the result of
    /// <see cref="PdfRevisions.ListWithSignatures"/>.
    /// </summary>
    public sealed class PdfRevisionAttribution
    {
        internal PdfRevisionAttribution(
            IReadOnlyList<PdfRevisionInfo> revisions,
            string notExaminedReason,
            IReadOnlyList<string> signaturesCoveringNoRevision)
        {
            Revisions = revisions;
            NotExaminedReason = notExaminedReason;

            // Derived on the same argument as SignaturesExamined below, and for the same reason:
            // names collected before the walk threw describe an examination that did not happen,
            // so reporting them would contradict this type's own contract that the list is empty
            // when the signatures were not examined — cleared here, not by a caller that has to
            // remember to.
            SignaturesCoveringNoRevision = notExaminedReason == null
                ? signaturesCoveringNoRevision
                : Array.Empty<string>();
        }

        /// <summary>
        /// The revisions, oldest first — identical to what <see cref="PdfRevisions.List"/> returns
        /// for the same input, since revision boundaries are found in the bytes and owe nothing to
        /// the object graph.
        /// </summary>
        public IReadOnlyList<PdfRevisionInfo> Revisions { get; }

        /// <summary>
        /// True when the form was read and every revision's <see cref="PdfRevisionInfo.SignedBy"/>
        /// is therefore meaningful.
        ///
        /// <para><b>Derived, never settable</b>, so it cannot contradict
        /// <see cref="NotExaminedReason"/>: a result claiming the signatures were examined while
        /// carrying a reason they were not is precisely the plausible-looking wrong answer this
        /// type exists to prevent.</para>
        /// </summary>
        public bool SignaturesExamined => NotExaminedReason == null;

        /// <summary>Why the signatures were not examined; <c>null</c> when they were.</summary>
        public string NotExaminedReason { get; }

        /// <summary>
        /// Signatures whose <c>/ByteRange</c> ends at no revision boundary at all. Reported rather
        /// than dropped: a range ending mid-file is either a damaged or a doctored signature, and
        /// a caller counting <see cref="PdfRevisionInfo.SignedBy"/> entries would otherwise never
        /// learn the document claimed one more signature than it could place.
        ///
        /// <para><b>Empty when the signatures were not examined, and derived rather than stored</b>
        /// — like <see cref="SignaturesExamined"/>, so it cannot contradict
        /// <see cref="NotExaminedReason"/>. A walk that named some signatures and then threw has
        /// examined nothing a caller may rely on, and a result carrying both a reason and a list of
        /// findings is exactly the plausible-looking wrong answer this type exists to prevent.</para>
        /// </summary>
        public IReadOnlyList<string> SignaturesCoveringNoRevision { get; }
    }
}
