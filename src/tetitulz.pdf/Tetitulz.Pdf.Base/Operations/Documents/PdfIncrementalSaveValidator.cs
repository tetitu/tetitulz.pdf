// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>Information about the incremental update structure of a PDF.</summary>
    public sealed class IncrementalSaveInfo
    {
        /// <summary>Number of revisions (1 = original, 2 = one incremental update, etc.).</summary>
        public int              RevisionCount      { get; init; }
        /// <summary>
        /// Stream position of each revision's cross-reference section (oldest first) — where its
        /// <c>xref</c> keyword or cross-reference stream object begins, counted from byte 0 of the
        /// stream even when bytes precede the <c>%PDF-</c> header (ISO 32000-2 §7.5.2), so it indexes
        /// the bytes as read. A hybrid file's <c>/XRefStm</c> stream belongs to its classic section's
        /// revision (§7.5.8.4) and is not listed.
        /// </summary>
        public IReadOnlyList<long> RevisionOffsets { get; init; }
        /// <summary>
        /// True when every later revision's cross-reference section starts after the first revision
        /// ends: a reading of the file's structure, which an overwrite of the first revision's bytes does
        /// not disturb. It says nothing of a signature's validity; a digest does, through
        /// <c>Signing.PdfSignatureVerifier</c>.
        /// </summary>
        public bool             SignaturesPreserved { get; init; }
        /// <summary>
        /// Per-signature ByteRange validation results, in document order.
        /// Empty when no /Sig fields exist.
        /// </summary>
        public IReadOnlyList<SignatureByteRangeReport> SignatureReports { get; init; }
            = System.Array.Empty<SignatureByteRangeReport>();
    }

    /// <summary>
    /// Validation outcome for a single <c>/Sig</c> field's <c>/ByteRange</c>.
    /// </summary>
    public sealed class SignatureByteRangeReport
    {
        /// <summary>
        /// Fully-qualified form-field name of the signature (<c>/T</c> joined through the
        /// <c>/Parent</c> chain), when known; <c>null</c> when no node in the chain carries a
        /// <c>/T</c>. For the common top-level field this is just its own <c>/T</c>.
        /// </summary>
        public string FieldName { get; init; }
        /// <summary>Raw <c>/ByteRange</c> values: [start1, length1, start2, length2].</summary>
        public IReadOnlyList<long> ByteRange { get; init; }
        /// <summary>
        /// True when the byte range covers the entire file except the signature's own <c>/Contents</c>
        /// hex string, which is read at the gap: a gap holding anything else is reported in <see cref="Issue"/>.
        /// </summary>
        public bool CoversWholeFile { get; init; }
        /// <summary>
        /// True when this signature is "outermost" — the /ByteRange end (start2 + length2)
        /// equals the file length.  Multi-sig PDFs should have exactly one outermost
        /// signature (the most recent); inner signatures cover everything up to the
        /// start of the outermost one.
        /// </summary>
        public bool IsOutermost { get; init; }
        /// <summary>Diagnostic note when validation failed; null when OK.</summary>
        public string Issue { get; init; }
    }

    /// <summary>
    /// Inspects the cross-reference chain to describe the incremental update structure.
    /// </summary>
    public static class PdfIncrementalSaveValidator
    {
        /// <summary>Inspects a PDF file and returns incremental save information.</summary>
        public static IncrementalSaveInfo Inspect(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Inspect(fs);
        }

        /// <summary>Inspects a PDF stream and returns incremental save information.</summary>
        public static IncrementalSaveInfo Inspect(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            input = PdfHelpers.EnsureSeekable(input);   // the one seek-normalisation; rewinds
            input.Position = 0;

            // Collect all startxref offsets by following /Prev chain.
            var offsets = new List<long>();
            CollectRevisions(input, offsets);
            offsets.Reverse(); // oldest first

            // Check if signatures are preserved: verify the file starts with %%EOF
            // and each revision is purely an append (bytes before the first xref untouched).
            bool signaturesPreserved = offsets.Count <= 1 || CheckSignaturesPreserved(input, offsets);

            // Walk the AcroForm's whole field tree and collect /Sig field ByteRanges so callers
            // can confirm each signature's range is consistent with file length and revision
            // boundaries — nested ones too: a signature under a parent field node is how common
            // viewers write a second signature into an existing form, the shape SignatureFieldWalk
            // exists to handle.
            var sigReports = CollectSignatureReports(input);

            return new IncrementalSaveInfo
            {
                RevisionCount       = offsets.Count,
                RevisionOffsets     = offsets.AsReadOnly(),
                SignaturesPreserved = signaturesPreserved,
                SignatureReports    = sigReports,
            };
        }

        private static IReadOnlyList<SignatureByteRangeReport> CollectSignatureReports(Stream stream)
        {
            var reports = new List<SignatureByteRangeReport>();
            stream.Position = 0;
            try
            {
                using var doc = PdfDocument.Open(stream);
                var acro = SignatureFieldWalk.ResolveAcroForm(doc);
                if (acro == null) return reports;

                long fileLen = stream.Length;
                long maxBrEnd = -1;
                var pending = new List<SignatureByteRangeReport>();

                foreach (var (field, sig) in SignatureFieldWalk.DistinctSignatures(acro))
                {
                    // ONE reader, in Core's SignedByteRanges. This copy was the weakest kind:
                    // GetArray does not follow a reference, so `/ByteRange 9 0 R` (§7.3.10 permits
                    // it) read as absent and the signature vanished from the report entirely —
                    // and an indirect ELEMENT made GetLong throw, which the blanket catch below
                    // turns into losing every signature after it as well, not just this one.
                    if (!SignedByteRanges.TryRead(sig, doc, out var br, out _)) continue;

                    long s1 = br.Offset1, l1 = br.Length1;
                    long s2 = br.Offset2, l2 = br.Length2;
                    long brEnd = br.CoverageEnd;

                    string issue = null;
                    if (s1 != 0)
                        issue = $"ByteRange start1={s1} (expected 0)";
                    else if (s1 + l1 > s2)
                        issue = $"ByteRange first segment overlaps gap (s1={s1} l1={l1} s2={s2})";
                    else if (brEnd > fileLen)
                        issue = $"ByteRange end {brEnd} exceeds file length {fileLen}";
                    else if (!(sig[PdfName.Contents] is PdfString contents)
                             || !SignedByteRanges.GapHoldsOnlyContents(stream, s1 + l1, s2 - s1 - l1, contents.RawBytes))
                        issue = "ByteRange gap holds something other than this signature's /Contents";

                    if (brEnd > maxBrEnd) maxBrEnd = brEnd;

                    pending.Add(new SignatureByteRangeReport
                    {
                        FieldName       = field.ReportedName,
                        ByteRange       = new long[] { s1, l1, s2, l2 },
                        CoversWholeFile = issue == null && SignedByteRanges.ReachesEndOfFile(stream, brEnd, fileLen),
                        IsOutermost     = false, // filled in below
                        Issue           = issue,
                    });
                }

                // Mark the outermost signature: the one whose ByteRange end
                // matches the file length (or, when none does, the largest end).
                for (int i = 0; i < pending.Count; i++)
                {
                    var p = pending[i];
                    long end = p.ByteRange[2] + p.ByteRange[3];
                    bool outer = end == maxBrEnd;
                    reports.Add(new SignatureByteRangeReport
                    {
                        FieldName       = p.FieldName,
                        ByteRange       = p.ByteRange,
                        CoversWholeFile = p.CoversWholeFile,
                        IsOutermost     = outer,
                        Issue           = p.Issue,
                    });
                }
            }
            catch { /* malformed document — return whatever we collected */ }
            return reports;
        }

        /// <summary>
        /// The stream position of every revision's cross-reference section, newest first — read off
        /// the parser's own walk, which has already visited each section once.
        /// </summary>
        /// <remarks>
        /// <para><b>It does not follow <c>/Prev</c> itself.</b> Re-parsing the <i>whole document</i>
        /// from its tail per hop to read "the trailer at the previous offset" gives the same merged
        /// trailer every time, whose <c>/Prev</c> is the newest section's again, so such a loop stops
        /// on the second hop and a document with three revisions reports two. An offset is not a
        /// stream position in a file with bytes before its header (§7.5.2);
        /// <see cref="PdfParser.RevisionSectionPositions"/> holds positions.</para>
        /// <para>A hybrid file's <c>/XRefStm</c> stream belongs to its classic section's revision
        /// (§7.5.8.4) and is not a revision; the parser's list already leaves it out. A file the parser
        /// could only recover by scanning read no section, and keeps the one-revision answer it always
        /// had — there is no chain to count.</para>
        /// </remarks>
        private static void CollectRevisions(Stream stream, List<long> offsets)
        {
            var parser = new PdfParser(stream);
            try { parser.ParseCrossReferences(); } catch { return; }

            if (parser.RevisionSectionPositions.Count == 0)
            {
                offsets.Add(parser.StartXrefOffset);
                return;
            }
            offsets.AddRange(parser.RevisionSectionPositions);
        }

        private static bool CheckSignaturesPreserved(Stream stream, IReadOnlyList<long> offsets)
        {
            // The first revision ends around the first %%EOF after the first xref.
            // Signatures are preserved if subsequent revisions only append after that point.
            // Simple heuristic: check that the first startxref offset is less than all others.
            if (offsets.Count < 2) return true;
            long firstEnd = FindEofAfter(stream, offsets[0]);
            for (int i = 1; i < offsets.Count; i++)
                if (offsets[i] < firstEnd) return false;
            return true;
        }

        /// <summary>
        /// Where the revision beginning at <paramref name="startFrom"/> ends: the offset just past
        /// its <c>%%EOF</c>, or <c>-1</c>.
        /// </summary>
        /// <remarks>
        /// <para>Through <see cref="PdfRevisions.FindValidatedEofAfter"/>, which requires a
        /// <c>startxref</c> with a strictly positive, backward-pointing offset before the marker.
        /// <b>Never the first <c>%%EOF</c> seen, unvalidated.</b></para>
        ///
        /// <para><c>startFrom</c> is a cross-reference section's offset, and for a PDF 1.5+ file
        /// that section is an <b>xref stream</b> whose Flate-compressed payload can contain the five
        /// bytes <c>%%EOF</c>. The scan then stopped inside it, the first revision was measured too
        /// short, and <see cref="CheckSignaturesPreserved"/> — which asks whether every later
        /// revision starts after the first one ends — could answer <c>true</c> for a file that had
        /// overwritten it. That is a wrong answer to a security question, not a missing one.</para>
        ///
        /// <para>The sibling scan in <c>Plugins.EthereumSign.PdfBlockchainVerifier</c> skips the
        /// same check and is <b>right to</b>: its verdict also holds the revision to one dictionary, an
        /// xref section and a trailer (<c>DocMdpAnalyzer.ChangesBesidesCatalogEntry</c>), with no stream
        /// beyond a cross-reference one. The difference between the two is the input, which is why the
        /// justification has to be written down rather than inferred from the shape of the loop.</para>
        /// </remarks>
        private static long FindEofAfter(Stream stream, long startFrom)
        {
            byte[] bytes = StreamBytes.ReadAll(stream);
            long eof = PdfRevisions.FindValidatedEofAfter(bytes, startFrom);

            // No validated terminator: fall back to the file length, which is what this returned
            // before and is the conservative answer. CheckSignaturesPreserved compares every later
            // revision's offset against this, so the whole file means "every one of them starts
            // before the first revision ends" — i.e. refuse to claim preservation. Returning -1
            // instead would make nothing compare less than it and silently answer "preserved".
            return eof >= 0 ? eof : bytes.Length;
        }

    }
}
