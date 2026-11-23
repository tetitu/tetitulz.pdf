// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// Rewrites a document's HTJ2K images as Part 1 JPEG 2000, losslessly, by an incremental update: each
    /// <c>JPXDecode</c> image XObject whose codestream uses HT code-blocks (ITU-T T.814) is superseded —
    /// same object number, same dictionary — by the same image re-coded with T.800's block coder
    /// (<see cref="JpxTranscoding.TryToPart1(byte[], out byte[], out string)"/>), which decodes to the same samples.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> ISO 32000-2 §7.4.9 limits <c>JPXDecode</c> data to the JPX baseline, whose codestreams
    /// are T.800's, so an HTJ2K image is outside PDF — PDF/A reports it (A29) — and most viewers show nothing for
    /// it. Transcoded, every reader decodes it, and nothing about the picture changes.</para>
    /// <para><b>Only the bytes change.</b> The dictionary is kept as it stands: <c>/ColorSpace</c>, <c>/Decode</c>,
    /// <c>/SMaskInData</c> and the rest describe the decoded samples, which are the same, so nothing is
    /// re-derived from the file — unlike <see cref="PdfImageReplacer"/>, which describes a new picture.</para>
    /// <para><b>Signatures survive, certifications refuse</b>, as for <see cref="PdfImageReplacer"/>: the update
    /// is appended, so an approval signature's digest still matches; changing an image is not a change any DocMDP
    /// <c>/P</c> permits. <b>An encrypted document is refused</b> — the new streams would be written in clear.</para>
    /// </remarks>
    public static class PdfJpxTranscoder
    {
        /// <summary>
        /// Transcodes every HTJ2K image the document holds to Part 1 JPEG 2000 and appends the revision to
        /// <paramref name="output"/>. With nothing to transcode, <paramref name="output"/> receives the input
        /// unchanged. Images whose codestream cannot be transcoded are left as they are and named in the result.
        /// </summary>
        /// <param name="input">Source PDF; read only.</param>
        /// <param name="output">Receives the original bytes followed by one appended revision.</param>
        /// <param name="allowCertifiedDocument">Proceed even though a DocMDP certification forbids the change.</param>
        public static JpxTranscodeResult TranscodeHighThroughputToPart1(Stream input, Stream output, bool allowCertifiedDocument = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            JpxTranscodePlan plan;
            using (var doc = PdfDocument.Open(ms))
            {
                EncryptedRevision.Refuse(doc, "transcode the images of", "the new image streams");
                plan = Plan(doc, allowCertifiedDocument);
                ms.Position = 0;
                if (plan.Objects.Count == 0)
                {
                    ms.CopyTo(output);
                    return plan.Result;
                }
                IncrementalUpdateWriter.WriteUpdate(ms, output, new List<IndirectObject>(plan.Objects), plan.Trailer,
                    doc.Parser.StartXrefOffset, plan.NextObjectNumber);
            }
            return plan.Result;
        }

        /// <summary>
        /// The objects a transcode would write, without writing them: every image XObject the pages reach
        /// (<see cref="DocumentImageIndex"/>) whose only filter is <c>JPXDecode</c> and whose codestream uses
        /// HT code-blocks, superseded by its Part 1 form.
        /// </summary>
        public static JpxTranscodePlan Plan(PdfDocument document, bool allowCertifiedDocument = false, int firstFreeObjectNumber = 0)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (DocMdpAnalyzer.TryReadCertification(document, out int permission, out string description) && !allowCertifiedDocument)
                throw new PdfException(
                    $"Document carries a DocMDP certification signature (P={permission}: {description}). Changing an " +
                    "image's data is not a permitted change at any /P level and will invalidate the certification. " +
                    "Pass allowCertifiedDocument to proceed anyway.");

            var objects = new List<IndirectObject>();
            var transcoded = new List<int>();
            var skipped = new List<KeyValuePair<int, string>>();
            // Each image's transcode is bounded by its own bytes; the document's images together by one
            // ceiling, so a document of many images is not many ceilings of work.
            var documentWork = new Core.Util.WorkBudget(PdfLimits.MaxJpxDecodeWork,
                "JPEG 2000 transcoding of this document (PdfLimits.MaxJpxDecodeWork)");
            foreach (KeyValuePair<int, DocumentImageIndex.Entry> entry in DocumentImageIndex.Build(document).Entries)
            {
                PdfStream image = entry.Value.Stream;
                if (!IsJpxOnly(document, image)) continue;
                byte[] codestream = image.RawData;
                if (!JpxHeader.TryReadHighThroughput(codestream, out bool ht) || !ht) continue;
                if (!JpxTranscoding.TryToPart1(codestream, documentWork, out byte[] part1, out string reason))
                {
                    skipped.Add(new KeyValuePair<int, string>(entry.Key, reason));
                    continue;
                }
                // A stream clones as a stream: the dictionary's entries, every one kept, the new codestream its
                // data (the writer states /Length from the data).
                var superseding = (PdfStream)PdfDictionary.CloneShallow(image);
                superseding.RawData = part1;
                superseding.ObjectNumber = entry.Key;
                superseding.GenerationNumber = image.GenerationNumber;
                objects.Add(new IndirectObject(entry.Key, image.GenerationNumber, superseding));
                transcoded.Add(entry.Key);
            }

            int next = Math.Max(document.CrossReferenceTable.GetMaxObjectNumber() + 1, firstFreeObjectNumber);
            PdfDictionary trailer = Documents.PdfPageContentAppender.BuildTrailer(document.Trailer, next);
            return new JpxTranscodePlan(objects, trailer, next, new JpxTranscodeResult(transcoded, skipped));
        }

        /// <summary>Whether <c>/Filter</c> is <c>JPXDecode</c> alone — a name, or an array of that one name — so the stream's bytes are the codestream.</summary>
        private static bool IsJpxOnly(PdfDocument document, PdfStream image)
        {
            PdfName name = PdfResolve.Name(document, image, PdfName.Filter);
            if (name != null) return name == PdfName.JPXDecode;
            PdfArray array = PdfResolve.Array(document, image, PdfName.Filter);
            return array != null && array.Count == 1 && document.ResolveObject(array[0]) is PdfName only && only == PdfName.JPXDecode;
        }
    }

    /// <summary>What <see cref="PdfJpxTranscoder"/> did: the images transcoded, by object number, and those left as they were, with why.</summary>
    public sealed class JpxTranscodeResult
    {
        internal JpxTranscodeResult(IReadOnlyList<int> transcoded, IReadOnlyList<KeyValuePair<int, string>> skipped)
        {
            Transcoded = transcoded;
            Skipped = skipped;
        }

        /// <summary>Object numbers of the image XObjects now Part 1 JPEG 2000.</summary>
        public IReadOnlyList<int> Transcoded { get; }

        /// <summary>HTJ2K images that could not be transcoded, with the reason; they are unchanged.</summary>
        public IReadOnlyList<KeyValuePair<int, string>> Skipped { get; }
    }

    /// <summary>The objects a transcode appends, and the trailer, for a caller that writes the revision itself (an encrypted document's route).</summary>
    public sealed class JpxTranscodePlan
    {
        internal JpxTranscodePlan(IReadOnlyList<IndirectObject> objects, PdfDictionary trailer, int nextObjectNumber, JpxTranscodeResult result)
        {
            Objects = objects;
            Trailer = trailer;
            NextObjectNumber = nextObjectNumber;
            Result = result;
        }

        /// <summary>The superseding image XObjects.</summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>The appended revision's trailer.</summary>
        public PdfDictionary Trailer { get; }

        /// <summary>One past the highest object number the revision uses.</summary>
        public int NextObjectNumber { get; }

        /// <summary>What the transcode would do.</summary>
        public JpxTranscodeResult Result { get; }
    }
}
